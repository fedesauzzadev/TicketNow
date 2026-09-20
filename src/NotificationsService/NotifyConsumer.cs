using MassTransit;
using TicketNow.Contracts.Events;

namespace TicketNow.NotificationsService;

/// <summary>
/// Notificaciones (FR-60): email mock (log estructurado) + punto de extensión para
/// in-app vía SignalR (Fase 4). Sin DB propia en Fase 3: duplicados solo duplican
/// el log (inofensivo y visible); el email real exigiría inbox.
/// </summary>
public sealed class NotifyConsumer(ILogger<NotifyConsumer> log)
    : IConsumer<OrderConfirmed>, IConsumer<TicketIssued>, IConsumer<OrderRejected>
{
    public Task Consume(ConsumeContext<OrderConfirmed> context)
    {
        var message = context.Message;
        log.LogInformation(
            "[EMAIL mock] para={UserId} asunto='¡Tus entradas están confirmadas!' orden={OrderId} total=${Total}",
            message.UserId, message.OrderId, message.Total);
        return Task.CompletedTask;
    }

    public Task Consume(ConsumeContext<TicketIssued> context)
    {
        var message = context.Message;
        log.LogInformation(
            "[EMAIL mock] para orden={OrderId}: ticket {TicketId} con QR listo para descargar",
            message.OrderId, message.TicketId);
        return Task.CompletedTask;
    }

    public Task Consume(ConsumeContext<OrderRejected> context)
    {
        var message = context.Message;
        log.LogInformation(
            "[EMAIL mock] orden={OrderId} rechazada: {Reason}. El stock fue devuelto automáticamente.",
            message.OrderId, message.Reason);
        return Task.CompletedTask;
    }
}
