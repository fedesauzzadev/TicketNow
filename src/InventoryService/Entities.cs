namespace TicketNow.InventoryService;

// Modelo del schema `inventory` (docs/04-datos-y-apis.md §2, ADR-006).
// El ledger es append-only: solo INSERTs. El stock vigente es una derivada.

public enum LedgerEntryType { Hold, Confirm, Release, Expire }

public enum HoldOutcome { Confirmed, Released, Expired }

public sealed class LedgerEntry
{
    public Guid EntryId { get; set; } = Guid.NewGuid();
    public Guid EventId { get; set; }
    public Guid ZoneId { get; set; }
    public LedgerEntryType EntryType { get; set; }
    public int Delta { get; set; }
    public Guid? HoldId { get; set; }
    public Guid? OrderId { get; set; }
    public string Actor { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// Auditoría de cada hold: nace con Outcome null y se cierra con Confirmed/Released/Expired.
/// Es la red de seguridad del sweeper y del reconciliador (§5 y §11 de docs/03).
/// </summary>
public sealed class HoldRecord
{
    public Guid HoldId { get; set; }
    public Guid EventId { get; set; }
    public Guid ZoneId { get; set; }
    public string UserId { get; set; } = string.Empty;
    public int Qty { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public HoldOutcome? Outcome { get; set; }

    /// <summary>
    /// Orden que reclamó el hold (claim-lite en ValidateHold, Fase 3). Null = libre.
    /// La puerta autoritativa sigue siendo el Lua de confirmación.
    /// </summary>
    public Guid? OrderId { get; set; }
}
