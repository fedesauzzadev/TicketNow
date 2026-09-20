namespace TicketNow.CatalogService;

// Modelo del schema `catalog` (docs/04-datos-y-apis.md §2). Solo catalog-service escribe aquí.

public enum EventStatus { Draft, Announced }

public sealed class Venue
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string City { get; set; } = string.Empty;
    public int CapacityTotal { get; set; }
}

public sealed class Event
{
    public Guid Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    public DateTimeOffset StartsAt { get; set; }
    public EventStatus Status { get; set; }
    public string? ImageUrl { get; set; }
    public Guid VenueId { get; set; }
    public Venue? Venue { get; set; }
    public List<Zone> Zones { get; set; } = [];
    public Onsale? Onsale { get; set; }
}

public sealed class Zone
{
    public Guid Id { get; set; }
    public Guid EventId { get; set; }
    public string Name { get; set; } = string.Empty;
    public int Capacity { get; set; }
    public decimal Price { get; set; }
    public int SortOrder { get; set; }
}

/// <summary>Configuración del onsale de un evento (FR-51 · docs/01-vision-y-requisitos.md §6.6).</summary>
public sealed class Onsale
{
    public Guid EventId { get; set; }
    public DateTimeOffset OpensAt { get; set; }
    public DateTimeOffset? ClosesAt { get; set; }
    public int MaxConcurrentInside { get; set; }
    public int AdmissionRatePerSec { get; set; }
    public int MaxPerAccount { get; set; }
    public bool RequiresQueue { get; set; }

    /// <summary>
    /// Dificultad del proof-of-work para entrar a la fila (Fase 6, docs/03 §12):
    /// bits en cero al inicio de SHA256(challengeId:nonce). 0 = desactivado.
    /// </summary>
    public int PowDifficulty { get; set; }

    /// <summary>
    /// Momento en que el opener PUBLICÓ OnsaleOpened (null = aún no abrió).
    /// El estado "onsale" es computado en lectura (EffectiveStatus); esta columna
    /// persiste la transición para publicar el evento EXACTAMENTE una vez (Fase 5).
    /// </summary>
    public DateTimeOffset? OpenedAt { get; set; }
}

public static class EventStatusExtensions
{
    /// <summary>
    /// Estado efectivo combinando el estado persistido con el reloj:
    /// draft | announced | onsale | onsale_closed | finished (docs/04 §5 catalog).
    /// </summary>
    public static string EffectiveStatus(this Event @event, DateTimeOffset now) => @event.Status switch
    {
        EventStatus.Draft => "draft",
        _ when @event.StartsAt <= now => "finished",
        _ when @event.Onsale is null => "announced",
        _ when now < @event.Onsale.OpensAt => "announced",
        _ when @event.Onsale.ClosesAt is { } closesAt && now >= closesAt => "onsale_closed",
        _ => "onsale",
    };
}
