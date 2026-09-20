using MassTransit;
using StackExchange.Redis;
using TicketNow.QueueService;
using TicketNow.ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);

builder.AddTicketNowDefaults("queue-service");
builder.AddRedisHealth();

builder.Services.AddProblemDetails();

// Fase 5: OnsaleOpened habilita la fila. Sin PG → sin outbox: handler
// idempotente con retry (at-least-once alcanza para invalidar/setear tasa).
builder.AddTicketNowMessaging(
    bus => bus.AddConsumer<OnsaleOpenedConsumer>(),
    (context, cfg) =>
    {
        cfg.ReceiveEndpoint(MessagingExtensions.QueueName("queue-onsale", builder.Configuration), e =>
        {
            MessagingExtensions.UseTicketNowRetry(e);
            e.ConfigureConsumer<OnsaleOpenedConsumer>(context);
        });
    });

// Backplane Redis para escalar el hub a N réplicas (docs/02 §4).
builder.Services.AddSignalR().AddStackExchangeRedis(
    builder.Configuration.GetConnectionString("Redis") ?? "localhost:6379",
    options => options.Configuration.ChannelPrefix = "ticketnow");

var redisConnectionString = builder.Configuration.GetConnectionString("Redis")
    ?? throw new InvalidOperationException("ConnectionStrings:Redis no configurado (la fila vive ahí)");
builder.Services.AddSingleton(_ => ConnectionMultiplexer.Connect(redisConnectionString));

builder.Services.AddSingleton<QueueStore>();
builder.Services.AddSingleton<OnsaleConfigProvider>();
builder.Services.AddSingleton<AdmissionTokenService>();
builder.Services.AddScoped<AdmissionService>();
builder.Services.AddHostedService<AdmissionLoop>();
builder.Services.AddHostedService<QueueReaper>();

builder.Services.AddHttpClient("catalog", client =>
{
    client.BaseAddress = new Uri(builder.Configuration["Catalog:BaseUrl"] ?? "http://catalog-service:8080");
    client.Timeout = TimeSpan.FromSeconds(5);
});
builder.Services.AddHttpClient("inventory-health", client =>
{
    client.BaseAddress = new Uri(builder.Configuration["Inventory:BaseUrl"] ?? "http://inventory-service:8080");
    client.Timeout = TimeSpan.FromSeconds(3);
});

var app = builder.Build();

app.UseExceptionHandler();
app.MapTicketNowDefaults();
app.MapQueueEndpoints();
app.MapHub<QueueHub>("/hubs/queue");

app.Run();

/// <summary>Marker para WebApplicationFactory en los tests.</summary>
namespace TicketNow.QueueService
{
    public sealed class Marker { }
}
