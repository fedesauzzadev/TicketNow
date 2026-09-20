using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace TicketNow.InventoryService;

/// <summary>Sweeper de holds expirados (docs/03 §5). Nunca tumba el host: cada ciclo está aislado.</summary>
public sealed class HoldSweeper(IServiceScopeFactory scopes, IConfiguration config, ILogger<HoldSweeper> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(config.GetValue("Inventory:SweeperIntervalSeconds", 5));
        log.LogInformation("sweeper iniciado (intervalo {Interval}s)", interval.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, stoppingToken);
                using var scope = scopes.CreateScope();
                var released = await scope.ServiceProvider.GetRequiredService<HoldService>().SweepExpiredAsync(stoppingToken);
                if (released > 0)
                {
                    log.LogInformation("sweeper liberó {Count} holds expirados", released);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                log.LogError(ex, "sweeper falló un ciclo (reintentará)");
            }
        }
    }
}

/// <summary>Reconciliador continuo (docs/03 §11, métrica `inventory_divergence_total`).</summary>
public sealed class ReconcilerWorker(IServiceScopeFactory scopes, IConfiguration config, ILogger<ReconcilerWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(config.GetValue("Inventory:ReconcilerIntervalSeconds", 30));
        log.LogInformation("reconciliador iniciado (intervalo {Interval}s)", interval.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, stoppingToken);
                using var scope = scopes.CreateScope();
                var report = await scope.ServiceProvider.GetRequiredService<InventoryReconciler>().ReconcileAsync(stoppingToken);
                if (!report.Skipped && (report.OrphansHealed > 0 || report.Backstopped > 0 || report.Repaired > 0))
                {
                    log.LogWarning(
                        "reconciliador: zonas={Zones} curados={Healed} backstop={Backstopped} reparados={Repaired}",
                        report.ZonesChecked, report.OrphansHealed, report.Backstopped, report.Repaired);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                log.LogError(ex, "reconciliador falló un ciclo (reintentará)");
            }
        }
    }
}
