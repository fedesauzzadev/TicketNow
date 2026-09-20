using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using StackExchange.Redis;
using Testcontainers.Redis;
using TicketNow.InventoryService;

namespace TicketNow.UnitTests;

// Redis REAL: en red Docker (test-runner) se usa el de compose vía
// TICKETNOW_TEST_REDIS="host:port"; en host, Testcontainers.
public sealed class RedisFixture : IAsyncLifetime
{
    private RedisContainer? _container;

    public string ConnectionString { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var shared = Environment.GetEnvironmentVariable("TICKETNOW_TEST_REDIS");
        if (!string.IsNullOrWhiteSpace(shared))
        {
            ConnectionString = shared.Contains(':') ? shared : $"{shared}:6379";
            return;
        }
        _container = new RedisBuilder().WithImage("redis:7-alpine").Build();
        await _container.StartAsync();
        ConnectionString = _container.GetConnectionString();
    }

    public async Task DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }
}

public class InventoryTests : IClassFixture<RedisFixture>, IClassFixture<PostgresFixture>
{
    private readonly RedisFixture _redis;
    private readonly PostgresFixture _postgres;

    public InventoryTests(RedisFixture redis, PostgresFixture postgres)
    {
        _redis = redis;
        _postgres = postgres;
    }

    private async Task<(WebApplicationFactory<TicketNow.InventoryService.Marker> Factory, HttpClient Client)> CreateAsync(int holdTtlSeconds = 480)
    {
        // PG REAL (como en producción): el outbox de MassTransit usa SQL específico
        // de Postgres que InMemory no soporta. Aislamiento por GUIDs únicos por test.
        // Sufijo único de colas por test: aislamiento total del broker.
        var factory = new WebApplicationFactory<TicketNow.InventoryService.Marker>().WithWebHostBuilder(web =>
        {
            web.UseEnvironment("Testing");
            web.UseSetting("ConnectionStrings:Postgres", _postgres.ConnectionString);
            web.UseSetting("ConnectionStrings:Redis", _redis.ConnectionString);
            web.UseSetting("Messaging:EndpointSuffix", Guid.NewGuid().ToString("N")[..8]);
            web.UseSetting("RabbitMq:Host", Environment.GetEnvironmentVariable("TICKETNOW_TEST_RABBITMQ_HOST") ?? "localhost");
            web.UseSetting("RabbitMq:Port", Environment.GetEnvironmentVariable("TICKETNOW_TEST_RABBITMQ_PORT") ?? "5672");
            web.UseSetting("RabbitMq:User", Environment.GetEnvironmentVariable("TICKETNOW_TEST_RABBITMQ_USER") ?? "guest");
            web.UseSetting("RabbitMq:Pass", Environment.GetEnvironmentVariable("TICKETNOW_TEST_RABBITMQ_PASS") ?? "guest");
            web.UseSetting("Inventory:HoldTtlSeconds", holdTtlSeconds.ToString());
            web.UseSetting("Inventory:SweeperIntervalSeconds", "3600"); // se invoca manual en tests
            web.UseSetting("Inventory:ReconcilerIntervalSeconds", "3600");
            web.ConfigureTestServices(services =>
            {
                // DbContext derivado (todo en `public`, ver TestDbContexts.cs) + schema
                // creado con EnsureCreated (las migraciones no aplican a derivados).
                services.RemoveAll(typeof(DbContextOptions<InventoryDbContext>));
                services.RemoveAll(typeof(InventoryDbContext));
                services.AddDbContext<InventoryDbContext, TestInventoryDbContext>(
                    options => options.UseNpgsql(_postgres.ConnectionString));
                // Solo workers propios (jamás RemoveAll<IHostedService>: mataría el bus).
                foreach (var worker in new[] { typeof(HoldSweeper), typeof(ReconcilerWorker) })
                {
                    var doomed = services
                        .Where(s => s.ServiceType == typeof(IHostedService) && s.ImplementationType == worker)
                        .ToList();
                    foreach (var descriptor in doomed)
                    {
                        services.Remove(descriptor);
                    }
                }
            });
        });

        // Las tablas las crea MigrateAsync del Program (migraciones TestInit).
        return (factory, factory.CreateClient());
    }

    private static async Task SeedAsync(HttpClient client, Guid eventId, Guid zoneId, int capacity)
    {
        var response = await client.PostAsJsonAsync("/api/inventory/admin/seed", new
        {
            eventId,
            zones = new[] { new { zoneId, capacity } },
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task<HttpResponseMessage> PostHoldAsync(HttpClient client, Guid eventId, Guid zoneId, int qty, string userId, string? idempotencyKey = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/inventory/holds");
        request.Headers.Add("X-User-Id", userId);
        if (idempotencyKey is not null)
        {
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        }
        request.Content = JsonContent.Create(new { eventId, zoneId, qty });
        return await client.SendAsync(request);
    }

    private sealed record HoldCreatedDto(Guid HoldId, int Qty, DateTimeOffset ExpiresAt, bool Replayed);

    // INV-1: el test estrella de la Fase 2. 200 compradores simultáneos por las
    // últimas 10 unidades: exactamente 10 holds otorgados, 190 rechazados, cero oversell.
    [Fact]
    public async Task Concurrencia_200_piden_10_unidades_solo_10_holds_otorgados()
    {
        var (factory, client) = await CreateAsync();
        var eventId = Guid.NewGuid();
        var zoneId = Guid.NewGuid();
        await SeedAsync(client, eventId, zoneId, capacity: 10);

        var results = await Task.WhenAll(Enumerable.Range(0, 200).Select(i => PostHoldAsync(client, eventId, zoneId, qty: 1, userId: $"user-{i}")));

        Assert.Equal(10, results.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(190, results.Count(r => r.StatusCode == HttpStatusCode.Conflict));

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
        Assert.Equal(-10, await db.Ledger.Where(l => l.EventId == eventId).SumAsync(l => l.Delta));
        Assert.Equal(10, await db.Holds.CountAsync(h => h.EventId == eventId && h.Outcome == null));
    }

    // INV-2: liberar devuelve el stock (el mismo usuario puede volver a reservar).
    [Fact]
    public async Task Hold_liberado_devuelve_stock_y_queda_auditado()
    {
        var (factory, client) = await CreateAsync();
        var eventId = Guid.NewGuid();
        var zoneId = Guid.NewGuid();
        await SeedAsync(client, eventId, zoneId, capacity: 5);

        var created = await PostHoldAsync(client, eventId, zoneId, qty: 2, userId: "u1");
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var hold = (await created.Content.ReadFromJsonAsync<HoldCreatedDto>())!;

        using var delete = new HttpRequestMessage(HttpMethod.Delete, $"/api/inventory/holds/{hold.HoldId}");
        delete.Headers.Add("X-User-Id", "u1");
        var released = await client.SendAsync(delete);
        Assert.Equal(HttpStatusCode.NoContent, released.StatusCode);

        var again = await PostHoldAsync(client, eventId, zoneId, qty: 2, userId: "u1");
        Assert.Equal(HttpStatusCode.Created, again.StatusCode);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
        var record = await db.Holds.FindAsync(hold.HoldId);
        Assert.Equal(HoldOutcome.Released, record!.Outcome);
        Assert.Contains(await db.Ledger.Where(l => l.HoldId == hold.HoldId).ToListAsync(),
            l => l.EntryType == LedgerEntryType.Release && l.Delta == 2);
    }

    [Fact]
    public async Task Hold_sin_stock_responde_409_sin_efecto()
    {
        var (_, client) = await CreateAsync();
        var eventId = Guid.NewGuid();
        var zoneId = Guid.NewGuid();
        await SeedAsync(client, eventId, zoneId, capacity: 1);

        Assert.Equal(HttpStatusCode.Created, (await PostHoldAsync(client, eventId, zoneId, 1, "u1")).StatusCode);

        var denied = await PostHoldAsync(client, eventId, zoneId, 1, "u2");
        Assert.Equal(HttpStatusCode.Conflict, denied.StatusCode);
        Assert.Equal("application/problem+json", denied.Content.Headers.ContentType?.MediaType);
    }

    // INV-5 (sabor holds): el mismo Idempotency-Key repetido devuelve el mismo hold,
    // sin consumir stock dos veces.
    [Fact]
    public async Task Reintento_con_misma_clave_devuelve_el_mismo_hold()
    {
        var (factory, client) = await CreateAsync();
        var eventId = Guid.NewGuid();
        var zoneId = Guid.NewGuid();
        await SeedAsync(client, eventId, zoneId, capacity: 5);

        var first = await PostHoldAsync(client, eventId, zoneId, 1, "u1", idempotencyKey: "key-abc");
        var second = await PostHoldAsync(client, eventId, zoneId, 1, "u1", idempotencyKey: "key-abc");
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);

        var a = (await first.Content.ReadFromJsonAsync<HoldCreatedDto>())!;
        var b = (await second.Content.ReadFromJsonAsync<HoldCreatedDto>())!;
        Assert.Equal(a.HoldId, b.HoldId);
        Assert.True(b.Replayed);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
        Assert.Equal(1, await db.Ledger.CountAsync(l => l.HoldId == a.HoldId));

        // Solo se consumió 1 unidad: todavía hay 4 libres.
        var rest = await PostHoldAsync(client, eventId, zoneId, 4, "u2");
        Assert.Equal(HttpStatusCode.Created, rest.StatusCode);
    }

    [Fact]
    public async Task Reconciliador_repara_contador_corrupto_desde_ledger()
    {
        var (factory, client) = await CreateAsync();
        var eventId = Guid.NewGuid();
        var zoneId = Guid.NewGuid();
        await SeedAsync(client, eventId, zoneId, capacity: 10);

        await using var scope = factory.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var redis = services.GetRequiredService<ConnectionMultiplexer>().GetDatabase();

        // Corrupción simulada: alguien/algo dejó el contador en 999.
        await redis.StringSetAsync($"stock:{eventId:N}:{zoneId:N}", 999);

        var report = await services.GetRequiredService<InventoryReconciler>().ReconcileAsync(CancellationToken.None);

        Assert.False(report.Skipped);
        Assert.Equal(1, report.Repaired);
        Assert.Equal(10, (int)await redis.StringGetAsync($"stock:{eventId:N}:{zoneId:N}"));
    }

    [Fact]
    public async Task Sweeper_expira_hold_y_devuelve_stock_con_ledger()
    {
        var (factory, client) = await CreateAsync(holdTtlSeconds: 2);
        var eventId = Guid.NewGuid();
        var zoneId = Guid.NewGuid();
        await SeedAsync(client, eventId, zoneId, capacity: 5);

        var created = await PostHoldAsync(client, eventId, zoneId, 2, "u1");
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var hold = (await created.Content.ReadFromJsonAsync<HoldCreatedDto>())!;

        await Task.Delay(TimeSpan.FromSeconds(3.5));

        await using var scope = factory.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var swept = await services.GetRequiredService<HoldService>().SweepExpiredAsync(CancellationToken.None);
        Assert.Equal(1, swept);

        var db = services.GetRequiredService<InventoryDbContext>();
        var record = await db.Holds.FindAsync(hold.HoldId);
        Assert.Equal(HoldOutcome.Expired, record!.Outcome);
        Assert.Contains(await db.Ledger.Where(l => l.HoldId == hold.HoldId).ToListAsync(),
            l => l.EntryType == LedgerEntryType.Expire && l.Delta == 2);

        // Stock restaurado: se puede reservar de nuevo (4 = máximo por hold).
        var again = await PostHoldAsync(client, eventId, zoneId, 4, "u2");
        Assert.Equal(HttpStatusCode.Created, again.StatusCode);
    }
}
