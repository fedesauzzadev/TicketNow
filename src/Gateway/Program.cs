using System.Collections.Concurrent;
using System.Security.Claims;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.IdentityModel.Tokens;
using TicketNow.ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);

builder.AddTicketNowDefaults("gateway");
builder.Services.AddReverseProxy().LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));
builder.AddGatewayDestinationsHealth();

// Admission token (docs/02 ADR-004/010, docs/03 §1): JWT HS256 emitido por
// queue-service. Sin token válido no se accede a compra (429 + a la fila).
var signingKey = builder.Configuration["Queue:AdmissionSigningKey"]
    ?? throw new InvalidOperationException("Queue:AdmissionSigningKey no configurado (debe coincidir con queue-service)");
var userSigningKey = builder.Configuration["Auth:UserSigningKey"]
    ?? throw new InvalidOperationException("Auth:UserSigningKey no configurado");
builder.Services.AddSingleton<UserTokenService>();
builder.Services
    .AddAuthentication()
    .AddJwtBearer("admission", options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = AdmissionTokenService.Issuer,
            ValidateAudience = true,
            ValidAudience = AdmissionTokenService.Audience,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30),
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey)),
        };
        // El token viaja en header dedicado (no Authorization: futuro JWT de usuario).
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                var token = context.Request.Headers["X-Admission-Token"].FirstOrDefault();
                if (!string.IsNullOrEmpty(token))
                {
                    context.Token = token;
                }
                return Task.CompletedTask;
            },
            OnTokenValidated = context =>
            {
                // AuthenticationType explícito: la policy "purchase" distingue
                // esquemas por identidad autenticada (no se confía en defaults).
                var identity = new ClaimsIdentity(
                    context.Principal!.Claims, "admission", ClaimTypes.Name, ClaimTypes.Role);
                context.Principal = new ClaimsPrincipal(identity);
                return Task.CompletedTask;
            },
            // Sin OnChallenge: los fallos de policy los traduce
            // PurchaseResultHandler (un solo escritor de respuesta; dos
            // challenges encadenados reventaban con "response already started").
        };
    })
    .AddJwtBearer("user", options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = UserTokenService.Issuer,
            ValidateAudience = true,
            ValidAudience = UserTokenService.Audience,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30),
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(userSigningKey)),
            NameClaimType = ClaimTypes.NameIdentifier,
        };
        options.Events = new JwtBearerEvents
        {
            OnTokenValidated = context =>
            {
                var identity = new ClaimsIdentity(
                    context.Principal!.Claims, "user", ClaimTypes.NameIdentifier, ClaimTypes.Role);
                context.Principal = new ClaimsPrincipal(identity);
                return Task.CompletedTask;
            },
        };
    });
builder.Services.AddAuthorizationBuilder()
    .AddPolicy("admission", policy => policy.RequireAuthenticatedUser())
    .AddPolicy("purchase", policy =>
    {
        // Compra = turno vigente (admission) + identidad (user). Ambas.
        policy.AddAuthenticationSchemes("admission", "user");
        policy.Requirements.Add(new BothSchemesRequirement());
    });
builder.Services.AddSingleton<IAuthorizationHandler, BothSchemesHandler>();
builder.Services.AddSingleton<IAuthorizationMiddlewareResultHandler, PurchaseResultHandler>();

// Rate limiting en el borde (docs/06-seguridad.md §3): token bucket por IP.
// La IP real viaja en X-Forwarded-For (el edge es el único ingreso; red
// cerrada de compose). Sin esto, TODO el tráfico comparte la IP del proxy y
// el bucket estrangula la tormenta entera (lección k6, Fase 5).
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
});
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = static (context, _) =>
    {
        context.HttpContext.Response.Headers.RetryAfter = "10";
        return ValueTask.CompletedTask;
    };
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(httpContext =>
        RateLimitPartition.GetTokenBucketLimiter(
            // IP REAL (vía ForwardedHeaders, antes en el pipeline). Cubre rutas
            // anónimas (enter a la fila, catálogo) y suma defensa en compra.
            $"ip:{httpContext.Connection.RemoteIpAddress?.ToString() ?? "anon"}",
            _ => new TokenBucketRateLimiterOptions
            {
                // 10 RPS sostenidos por IP: holgado para humanos, frena scripts.
                // La tormenta legítima la ordena la FILA (tokens de admisión), no esto.
                TokenLimit = 600,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = 0, // fail fast: 429+Retry-After inmediato, jamás aparcar clientes
                TokensPerPeriod = 600,
                ReplenishmentPeriod = TimeSpan.FromMinutes(1),
            }));
});

var app = builder.Build();

app.MapTicketNowDefaults();
app.UseForwardedHeaders(); // antes del limiter: la partición usa la IP real
app.UseAuthentication();

app.UseRateLimiter();
app.UseAuthorization();

// El sub del JWT de usuario viaja aguas abajo como X-User-Id: los servicios
// no cambian (siguen leyendo el header; el gateway ya autenticó).
// Va DESPUÉS de UseAuthorization a propósito: sin esquema default, el
// AuthenticationMiddleware no autentica nada y el User se puebla recién en
// la evaluación de la policy (PolicyEvaluator corre los dos esquemas).
// Acá también vive el bucket por cuenta (30 req/min en compra, docs/06 §3):
// manual porque el GlobalLimiter corre pre-auth con User vacío.
var userBuckets = new ConcurrentDictionary<string, TokenBucketRateLimiter>();
app.Use(async (context, next) =>
{
    var sub = context.User.Identities
        .FirstOrDefault(i => i.IsAuthenticated && i.AuthenticationType == "user")
        ?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
    if (sub is null)
    {
        await next();
        return;
    }
    context.Request.Headers["X-User-Id"] = sub;
    if (IsPurchasePath(context.Request.Path))
    {
        var limiter = userBuckets.GetOrAdd(sub, _ => new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
        {
            TokenLimit = 30,
            TokensPerPeriod = 30,
            ReplenishmentPeriod = TimeSpan.FromMinutes(1),
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            QueueLimit = 0,
            AutoReplenishment = true,
        }));
        using var lease = limiter.AttemptAcquire();
        if (!lease.IsAcquired)
        {
            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            context.Response.Headers.RetryAfter = "60";
            await context.Response.WriteAsJsonAsync(new
            {
                error = "rate_limited",
                detail = "límite por cuenta: 30 req/min en compra",
            });
            return;
        }
    }
    await next();
});

static bool IsPurchasePath(PathString path) =>
    path.StartsWithSegments("/api/inventory", StringComparison.OrdinalIgnoreCase)
    || path.StartsWithSegments("/api/orders", StringComparison.OrdinalIgnoreCase)
    || path.StartsWithSegments("/api/payments", StringComparison.OrdinalIgnoreCase)
    || path.StartsWithSegments("/api/tickets", StringComparison.OrdinalIgnoreCase);

// Login mock didáctico (Fase 6): sin password real — en producción esto es un
// IdP. El sub es el email normalizado (identidad estable para límites).
app.MapPost("/api/auth/token", (LoginRequest request, UserTokenService tokens) =>
{
    if (string.IsNullOrWhiteSpace(request.Email) || !request.Email.Contains('@'))
    {
        return Results.BadRequest(new { error = "email inválido" });
    }
    var email = request.Email.Trim().ToLowerInvariant();
    var name = string.IsNullOrWhiteSpace(request.Name) ? email.Split('@')[0] : request.Name.Trim();
    var ttl = TimeSpan.FromHours(12);
    return Results.Ok(new LoginResponse(email, name, tokens.Issue(email, name, ttl), DateTimeOffset.UtcNow.Add(ttl)));
});

app.MapReverseProxy();

app.Run();

public sealed record LoginRequest(string Email, string? Name);
public sealed record LoginResponse(string UserId, string DisplayName, string Token, DateTimeOffset ExpiresAt);

/// <summary>La policy "purchase" exige AMBAS identidades (turno + usuario).</summary>
public sealed class BothSchemesRequirement : IAuthorizationRequirement;

public sealed class BothSchemesHandler : AuthorizationHandler<BothSchemesRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, BothSchemesRequirement requirement)
    {
        var schemes = context.User.Identities
            .Where(i => i.IsAuthenticated)
            .Select(i => i.AuthenticationType)
            .ToHashSet();
        if (schemes.Contains("admission") && schemes.Contains("user"))
        {
            context.Succeed(requirement);
        }
        return Task.CompletedTask;
    }
}

/// <summary>
/// Traductor único de fallos de autorización (Fase 6): sin turno → 429 a la
/// fila; con turno pero sin identidad → 401 al login. Un solo escritor evita
/// el crash de challenges encadenados ("response already started").
/// </summary>
public sealed class PurchaseResultHandler : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler _default = new();

    public async Task HandleAsync(
        RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult result)
    {
        if (result.Succeeded)
        {
            await next(context);
            return;
        }
        var schemes = context.User.Identities
            .Where(i => i.IsAuthenticated)
            .Select(i => i.AuthenticationType)
            .ToHashSet();
        context.Response.Headers.RetryAfter = "5";
        if (!schemes.Contains("admission"))
        {
            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            await context.Response.WriteAsJsonAsync(new
            {
                error = "admission_required",
                detail = "sin admission token válido: entrar por la fila virtual (/queue)",
            });
            return;
        }
        if (!schemes.Contains("user"))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new
            {
                error = "login_required",
                detail = "falta el JWT de usuario: POST /api/auth/token",
            });
            return;
        }
        await _default.HandleAsync(next, context, policy, result);
    }
}

public partial class Program { }
