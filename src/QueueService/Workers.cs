using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace TicketNow.QueueService;

/// <summary>Loop de admisión cada 1 s (docs/03 §1).</summary>
public sealed class AdmissionLoop(IServiceScopeFactory scopes, IConfiguration config, ILogger<AdmissionLoop> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(config.GetValue("Queue:AdmissionIntervalSeconds", 1));
        log.LogInformation("admission loop iniciado (intervalo {Interval}s)", interval.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, stoppingToken);
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<AdmissionService>().RunCycleAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                log.LogError(ex, "ciclo de admisión falló (sigue)");
            }
        }
    }
}

/// <summary>
/// Reaper cada 10 s: abandonos (sin heartbeat > gracia), reconciliación del
/// contador de admitidos y poda de onsales drenados.
/// </summary>
public sealed class QueueReaper(IServiceScopeFactory scopes, IConfiguration config, ILogger<QueueReaper> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(config.GetValue("Queue:ReaperIntervalSeconds", 10));
        var grace = config.GetValue("Queue:ReconnectGraceSeconds", 60);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, stoppingToken);
                using var scope = scopes.CreateScope();
                var store = scope.ServiceProvider.GetRequiredService<QueueStore>();
                foreach (var onsaleId in await store.ActiveOnsalesAsync())
                {
                    var abandoned = await store.ReapAbandonedAsync(onsaleId, grace);
                    if (abandoned > 0)
                    {
                        log.LogInformation("reaper: {Count} abandonos en {Onsale}", abandoned, onsaleId);
                    }
                    await store.ReconcileCounterAsync(onsaleId);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                log.LogError(ex, "reaper falló un ciclo (sigue)");
            }
        }
    }
}
