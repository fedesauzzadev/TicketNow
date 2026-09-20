using Microsoft.EntityFrameworkCore;
using TicketNow.ServiceDefaults;

namespace TicketNow.CatalogService;

public static class Api
{
    // Headers de caché para el edge (ADR-008): el borde puede servir 30 s y refrescar en background.
    private const string EdgeCacheControl = "public, max-age=0, s-maxage=30, stale-while-revalidate=60";

    // En Fase 2 la disponibilidad pasa a ser una proyección actualizada por eventos de inventory.
    private const string PlaceholderAvailability = "high";

    public static void MapCatalogEndpoints(this WebApplication app)
    {
        var catalog = app.MapGroup("/api");

        // ---------- Lecturas públicas (cache-aside) ----------

        catalog.MapGet("/events", async (
            [AsParameters] ListEventsQuery query,
            CatalogDbContext db,
            IEventCache cache,
            HttpContext http,
            CancellationToken ct) =>
        {
            var search = string.IsNullOrWhiteSpace(query.Search) ? null : query.Search.Trim();
            var city = string.IsNullOrWhiteSpace(query.City) ? null : query.City.Trim();
            var page = Math.Max(query.Page, 1);
            var pageSize = Math.Clamp(query.PageSize < 1 ? 12 : query.PageSize, 1, 50);

            var cacheKey = $"catalog:events:page:{page}:{pageSize}:s={search?.ToLowerInvariant() ?? "-"}:c={city?.ToLowerInvariant() ?? "-"}";
            var now = DateTimeOffset.UtcNow;

            var result = await cache.GetOrSetAsync(cacheKey, TimeSpan.FromSeconds(60), async token =>
            {
                var source = db.Events.AsNoTracking().Where(e => e.Status != EventStatus.Draft);
                if (search is not null)
                {
                    source = source.Where(e => EF.Functions.ILike(e.Title, $"%{search}%") || EF.Functions.ILike(e.Artist, $"%{search}%"));
                }
                if (city is not null)
                {
                    source = source.Where(e => e.Venue!.City == city);
                }

                var total = await source.CountAsync(token);
                var events = await source
                    .OrderBy(e => e.StartsAt)
                    .Skip((page - 1) * pageSize)
                    .Take(pageSize)
                    .Include(e => e.Venue)
                    .Include(e => e.Onsale)
                    .Include(e => e.Zones)
                    .ToListAsync(token);

                var items = new List<EventListItemDto>();
                foreach (var e in events)
                {
                    // Peor disponibilidad de sus zonas (para el badge de la card).
                    var free = await cache.GetFreeAsync(e.Id, e.Zones.Select(z => z.Id), token);
                    var worst = "high";
                    foreach (var z in e.Zones)
                    {
                        var level = AvailabilityLevel(free.GetValueOrDefault(z.Id, -1), z.Capacity);
                        if (Rank(level) < Rank(worst))
                        {
                            worst = level;
                        }
                    }
                    items.Add(new EventListItemDto(
                        e.Id, e.Title, e.Artist, e.StartsAt,
                        e.Venue!.Name, e.Venue.City, e.EffectiveStatus(now),
                        e.Zones.Count,
                        e.Zones.Count == 0 ? null : e.Zones.Min(z => z.Price),
                        e.Onsale?.OpensAt, worst, e.Onsale?.RequiresQueue ?? false));
                }

                return new PagedResult<EventListItemDto>(items, page, pageSize, total);
            }, ct);

            http.Response.Headers.CacheControl = EdgeCacheControl;
            return Results.Ok(result);
        });

        catalog.MapGet("/events/{id:guid}", async (Guid id, CatalogDbContext db, IEventCache cache, HttpContext http, CancellationToken ct) =>
        {
            var now = DateTimeOffset.UtcNow;
            var dto = await cache.GetOrSetAsync($"catalog:event:{id}", TimeSpan.FromSeconds(60), async token =>
            {
                var @event = await db.Events.AsNoTracking()
                    .Include(e => e.Venue)
                    .Include(e => e.Onsale)
                    .Include(e => e.Zones)
                    .FirstOrDefaultAsync(e => e.Id == id, token);

                return @event is null || @event.Status == EventStatus.Draft ? null : MapDetail(@event, now);
            }, ct);

            if (dto is null)
            {
                return NotFoundEvent(id);
            }

            // Disponibilidad en vivo sobre el detalle cacheado (eventual por diseño, docs/03 §8).
            var free = await cache.GetFreeAsync(id, dto.Zones.Select(z => z.Id), ct);
            dto = dto with
            {
                Zones = dto.Zones.Select(z => z with { Availability = AvailabilityLevel(free.GetValueOrDefault(z.Id, -1), z.Capacity) }).ToList(),
            };

            http.Response.Headers.CacheControl = EdgeCacheControl;
            return Results.Ok(dto);
        });

        catalog.MapGet("/events/{id:guid}/zones", async (Guid id, CatalogDbContext db, IEventCache cache, CancellationToken ct) =>
        {
            var @event = await db.Events.AsNoTracking()
                .Include(e => e.Zones.OrderBy(z => z.SortOrder))
                .FirstOrDefaultAsync(e => e.Id == id, ct);

            if (@event is null || @event.Status == EventStatus.Draft)
            {
                return NotFoundEvent(id);
            }

            var free = await cache.GetFreeAsync(id, @event.Zones.Select(z => z.Id), ct);
            return Results.Ok(@event.Zones.Select(z => new ZoneDto(z.Id, z.Name, z.Capacity, z.Price, AvailabilityLevel(free.GetValueOrDefault(z.Id, -1), z.Capacity))));
        });

        // Config del onsale para queue-service (control de admisión, Fase 4).
        // Lectura pública (capacidades y tasas no son secretas) con caché de borde.
        catalog.MapGet("/events/{id:guid}/onsale-config", async (Guid id, CatalogDbContext db, HttpContext http, CancellationToken ct) =>
        {
            var @event = await db.Events.AsNoTracking().Include(e => e.Onsale).FirstOrDefaultAsync(e => e.Id == id, ct);
            if (@event is null || @event.Status == EventStatus.Draft)
            {
                return NotFoundEvent(id);
            }
            var onsale = @event.Onsale;
            if (onsale is null)
            {
                return Results.Ok(new OnsaleConfigDto(false, null, null, 0, 0, 0, false, 0));
            }
            http.Response.Headers.CacheControl = EdgeCacheControl;
    return Results.Ok(new OnsaleConfigDto(
        true, onsale.OpensAt, onsale.ClosesAt, onsale.MaxConcurrentInside,
        onsale.AdmissionRatePerSec, onsale.MaxPerAccount, onsale.RequiresQueue, onsale.PowDifficulty));
        });

        // ---------- Admin (FR-50/51 · sin auth hasta Fase 6, anotado en docs/06) ----------

        var admin = catalog.MapGroup("/admin");

        admin.MapGet("/venues", async (CatalogDbContext db, CancellationToken ct) =>
            Results.Ok(await db.Venues.AsNoTracking().OrderBy(v => v.Name).Select(v => new VenueDto(v.Id, v.Name, v.City, v.CapacityTotal)).ToListAsync(ct)));

        admin.MapGet("/events", async (CatalogDbContext db, CancellationToken ct) =>
        {
            var events = await db.Events.AsNoTracking().Include(e => e.Venue).OrderBy(e => e.StartsAt).ToListAsync(ct);
            return Results.Ok(events.Select(e => new AdminEventDto(e.Id, e.Title, e.Status, e.StartsAt, e.Venue!.Name, e.Onsale is not null)));
        });

        admin.MapPost("/venues", async (CreateVenueRequest request, CatalogDbContext db, CancellationToken ct) =>
        {
            var errors = new Dictionary<string, string[]>();
            if (string.IsNullOrWhiteSpace(request.Name)) errors["name"] = ["obligatorio"];
            if (string.IsNullOrWhiteSpace(request.City)) errors["city"] = ["obligatorio"];
            if (request.CapacityTotal < 1) errors["capacityTotal"] = ["debe ser mayor a 0"];
            if (errors.Count > 0) return Results.ValidationProblem(errors);

            var venue = new Venue { Id = Guid.NewGuid(), Name = request.Name.Trim(), City = request.City.Trim(), CapacityTotal = request.CapacityTotal };
            db.Venues.Add(venue);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/admin/venues/{venue.Id}", new VenueDto(venue.Id, venue.Name, venue.City, venue.CapacityTotal));
        });

        admin.MapPost("/events", async (CreateEventRequest request, CatalogDbContext db, IEventCache cache, CancellationToken ct) =>
        {
            var errors = new Dictionary<string, string[]>();
            if (string.IsNullOrWhiteSpace(request.Title)) errors["title"] = ["obligatorio"];
            if (string.IsNullOrWhiteSpace(request.Artist)) errors["artist"] = ["obligatorio"];
            if (request.StartsAt <= DateTimeOffset.UtcNow) errors["startsAt"] = ["debe ser en el futuro"];
            if (request.Zones.Count == 0) errors["zones"] = ["al menos una zona"];
            if (request.Zones.Any(z => z.Capacity < 1)) errors["zones"] = ["capacidades deben ser mayores a 0"];
            if (request.Zones.Any(z => z.Price < 0)) errors["zones"] = ["precios no pueden ser negativos"];
            if (errors.Count > 0) return Results.ValidationProblem(errors);

            var venue = await db.Venues.FindAsync([request.VenueId], ct);
            if (venue is null)
            {
                return Results.Problem(title: "venue inexistente", statusCode: StatusCodes.Status404NotFound);
            }

            var @event = new Event
            {
                Id = Guid.NewGuid(),
                Title = request.Title.Trim(),
                Artist = request.Artist.Trim(),
                StartsAt = request.StartsAt,
                ImageUrl = request.ImageUrl,
                Status = EventStatus.Draft,
                VenueId = venue.Id,
                Zones = request.Zones.Select((z, index) => new Zone
                {
                    Id = Guid.NewGuid(),
                    Name = z.Name.Trim(),
                    Capacity = z.Capacity,
                    Price = z.Price,
                    SortOrder = index + 1,
                }).ToList(),
            };

            db.Events.Add(@event);
            await db.SaveChangesAsync(ct);
            await cache.InvalidateListingsAsync();

            return Results.Created($"/api/events/{@event.Id}", new CreateEventResponse(@event.Id));
        });

        admin.MapPost("/events/{id:guid}/onsale", async (Guid id, ConfigureOnsaleRequest request, CatalogDbContext db, IEventCache cache, CancellationToken ct) =>
        {
            var @event = await db.Events.Include(e => e.Onsale).FirstOrDefaultAsync(e => e.Id == id, ct);
            if (@event is null)
            {
                return NotFoundEvent(id);
            }

            if (@event.Onsale is not null)
            {
                db.Onsales.Remove(@event.Onsale);
            }
            @event.Onsale = new Onsale
            {
                EventId = @event.Id,
                OpensAt = request.OpensAt,
                ClosesAt = request.ClosesAt,
                MaxConcurrentInside = request.MaxConcurrentInside ?? 2000,
                AdmissionRatePerSec = request.AdmissionRatePerSec ?? 50,
                MaxPerAccount = request.MaxPerAccount ?? 4,
                RequiresQueue = request.RequiresQueue ?? true,
                PowDifficulty = request.PowDifficulty ?? 0,
            };
            @event.Status = EventStatus.Announced;

            await db.SaveChangesAsync(ct);
            await cache.InvalidateEventAsync(@event.Id);
            await cache.InvalidateListingsAsync();

            return Results.Ok(new { @event.Id, onsaleOpensAt = request.OpensAt });
        });

        // Ping de Fase 0 (smoke tests del borde)
        catalog.MapGet("/events/ping", () => Results.Ok(new { service = "catalog-service", status = "pong", utc = DateTime.UtcNow }));
        admin.MapGet("/ping", () => Results.Ok(new { service = "catalog-service", area = "admin", status = "pong" }));
    }

    private static IResult NotFoundEvent(Guid id) =>
        Results.Problem(title: "evento inexistente o no publicado", detail: $"id={id}", statusCode: StatusCodes.Status404NotFound);

    /// <summary>Nivel de disponibilidad desde libres/capacidad (docs/04 §5).</summary>
    private static string AvailabilityLevel(int free, int capacity) => free switch
    {
        < 0 => "high", // sin proyección todavía: el origen manda
        0 => "soldout",
        var f when f > capacity * 0.3 => "high",
        _ => "medium",
    };

    private static int Rank(string level) => level switch
    {
        "soldout" => 0,
        "low" => 1,
        "medium" => 2,
        _ => 3,
    };

    private static EventDetailDto MapDetail(Event @event, DateTimeOffset now)
    {
        var onsale = @event.Onsale is null ? null : new OnsaleInfoDto(
            @event.Onsale.OpensAt,
            @event.Onsale.ClosesAt,
            @event.Onsale.MaxPerAccount,
            @event.Onsale.RequiresQueue,
            (int)Math.Max(0, (@event.Onsale.OpensAt - now).TotalSeconds));

        return new EventDetailDto(
            @event.Id, @event.Title, @event.Artist, @event.StartsAt, @event.ImageUrl,
            @event.Venue!.Name, @event.Venue.City, @event.EffectiveStatus(now),
            onsale,
            @event.Zones.OrderBy(z => z.SortOrder)
                .Select(z => new ZoneDto(z.Id, z.Name, z.Capacity, z.Price, PlaceholderAvailability))
                .ToList());
    }
}

public readonly record struct ListEventsQuery(string? Search = null, string? City = null, int Page = 1, int PageSize = 12);
