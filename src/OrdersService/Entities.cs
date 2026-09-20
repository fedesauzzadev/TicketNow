namespace TicketNow.OrdersService;

// Modelo del schema `orders` (docs/04-datos-y-apis.md §2).
// El dinero vive acá: órdenes, pagos, tickets. Todo cambio pasa por consumers (outbox).

public enum OrderStatus { Pending, Confirmed, Rejected, Expired }

public enum PaymentStatus { Authorized, Declined, Captured, Refunded }

public sealed class Order
{
    public Guid Id { get; set; }
    public string UserId { get; set; } = string.Empty;
    public Guid HoldId { get; set; }
    /// <summary>Evento de la compra (Fase 6): permite el límite por cuenta
    /// (tickets + órdenes vivas por usuario y evento) sin cruzar servicios.</summary>
    public Guid EventId { get; set; }
    public string IdempotencyKey { get; set; } = string.Empty;
    public OrderStatus Status { get; set; }
    public decimal Total { get; set; }
    public string PaymentToken { get; set; } = string.Empty; // mock-only: jamás un PAN real (docs/06 §4)
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }
    public List<OrderItem> Items { get; set; } = [];
}

public sealed class OrderItem
{
    public Guid Id { get; set; }
    public Guid OrderId { get; set; }
    public Guid ZoneId { get; set; }
    public int Qty { get; set; }
    public decimal UnitPrice { get; set; }
}

public sealed class Payment
{
    public Guid Id { get; set; }
    public Guid OrderId { get; set; }
    public string ProviderRef { get; set; } = string.Empty;
    public PaymentStatus Status { get; set; }
    public decimal Amount { get; set; }
    public DateTimeOffset RecordedAt { get; set; }
}

public sealed class Ticket
{
    public Guid Id { get; set; }
    public Guid OrderId { get; set; }
    public Guid EventId { get; set; }
    public Guid ZoneId { get; set; }
    public int Qty { get; set; }
    public string QrJti { get; set; } = string.Empty;
    public bool Redeemed { get; set; }
    public DateTimeOffset? RedeemedAt { get; set; }
}
