using MassTransit;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using TicketNow.Contracts.Commands;
using TicketNow.Contracts.Events;
using TicketNow.ServiceDefaults;

namespace TicketNow.OrdersService;

public static class Api
{
    public static void MapOrdersEndpoints(this WebApplication app)
    {
        var orders = app.MapGroup("/api/orders");

        // POST /orders — checkout (docs/04 §5, INV-5). Flujo (ADR-009):
        //  1) validación rápida del hold por request/response (falla veloz 404/409);
        //  2) se ENVÍA SubmitOrder: la orden nace dentro del consumer (atomicidad total);
        //  3) 202 + polling de GET /orders/{id} (la saga avanza async).
        orders.MapPost("/", async (
            CreateOrderRequest request,
            [FromHeader(Name = "X-User-Id")] string? userId,
            [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
            [FromHeader(Name = "X-Admission-Token")] string? admissionToken,
            OrdersDbContext db,
            ConnectionMultiplexer mux,
            AdmissionChecker admission,
            ILoggerFactory loggerFactory,
            IRequestClient<ValidateHold> validate,
            IBus bus,
            CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("Orders.Api");

            if (string.IsNullOrWhiteSpace(userId))
            {
                return Results.Problem(title: "falta el header X-User-Id (hasta Fase 6, sin JWT)", statusCode: StatusCodes.Status400BadRequest);
            }
            if (string.IsNullOrWhiteSpace(idempotencyKey))
            {
                return Results.Problem(title: "falta el header Idempotency-Key (obligatorio en checkout)", statusCode: StatusCodes.Status400BadRequest);
            }

            var errors = new Dictionary<string, string[]>();
            if (request.HoldId == Guid.Empty) errors["holdId"] = ["obligatorio"];
            if (request.EventId == Guid.Empty) errors["eventId"] = ["obligatorio"];
            if (request.MaxPerAccount < 1) errors["maxPerAccount"] = ["debe ser >= 1"];
            if (request.Items.Count == 0) errors["items"] = ["al menos un item"];
            if (request.Items.Any(i => i.Qty < 1)) errors["items"] = ["cantidades mayores a 0"];
            if (request.Items.Any(i => i.UnitPrice < 0)) errors["items"] = ["precios no negativos"];
            if (!request.PaymentToken.StartsWith("tok_mock_", StringComparison.Ordinal)) errors["paymentToken"] = ["token mock inválido (usar POST /api/payments/token)"];
            if (errors.Count > 0) return Results.ValidationProblem(errors);

            // Turno atado al evento (ADR-012): el token dice PARA QUÉ evento es
            // el turno; si es de otro (o la sesión salió), se rechaza acá.
            // Missing = fail-open a propósito (ver AdmissionChecker).
            var admissionCheck = await admission.CheckAsync(admissionToken, request.EventId.ToString());
            if (admissionCheck is AdmissionCheckResult.WrongEvent)
            {
                return Results.Problem(
                    title: "este turno es para otro evento: hacé la fila del evento que querés comprar",
                    statusCode: StatusCodes.Status403Forbidden,
                    extensions: new Dictionary<string, object?> { ["error"] = "admission_for_other_event" });
            }
            if (admissionCheck is AdmissionCheckResult.Revoked)
            {
                return Results.Problem(
                    title: "saliste de la fila: volvé a entrar para comprar",
                    statusCode: StatusCodes.Status403Forbidden,
                    extensions: new Dictionary<string, object?> { ["error"] = "admission_revoked" });
            }
            if (admissionCheck is AdmissionCheckResult.Invalid)
            {
                return Results.Problem(
                    title: "turno inválido o vencido: volvé a entrar a la fila",
                    statusCode: StatusCodes.Status401Unauthorized,
                    extensions: new Dictionary<string, object?> { ["error"] = "admission_invalid" });
            }

            var existing = await db.Orders.FirstOrDefaultAsync(o => o.IdempotencyKey == idempotencyKey, ct);
            if (existing is not null)
            {
                return Results.Ok(new OrderAcceptedResponse(existing.Id, existing.Status.ToString()));
            }

            // Lock distribuido por clave (Redis, TTL 10 s, liberación con Lua comparando
            // token) + registro de "enviados recientes" (TTL 30 s). Cubre los dos casos:
            //  - concurrentes: el segundo espera (hasta 10 s) a que aparezca la fila;
            //  - secuenciales: el segundo encuentra el registro reciente y devuelve EL
            //    MISMO orderId aunque la fila aún no exista (el consumer la crea después;
            //    el estado se reporta Pending y el cliente hace polling).
            // Si Redis falla, se sigue sin lock: la UNIQUE(idempotency_key) impide
            // duplicados igual (fail-open con backstop en DB).
            var mutex = mux.GetDatabase();
            var lockKey = $"order-submit-lock:{idempotencyKey}";
            var recentKey = $"order-submit-recent:{idempotencyKey}";
            var lockToken = Guid.NewGuid().ToString("N");
            var locked = false;
            try
            {
                try
                {
                    locked = await mutex.StringSetAsync(lockKey, lockToken, TimeSpan.FromSeconds(10), when: When.NotExists);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Redis no disponible para lock de idempotencia; se sigue sin lock");
                }

                if (!locked)
                {
                    for (var attempt = 0; attempt < 100; attempt++)
                    {
                        await Task.Delay(100, ct);
                        var raced = await FindOrderAsync();
                        if (raced is not null)
                        {
                            return Results.Ok(raced);
                        }
                    }
                }

                // Doble chequeo bajo lock + registro reciente (cubre el caso secuencial).
                var rechecked = await FindOrderAsync();
                if (rechecked is not null)
                {
                    return Results.Ok(rechecked);
                }

                // Chequeo veloz del límite por cuenta (Fase 6): el autoritativo
                // vive en SubmitOrderConsumer (cubre la carrera entre gemelos).
                var requested = request.Items.Sum(i => i.Qty);
                var owned = await AccountLimits.OwnedQtyAsync(db, userId, request.EventId, ct);
                if (owned + requested > request.MaxPerAccount)
                {
                    return Results.Problem(
                        title: $"límite por cuenta excedido: ya tenés {owned}, pedís {requested}, máximo {request.MaxPerAccount}",
                        statusCode: StatusCodes.Status409Conflict);
                }

                // Chequeo veloz con reintentos (request/response sobre RabbitMQ).
                // Un fault del consumer NO responde (el fault va a error queue), así que
                // se reintenta: el redelivery lo procesa otra ejecución (idempotente).
                // La validación autoritativa la hace la saga antes de cobrar.
                Response<HoldValidated, HoldInvalid>? validation = null;
                var validated = false;
                for (var attempt = 0; attempt < 3 && !validated; attempt++)
                {
                    try
                    {
                        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        timeout.CancelAfter(TimeSpan.FromSeconds(10));
                        validation = await validate.GetResponse<HoldValidated, HoldInvalid>(
                            new ValidateHold(Guid.Empty, request.HoldId), timeout.Token);
                        validated = true;
                    }
                    catch (Exception ex) when (ex is RequestTimeoutException or OperationCanceledException)
                    {
                        logger.LogWarning(ex, "validación de hold intento {Attempt} sin respuesta; reintentando", attempt + 1);
                    }
                }
                var validResponse = validation.GetValueOrDefault();
                if (validation is null)
                {
                    return Results.Problem(title: "inventory no responde (reintentar)", statusCode: StatusCodes.Status503ServiceUnavailable);
                }
                if (validResponse.Is(out Response<HoldInvalid>? invalid))
                {
                    return Results.Problem(title: $"hold no utilizable: {invalid!.Message.Reason}", statusCode: StatusCodes.Status409Conflict);
                }

                var orderId = Guid.NewGuid();
                await bus.Send(new SubmitOrder(
                    orderId, request.HoldId, userId.Trim(), request.EventId, request.MaxPerAccount,
                    request.Items.Select(i => new OrderItemDto(i.ZoneId, i.Qty, i.UnitPrice)).ToList(),
                    request.PaymentToken, idempotencyKey), ct);

                try
                {
                    await mutex.StringSetAsync(recentKey, orderId.ToString("N"), TimeSpan.FromSeconds(30));
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "no se pudo registrar el envío reciente (UX degradada, sin duplicados)");
                }

                return Results.Accepted($"/api/orders/{orderId}", new OrderAcceptedResponse(orderId, OrderStatus.Pending.ToString()));

                async Task<OrderAcceptedResponse?> FindOrderAsync()
                {
                    var row = await db.Orders.FirstOrDefaultAsync(o => o.IdempotencyKey == idempotencyKey, ct);
                    if (row is not null)
                    {
                        return new OrderAcceptedResponse(row.Id, row.Status.ToString());
                    }
                    try
                    {
                        // Sin fila todavía: el ID del envío reciente alcanza (el estado
                        // real se obtiene por polling; lo importante es no duplicar).
                        var recent = await mutex.StringGetAsync(recentKey);
                        if (recent.HasValue && Guid.TryParse((string?)recent, out var recentId))
                        {
                            return new OrderAcceptedResponse(recentId, OrderStatus.Pending.ToString());
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "lectura de envío reciente falló");
                    }
                    return null;
                }
            }
            finally
            {
                if (locked)
                {
                    try
                    {
                        await mutex.ScriptEvaluateAsync(
                            "if redis.call('GET',KEYS[1])==ARGV[1] then return redis.call('DEL',KEYS[1]) else return 0 end",
                            new RedisKey[] { lockKey }, new RedisValue[] { lockToken });
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "no se pudo liberar el lock (expira solo por TTL)");
                    }
                }
            }
        });

        orders.MapGet("/{id:guid}", async (Guid id, [FromHeader(Name = "X-User-Id")] string? userId, OrdersDbContext db, CancellationToken ct) =>
        {
            var order = await db.Orders.Include(o => o.Items).FirstOrDefaultAsync(o => o.Id == id, ct);
            if (order is null)
            {
                return Results.Problem(title: "orden inexistente", statusCode: StatusCodes.Status404NotFound);
            }
            if (userId != order.UserId)
            {
                return Results.Problem(title: "la orden pertenece a otro usuario", statusCode: StatusCodes.Status403Forbidden);
            }
            var tickets = await db.Tickets.Where(t => t.OrderId == id).Select(t => t.Id).ToListAsync(ct);
            return Results.Ok(new OrderDetailResponse(
                order.Id, order.HoldId, order.Status.ToString(), order.Total,
                order.Items.Select(i => new OrderItemResponse(i.ZoneId, i.Qty, i.UnitPrice)).ToList(), tickets));
        });

        orders.MapGet("/mine", async ([FromHeader(Name = "X-User-Id")] string? userId, OrdersDbContext db, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(userId))
            {
                return Results.Problem(title: "falta el header X-User-Id", statusCode: StatusCodes.Status400BadRequest);
            }
            var mine = await db.Orders.AsNoTracking().Where(o => o.UserId == userId).OrderByDescending(o => o.CreatedAt)
                .Select(o => new OrderAcceptedResponse(o.Id, o.Status.ToString())).ToListAsync(ct);
            return Results.Ok(mine);
        });

        var payments = app.MapGroup("/api/payments");

        // Captcha mock (Fase 6, docs/03 §12): aritmética trivial, un solo uso,
        // TTL 5 min. Placeholder honesto de un hCaptcha: frena scripts tontos,
        // no bots de verdad. Sin esto no se puede tokenizar (ni pagar).
        payments.MapGet("/captcha", (IMemoryCache cache) =>
        {
            var a = Random.Shared.Next(1, 10);
            var b = Random.Shared.Next(1, 10);
            var id = Guid.NewGuid().ToString("N");
            cache.Set(CaptchaKey(id), a + b, TimeSpan.FromMinutes(5));
            return Results.Ok(new CaptchaChallenge(id, $"{a} + {b}", DateTimeOffset.UtcNow.AddMinutes(5)));
        });

        // Tokenización mock: el "plástico" entra una vez y sale un token opaco (docs/06 §4).
        // Convención estilo Stripe test: last4 "0002" = tarjeta con rechazo forzado.
        payments.MapPost("/token", (TokenizeRequest request, IMemoryCache cache) =>
        {
            if (request.Last4 is not { Length: 4 } || !request.Last4.All(char.IsDigit))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["last4"] = ["4 dígitos"] });
            }
            if (request.CaptchaId is null || request.CaptchaAnswer is null
                || !cache.TryGetValue(CaptchaKey(request.CaptchaId), out int expected)
                || request.CaptchaAnswer.Trim() != expected.ToString())
            {
                return Results.BadRequest(new { error = "captcha inválido o vencido (GET /api/payments/captcha)" });
            }
            cache.Remove(CaptchaKey(request.CaptchaId)); // un solo uso
            var marker = request.Last4 == "0002" ? "decline_" : string.Empty;
            return Results.Ok(new TokenizeResponse($"tok_mock_{marker}{Guid.NewGuid():N}"));
        });

        var tickets = app.MapGroup("/api/tickets");

        tickets.MapGet("/{id:guid}/qr", async (Guid id, [FromHeader(Name = "X-User-Id")] string? userId, OrdersDbContext db, QrService qr, CancellationToken ct) =>
        {
            var ticket = await db.Tickets.FirstOrDefaultAsync(t => t.Id == id, ct);
            if (ticket is null)
            {
                return Results.Problem(title: "ticket inexistente", statusCode: StatusCodes.Status404NotFound);
            }
            var order = await db.Orders.FindAsync([ticket.OrderId], ct);
            if (order is null || order.UserId != userId)
            {
                return Results.Problem(title: "el ticket pertenece a otro usuario", statusCode: StatusCodes.Status403Forbidden);
            }
            return Results.Ok(new { qrJwt = qr.Issue(ticket.Id, ticket.OrderId, ticket.EventId, ticket.ZoneId, ticket.Qty) });
        });

        // Verificación staff (docs/06 §6): firma + no-expirado + canje único atómico.
        // El nombre de tabla se resuelve del modelo (en tests vive en otro schema).
        tickets.MapPost("/verify", async (VerifyTicketRequest request, OrdersDbContext db, QrService qr, CancellationToken ct) =>
        {
            if (!qr.TryVerify(request.QrJwt, out var claims))
            {
                return Results.Ok(new VerifyTicketResponse("invalid", null, null));
            }
            var entityType = db.Model.FindEntityType(typeof(Ticket))!;
            var schema = entityType.GetSchema() ?? db.Model.GetDefaultSchema() ?? "public";
            var table = entityType.GetTableName()!;
            var redeemed = await db.Database.ExecuteSqlRawAsync(
                $"UPDATE \"{schema}\".\"{table}\" SET \"Redeemed\" = true, \"RedeemedAt\" = now() WHERE \"Id\" = {{0}} AND \"Redeemed\" = false",
                [claims.TicketId], ct);
            if (redeemed == 0)
            {
                return Results.Ok(new VerifyTicketResponse("used", claims.TicketId, claims.OrderId));
            }
            return Results.Ok(new VerifyTicketResponse("valid", claims.TicketId, claims.OrderId));
        });

        // Pings de Fase 0
        orders.MapGet("/ping", () => Results.Ok(new { service = "orders-service", status = "pong", utc = DateTime.UtcNow }));
        payments.MapGet("/ping", () => Results.Ok(new { service = "orders-service", area = "payments", status = "pong" }));
        tickets.MapGet("/ping", () => Results.Ok(new { service = "orders-service", area = "tickets", status = "pong" }));
    }

    private static string CaptchaKey(string captchaId) => $"captcha:{captchaId}";
}
