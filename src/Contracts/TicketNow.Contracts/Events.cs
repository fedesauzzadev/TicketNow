namespace TicketNow.Contracts.Events;

// Contratos de eventos de dominio (docs/02-arquitectura.md §6 y docs/04-datos-y-apis.md §4).
// Mensajería: MassTransit sobre RabbitMQ (ADR-003). Sin dependencias: son records POCO.

/// <summary>El onsale de un evento abre: inventory siembra contadores Redis; queue habilita la fila.</summary>
public sealed record OnsaleOpened(
    Guid EventId,
    DateTimeOffset OpensAt,
    IReadOnlyList<OnsaleZone> Zones,
    int MaxPerAccount);

public sealed record OnsaleZone(Guid ZoneId, string Name, int Capacity, decimal Price);

/// <summary>Un hold fue otorgado (stock decrementado atómicamente).</summary>
public sealed record HoldGranted(
    Guid HoldId,
    Guid EventId,
    Guid ZoneId,
    int Qty,
    DateTimeOffset ExpiresAt,
    string UserId);

/// <summary>No había stock (o límite violado) para el hold solicitado.</summary>
public sealed record HoldDenied(Guid EventId, Guid ZoneId, int Qty, string UserId, string Reason);

/// <summary>Hold liberado voluntariamente (checkout abandonado): stock devuelto.</summary>
public sealed record HoldReleased(Guid HoldId, Guid EventId, Guid ZoneId, int Qty, string Reason);

/// <summary>Hold expirado por TTL (sweeper): stock devuelto; la saga cancela órdenes pendientes.</summary>
public sealed record HoldExpired(Guid HoldId, Guid EventId, Guid ZoneId, int Qty, Guid OrderId);

/// <summary>Checkout iniciado: la saga valida el hold y avanza al pago.</summary>
public sealed record OrderSubmitted(Guid OrderId, Guid HoldId, string UserId, decimal Total);

public sealed record PaymentAuthorized(Guid OrderId, string ProviderRef, decimal Amount);

public sealed record PaymentDeclined(Guid OrderId, string Reason);

/// <summary>Orden cementada: ledger CONFIRM + ticket; notifica al fan.</summary>
public sealed record OrderConfirmed(Guid OrderId, Guid HoldId, string UserId, decimal Total);

public sealed record OrderRejected(Guid OrderId, string Reason);

public sealed record TicketIssued(Guid TicketId, Guid OrderId, Guid EventId, string QrJti);
