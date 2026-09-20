using Microsoft.AspNetCore.SignalR;
using TicketNow.ServiceDefaults;

namespace TicketNow.QueueService;

public sealed record AdmissionResult(string SessionId, string AdmissionToken, DateTimeOffset ExpiresAt);

/// <summary>
/// Loop de admisión (docs/03 §1): cada segundo decide cuántos entran.
/// Salud = dependencias OK (Redis + inventory listo); la tasa K vive en Redis
/// para que N réplicas converjan. Puertas: capacidad (cupo) y tasa (K).
/// </summary>
public sealed class AdmissionService
{
    private readonly QueueStore _store;
    private readonly OnsaleConfigProvider _config;
    private readonly AdmissionTokenService _tokens;
    private readonly IHubContext<QueueHub> _hub;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AdmissionService> _log;
    private int _tick;

    public AdmissionService(
        QueueStore store,
        OnsaleConfigProvider config,
        AdmissionTokenService tokens,
        IHubContext<QueueHub> hub,
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<AdmissionService> log)
    {
        _store = store;
        _config = config;
        _tokens = tokens;
        _hub = hub;
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _log = log;
    }

    public async Task RunCycleAsync(CancellationToken ct)
    {
        var tick = Interlocked.Increment(ref _tick);
        var onsales = await _store.ActiveOnsalesAsync();
        foreach (var onsaleId in onsales)
        {
            try
            {
                await RunOnsaleAsync(onsaleId, tick, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogError(ex, "ciclo de admisión falló para {Onsale} (sigue con el resto)", onsaleId);
            }
        }
    }

    private async Task RunOnsaleAsync(string onsaleId, int tick, CancellationToken ct)
    {
        var config = await _config.ResolveAsync(onsaleId, ct);
        if (config is null || !config.Configured)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (config.OpensAt is not null && now < config.OpensAt)
        {
            return; // pre-onsale: la fila junta gente pero no admite
        }
        if (config.ClosesAt is not null && now >= config.ClosesAt)
        {
            await CloseIfDrainedAsync(onsaleId, config);
            return;
        }

        // Vencimiento de leases inline (rápido): los slots se liberan solos.
        await _store.ReapLeasesAsync(onsaleId);

        var healthy = await CheckInventoryHealthAsync(ct);
        var stored = await _store.GetRateAsync(onsaleId, config.InitialRate);
        var rate = AdmissionRate.Compute(stored, healthy, config.MinRate, config.MaxRate);
        if (rate != stored)
        {
            await _store.SetRateAsync(onsaleId, rate);
        }

        int admitted;
        long waiting;
        if (!config.RequiresQueue)
        {
            // Free-pass: sin fila, todos los que llegan entran (ADR-010).
            admitted = 0;
            waiting = await _store.WaitingCountAsync(onsaleId);
        }
        else
        {
            admitted = await _store.AdmittedCountAsync(onsaleId);
            waiting = await _store.WaitingCountAsync(onsaleId);
        }

        var capacityLeft = Math.Max(0, config.MaxConcurrentInside - admitted);
        var n = (int)Math.Min(Math.Min(rate, capacityLeft), waiting);
        if (n > 0)
        {
            var batch = await _store.PopWaitingAsync(onsaleId, n);
            foreach (var entry in batch)
            {
                await AdmitAsync(onsaleId, entry.Element.ToString(), config, ct);
            }
        }

        // Push de posiciones cada 2 ticks (~2 s) y solo si cambió.
        if (tick % 2 == 0 && config.RequiresQueue)
        {
            await PushPositionsAsync(onsaleId, rate, config, ct);
        }
    }

    private async Task AdmitAsync(string onsaleId, string sessionId, OnsaleConfig config, CancellationToken ct)
    {
        var eventId = onsaleId; // en Fase 4, onsaleId = eventId del catálogo
        var token = _tokens.Issue(sessionId, onsaleId, eventId, TimeSpan.FromMinutes(5));
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(5);
        await _store.AdmitAsync(onsaleId, sessionId, leaseSeconds: 60);
        var connectionId = await _store.GetConnectionAsync(sessionId);
        if (connectionId is not null)
        {
            await _hub.Clients.Client(connectionId).SendAsync("OnAdmitted", new
            {
                admissionToken = token,
                expiresAt,
            }, ct);
        }
        _log.LogInformation("admitido {Session} en {Onsale} (tasa {Rate})", sessionId, onsaleId, await _store.GetRateAsync(onsaleId, config.InitialRate));
    }

    private async Task PushPositionsAsync(string onsaleId, int rate, OnsaleConfig config, CancellationToken ct)
    {
        var snapshot = await _store.WaitingSnapshotAsync(onsaleId);
        var perSecond = Math.Max(1, rate);
        var tasks = new List<Task>(snapshot.Count);
        for (var i = 0; i < snapshot.Count; i++)
        {
            var (sessionId, _) = snapshot[i];
            var position = i + 1;
            tasks.Add(PushOneAsync(onsaleId, sessionId, position, snapshot.Count, perSecond, config, ct));
        }
        await Task.WhenAll(tasks);
    }

    private async Task PushOneAsync(string onsaleId, string sessionId, int position, int waiting, int perSecond, OnsaleConfig config, CancellationToken ct)
    {
        try
        {
            var last = await _store.GetLastPositionAsync(sessionId);
            if (last == position)
            {
                return;
            }
            await _store.SetLastPositionAsync(sessionId, position);
            var connectionId = await _store.GetConnectionAsync(sessionId);
            if (connectionId is null)
            {
                return; // polling-REST: lee con GET /api/queue/me
            }
            var eta = (int)Math.Ceiling((double)(position - 1) / perSecond);
            await _hub.Clients.Client(connectionId).SendAsync("OnPositionChanged", new
            {
                position,
                etaSeconds = eta,
                aheadCount = position - 1,
            }, ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "push de posición falló para {Session}", sessionId);
        }
    }

    private async Task<bool> CheckInventoryHealthAsync(CancellationToken ct)
    {
        // Fail-open: si el monitor no responde, se mantiene la tasa (no se castiga
        // al sistema por una falla del observador). Solo un 503 explícito frena.
        try
        {
            var client = _httpClientFactory.CreateClient("inventory-health");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            var response = await client.GetAsync("/health/ready", timeout.Token);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "health de inventory no disponible; se mantiene tasa");
            return true;
        }
    }

    private async Task CloseIfDrainedAsync(string onsaleId, OnsaleConfig config)
    {
        var waiting = await _store.WaitingCountAsync(onsaleId);
        var admitted = await _store.AdmittedCountAsync(onsaleId);
        if (waiting == 0 && admitted == 0)
        {
            await _store.UntrackOnsaleAsync(onsaleId);
            _config.Invalidate(onsaleId);
            _log.LogInformation("onsale {Onsale} drenado y cerrado", onsaleId);
            return;
        }
        // Cerrado pero con gente adentro: seguir admitiendo el remanente a tasa mínima.
        var rate = await _store.GetRateAsync(onsaleId, config.MinRate);
        var n = (int)Math.Min(rate, await _store.WaitingCountAsync(onsaleId));
        if (n > 0)
        {
            var batch = await _store.PopWaitingAsync(onsaleId, n);
            foreach (var entry in batch)
            {
                await AdmitAsync(onsaleId, entry.Element.ToString(), config, CancellationToken.None);
            }
        }
    }
}
