using System.Collections.Concurrent;
using System.Net.Http.Json;

namespace TicketNow.QueueService;

public sealed record OnsaleConfig(
    bool Configured,
    DateTimeOffset? OpensAt,
    DateTimeOffset? ClosesAt,
    int MaxConcurrentInside,
    int InitialRate,
    int MinRate,
    int MaxRate,
    bool RequiresQueue,
    int PowDifficulty);

/// <summary>
/// Configuración del onsale para la fila: override manual (incidentes) con
/// precedencia, si no catálogo vía HTTP con caché local de 30 s (docs/02 §4).
/// El cableado automático vía evento OnsaleOpened queda para Fase 5.
/// </summary>
public sealed class OnsaleConfigProvider
{
    private readonly record struct Cached(OnsaleConfig Config, DateTimeOffset At);

    private readonly HttpClient _catalog;
    private readonly QueueStore _store;
    private readonly ILogger<OnsaleConfigProvider> _log;
    private readonly ConcurrentDictionary<string, Cached> _cache = new();
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);

    public OnsaleConfigProvider(IHttpClientFactory httpClientFactory, QueueStore store, ILogger<OnsaleConfigProvider> log)
    {
        _catalog = httpClientFactory.CreateClient("catalog");
        _store = store;
        _log = log;
    }

    public async Task<OnsaleConfig?> ResolveAsync(string onsaleId, CancellationToken ct)
    {
        if (_cache.TryGetValue(onsaleId, out var cached) && DateTimeOffset.UtcNow - cached.At < CacheTtl)
        {
            return cached.Config;
        }

        int? maxConcurrent = null;
        int? initial = null;
        int? min = null;
        int? max = null;
        bool? requiresQueue = null;
        int? pow = null;
        try
        {
            var manual = await _store.GetOverrideAsync(onsaleId);
            if (manual.TryGetValue("maxConcurrent", out var mc) && int.TryParse(mc, out var mcv)) maxConcurrent = mcv;
            if (manual.TryGetValue("initialRate", out var ir) && int.TryParse(ir, out var irv)) initial = irv;
            if (manual.TryGetValue("minRate", out var mr) && int.TryParse(mr, out var mrv)) min = mrv;
            if (manual.TryGetValue("maxRate", out var xr) && int.TryParse(xr, out var xrv)) max = xrv;
            if (manual.TryGetValue("requiresQueue", out var rq) && bool.TryParse(rq, out var rqv)) requiresQueue = rqv;
            if (manual.TryGetValue("powDifficulty", out var pw) && int.TryParse(pw, out var pwv)) pow = pwv;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "leyendo override de fila para {Onsale}", onsaleId);
        }

        CatalogOnsaleConfig? remote = null;
        try
        {
            remote = await _catalog.GetFromJsonAsync<CatalogOnsaleConfig>($"/api/events/{onsaleId}/onsale-config", ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "catálogo no disponible para config de {Onsale}", onsaleId);
        }

        OnsaleConfig? resolved = null;
        if (remote is { Configured: true })
        {
            resolved = new OnsaleConfig(
                true, remote.OpensAt, remote.ClosesAt,
                maxConcurrent ?? remote.MaxConcurrentInside,
                initial ?? remote.AdmissionRatePerSec,
                min ?? Math.Max(1, remote.AdmissionRatePerSec / 10),
                max ?? remote.AdmissionRatePerSec,
                requiresQueue ?? remote.RequiresQueue,
                pow ?? remote.PowDifficulty);
        }
        else if (maxConcurrent.HasValue)
        {
            // Solo override (útil en tests e incidentes): se asume abierto.
            resolved = new OnsaleConfig(
                true, null, null, maxConcurrent.Value,
                initial ?? 50, min ?? 1, max ?? initial ?? 50,
                requiresQueue ?? true, pow ?? 0);
        }

        if (resolved is not null)
        {
            _cache[onsaleId] = new Cached(resolved, DateTimeOffset.UtcNow);
            return resolved;
        }
        return cached.Config; // lo último conocido (o null si nunca hubo)
    }

    public void Invalidate(string onsaleId) => _cache.TryRemove(onsaleId, out _);

    private sealed record CatalogOnsaleConfig(
        bool Configured,
        DateTimeOffset? OpensAt,
        DateTimeOffset? ClosesAt,
        int MaxConcurrentInside,
        int AdmissionRatePerSec,
        int MaxPerAccount,
        bool RequiresQueue,
        int PowDifficulty);
}

/// <summary>
/// Núcleo de admisión (docs/03 §1 y §9). La tasa K se adapta con histéresis:
/// salud degradada → se parte a la mitad (hasta el mínimo); salud OK →
/// se recupera +10% por ciclo (hasta el máximo). Función pura para tests.
/// </summary>
public static class AdmissionRate
{
    public static int Compute(int current, bool healthy, int min, int max)
    {
        if (!healthy)
        {
            return Math.Max(min, current / 2);
        }
        if (current >= max)
        {
            return max;
        }
        return Math.Min(max, current + Math.Max(1, current / 10));
    }
}
