namespace TicketNow.Contracts.Commands;

// Fase 3: comandos punto a punto (send) y eventos de la saga (docs/02 §6).
// Correlación de la saga por OrderId en todos los mensajes.

public sealed record OrderItemDto(Guid ZoneId, int Qty, decimal UnitPrice);

/// <summary>La API envía este comando en vez de crear la orden directamente:
/// toda mutación de estado ocurre dentro de consumers (atomicidad total).
/// EventId + MaxPerAccount viajan en el mensaje para el límite por cuenta
/// (Fase 6, ADR-011: el valor lo aporta el cliente desde el catálogo visible;
/// el CONTEO lo hace el servidor — lo que se enforcea es server-side).</summary>
public sealed record SubmitOrder(
    Guid OrderId,
    Guid HoldId,
    string UserId,
    Guid EventId,
    int MaxPerAccount,
    IReadOnlyList<OrderItemDto> Items,
    string PaymentToken,
    string IdempotencyKey);

/// <summary>Saga → inventory: ¿sigue válido este hold para esta orden?</summary>
public sealed record ValidateHold(Guid OrderId, Guid HoldId);

/// <summary>Saga → inventory: compensación, devolver el stock.</summary>
public sealed record ReleaseHold(Guid OrderId, Guid HoldId, string Reason);

/// <summary>Saga → inventory: cementar el hold (puerta autoritativa).</summary>
public sealed record ConfirmHold(Guid OrderId, Guid HoldId);

/// <summary>Saga → payments (mock interno de orders-service). El token vive en la
/// orden (mock-only); el consumer lo valida contra el monto antes de autorizar.</summary>
public sealed record AuthorizePayment(Guid OrderId, decimal Amount);

public sealed record RefundPayment(Guid OrderId, string ProviderRef, decimal Amount);
