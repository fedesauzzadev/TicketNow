namespace TicketNow.CatalogService;

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int Total);

public sealed record EventListItemDto(
    Guid Id,
    string Title,
    string Artist,
    DateTimeOffset StartsAt,
    string Venue,
    string City,
    string Status,
    int ZonesCount,
    decimal? PriceFrom,
    DateTimeOffset? OnsaleOpensAt);

public sealed record ZoneDto(Guid Id, string Name, int Capacity, decimal Price, string Availability);

public sealed record OnsaleInfoDto(
    DateTimeOffset OpensAt,
    DateTimeOffset? ClosesAt,
    int MaxPerAccount,
    bool RequiresQueue,
    int SecondsUntilOpen);

public sealed record EventDetailDto(
    Guid Id,
    string Title,
    string Artist,
    DateTimeOffset StartsAt,
    string? ImageUrl,
    string Venue,
    string City,
    string Status,
    OnsaleInfoDto? Onsale,
    IReadOnlyList<ZoneDto> Zones);

// --- Admin ---

public sealed record VenueDto(Guid Id, string Name, string City, int CapacityTotal);

public sealed record AdminEventDto(Guid Id, string Title, EventStatus Status, DateTimeOffset StartsAt, string Venue, bool HasOnsale);

public sealed record CreateVenueRequest(string Name, string City, int CapacityTotal);

public sealed record CreateZoneRequest(string Name, int Capacity, decimal Price);

public sealed record CreateEventRequest(
    string Title,
    string Artist,
    Guid VenueId,
    DateTimeOffset StartsAt,
    string? ImageUrl,
    IReadOnlyList<CreateZoneRequest> Zones);

public sealed record ConfigureOnsaleRequest(
    DateTimeOffset OpensAt,
    DateTimeOffset? ClosesAt,
    int? MaxConcurrentInside,
    int? AdmissionRatePerSec,
    int? MaxPerAccount,
    bool? RequiresQueue,
    int? PowDifficulty);

public sealed record CreateEventResponse(Guid Id);

// Configuración del onsale expuesta a queue-service (Fase 4).
public sealed record OnsaleConfigDto(
    bool Configured,
    DateTimeOffset? OpensAt,
    DateTimeOffset? ClosesAt,
    int MaxConcurrentInside,
    int AdmissionRatePerSec,
    int MaxPerAccount,
    bool RequiresQueue,
    int PowDifficulty);
