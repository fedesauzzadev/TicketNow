using StackExchange.Redis;
using TicketNow.ServiceDefaults;

namespace TicketNow.QueueService;

// Estado de la fila virtual en Redis (docs/04 §3, docs/03 §1):
//  - waiting:{onsale}      ZSET member=sessionId score=llegadaUnixMs (FIFO)
//  - admitted-set:{onsale} ZSET member=sessionId score=expiración del slot (leases)
//  - seen:{onsale}         ZSET member=sessionId score=último heartbeat (abandonos)
//  - session:{id}          HASH {userId, onsaleId, connectionId, admitted, arrival, lastPosition}
//  - admitted:{onsale}     contador de slots ocupados (rápido; reconciliado)
//  - admission:{onsale}:rate  tasa K actual (observable)
//  - queue-service:onsales SET de onsales con fila alguna vez abierta
//  - queue:override:{onsale} HASH de config manual (precedencia sobre catálogo)
// Todo O(log n): sin SCAN, sin KEYS.
public sealed class QueueStore
{
    private readonly IDatabase _redis;

    public QueueStore(ConnectionMultiplexer redis) => _redis = redis.GetDatabase();

    private static string Waiting(string onsale) => $"waiting:{onsale}";
    private static string AdmittedSet(string onsale) => $"admitted-set:{onsale}";
    private static string Seen(string onsale) => $"seen:{onsale}";
    private static string Session(string session) => $"session:{session}";
    private static string Admitted(string onsale) => $"admitted:{onsale}";
    private static string Rate(string onsale) => $"admission:{onsale}:rate";
    private const string Actives = "queue-service:onsales";
    private static string Override(string onsale) => $"queue:override:{onsale}";

    public async Task TrackOnsaleAsync(string onsaleId) =>
        await _redis.SetAddAsync(Actives, onsaleId);

    public async Task<HashSet<string>> ActiveOnsalesAsync()
    {
        var members = await _redis.SetMembersAsync(Actives);
        return members.Select(m => m.ToString()).ToHashSet();
    }

    public async Task UntrackOnsaleAsync(string onsaleId) =>
        await _redis.SetRemoveAsync(Actives, onsaleId);

    public async Task<(bool Created, long ArrivalMs)> EnterAsync(string onsaleId, string sessionId, string? userId)
    {
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var sessionKey = Session(sessionId);
        var exists = await _redis.HashGetAsync(sessionKey, "onsaleId");

        // Entrar (o re-entrar) es turno nuevo: limpia una revocación previa
        // (ADR-012). Sin esto, salir y volver dejaría los tokens viejos muertos
        // para siempre dentro de su TTL.
        await _redis.KeyDeleteAsync(AdmissionChecker.RevokedKey(sessionId));

        if (exists.HasValue && (string?)exists == onsaleId)
        {
            // Reingreso dentro de la gracia: se conserva la posición original.
            await TouchAsync(onsaleId, sessionId);
            return (false, long.Parse((string)await _redis.HashGetAsync(sessionKey, "arrival")));
        }

        var entries = new HashEntry[]
        {
            new("userId", userId ?? string.Empty),
            new("onsaleId", onsaleId),
            new("connectionId", string.Empty),
            new("admitted", 0),
            new("arrival", nowMs),
            new("lastPosition", -1),
        };
        await _redis.HashSetAsync(sessionKey, entries);
        await _redis.KeyExpireAsync(sessionKey, TimeSpan.FromMinutes(5));
        await _redis.SortedSetAddAsync(Waiting(onsaleId), sessionId, nowMs);
        await _redis.SortedSetAddAsync(Seen(onsaleId), sessionId, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        await TrackOnsaleAsync(onsaleId);
        return (true, nowMs);
    }

    public async Task TouchAsync(string onsaleId, string sessionId)
    {
        var sessionKey = Session(sessionId);
        await _redis.HashSetAsync(sessionKey, "lastSeen", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        await _redis.KeyExpireAsync(sessionKey, TimeSpan.FromMinutes(5));
        await _redis.SortedSetAddAsync(Seen(onsaleId), sessionId, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    }

    public async Task SetConnectionAsync(string sessionId, string? connectionId)
    {
        await _redis.HashSetAsync(Session(sessionId), "connectionId", connectionId ?? string.Empty);
        await _redis.KeyExpireAsync(Session(sessionId), TimeSpan.FromMinutes(5));
    }

    public async Task<string?> GetConnectionAsync(string sessionId) =>
        (string?)await _redis.HashGetAsync(Session(sessionId), "connectionId") is { Length: > 0 } c ? c : null;

    public async Task<long?> PositionAsync(string onsaleId, string sessionId)
    {
        var rank = await _redis.SortedSetRankAsync(Waiting(onsaleId), sessionId);
        return rank;
    }

    public async Task<long> WaitingCountAsync(string onsaleId) =>
        await _redis.SortedSetLengthAsync(Waiting(onsaleId));

    public async Task<IReadOnlyList<(string SessionId, long ArrivalMs)>> WaitingSnapshotAsync(string onsaleId)
    {
        var entries = await _redis.SortedSetRangeByRankWithScoresAsync(Waiting(onsaleId));
        return entries.Select(e => (e.Element.ToString(), (long)e.Score)).ToList();
    }

    public async Task<SortedSetEntry[]> PopWaitingAsync(string onsaleId, long count) =>
        await _redis.SortedSetPopAsync(Waiting(onsaleId), count);

    public async Task AdmitAsync(string onsaleId, string sessionId, int leaseSeconds)
    {
        var expiry = DateTimeOffset.UtcNow.AddSeconds(leaseSeconds).ToUnixTimeSeconds();
        await _redis.HashSetAsync(Session(sessionId), "admitted", 1);
        await _redis.SortedSetAddAsync(AdmittedSet(onsaleId), sessionId, expiry);
        await _redis.StringIncrementAsync(Admitted(onsaleId));
    }

    public async Task<int> AdmittedCountAsync(string onsaleId) =>
        (int)await _redis.StringGetAsync(Admitted(onsaleId));

    /// <summary>Vence leases de slots y devuelve cuántos liberó.</summary>
    public async Task<long> ReapLeasesAsync(string onsaleId)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var expired = await _redis.SortedSetRangeByScoreAsync(AdmittedSet(onsaleId), stop: now);
        if (expired.Length == 0)
        {
            return 0;
        }
        await _redis.SortedSetRemoveAsync(AdmittedSet(onsaleId), expired);
        foreach (var member in expired)
        {
            await _redis.HashSetAsync(Session(member.ToString()), "admitted", 0);
        }
        return await _redis.StringDecrementAsync(Admitted(onsaleId), expired.Length);
    }

    /// <summary>Saca de la fila a sesiones sin heartbeat por más de la gracia.</summary>
    public async Task<long> ReapAbandonedAsync(string onsaleId, int graceSeconds)
    {
        var cutoff = DateTimeOffset.UtcNow.AddSeconds(-graceSeconds).ToUnixTimeSeconds();
        var stale = await _redis.SortedSetRangeByScoreAsync(Seen(onsaleId), stop: cutoff);
        if (stale.Length == 0)
        {
            return 0;
        }
        await _redis.SortedSetRemoveAsync(Seen(onsaleId), stale);
        await _redis.SortedSetRemoveAsync(Waiting(onsaleId), stale);
        return stale.Length;
    }

    /// <summary>Auto-reparación del contador desde el set autoritativo.</summary>
    public async Task ReconcileCounterAsync(string onsaleId)
    {
        var real = await _redis.SortedSetLengthAsync(AdmittedSet(onsaleId));
        await _redis.StringSetAsync(Admitted(onsaleId), real);
    }

    public async Task LeaveAsync(string onsaleId, string sessionId, bool wasAdmitted)
    {
        await _redis.SortedSetRemoveAsync(Waiting(onsaleId), sessionId);
        await _redis.SortedSetRemoveAsync(Seen(onsaleId), sessionId);
        if (wasAdmitted)
        {
            await _redis.SortedSetRemoveAsync(AdmittedSet(onsaleId), sessionId);
            await _redis.StringDecrementAsync(Admitted(onsaleId));
            await _redis.HashSetAsync(Session(sessionId), "admitted", 0);
        }
        // Salir = perder el turno (ADR-012): los tokens ya emitidos para esta
        // sesión mueren acá, aunque su firma siga válida hasta expirar.
        await _redis.StringSetAsync(
            AdmissionChecker.RevokedKey(sessionId), "1", AdmissionChecker.RevokedTtl);
    }

    public async Task<int> GetRateAsync(string onsaleId, int fallback)
    {
        var raw = await _redis.StringGetAsync(Rate(onsaleId));
        return raw.HasValue && int.TryParse((string?)raw, out var k) ? k : fallback;
    }

    public async Task SetRateAsync(string onsaleId, int rate) =>
        await _redis.StringSetAsync(Rate(onsaleId), rate);

    public async Task SetLastPositionAsync(string sessionId, long position) =>
        await _redis.HashSetAsync(Session(sessionId), "lastPosition", position);

    public async Task<long> GetLastPositionAsync(string sessionId)
    {
        var raw = await _redis.HashGetAsync(Session(sessionId), "lastPosition");
        return raw.HasValue && long.TryParse((string?)raw, out var p) ? p : -1;
    }

    public async Task<bool> IsAdmittedAsync(string sessionId) =>
        (string?)await _redis.HashGetAsync(Session(sessionId), "admitted") == "1";

    public async Task<string?> SessionOnsaleAsync(string sessionId)
    {
        var onsale = await _redis.HashGetAsync(Session(sessionId), "onsaleId");
        var value = (string?)onsale;
        return string.IsNullOrEmpty(value) ? null : value;
    }

    public async Task SetOverrideAsync(string onsaleId, int? maxConcurrent, int? initialRate, int? minRate, int? maxRate, bool? requiresQueue = null, int? powDifficulty = null)
    {
        var entries = new List<HashEntry>();
        if (maxConcurrent is not null) entries.Add(new("maxConcurrent", maxConcurrent.Value));
        if (initialRate is not null) entries.Add(new("initialRate", initialRate.Value));
        if (minRate is not null) entries.Add(new("minRate", minRate.Value));
        if (maxRate is not null) entries.Add(new("maxRate", maxRate.Value));
        if (requiresQueue is not null) entries.Add(new("requiresQueue", requiresQueue.Value ? "true" : "false"));
        if (powDifficulty is not null) entries.Add(new("powDifficulty", powDifficulty.Value));
        if (entries.Count > 0)
        {
            await _redis.HashSetAsync(Override(onsaleId), entries.ToArray());
            await TrackOnsaleAsync(onsaleId);
        }
    }

    public async Task ClearOverrideAsync(string onsaleId) =>
        await _redis.KeyDeleteAsync(Override(onsaleId));

    public async Task<Dictionary<string, string>> GetOverrideAsync(string onsaleId)
    {
        var entries = await _redis.HashGetAllAsync(Override(onsaleId));
        return entries.ToDictionary(e => e.Name.ToString(), e => e.Value.ToString());
    }

    // --- Proof-of-work (Fase 6): challenges de un solo uso, TTL 2 min ---

    private static string PowChallenge(string onsale, string challengeId) => $"pow:{onsale}:{challengeId}";

    public const int PowChallengeTtlSeconds = 120;

    public async Task<string> IssueChallengeAsync(string onsaleId)
    {
        var challengeId = Guid.NewGuid().ToString("N");
        await _redis.StringSetAsync(PowChallenge(onsaleId, challengeId), "1", TimeSpan.FromSeconds(PowChallengeTtlSeconds));
        return challengeId;
    }

    /// <summary>Consume el challenge (un solo uso) y valida el nonce.</summary>
    public async Task<bool> ConsumeChallengeAsync(string onsaleId, string challengeId, string nonce, int difficulty)
    {
        var key = PowChallenge(onsaleId, challengeId);
        var exists = await _redis.KeyDeleteAsync(key); // GETDEL manual: si no existía, expiró o ya se usó
        if (!exists)
        {
            return false;
        }
        return ProofOfWork.Verify(challengeId, nonce, difficulty);
    }
}
