using MassTransit;
using Microsoft.Extensions.Configuration;
using TicketNow.Contracts.Commands;
using TicketNow.Contracts.Events;
using TicketNow.ServiceDefaults;

namespace TicketNow.OrdersService;

/// <summary>Instancia de la saga de compra. CorrelationId = OrderId.</summary>
public sealed class OrderState : SagaStateMachineInstance
{
    public Guid CorrelationId { get; set; }
    public string CurrentState { get; set; } = null!;
    public Guid HoldId { get; set; }
    public string UserId { get; set; } = null!;
    public decimal Total { get; set; }
    public string? ProviderRef { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// Saga orquestada de la compra (docs/03 §4): reservar → cobrar → confirmar → emitir.
/// El TTL del hold es el timeout natural: si el sweeper libera el hold, el evento
/// HoldExpired cancela la saga en cualquier estado intermedio. Compensaciones:
/// ReleaseHold (devolver stock) y RefundPayment (devolver dinero).
/// </summary>
public sealed class OrderStateMachine : MassTransitStateMachine<OrderState>
{
    public State Holding { get; private set; } = null!;
    public State AwaitingPayment { get; private set; } = null!;
    public State Confirming { get; private set; } = null!;
    public State Completed { get; private set; } = null!;
    public State Rejected { get; private set; } = null!;

    public Event<OrderSubmitted> OrderSubmitted { get; private set; } = null!;
    public Event<HoldValidated> HoldValidated { get; private set; } = null!;
    public Event<HoldInvalid> HoldInvalid { get; private set; } = null!;
    public Event<HoldExpired> HoldExpired { get; private set; } = null!;
    public Event<PaymentAuthorized> PaymentAuthorized { get; private set; } = null!;
    public Event<PaymentDeclined> PaymentDeclined { get; private set; } = null!;
    public Event<HoldConfirmed> HoldConfirmed { get; private set; } = null!;
    public Event<HoldConfirmFailed> HoldConfirmFailed { get; private set; } = null!;

    private readonly record struct SagaUris(Uri Validate, Uri Payments, Uri Release, Uri Confirm);
    private readonly SagaUris _uris;

    public OrderStateMachine(IConfiguration configuration)
    {
        // Destinos con sufijo de entorno (tests aíslan su topología).
        _uris = new(
            MessagingExtensions.QueueUri("inventory-validate", configuration),
            MessagingExtensions.QueueUri("orders-payments", configuration),
            MessagingExtensions.QueueUri("inventory-release", configuration),
            MessagingExtensions.QueueUri("inventory-confirm", configuration));

        InstanceState(x => x.CurrentState);

        Event(() => OrderSubmitted, x => x.CorrelateById(m => m.Message.OrderId));
        Event(() => HoldValidated, x => x.CorrelateById(m => m.Message.OrderId));
        Event(() => HoldInvalid, x => x.CorrelateById(m => m.Message.OrderId));
        Event(() => HoldExpired, x => x.CorrelateById(m => m.Message.OrderId));
        Event(() => PaymentAuthorized, x => x.CorrelateById(m => m.Message.OrderId));
        Event(() => PaymentDeclined, x => x.CorrelateById(m => m.Message.OrderId));
        Event(() => HoldConfirmed, x => x.CorrelateById(m => m.Message.OrderId));
        Event(() => HoldConfirmFailed, x => x.CorrelateById(m => m.Message.OrderId));

        Initially(
            When(OrderSubmitted)
                .Then(ctx =>
                {
                    ctx.Saga.HoldId = ctx.Message.HoldId;
                    ctx.Saga.UserId = ctx.Message.UserId;
                    ctx.Saga.Total = ctx.Message.Total;
                    ctx.Saga.CreatedAt = DateTimeOffset.UtcNow;
                })
                .Send(_uris.Validate, ctx => new ValidateHold(ctx.Saga.CorrelationId, ctx.Saga.HoldId))
                .TransitionTo(Holding));

        During(Holding,
            When(HoldValidated)
                .Send(_uris.Payments, ctx => new AuthorizePayment(ctx.Saga.CorrelationId, ctx.Saga.Total))
                .TransitionTo(AwaitingPayment),
            When(HoldInvalid)
                .Publish(ctx => new OrderRejected(ctx.Saga.CorrelationId, "hold inválido o vencido"))
                .TransitionTo(Rejected)
                .Finalize(),
            When(HoldExpired)
                .Publish(ctx => new OrderRejected(ctx.Saga.CorrelationId, "el hold expiró antes del pago"))
                .TransitionTo(Rejected)
                .Finalize(),
            Ignore(OrderSubmitted)); // reintento del comando: la orden ya nació

        During(AwaitingPayment,
            When(PaymentAuthorized)
                .Then(ctx => ctx.Saga.ProviderRef = ctx.Message.ProviderRef)
                .Send(_uris.Confirm, ctx => new ConfirmHold(ctx.Saga.CorrelationId, ctx.Saga.HoldId))
                .TransitionTo(Confirming),
            When(PaymentDeclined)
                .Send(_uris.Release, ctx => new ReleaseHold(ctx.Saga.CorrelationId, ctx.Saga.HoldId, "pago rechazado"))
                .Publish(ctx => new OrderRejected(ctx.Saga.CorrelationId, "pago rechazado"))
                .TransitionTo(Rejected)
                .Finalize(),
            When(HoldExpired)
                .Send(_uris.Release, ctx => new ReleaseHold(ctx.Saga.CorrelationId, ctx.Saga.HoldId, "hold expirado durante el pago"))
                .Publish(ctx => new OrderRejected(ctx.Saga.CorrelationId, "el hold expiró durante el pago"))
                .TransitionTo(Rejected)
                .Finalize(),
            Ignore(HoldValidated), // duplicado tardío: ya se pidió el cobro
            Ignore(OrderSubmitted));

        During(Confirming,
            When(HoldConfirmed)
                .Publish(ctx => new OrderConfirmed(ctx.Saga.CorrelationId, ctx.Saga.HoldId, ctx.Saga.UserId, ctx.Saga.Total))
                .TransitionTo(Completed)
                .Finalize(),
            When(HoldConfirmFailed)
                .Send(_uris.Payments, ctx => new RefundPayment(ctx.Saga.CorrelationId, ctx.Saga.ProviderRef!, ctx.Saga.Total))
                .Publish(ctx => new OrderRejected(ctx.Saga.CorrelationId, "no se pudo confirmar el hold"))
                .TransitionTo(Rejected)
                .Finalize(),
            Ignore(HoldValidated),
            Ignore(PaymentAuthorized),
            Ignore(OrderSubmitted));

        SetCompletedWhenFinalized();
    }
}
