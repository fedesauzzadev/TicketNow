using MassTransit;
using TicketNow.NotificationsService;
using TicketNow.ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);

builder.AddTicketNowDefaults("notifications-service");
builder.AddRabbitMqHealth();

// Solo consume (sin outbox: no escribe estado propio en Fase 3).
builder.AddTicketNowMessaging(
    bus => bus.AddConsumer<NotifyConsumer>(),
    (context, cfg) =>
    {
        cfg.ReceiveEndpoint(MessagingExtensions.QueueName("notifications", builder.Configuration), e =>
        {
            e.ConfigureConsumer<NotifyConsumer>(context);
        });
    });

var app = builder.Build();

app.MapTicketNowDefaults();

app.MapGet("/api/notifications/ping", () => Results.Ok(new
{
    service = "notifications-service",
    status = "pong",
    utc = DateTime.UtcNow,
}));

app.Run();

public partial class Program { }
