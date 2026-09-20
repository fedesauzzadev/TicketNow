using System.Net;
using System.Net.Http.Json;
using MassTransit;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using StackExchange.Redis;
using TicketNow.CatalogService;
using TicketNow.InventoryService;
using TicketNow.OrdersService;
using TicketNow.ServiceDefaults;

namespace TicketNow.UnitTests;

// Flujo completo de compra contra PG + Redis + bus REALES.
// Entorno COMPARTIDO por todos los tests de la clase (IClassFixture): los 3 hosts
// se levantan UNA vez (migraciones + buses + topología), y cada test aísla por
// GUIDs + correlación por OrderId. Levantar hosts por test multiplica el churn
// y desestabiliza la suite.
public sealed class CheckoutEnvironment : IAsyncLifetime
{
    private readonly PostgresFixture _postgres = new();
    private readonly RedisFixture _redis = new();

    public WebApplicationFactory<OrdersService.Marker> OrdersFactory { get; private set; } = null!;
    public WebApplicationFactory<InventoryService.Marker> InventoryFactory { get; private set; } = null!;
    public WebApplicationFactory<CatalogService.Marker> CatalogFactory { get; private set; } = null!;

    public HttpClient Orders { get; private set; } = null!;
    public HttpClient Inventory { get; private set; } = null!;
    public HttpClient Catalog { get; private set; } = null!;

    public IServiceProvider Services => OrdersFactory.Services;

    private string _endpointSuffix = null!;

    public async Task InitializeAsync()
    {
        await _postgres.InitializeAsync();
        await _redis.InitializeAsync();

        // Sufijo único de colas: este entorno jamás compite con otros runs/clases.
        _endpointSuffix = Guid.NewGuid().ToString("N")[..8];

        // Una DATABASE por servicio: las migraciones TestInit crean las tablas
        // compartidas (public.*) y colisionarían en una sola DB.
        var ordersDb = await _postgres.CreateDatabaseAsync("orders");
        var inventoryDb = await _postgres.CreateDatabaseAsync("inventory");
        var catalogDb = await _postgres.CreateDatabaseAsync("catalog");

        OrdersFactory = BuildWithDb<OrdersService.Marker>(ordersDb, new Dictionary<string, string?>
        {
            ["Payment:MinLatencyMs"] = "10",
            ["Payment:MaxLatencyMs"] = "50",
            ["Payment:DeclineRate"] = "0", // determinismo; el rechazo se fuerza por token
        }, services => UseTestDbContext<OrdersDbContext, TestOrdersDbContext>(services, ordersDb));
        InventoryFactory = BuildWithDb<InventoryService.Marker>(inventoryDb, new Dictionary<string, string?>
        {
            ["Inventory:HoldTtlSeconds"] = "480",
            ["Inventory:SweeperIntervalSeconds"] = "3600",
            ["Inventory:ReconcilerIntervalSeconds"] = "3600",
        }, services =>
        {
            UseTestDbContext<InventoryDbContext, TestInventoryDbContext>(services, inventoryDb);
            RemoveInventoryWorkers(services);
        });
        CatalogFactory = BuildWithDb<CatalogService.Marker>(catalogDb, new Dictionary<string, string?>
        {
            ["Catalog:OnsaleOpenerIntervalSeconds"] = "0.2", // Fase 5: abre veloz en tests
        }, services => UseTestDbContext<CatalogDbContext, TestCatalogDbContext>(services, catalogDb));

        // Las tablas las crea MigrateAsync de cada Program (migraciones TestInit del
        // proyecto de tests, todo en `public`).
        Orders = OrdersFactory.CreateClient();
        Inventory = InventoryFactory.CreateClient();
        Catalog = CatalogFactory.CreateClient();
    }

    private WebApplicationFactory<TMarker> BuildWithDb<TMarker>(
        string postgresConnection,
        Dictionary<string, string?> settings,
        Action<IServiceCollection>? configure = null)
        where TMarker : class
    {
        // Sufijo capturado por test: cada factory del entorno comparte topología.
        var suffix = _endpointSuffix;
        return new WebApplicationFactory<TMarker>().WithWebHostBuilder(web =>
        {
            web.UseEnvironment("Testing");
            web.UseSetting("ConnectionStrings:Postgres", postgresConnection);
            web.UseSetting("ConnectionStrings:Redis", _redis.ConnectionString);
            web.UseSetting("Messaging:EndpointSuffix", suffix);
            web.UseSetting("Messaging:OutboxQueryDelaySeconds", "0.2");
            web.UseSetting("RabbitMq:Host", Environment.GetEnvironmentVariable("TICKETNOW_TEST_RABBITMQ_HOST") ?? "localhost");
            web.UseSetting("RabbitMq:Port", Environment.GetEnvironmentVariable("TICKETNOW_TEST_RABBITMQ_PORT") ?? "5672");
            web.UseSetting("RabbitMq:User", Environment.GetEnvironmentVariable("TICKETNOW_TEST_RABBITMQ_USER") ?? "guest");
            web.UseSetting("RabbitMq:Pass", Environment.GetEnvironmentVariable("TICKETNOW_TEST_RABBITMQ_PASS") ?? "guest");
            foreach (var (key, value) in settings)
            {
                web.UseSetting(key, value);
            }
            if (configure is not null)
            {
                web.ConfigureTestServices(configure);
            }
        });
    }

    // Registra el DbContext DERIVADO (schema public, ver TestDbContexts.cs) en lugar
    // del de producción, y crea el schema con EnsureCreated (las migraciones del
    // proyecto no aplican a tipos derivados: no-op).
    private static void UseTestDbContext<TService, TTest>(IServiceCollection services, string connectionString)
        where TService : DbContext
        where TTest : DbContext, TService
    {
        services.RemoveAll(typeof(DbContextOptions<TService>));
        services.RemoveAll(typeof(TService));
        services.AddDbContext<TService, TTest>(options => options.UseNpgsql(connectionString));
    }

    // Apaga SOLO los workers propios (sweeper/reconciliador se invocan manual).
    // ¡No usar RemoveAll<IHostedService>!: mataría también el host de MassTransit
    // (el bus nunca arrancaría y los mensajes se acumularían sin consumer).
    private static void RemoveInventoryWorkers(IServiceCollection services)
    {
        RemoveWorker<HoldSweeper>(services);
        RemoveWorker<ReconcilerWorker>(services);
    }

    private static void RemoveWorker<TWorker>(IServiceCollection services) where TWorker : class
    {
        var doomed = services
            .Where(s => s.ServiceType == typeof(IHostedService) && s.ImplementationType == typeof(TWorker))
            .ToList();
        foreach (var descriptor in doomed)
        {
            services.Remove(descriptor);
        }
    }

    public async Task DisposeAsync()
    {
        // Disponer los hosts frena buses y dispatchers: adiós ruido de teardown.
        await OrdersFactory.DisposeAsync();
        await InventoryFactory.DisposeAsync();
        await CatalogFactory.DisposeAsync();
        await _postgres.DisposeAsync();
        await _redis.DisposeAsync();
    }
}

public class CheckoutTests : IClassFixture<CheckoutEnvironment>
{
    private readonly CheckoutEnvironment _env;

    public CheckoutTests(CheckoutEnvironment env) => _env = env;

    private static async Task SeedAsync(HttpClient inventory, Guid eventId, Guid zoneId, int capacity)
    {
        var response = await inventory.PostAsJsonAsync("/api/inventory/admin/seed", new
        {
            eventId,
            zones = new[] { new { zoneId, capacity } },
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task<Guid> HoldAsync(HttpClient inventory, Guid eventId, Guid zoneId, int qty, string user)
    {
        var (status, holdId) = await TryHoldAsync(inventory, eventId, zoneId, qty, user);
        Assert.Equal(HttpStatusCode.Created, status);
        return holdId!.Value;
    }

    private static async Task<(HttpStatusCode Status, Guid? HoldId)> TryHoldAsync(HttpClient inventory, Guid eventId, Guid zoneId, int qty, string user)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/inventory/holds");
        request.Headers.Add("X-User-Id", user);
        request.Content = JsonContent.Create(new { eventId, zoneId, qty });
        var response = await inventory.SendAsync(request);
        if (response.StatusCode != HttpStatusCode.Created)
        {
            return (response.StatusCode, null);
        }
        return (response.StatusCode, (await response.Content.ReadFromJsonAsync<HoldDto>())!.HoldId);
    }

    private static async Task<string> TokenizeAsync(HttpClient orders, string last4 = "4242")
    {
        // Captcha mock (Fase 6): se resuelve el desafío aritmético (un solo uso).
        var captcha = await orders.GetFromJsonAsync<CaptchaDto>("/api/payments/captcha");
        Assert.NotNull(captcha);
        var parts = captcha!.Question.Split('+', StringSplitOptions.TrimEntries);
        var answer = (int.Parse(parts[0]) + int.Parse(parts[1])).ToString();
        var response = await orders.PostAsJsonAsync("/api/payments/token", new
        {
            last4,
            captchaId = captcha.CaptchaId,
            captchaAnswer = answer,
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<TokenDto>())!.PaymentToken;
    }

    private sealed record CaptchaDto(string CaptchaId, string Question);

    private static async Task<OrderStatusDto> WaitForStatusAsync(HttpClient orders, Guid orderId, string user, string expected, int timeoutSeconds = 180)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        while (!timeout.IsCancellationRequested)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/orders/{orderId}");
            request.Headers.Add("X-User-Id", user);
            HttpResponseMessage response;
            try
            {
                response = await orders.SendAsync(request, timeout.Token);
            }
            catch (HttpRequestException)
            {
                await Task.Delay(500, timeout.Token);
                continue;
            }
            if (!response.IsSuccessStatusCode)
            {
                // 404 al principio (la saga aún no creó la fila) o 5xx transitorio: seguir esperando.
                await Task.Delay(500, timeout.Token);
                continue;
            }
            var detail = await response.Content.ReadFromJsonAsync<OrderDetailDto>(cancellationToken: timeout.Token);
            if (detail!.Status == expected)
            {
                return new OrderStatusDto(detail.Status, detail.Tickets);
            }
            await Task.Delay(500, timeout.Token);
        }
        throw new TimeoutException($"la orden {orderId} no llegó a {expected} en {timeoutSeconds}s");
    }

    private sealed record HoldDto(Guid HoldId);
    private sealed record TokenDto(string PaymentToken);
    private sealed record OrderAcceptedDto(Guid OrderId, string Status);
    private sealed record OrderDetailDto(Guid OrderId, string Status, IReadOnlyList<Guid> Tickets);
    private sealed record OrderStatusDto(string Status, IReadOnlyList<Guid> Tickets);
    private sealed record QrDto(string QrJwt);
    private sealed record VerifyDto(string Status);
    private sealed record ZoneAvailabilityDto(Guid Id, string Availability);
    private sealed record VenueDto(Guid Id);
    private sealed record CreatedDto(Guid Id);

    // El flujo feliz completo: admin crea evento → seed → hold → checkout → pago →
    // confirm → ticket → QR válido → canje → proyección "soldout".
    [Fact]
    public async Task Compra_e2e_ok_pago_confirmacion_ticket_y_canje()
    {
        var orders = _env.Orders;
        var inventory = _env.Inventory;
        var catalog = _env.Catalog;
        const string user = "e2e-comprador";

        // El evento nace en el catálogo (admin API real).
        var venue = await catalog.PostAsJsonAsync("/api/admin/venues", new { name = "Arena E2E", city = "Test", capacityTotal = 100 });
        var venueId = (await venue.Content.ReadFromJsonAsync<VenueDto>())!.Id;
        var created = await catalog.PostAsJsonAsync("/api/admin/events", new
        {
            title = "Recital E2E",
            artist = "Banda E2E",
            venueId,
            startsAt = DateTimeOffset.UtcNow.AddMonths(2),
            zones = new[] { new { name = "Campo", capacity = 2, price = 85m } },
        });
        var eventId = (await created.Content.ReadFromJsonAsync<CreatedDto>())!.Id;
        // El evento nace en draft (zonas públicas 404): se publica vía onsale.
        var onsale = await catalog.PostAsJsonAsync($"/api/admin/events/{eventId}/onsale", new { opensAt = DateTimeOffset.UtcNow.AddMinutes(-5) });
        Assert.Equal(HttpStatusCode.OK, onsale.StatusCode);
        var zones = await catalog.GetFromJsonAsync<IReadOnlyList<ZoneAvailabilityDto>>($"/api/events/{eventId}/zones");
        var zoneId = zones!.Single().Id;

        await SeedAsync(inventory, eventId, zoneId, capacity: 2);

        var holdId = await HoldAsync(inventory, eventId, zoneId, qty: 2, user);
        var paymentToken = await TokenizeAsync(orders);

        using var checkout = new HttpRequestMessage(HttpMethod.Post, "/api/orders");
        checkout.Headers.Add("X-User-Id", user);
        checkout.Headers.Add("Idempotency-Key", $"e2e-{Guid.NewGuid():N}");
        checkout.Content = JsonContent.Create(new
        {
            holdId,
            eventId,
            maxPerAccount = 4,
            items = new[] { new { zoneId, qty = 2, unitPrice = 85m } },
            paymentToken,
        });
        var accepted = await orders.SendAsync(checkout);
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        var orderId = (await accepted.Content.ReadFromJsonAsync<OrderAcceptedDto>())!.OrderId;

        var final = await WaitForStatusAsync(orders, orderId, user, "Confirmed");
        Assert.Single(final.Tickets);

        // QR: válido la primera vez, usado la segunda (anti-replay, docs/06 §6).
        using var qrRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/tickets/{final.Tickets[0]}/qr");
        qrRequest.Headers.Add("X-User-Id", user);
        var qrResponse = await orders.SendAsync(qrRequest);
        Assert.Equal(HttpStatusCode.OK, qrResponse.StatusCode);
        var qrJwt = (await qrResponse.Content.ReadFromJsonAsync<QrDto>())!.QrJwt;

        var valid = await orders.PostAsJsonAsync("/api/tickets/verify", new { qrJwt });
        Assert.Equal("valid", (await valid.Content.ReadFromJsonAsync<VerifyDto>())!.Status);

        var used = await orders.PostAsJsonAsync("/api/tickets/verify", new { qrJwt });
        Assert.Equal("used", (await used.Content.ReadFromJsonAsync<VerifyDto>())!.Status);

        // INV-3: hay pago autorizado y ticket para la orden confirmada.
        await using var scope = _env.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrdersDbContext>();
        Assert.NotNull(await db.Payments.FirstOrDefaultAsync(p => p.OrderId == orderId && p.Status == PaymentStatus.Authorized));
        Assert.NotNull(await db.Tickets.FirstOrDefaultAsync(t => t.OrderId == orderId));

        // Proyección CQRS-lite (§8): se vendió todo → la zona queda "soldout" en el catálogo.
        using var zoneTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        while (!zoneTimeout.IsCancellationRequested)
        {
            var current = await catalog.GetFromJsonAsync<IReadOnlyList<ZoneAvailabilityDto>>(
                $"/api/events/{eventId}/zones", zoneTimeout.Token);
            if (current!.Any(z => z.Id == zoneId && z.Availability == "soldout"))
            {
                break;
            }
            await Task.Delay(1000, zoneTimeout.Token);
        }
        var final_zones = await catalog.GetFromJsonAsync<IReadOnlyList<ZoneAvailabilityDto>>($"/api/events/{eventId}/zones");
        Assert.Contains(final_zones!, z => z.Id == zoneId && z.Availability == "soldout");
    }

    // Pago rechazado → compensación: stock devuelto + orden rechazada (saga §4).
    // El rechazo se fuerza con tarjeta de prueba (last4 0002), sin overrides.
    [Fact]
    public async Task Pago_rechazado_compensa_stock_y_rechaza_orden()
    {
        var orders = _env.Orders;
        var inventory = _env.Inventory;
        const string user = "e2e-rechazado";
        var eventId = Guid.NewGuid();
        var zoneId = Guid.NewGuid();
        await SeedAsync(inventory, eventId, zoneId, capacity: 2);

        var holdId = await HoldAsync(inventory, eventId, zoneId, qty: 2, user);
        var paymentToken = await TokenizeAsync(orders, last4: "0002");

        using var checkout = new HttpRequestMessage(HttpMethod.Post, "/api/orders");
        checkout.Headers.Add("X-User-Id", user);
        checkout.Headers.Add("Idempotency-Key", $"e2e-{Guid.NewGuid():N}");
        checkout.Content = JsonContent.Create(new
        {
            holdId,
            eventId,
            maxPerAccount = 4,
            items = new[] { new { zoneId, qty = 2, unitPrice = 85m } },
            paymentToken,
        });
        var accepted = await orders.SendAsync(checkout);
        var orderId = (await accepted.Content.ReadFromJsonAsync<OrderAcceptedDto>())!.OrderId;

        await WaitForStatusAsync(orders, orderId, user, "Rejected");

        // El stock vuelve EVENTUALMENTE (el ReleaseHold viaja async): reintentar.
        Guid? restored = null;
        using var stockTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        while (!stockTimeout.IsCancellationRequested)
        {
            var (status, freshHoldId) = await TryHoldAsync(inventory, eventId, zoneId, qty: 2, user: "otro");
            if (status == HttpStatusCode.Created)
            {
                restored = freshHoldId;
                break;
            }
            await Task.Delay(500, stockTimeout.Token);
        }
        Assert.NotNull(restored);
    }

    // Hold vencido → el checkout falla veloz (409) sin crear orden.
    // Usa expiración forzada (mismo camino que el sweeper) sobre el entorno compartido.
    [Fact]
    public async Task Hold_expirado_rechaza_checkout_sin_crear_orden()
    {
        var orders = _env.Orders;
        var inventory = _env.Inventory;
        const string user = "e2e-expirado";
        var eventId = Guid.NewGuid();
        var zoneId = Guid.NewGuid();
        await SeedAsync(inventory, eventId, zoneId, capacity: 2);

        var holdId = await HoldAsync(inventory, eventId, zoneId, qty: 1, user);

        // Expiración forzada por endpoint admin (mismo camino que el sweeper).
        var expired = await inventory.PostAsync($"/api/inventory/admin/holds/{holdId}/expire", null);
        Assert.Equal(HttpStatusCode.NoContent, expired.StatusCode);

        using var checkout = new HttpRequestMessage(HttpMethod.Post, "/api/orders");
        checkout.Headers.Add("X-User-Id", user);
        checkout.Headers.Add("Idempotency-Key", $"e2e-{Guid.NewGuid():N}");
        checkout.Content = JsonContent.Create(new
        {
            holdId,
            eventId,
            maxPerAccount = 4,
            items = new[] { new { zoneId, qty = 1, unitPrice = 85m } },
            paymentToken = await TokenizeAsync(orders),
        });
        var response = await orders.SendAsync(checkout);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        await using var scope = _env.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrdersDbContext>();
        Assert.Equal(0, await db.Orders.CountAsync(o => o.HoldId == holdId));
    }

    // INV-5: doble POST con la misma clave crea una sola orden.
    [Fact]
    public async Task Doble_post_misma_clave_crea_una_sola_orden()    {
        var orders = _env.Orders;
        var inventory = _env.Inventory;
        const string user = "e2e-idempotente";
        var eventId = Guid.NewGuid();
        var zoneId = Guid.NewGuid();
        await SeedAsync(inventory, eventId, zoneId, capacity: 4);

        var holdId = await HoldAsync(inventory, eventId, zoneId, qty: 2, user);
        var paymentToken = await TokenizeAsync(orders);
        var key = $"e2e-{Guid.NewGuid():N}";

        async Task<Guid> PostOnce()
        {
            using var checkout = new HttpRequestMessage(HttpMethod.Post, "/api/orders");
            checkout.Headers.Add("X-User-Id", user);
            checkout.Headers.Add("Idempotency-Key", key);
            checkout.Content = JsonContent.Create(new
            {
                holdId,
                eventId,
                maxPerAccount = 4,
                items = new[] { new { zoneId, qty = 2, unitPrice = 85m } },
                paymentToken,
            });
            var response = await orders.SendAsync(checkout);
            var body = await response.Content.ReadAsStringAsync();
            Assert.True(response.StatusCode is HttpStatusCode.Accepted or HttpStatusCode.OK, $"status: {response.StatusCode} body: {body}");
            return (await response.Content.ReadFromJsonAsync<OrderAcceptedDto>())!.OrderId;
        }

        var first = await PostOnce();
        var second = await PostOnce();
        Assert.Equal(first, second);

        await WaitForStatusAsync(orders, first, user, "Confirmed");

        await using var scope = _env.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrdersDbContext>();
        Assert.Equal(1, await db.Orders.CountAsync(o => o.IdempotencyKey == key));
        Assert.Equal(1, await db.Tickets.CountAsync(t => t.OrderId == first));
    }

    // Fase 5 (FR-52): llega la hora del onsale → el opener del catálogo publica
    // OnsaleOpened → inventory siembra el stock SOLO. Sin admin/seed manual.
    [Fact]
    public async Task Onsale_que_llega_su_hora_se_abre_y_siembra_stock_solo()
    {
        var catalog = _env.Catalog;
        var inventory = _env.Inventory;

        var venue = await catalog.PostAsJsonAsync("/api/admin/venues", new
        {
            name = $"Estadio F5 {Guid.NewGuid():N}",
            city = "Rosario",
            capacityTotal = 1000,
        });
        Assert.Equal(HttpStatusCode.Created, venue.StatusCode);
        var venueId = (await venue.Content.ReadFromJsonAsync<VenueCreatedDto>())!.Id;

        var zoneName = "General";
        var created = await catalog.PostAsJsonAsync("/api/admin/events", new
        {
            title = "Fase 5 Fest",
            artist = "La Banda",
            venueId,
            startsAt = DateTimeOffset.UtcNow.AddMonths(2),
            zones = new[] { new { name = zoneName, capacity = 3, price = 10m } },
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var eventId = (await created.Content.ReadFromJsonAsync<VenueCreatedDto>())!.Id;

        // Onsale cuya hora YA pasó (abre hace un minuto): el opener (0.2 s) debe
        // marcarlo y publicar OnsaleOpened con las zonas.
        var onsale = await catalog.PostAsJsonAsync($"/api/admin/events/{eventId}/onsale", new
        {
            opensAt = DateTimeOffset.UtcNow.AddMinutes(-1),
        });
        Assert.Equal(HttpStatusCode.OK, onsale.StatusCode);

        // El cliente lo ve "onsale" (estado computado en lectura) y el zoneId
        // lo asigna el catálogo al crear el evento.
        EventStatusDto? detail;
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
        {
            do
            {
                detail = await catalog.GetFromJsonAsync<EventStatusDto>($"/api/events/{eventId}", timeout.Token);
                await Task.Delay(100, timeout.Token);
            }
            while (detail is null && !timeout.IsCancellationRequested);
        }
        Assert.NotNull(detail);
        Assert.Equal("onsale", detail!.Status);
        var zoneId = detail.Zones.Single(z => z.Name == zoneName).Id;

        // El stock aparece en inventory SIN seed manual: un hold funciona.
        HttpStatusCode status = HttpStatusCode.BadRequest;
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
        {
            while (!timeout.IsCancellationRequested)
            {
                (status, _) = await TryHoldAsync(inventory, eventId, zoneId, qty: 1, "fase5-fan");
                if (status == HttpStatusCode.Created)
                {
                    break;
                }
                await Task.Delay(200, timeout.Token);
            }
        }
        Assert.Equal(HttpStatusCode.Created, status);
    }

    // Fase 6: límite por cuenta — camino veloz (API 409) y camino autoritativo
    // (el consumer rechaza aunque se saltee la API, vía bus directo).
    [Fact]
    public async Task Limite_por_cuenta_rechaza_en_api_y_en_saga()
    {
        var orders = _env.Orders;
        var inventory = _env.Inventory;
        const string user = "e2e-limitado";
        var eventId = Guid.NewGuid();
        var zoneId = Guid.NewGuid();
        await SeedAsync(inventory, eventId, zoneId, capacity: 6);

        // Primera compra (2 de 2): confirma.
        var hold1 = await HoldAsync(inventory, eventId, zoneId, qty: 2, user);
        var order1 = await PostOrderAsync(orders, hold1, eventId, zoneId, 2, 2, user);
        await WaitForStatusAsync(orders, order1, user, "Confirmed");

        // Segunda compra por API: 409 veloz (ya tiene 2, pide 1, máximo 2).
        var hold2 = await HoldAsync(inventory, eventId, zoneId, qty: 1, user);
        using var second = new HttpRequestMessage(HttpMethod.Post, "/api/orders");
        second.Headers.Add("X-User-Id", user);
        second.Headers.Add("Idempotency-Key", $"e2e-{Guid.NewGuid():N}");
        second.Content = JsonContent.Create(new
        {
            holdId = hold2,
            eventId,
            maxPerAccount = 2,
            items = new[] { new { zoneId, qty = 1, unitPrice = 85m } },
            paymentToken = await TokenizeAsync(orders),
        });
        Assert.Equal(HttpStatusCode.Conflict, (await orders.SendAsync(second)).StatusCode);

        // Camino autoritativo: SubmitOrder directo al bus salteando la API
        // (Send usa la convención al endpoint orders-submit del entorno).
        var hold3 = await HoldAsync(inventory, eventId, zoneId, qty: 1, user);
        var bus = _env.OrdersFactory.Services.GetRequiredService<MassTransit.IBus>();
        var rejectedId = Guid.NewGuid();
        await bus.Send(new TicketNow.Contracts.Commands.SubmitOrder(
            rejectedId, hold3, user, eventId, 2,
            new[] { new TicketNow.Contracts.Commands.OrderItemDto(zoneId, 1, 85m) },
            await TokenizeAsync(orders), $"e2e-{Guid.NewGuid():N}"));
        await WaitForStatusAsync(orders, rejectedId, user, "Rejected");
    }

    // Fase 6: captcha mock — sin captcha o con respuesta mala no hay token;
    // el captcha es de un solo uso.
    [Fact]
    public async Task Captcha_exigido_y_de_un_solo_uso_para_tokenizar()
    {
        var orders = _env.Orders;

        var naked = await orders.PostAsJsonAsync("/api/payments/token", new { last4 = "4242" });
        Assert.Equal(HttpStatusCode.BadRequest, naked.StatusCode);

        var captcha = await orders.GetFromJsonAsync<CaptchaDto>("/api/payments/captcha");
        Assert.NotNull(captcha);
        var parts = captcha!.Question.Split('+', StringSplitOptions.TrimEntries);
        var answer = (int.Parse(parts[0]) + int.Parse(parts[1])).ToString();

        var wrong = await orders.PostAsJsonAsync("/api/payments/token", new
        {
            last4 = "4242",
            captchaId = captcha.CaptchaId,
            captchaAnswer = "no-es-numero",
        });
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);

        var ok = await orders.PostAsJsonAsync("/api/payments/token", new
        {
            last4 = "4242",
            captchaId = captcha.CaptchaId,
            captchaAnswer = answer,
        });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);

        var replay = await orders.PostAsJsonAsync("/api/payments/token", new
        {
            last4 = "4242",
            captchaId = captcha.CaptchaId,
            captchaAnswer = answer,
        });
        Assert.Equal(HttpStatusCode.BadRequest, replay.StatusCode);
    }

    private static async Task<Guid> PostOrderAsync(
        HttpClient orders, Guid holdId, Guid eventId, Guid zoneId, int qty, int maxPerAccount, string user)
    {
        using var checkout = new HttpRequestMessage(HttpMethod.Post, "/api/orders");
        checkout.Headers.Add("X-User-Id", user);
        checkout.Headers.Add("Idempotency-Key", $"e2e-{Guid.NewGuid():N}");
        checkout.Content = JsonContent.Create(new
        {
            holdId,
            eventId,
            maxPerAccount,
            items = new[] { new { zoneId, qty, unitPrice = 85m } },
            paymentToken = await TokenizeAsync(orders),
        });
        var response = await orders.SendAsync(checkout);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<OrderAcceptedDto>())!.OrderId;
    }

    private sealed record VenueCreatedDto(Guid Id);
    private sealed record EventStatusDto(Guid Id, string Status, IReadOnlyList<ZoneRefDto> Zones);
    private sealed record ZoneRefDto(Guid Id, string Name);

    // ADR-012: el turno está atado a UN evento. Token del evento A no compra B.
    private static string IssueAdmission(WebApplicationFactory<OrdersService.Marker> factory, string session, Guid onsaleEvent)
    {
        using var scope = factory.Services.CreateScope();
        var tokens = scope.ServiceProvider.GetRequiredService<AdmissionTokenService>();
        return tokens.Issue(session, onsaleEvent.ToString(), onsaleEvent.ToString(), TimeSpan.FromMinutes(5));
    }

    private static void WithAdmission(HttpRequestMessage request, string token) =>
        request.Headers.Add("X-Admission-Token", token);

    [Fact]
    public async Task Turno_de_otro_evento_no_compra_ni_reserva()
    {
        var orders = _env.Orders;
        var inventory = _env.Inventory;
        const string user = "e2e-otro-evento";
        var eventA = Guid.NewGuid();
        var eventB = Guid.NewGuid();
        var zoneB = Guid.NewGuid();
        await SeedAsync(inventory, eventB, zoneB, capacity: 4);

        var tokenA = IssueAdmission(_env.OrdersFactory, $"s-{Guid.NewGuid():N}", eventA);

        // Hold en B con turno de A → 403.
        using var holdReq = new HttpRequestMessage(HttpMethod.Post, "/api/inventory/holds");
        holdReq.Headers.Add("X-User-Id", user);
        WithAdmission(holdReq, tokenA);
        holdReq.Content = JsonContent.Create(new { eventId = eventB, zoneId = zoneB, qty = 1 });
        var holdRes = await inventory.SendAsync(holdReq);
        Assert.Equal(HttpStatusCode.Forbidden, holdRes.StatusCode);
        Assert.Contains("admission_for_other_event", await holdRes.Content.ReadAsStringAsync());

        // Orden en B con turno de A → 403 (antes de tocar saga/stock).
        var holdB = await HoldAsync(inventory, eventB, zoneB, qty: 1, user);
        using var orderReq = new HttpRequestMessage(HttpMethod.Post, "/api/orders");
        orderReq.Headers.Add("X-User-Id", user);
        orderReq.Headers.Add("Idempotency-Key", $"e2e-{Guid.NewGuid():N}");
        WithAdmission(orderReq, tokenA);
        orderReq.Content = JsonContent.Create(new
        {
            holdId = holdB,
            eventId = eventB,
            maxPerAccount = 4,
            items = new[] { new { zoneId = zoneB, qty = 1, unitPrice = 85m } },
            paymentToken = await TokenizeAsync(orders),
        });
        var orderRes = await orders.SendAsync(orderReq);
        Assert.Equal(HttpStatusCode.Forbidden, orderRes.StatusCode);
        Assert.Contains("admission_for_other_event", await orderRes.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Turno_del_mismo_evento_si_compra()
    {
        var orders = _env.Orders;
        var inventory = _env.Inventory;
        const string user = "e2e-mismo-evento";
        var eventB = Guid.NewGuid();
        var zoneB = Guid.NewGuid();
        await SeedAsync(inventory, eventB, zoneB, capacity: 4);

        var tokenB = IssueAdmission(_env.OrdersFactory, $"s-{Guid.NewGuid():N}", eventB);

        using var holdReq = new HttpRequestMessage(HttpMethod.Post, "/api/inventory/holds");
        holdReq.Headers.Add("X-User-Id", user);
        WithAdmission(holdReq, tokenB);
        holdReq.Content = JsonContent.Create(new { eventId = eventB, zoneId = zoneB, qty = 1 });
        var holdRes = await inventory.SendAsync(holdReq);
        Assert.Equal(HttpStatusCode.Created, holdRes.StatusCode);
    }

    [Fact]
    public async Task Salir_revoca_el_turno_aunque_el_token_siga_vigente()
    {
        var orders = _env.Orders;
        var inventory = _env.Inventory;
        const string user = "e2e-revocado";
        var eventB = Guid.NewGuid();
        var zoneB = Guid.NewGuid();
        await SeedAsync(inventory, eventB, zoneB, capacity: 4);

        var session = $"s-{Guid.NewGuid():N}";
        var tokenB = IssueAdmission(_env.OrdersFactory, session, eventB);

        // Simula LeaveAsync del queue-service: marca la sesión como salida.
        using (var scope = _env.OrdersFactory.Services.CreateScope())
        {
            var mux = scope.ServiceProvider.GetRequiredService<ConnectionMultiplexer>();
            await mux.GetDatabase().StringSetAsync(
                AdmissionChecker.RevokedKey(session), "1", AdmissionChecker.RevokedTtl);
        }

        using var orderReq = new HttpRequestMessage(HttpMethod.Post, "/api/orders");
        orderReq.Headers.Add("X-User-Id", user);
        orderReq.Headers.Add("Idempotency-Key", $"e2e-{Guid.NewGuid():N}");
        WithAdmission(orderReq, tokenB);
        var holdB = await HoldAsync(inventory, eventB, zoneB, qty: 1, user);
        orderReq.Content = JsonContent.Create(new
        {
            holdId = holdB,
            eventId = eventB,
            maxPerAccount = 4,
            items = new[] { new { zoneId = zoneB, qty = 1, unitPrice = 85m } },
            paymentToken = await TokenizeAsync(orders),
        });
        var orderRes = await orders.SendAsync(orderReq);
        Assert.Equal(HttpStatusCode.Forbidden, orderRes.StatusCode);
        Assert.Contains("admission_revoked", await orderRes.Content.ReadAsStringAsync());
    }
}
