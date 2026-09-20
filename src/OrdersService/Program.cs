using MassTransit;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;
using TicketNow.Contracts.Commands;
using TicketNow.OrdersService;
using TicketNow.ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);

builder.AddTicketNowDefaults("orders-service");
builder.AddPostgresHealth();
builder.AddRabbitMqHealth();

builder.Services.AddProblemDetails();
builder.Services.AddMemoryCache(); // captcha mock de un solo uso (Fase 6)

var connectionString = builder.Configuration.GetConnectionString("Postgres")
    ?? throw new InvalidOperationException("ConnectionStrings:Postgres no configurado");

builder.Services.AddDbContext<OrdersDbContext>(options => options
    .UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable("__efmigrations_history", "orders")));

builder.Services.AddSingleton<QrService>();

// Bindeo turno→evento + revocación (ADR-012): el gateway valida firma, pero
// solo acá se sabe QUÉ evento se compra.
builder.Services.AddSingleton<AdmissionTokenService>();
builder.Services.AddScoped(sp => new AdmissionChecker(
    sp.GetRequiredService<AdmissionTokenService>(),
    sp.GetRequiredService<ConnectionMultiplexer>().GetDatabase()));

var redisConnectionString = builder.Configuration.GetConnectionString("Redis")
    ?? throw new InvalidOperationException("ConnectionStrings:Redis no configurado (lock de idempotencia)");
builder.Services.AddSingleton(_ => ConnectionMultiplexer.Connect(redisConnectionString));

// Topología MassTransit (docs/02 §6): endpoints explícitos con inbox/outbox (docs/03 §6-§7).
builder.AddTicketNowMessaging(
    bus =>
    {
        bus.AddSagaStateMachine<OrderStateMachine, OrderState>()
            .EntityFrameworkRepository(r =>
            {
                r.ExistingDbContext<OrdersDbContext>();
                r.UsePostgres();
            });

        bus.AddConsumer<SubmitOrderConsumer>();
        bus.AddConsumer<PaymentConsumer>();
        bus.AddConsumer<PaymentRecorder>();
        bus.AddConsumer<OrderFinalizer>();

        bus.AddRequestClient<ValidateHold>(MessagingExtensions.QueueUri("inventory-validate", builder.Configuration));

        MessagingExtensions.AddTicketNowOutbox<OrdersDbContext>(bus, builder.Configuration);
    },
    (context, cfg) =>
    {
        cfg.ReceiveEndpoint(MessagingExtensions.QueueName("orders-submit", builder.Configuration), e =>
        {
            MessagingExtensions.UseTicketNowRetry(e);
            e.UseEntityFrameworkOutbox<OrdersDbContext>(context);
            e.ConfigureConsumer<SubmitOrderConsumer>(context);
        });
        cfg.ReceiveEndpoint(MessagingExtensions.QueueName("orders-payments", builder.Configuration), e =>
        {
            MessagingExtensions.UseTicketNowRetry(e);
            e.UseEntityFrameworkOutbox<OrdersDbContext>(context);
            e.ConfigureConsumer<PaymentConsumer>(context);
        });
        cfg.ReceiveEndpoint(MessagingExtensions.QueueName("orders-recorder", builder.Configuration), e =>
        {
            MessagingExtensions.UseTicketNowRetry(e);
            e.UseEntityFrameworkOutbox<OrdersDbContext>(context);
            e.ConfigureConsumer<PaymentRecorder>(context);
        });
        cfg.ReceiveEndpoint(MessagingExtensions.QueueName("orders-finalizer", builder.Configuration), e =>
        {
            MessagingExtensions.UseTicketNowRetry(e);
            e.UseEntityFrameworkOutbox<OrdersDbContext>(context);
            e.ConfigureConsumer<OrderFinalizer>(context);
        });
        cfg.ReceiveEndpoint(MessagingExtensions.QueueName("orders-saga", builder.Configuration), e =>
        {
            // Serializado a propósito: sin token de concurrencia en PG, procesar
            // de a un mensaje evita lost-updates entre eventos de la misma saga
            // (el throughput del control plane lo tolera de sobra).
            e.ConcurrentMessageLimit = 1;
            MessagingExtensions.UseTicketNowRetry(e);
            e.UseEntityFrameworkOutbox<OrdersDbContext>(context);
            e.ConfigureSaga<OrderState>(context);
        });
    });

var app = builder.Build();

// Convención de destino para los Send directos desde la API (los consumers y la
// saga usan Publish o URIs explícitas). Sin esto, Send<T> no sabe a qué cola ir.
EndpointConvention.Map<SubmitOrder>(MessagingExtensions.QueueUri("orders-submit", builder.Configuration));

app.UseExceptionHandler();
app.MapTicketNowDefaults();
app.MapOrdersEndpoints();

// Migraciones al arranque (didáctico; en producción, job aparte — docs/05 §1)
using (var scope = app.Services.CreateScope())
{
    var database = scope.ServiceProvider.GetRequiredService<OrdersDbContext>().Database;
    if (database.IsRelational())
    {
        await database.MigrateAsync();
    }
}

app.Run();

/// <summary>Marker para WebApplicationFactory en los tests.</summary>
namespace TicketNow.OrdersService
{
    public sealed class Marker { }
}
