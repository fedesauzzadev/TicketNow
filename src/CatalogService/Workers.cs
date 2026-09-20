using MassTransit;
using Microsoft.EntityFrameworkCore;
using TicketNow.Contracts.Events;

namespace TicketNow.CatalogService;

/// <summary>
/// Abre los onsales cuya hora llegó (Fase 5): marca OpenedAt y publica
/// OnsaleOpened con las zonas (id, capacidad) para que inventory siembre el
/// stock y queue habilite la fila. El estado "onsale" que ve el cliente sigue
/// computándose en lectura; esta pieza persiste la TRANSICIÓN para emitir el
/// evento exactamente una vez (replay-safe: OpenedAt es la marca de agua).
/// Publish + SaveChanges en la misma transacción (outbox, ADR-007).
/// </summary>
public sealed class OnsaleOpenerWorker(
    IServiceScopeFactory scopes,
    IConfiguration config,
    ILogger<OnsaleOpenerWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(config.GetValue("Catalog:OnsaleOpenerIntervalSeconds", 5.0));
        using var timer = new PeriodicTimer(interval);
        while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await OpenDueAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                log.LogError(ex, "ciclo del opener de onsales falló; se reintenta en el próximo tick");
            }
        }
    }

    private async Task OpenDueAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        var publish = scope.ServiceProvider.GetRequiredService<IPublishEndpoint>();
        var now = DateTimeOffset.UtcNow;

        var due = await db.Events
            .Include(e => e.Onsale)
            .Include(e => e.Zones)
            .Where(e => e.Onsale != null
                && e.Onsale.OpenedAt == null
                && e.Onsale.OpensAt <= now
                && e.Status == EventStatus.Announced)
            .OrderBy(e => e.Onsale!.OpensAt)
            .Take(50)
            .ToListAsync(ct);

        foreach (var @event in due)
        {
            var onsale = @event.Onsale!;
            onsale.OpenedAt = now;

            // Outbox: el evento viaja en la misma transacción que la marca de agua.
            await publish.Publish(new OnsaleOpened(
                @event.Id,
                onsale.OpensAt,
                @event.Zones
                    .OrderBy(z => z.SortOrder)
                    .Select(z => new OnsaleZone(z.Id, z.Name, z.Capacity, z.Price))
                    .ToList(),
                onsale.MaxPerAccount), ct);
            await db.SaveChangesAsync(ct);

            log.LogInformation(
                "onsale ABIERTO para {EventId} ({Zones} zonas, {TotalCapacity} lugares, abre={OpensAt})",
                @event.Id, @event.Zones.Count, @event.Zones.Sum(z => z.Capacity), onsale.OpensAt);
        }
    }
}
