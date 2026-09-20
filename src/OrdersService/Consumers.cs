using MassTransit;
using Microsoft.EntityFrameworkCore;
using TicketNow.Contracts.Commands;
using TicketNow.Contracts.Events;

namespace TicketNow.OrdersService;

/// <summary>
/// Crea la orden PENDING de forma idempotente y dispara la saga (docs/03 §4 y §7).
/// Toda la mutación ocurre acá adentro: con outbox, orden + evento son atómicos.
/// </summary>
public sealed class SubmitOrderConsumer(OrdersDbContext db) : IConsumer<SubmitOrder>
{
    public async Task Consume(ConsumeContext<SubmitOrder> context)
    {
        var message = context.Message;
        var ct = context.CancellationToken;

        var existing = await db.Orders.FirstOrDefaultAsync(o => o.IdempotencyKey == message.IdempotencyKey, ct);
        if (existing is not null)
        {
            return; // replay idempotente (INV-5): la primera ejecución ya avanzó la saga
        }

        // Límite por cuenta, camino autoritativo (Fase 6): el chequeo veloz de la
        // API puede perder la carrera entre dos checkouts concurrentes; acá manda.
        var requestedLimit = message.Items.Sum(i => i.Qty);
        var owned = await AccountLimits.OwnedQtyAsync(db, message.UserId, message.EventId, ct);
        if (owned + requestedLimit > message.MaxPerAccount)
        {
            var now = DateTimeOffset.UtcNow;
            db.Orders.Add(new Order
            {
                Id = message.OrderId,
                UserId = message.UserId,
                HoldId = message.HoldId,
                EventId = message.EventId,
                IdempotencyKey = message.IdempotencyKey,
                Status = OrderStatus.Rejected,
                Total = message.Items.Sum(i => i.Qty * i.UnitPrice),
                PaymentToken = message.PaymentToken,
                CreatedAt = now,
                ClosedAt = now,
                Items = message.Items.Select(i => new OrderItem
                {
                    Id = Guid.NewGuid(), ZoneId = i.ZoneId, Qty = i.Qty, UnitPrice = i.UnitPrice,
                }).ToList(),
            });
            await db.SaveChangesAsync(ct);
            await context.Publish(new OrderRejected(message.OrderId, "límite por cuenta excedido"), ct);
            return;
        }

        var total = message.Items.Sum(i => i.Qty * i.UnitPrice);
        db.Orders.Add(new Order
        {
            Id = message.OrderId,
            UserId = message.UserId,
            HoldId = message.HoldId,
            EventId = message.EventId,
            IdempotencyKey = message.IdempotencyKey,
            Status = OrderStatus.Pending,
            Total = total,
            PaymentToken = message.PaymentToken,
            CreatedAt = DateTimeOffset.UtcNow,
            Items = message.Items.Select(i => new OrderItem
            {
                Id = Guid.NewGuid(), ZoneId = i.ZoneId, Qty = i.Qty, UnitPrice = i.UnitPrice,
            }).ToList(),
        });

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Carrera: otra réplica creó la orden con la misma clave (UNIQUE idempotency_key).
            if (await db.Orders.AnyAsync(o => o.IdempotencyKey == message.IdempotencyKey, ct))
            {
                return;
            }
            throw;
        }

        await context.Publish(new OrderSubmitted(message.OrderId, message.HoldId, message.UserId, total), ct);
    }
}

/// <summary>
/// Proveedor de pagos mock (docs/05: Payment__Mock__*). Simula latencia (1-3 s),
/// rechazos configurables y reembolsos. Jamás ve un PAN: solo tokens mock.
/// </summary>
public sealed class PaymentConsumer(OrdersDbContext db, IConfiguration config, ILogger<PaymentConsumer> log)
    : IConsumer<AuthorizePayment>, IConsumer<RefundPayment>
{
    public async Task Consume(ConsumeContext<AuthorizePayment> context)
    {
        var message = context.Message;
        var ct = context.CancellationToken;

        var order = await db.Orders.FirstOrDefaultAsync(o => o.Id == message.OrderId, ct);
        if (order is null || order.Total != message.Amount || !order.PaymentToken.StartsWith("tok_mock_", StringComparison.Ordinal))
        {
            await context.Publish(new PaymentDeclined(message.OrderId, "orden o token inválidos"), ct);
            return;
        }

        var latencyMs = Random.Shared.Next(
            config.GetValue("Payment:MinLatencyMs", 800),
            config.GetValue("Payment:MaxLatencyMs", 2500));
        await Task.Delay(latencyMs, ct);

        // Tarjeta de prueba con rechazo forzado (convención estilo Stripe test:
        // tokenizar con last4 "0002"). Sin esto solo rige DeclineRate.
        if (order.PaymentToken.Contains("decline", StringComparison.OrdinalIgnoreCase))
        {
            log.LogInformation("[PAGO mock] orden {OrderId} RECHAZADA (tarjeta de prueba)", message.OrderId);
            await context.Publish(new PaymentDeclined(message.OrderId, "tarjeta de prueba con rechazo forzado"), ct);
            return;
        }

        if (Random.Shared.NextDouble() < config.GetValue("Payment:DeclineRate", 0.10))
        {
            log.LogInformation("[PAGO mock] orden {OrderId} RECHAZADA (decline rate)", message.OrderId);
            await context.Publish(new PaymentDeclined(message.OrderId, "tarjeta rechazada por el banco (mock)"), ct);
            return;
        }

        var providerRef = $"pay_mock_{Guid.NewGuid():N}";
        log.LogInformation("[PAGO mock] orden {OrderId} autorizada {ProviderRef} ${Amount}", message.OrderId, providerRef, message.Amount);
        await context.Publish(new PaymentAuthorized(message.OrderId, providerRef, message.Amount), ct);
    }

    public async Task Consume(ConsumeContext<RefundPayment> context)
    {
        var message = context.Message;
        log.LogInformation("[PAGO mock] reembolso {ProviderRef} orden {OrderId} ${Amount}", message.ProviderRef, message.OrderId, message.Amount);
        await context.Publish(new PaymentRefunded(message.OrderId, message.ProviderRef), context.CancellationToken);
    }
}

/// <summary>Registra el resultado del pago en la tabla payments (INV-3: sin pago no hay entrada).</summary>
public sealed class PaymentRecorder(OrdersDbContext db) : IConsumer<PaymentAuthorized>, IConsumer<PaymentDeclined>
{
    public async Task Consume(ConsumeContext<PaymentAuthorized> context)
    {
        var message = context.Message;
        if (await db.Payments.AnyAsync(p => p.OrderId == message.OrderId, context.CancellationToken))
        {
            return;
        }
        db.Payments.Add(new Payment
        {
            Id = Guid.NewGuid(), OrderId = message.OrderId, ProviderRef = message.ProviderRef,
            Status = PaymentStatus.Authorized, Amount = message.Amount, RecordedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync(context.CancellationToken);
    }

    public async Task Consume(ConsumeContext<PaymentDeclined> context)
    {
        var message = context.Message;
        if (await db.Payments.AnyAsync(p => p.OrderId == message.OrderId, context.CancellationToken))
        {
            return;
        }
        db.Payments.Add(new Payment
        {
            Id = Guid.NewGuid(), OrderId = message.OrderId, ProviderRef = string.Empty,
            Status = PaymentStatus.Declined, Amount = 0, RecordedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync(context.CancellationToken);
    }
}

/// <summary>
/// Cierra el ciclo de la orden: confirma + emite ticket (INV-3) o rechaza.
/// Idempotente por estado: si la orden ya está cerrada, no hace nada.
/// Nota: NO maneja HoldExpired (la saga lo traduce a OrderRejected, un solo
/// camino de cierre para evitar carreras Rejected/Expired).
/// </summary>
public sealed class OrderFinalizer(OrdersDbContext db, IPublishEndpoint publish, ILogger<OrderFinalizer> log) : IConsumer<HoldConfirmed>, IConsumer<HoldConfirmFailed>, IConsumer<OrderRejected>
{
    public async Task Consume(ConsumeContext<HoldConfirmed> context)
    {
        var message = context.Message;
        var ct = context.CancellationToken;

        var order = await db.Orders.Include(o => o.Items).FirstOrDefaultAsync(o => o.Id == message.OrderId, ct);
        if (order is null || order.Status != OrderStatus.Pending)
        {
            return;
        }

        order.Status = OrderStatus.Confirmed;
        order.ClosedAt = DateTimeOffset.UtcNow;
        var ticket = new Ticket
        {
            Id = Guid.NewGuid(), OrderId = order.Id, EventId = message.EventId, ZoneId = message.ZoneId,
            Qty = message.Qty, QrJti = Guid.NewGuid().ToString("N"),
        };
        db.Tickets.Add(ticket);
        await db.SaveChangesAsync(ct);

        log.LogInformation("orden {OrderId} CONFIRMADA, ticket {TicketId} emitido", order.Id, ticket.Id);
        await publish.Publish(new TicketIssued(ticket.Id, order.Id, ticket.EventId, ticket.QrJti), ct);
    }

    public async Task Consume(ConsumeContext<HoldConfirmFailed> context)
    {
        await CloseAsAsync(context.Message.OrderId, OrderStatus.Rejected, context.CancellationToken);
    }

    public async Task Consume(ConsumeContext<OrderRejected> context)
    {
        await CloseAsAsync(context.Message.OrderId, OrderStatus.Rejected, context.CancellationToken);
    }

    private async Task CloseAsAsync(Guid orderId, OrderStatus status, CancellationToken ct)
    {
        var order = await db.Orders.FirstOrDefaultAsync(o => o.Id == orderId, ct);
        if (order is null || order.Status != OrderStatus.Pending)
        {
            return;
        }
        order.Status = status;
        order.ClosedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }
}
