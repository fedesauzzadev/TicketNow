using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using TicketNow.CatalogService;
using TicketNow.Contracts.Events;

namespace TicketNow.UnitTests;

public class SmokeTests
{
    [Fact]
    public async Task Queue_service_responde_liveness_y_ping()
    {
        await using var factory = new WebApplicationFactory<TicketNow.QueueService.Marker>()
            .WithWebHostBuilder(web => web.UseSetting("Messaging:Transport", "inmemory")); // Fase 5: bus opcional en smoke
        var client = factory.CreateClient();

        var health = await client.GetAsync("/health/live");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);

        var ping = await client.GetAsync("/api/queue/ping");
        Assert.Equal(HttpStatusCode.OK, ping.StatusCode);
    }

    [Fact]
    public void Eventos_de_dominio_son_records_inmutables_comparables()
    {
        var expiresAt = DateTimeOffset.Parse("2026-09-18T18:11:05Z");

        var a = new HoldGranted(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 2, expiresAt, "user-1");
        var b = a with { };

        Assert.Equal(a, b);
        Assert.NotEqual(a, b with { Qty = 4 });
    }
}

public class CatalogApiTests
{
    private static async Task<(WebApplicationFactory<TicketNow.CatalogService.Marker> Factory, HttpClient Client)> CreateAsync()
    {
        var databaseName = $"catalog-tests-{Guid.NewGuid():N}";
        var factory = new WebApplicationFactory<TicketNow.CatalogService.Marker>().WithWebHostBuilder(web =>
        {
            web.UseEnvironment("Testing");
            web.UseSetting("Messaging:EndpointSuffix", Guid.NewGuid().ToString("N")[..8]);
            web.UseSetting("RabbitMq:Host", Environment.GetEnvironmentVariable("TICKETNOW_TEST_RABBITMQ_HOST") ?? "localhost");
            web.UseSetting("RabbitMq:Port", Environment.GetEnvironmentVariable("TICKETNOW_TEST_RABBITMQ_PORT") ?? "5672");
            web.UseSetting("RabbitMq:User", Environment.GetEnvironmentVariable("TICKETNOW_TEST_RABBITMQ_USER") ?? "guest");
            web.UseSetting("RabbitMq:Pass", Environment.GetEnvironmentVariable("TICKETNOW_TEST_RABBITMQ_PASS") ?? "guest");
            web.ConfigureTestServices(services =>
            {
                services.RemoveAll(typeof(DbContextOptions<CatalogDbContext>));
                services.RemoveAll<CatalogDbContext>();
                services.AddDbContext<CatalogDbContext, TestCatalogDbContext>(
                    options => options.UseInMemoryDatabase(databaseName));

                services.RemoveAll<IEventCache>();
                services.RemoveAll<RedisEventCache>();
                services.AddSingleton<IEventCache, NullEventCache>();

                // El opener de onsales no participa de los tests de API del catálogo
                // (Fase 5): sin él no se publican eventos al broker desde acá.
                var opener = services.First(s => s.ServiceType == typeof(IHostedService) && s.ImplementationType == typeof(OnsaleOpenerWorker));
                services.Remove(opener);
            });
        });

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
            await db.Database.EnsureCreatedAsync(); // aplica el seed (HasData)
        }

        return (factory, factory.CreateClient());
    }

    [Fact]
    public async Task Listado_publico_muestra_solo_eventos_anunciados()
    {
        var (_, client) = await CreateAsync();

        var page = await client.GetFromJsonAsync<PagedResult<EventListItem>>("/api/events");

        Assert.NotNull(page);
        Assert.Equal(2, page.Total); // Metallica + Iron Maiden (Íntimos es draft)
        Assert.All(page.Items, item => Assert.NotEqual("draft", item.Status));
        Assert.Contains(page.Items, i => i.Title == "Metallica" && i.Status == "onsale"); // onsale abierto desde 2026-09-01
        Assert.Contains(page.Items, i => i.Title == "Iron Maiden" && i.Status == "announced"); // countdown hasta 2026-12-01
    }

    [Fact]
    public async Task Detalle_incluye_zonas_y_config_de_onsale()
    {
        var (_, client) = await CreateAsync();
        var neon = Seed.EventNeon;

        var detail = await client.GetFromJsonAsync<EventDetail>($"/api/events/{neon}");

        Assert.NotNull(detail);
        Assert.Equal("Metallica", detail.Artist);
        Assert.Equal(4, detail.Zones.Count);
        Assert.NotNull(detail.Onsale);
        Assert.True(detail.Onsale.RequiresQueue);
        Assert.Equal(4, detail.Onsale.MaxPerAccount);
    }

    [Fact]
    public async Task Detalle_inexistente_responde_404_problem_json()
    {
        var (_, client) = await CreateAsync();

        var response = await client.GetAsync($"/api/events/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Admin_crea_evento_con_zonas_y_onsale_y_aparece_publicado()
    {
        var (_, client) = await CreateAsync();

        var create = await client.PostAsJsonAsync("/api/admin/events", new
        {
            title = "Festival Cerrado",
            artist = "Banda Test",
            venueId = Seed.VenueArena,
            startsAt = DateTimeOffset.UtcNow.AddMonths(4),
            zones = new[] { new { name = "General", capacity = 5000, price = 70m } },
        });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var created = await create.Content.ReadFromJsonAsync<CreatedEvent>();
        Assert.NotNull(created);

        var onsale = await client.PostAsJsonAsync($"/api/admin/events/{created!.Id}/onsale", new
        {
            opensAt = DateTimeOffset.UtcNow.AddMinutes(10),
        });
        Assert.Equal(HttpStatusCode.OK, onsale.StatusCode);

        var page = await client.GetFromJsonAsync<PagedResult<EventListItem>>("/api/events");
        Assert.Contains(page!.Items, i => i.Id == created.Id && i.Status == "announced"); // countdown activo
    }

    [Fact]
    public async Task Crear_evento_sin_zonas_da_400_con_detalles()
    {
        var (_, client) = await CreateAsync();

        var response = await client.PostAsJsonAsync("/api/admin/events", new
        {
            title = "Sin zonas",
            artist = "X",
            venueId = Seed.VenueArena,
            startsAt = DateTimeOffset.UtcNow.AddMonths(1),
            zones = Array.Empty<object>(),
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ValidationError>();
        Assert.True(problem!.Errors.ContainsKey("zones"));
    }

    // --- DTOs de deserialización en tests (independientes de los del servicio) ---

    private sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int Total);

    private sealed record EventListItem(Guid Id, string Title, string Artist, string Status, int ZonesCount, DateTimeOffset? OnsaleOpensAt);

    private sealed record EventDetail(string Title, string Artist, IReadOnlyList<ZoneDto> Zones, OnsaleInfo? Onsale);

    private sealed record ZoneDto(Guid Id, string Name, int Capacity, decimal Price, string Availability);

    private sealed record OnsaleInfo(DateTimeOffset OpensAt, int MaxPerAccount, bool RequiresQueue);

    private sealed record CreatedEvent(Guid Id);

    private sealed record ValidationError(Dictionary<string, string[]> Errors);
}
