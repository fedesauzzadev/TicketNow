namespace TicketNow.InventoryService;

public sealed record ZoneSeedDto(Guid ZoneId, int Capacity);

public sealed record SeedStockRequest(Guid EventId, IReadOnlyList<ZoneSeedDto> Zones);

public sealed record SeedStockResponse(Guid EventId, int Seeded, int Skipped);

public sealed record CreateHoldRequest(Guid EventId, Guid ZoneId, int Qty);

public sealed record HoldResponse(Guid HoldId, Guid EventId, Guid ZoneId, int Qty, DateTimeOffset ExpiresAt, bool Replayed);

public sealed record HoldStatusResponse(
    Guid HoldId, Guid EventId, Guid ZoneId, string UserId, int Qty,
    DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, string? Outcome);
