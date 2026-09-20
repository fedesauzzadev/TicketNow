using MassTransit;
using Microsoft.EntityFrameworkCore;
using TicketNow.CatalogService;
using TicketNow.ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);

builder.AddTicketNowDefaults("catalog-service");
builder.AddPostgresHealth();
builder.AddRedisHealth(); // desde Fase 1 el catálogo usa Redis (cache-aside)

builder.Services.AddProblemDetails();

var connectionString = builder.Configuration.GetConnectionString("Postgres")
    ?? throw new InvalidOperationException("ConnectionStrings:Postgres no configurado");

builder.Services.AddDbContext<CatalogDbContext>(options => options
    .UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable("__efmigrations_history", "catalog")));

builder.Services.AddSingleton<IEventCache, RedisEventCache>();

// Proyección de disponibilidad (docs/03 §8): consume eventos de inventory con inbox.
builder.AddTicketNowMessaging(
    bus =>
    {
        bus.AddConsumer<AvailabilityProjectionConsumer>();
        MessagingExtensions.AddTicketNowOutbox<CatalogDbContext>(bus, builder.Configuration);
    },
    (context, cfg) =>
    {
        cfg.ReceiveEndpoint(MessagingExtensions.QueueName("catalog-projection", builder.Configuration), e =>
        {
            MessagingExtensions.UseTicketNowRetry(e);
            e.UseEntityFrameworkOutbox<CatalogDbContext>(context);
            e.ConfigureConsumer<AvailabilityProjectionConsumer>(context);
        });
    });

// Apertura de onsales (Fase 5): publica OnsaleOpened cuando llega la hora.
builder.Services.AddHostedService<OnsaleOpenerWorker>();

var app = builder.Build();

app.UseExceptionHandler();
app.MapTicketNowDefaults();
app.MapCatalogEndpoints();

// Migraciones al arranque (didáctico; en producción, job aparte — docs/05 §1)
using (var scope = app.Services.CreateScope())
{
    var database = scope.ServiceProvider.GetRequiredService<CatalogDbContext>().Database;
    if (database.IsRelational())
    {
        await database.MigrateAsync();
    }
}

app.Run();

/// <summary>Marker para WebApplicationFactory en los tests (evita ambigüedad entre Program de cada servicio).</summary>
namespace TicketNow.CatalogService
{
    public sealed class Marker { }
}
