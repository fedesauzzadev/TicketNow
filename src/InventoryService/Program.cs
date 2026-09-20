using MassTransit;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;
using TicketNow.InventoryService;
using TicketNow.ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);

builder.AddTicketNowDefaults("inventory-service");
builder.AddRedisHealth();
builder.AddPostgresHealth();

builder.Services.AddProblemDetails();

var connectionString = builder.Configuration.GetConnectionString("Postgres")
    ?? throw new InvalidOperationException("ConnectionStrings:Postgres no configurado");

builder.Services.AddDbContext<InventoryDbContext>(options => options
    .UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable("__efmigrations_history", "inventory")));

var redisConnectionString = builder.Configuration.GetConnectionString("Redis")
    ?? throw new InvalidOperationException("ConnectionStrings:Redis no configurado (el inventario lo exige)");
builder.Services.AddSingleton(_ => ConnectionMultiplexer.Connect(redisConnectionString));

builder.Services.AddScoped<HoldService>();
builder.Services.AddScoped<InventoryReconciler>();

// Bindeo turno→evento + revocación (ADR-012): ver OrdersService/Program.cs.
builder.Services.AddSingleton<AdmissionTokenService>();
builder.Services.AddScoped(sp => new AdmissionChecker(
    sp.GetRequiredService<AdmissionTokenService>(),
    sp.GetRequiredService<ConnectionMultiplexer>().GetDatabase()));
builder.Services.AddHostedService<HoldSweeper>();
builder.Services.AddHostedService<ReconcilerWorker>();

// Topología MassTransit (docs/02 §6): endpoints explícitos con inbox/outbox.
builder.AddTicketNowMessaging(
    bus =>
    {
        bus.AddConsumer<ValidateHoldConsumer>();
        bus.AddConsumer<ReleaseHoldConsumer>();
        bus.AddConsumer<ConfirmHoldConsumer>();
        bus.AddConsumer<OnsaleOpenedConsumer>(); // Fase 5: siembra al abrir el onsale

        MessagingExtensions.AddTicketNowOutbox<InventoryDbContext>(bus, builder.Configuration);
    },
    (context, cfg) =>
    {
        cfg.ReceiveEndpoint(MessagingExtensions.QueueName("inventory-validate", builder.Configuration), e =>
        {
            MessagingExtensions.UseTicketNowRetry(e);
            e.UseEntityFrameworkOutbox<InventoryDbContext>(context);
            e.ConfigureConsumer<ValidateHoldConsumer>(context);
        });
        cfg.ReceiveEndpoint(MessagingExtensions.QueueName("inventory-release", builder.Configuration), e =>
        {
            MessagingExtensions.UseTicketNowRetry(e);
            e.UseEntityFrameworkOutbox<InventoryDbContext>(context);
            e.ConfigureConsumer<ReleaseHoldConsumer>(context);
        });
        cfg.ReceiveEndpoint(MessagingExtensions.QueueName("inventory-confirm", builder.Configuration), e =>
        {
            MessagingExtensions.UseTicketNowRetry(e);
            e.UseEntityFrameworkOutbox<InventoryDbContext>(context);
            e.ConfigureConsumer<ConfirmHoldConsumer>(context);
        });
        cfg.ReceiveEndpoint(MessagingExtensions.QueueName("inventory-seed", builder.Configuration), e =>
        {
            MessagingExtensions.UseTicketNowRetry(e);
            e.UseEntityFrameworkOutbox<InventoryDbContext>(context);
            e.ConfigureConsumer<OnsaleOpenedConsumer>(context);
        });
    });

var app = builder.Build();

app.UseExceptionHandler();
app.MapTicketNowDefaults();
app.MapInventoryEndpoints();

// Migraciones al arranque (didáctico; en producción, job aparte — docs/05 §1)
using (var scope = app.Services.CreateScope())
{
    var database = scope.ServiceProvider.GetRequiredService<InventoryDbContext>().Database;
    if (database.IsRelational())
    {
        await database.MigrateAsync();
    }
}

app.Run();

/// <summary>Marker para WebApplicationFactory en los tests.</summary>
namespace TicketNow.InventoryService
{
    public sealed class Marker { }
}
