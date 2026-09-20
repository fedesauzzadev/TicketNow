namespace TicketNow.Contracts.Events;

// Fase 3: eventos de la saga de compra (docs/02 §6, docs/03 §4).
// Todos llevan OrderId para correlación de la saga.

/// <summary>Inventory → saga: el hold es válido y quedó asociado a la orden.</summary>
public sealed record HoldValidated(Guid OrderId, Guid HoldId, Guid EventId, Guid ZoneId, int Qty);

/// <summary>Inventory → saga: el hold no sirve (inexistente, vencido, de otro, ocupado).</summary>
public sealed record HoldInvalid(Guid OrderId, Guid HoldId, string Reason);

/// <summary>Inventory → saga/órdenes: confirmación cementada (ledger CONFIRM).</summary>
public sealed record HoldConfirmed(Guid OrderId, Guid HoldId, Guid EventId, Guid ZoneId, int Qty);

/// <summary>Inventory → saga: el hold ya no estaba activo al confirmar.</summary>
public sealed record HoldConfirmFailed(Guid OrderId, Guid HoldId, string Reason);

public sealed record PaymentRefunded(Guid OrderId, string ProviderRef);
