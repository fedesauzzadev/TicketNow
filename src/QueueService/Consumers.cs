using MassTransit;
using TicketNow.Contracts.Events;

namespace TicketNow.QueueService;

/// <summary>
/// Fase 5: el onsale abre → habilitar la fila. Sin PG en el queue-service:
/// el handler es idempotente (invalidar caché y setear tasa inicial es
/// replay-safe), así que at-least-once del bus alcanza.
/// </summary>
public sealed class OnsaleOpenedConsumer(
    QueueStore store,
    OnsaleConfigProvider config,
    ILogger<OnsaleOpenedConsumer> log) : IConsumer<OnsaleOpened>
{
    public async Task Consume(ConsumeContext<OnsaleOpened> context)
    {
        var onsaleId = context.Message.EventId.ToString();
        config.Invalidate(onsaleId);

        // Resuelve la config fresca del catálogo y arranca la tasa en el valor
        // inicial (evita el arranque en 0 del primer tick del AdmissionLoop).
        var cfg = await config.ResolveAsync(onsaleId, context.CancellationToken);
        if (cfg is { Configured: true })
        {
            await store.SetRateAsync(onsaleId, cfg.InitialRate);
        }

        log.LogInformation(
            "fila HABILITADA para {OnsaleId} (requiresQueue={Requires}, tasa inicial={Rate}/s)",
            onsaleId, cfg?.RequiresQueue, cfg?.InitialRate);
    }
}
