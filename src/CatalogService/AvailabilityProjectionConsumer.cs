using MassTransit;
using Microsoft.EntityFrameworkCore;
using TicketNow.Contracts.Events;

namespace TicketNow.CatalogService;

/// <summary>
/// Proyección de disponibilidad (docs/03 §8, CQRS-lite): mantiene en Redis los
/// libres por zona a partir de los eventos de inventory. Inicialización perezosa
/// desde PG (la verdad de zonas vive acá). Dedupe vía inbox del endpoint.
/// </summary>
public sealed class AvailabilityProjectionConsumer(CatalogDbContext db, IEventCache cache)
    : IConsumer<HoldGranted>, IConsumer<HoldReleased>, IConsumer<HoldExpired>
{
    public async Task Consume(ConsumeContext<HoldGranted> context)
    {
        var message = context.Message;
        await EnsureAsync(message.EventId, context.CancellationToken);
        await cache.ApplyAvailabilityDeltaAsync(message.EventId, message.ZoneId, -message.Qty, context.CancellationToken);
    }

    public async Task Consume(ConsumeContext<HoldReleased> context)
    {
        var message = context.Message;
        await EnsureAsync(message.EventId, context.CancellationToken);
        await cache.ApplyAvailabilityDeltaAsync(message.EventId, message.ZoneId, message.Qty, context.CancellationToken);
    }

    public async Task Consume(ConsumeContext<HoldExpired> context)
    {
        var message = context.Message;
        await EnsureAsync(message.EventId, context.CancellationToken);
        await cache.ApplyAvailabilityDeltaAsync(message.EventId, message.ZoneId, message.Qty, context.CancellationToken);
    }

    private async Task EnsureAsync(Guid eventId, CancellationToken ct)
    {
        if (await cache.HasAvailabilityAsync(eventId, ct))
        {
            return;
        }
        var zones = await db.Zones.AsNoTracking().Where(z => z.EventId == eventId)
            .Select(z => new { z.Id, z.Capacity }).ToListAsync(ct);
        if (zones.Count > 0)
        {
            await cache.InitializeAvailabilityAsync(eventId, zones.Select(z => (z.Id, z.Capacity)).ToList(), ct);
        }
    }
}
