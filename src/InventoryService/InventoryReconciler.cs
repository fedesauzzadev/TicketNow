using System.Diagnostics.Metrics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace TicketNow.InventoryService;

public sealed record ReconcileReport(int ZonesChecked, int OrphansHealed, int Backstopped, int Repaired, bool Skipped);

/// <summary>
/// Watchdog de INV-1/INV-2 (docs/03 §11, docs/05 §4.2 `inventory_divergence_total`).
///
/// Política en tres pasos, en este orden (el orden importa):
///  1. Curar el LEDGER desde Redis: holds activos sin HoldRecord (ventana de crash
///     entre el Lua y el INSERT) se reconstruyen desde el hash. No se toca stock.
///  2. Backstop: HoldRecords abiertos vencidos hace +5 min (el sweeper no pudo)
///     se cierran vía ReleaseHoldAsync (idempotente por ZREM + filas delta 0).
///  3. Comparar y reparar: esperado = capacidad + SUM(ledger); si Redis difiere,
///     el ledger manda (ADR-006) + contador de divergencia + log de alerta.
/// Si PG no responde, el ciclo se salta (sin la verdad no se puede decidir).
/// </summary>
public sealed class InventoryReconciler
{
    private static readonly Meter Meter = new("TicketNow.Inventory");
    private static readonly Counter<long> DivergenceCounter =
        Meter.CreateCounter<long>("inventory_divergence_total", description: "Divergencias Redis vs ledger detectadas");

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan BackstopGrace = TimeSpan.FromMinutes(5);

    private readonly InventoryDbContext _db;
    private readonly IDatabase _redis;
    private readonly HoldService _holds;
    private readonly ILogger<InventoryReconciler> _log;

    public InventoryReconciler(InventoryDbContext db, ConnectionMultiplexer redis, HoldService holds, ILogger<InventoryReconciler> log)
    {
        _db = db;
        _redis = redis.GetDatabase();
        _holds = holds;
        _log = log;
    }

    public async Task<ReconcileReport> ReconcileAsync(CancellationToken ct)
    {
        // En proveedores no relacionales (tests con InMemory) no hay probe SQL.
        if (_db.Database.IsRelational())
        {
            try
            {
                await _db.Database.ExecuteSqlRawAsync("SELECT 1", ct);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "reconciliador: PG no responde, ciclo salteado");
                return new ReconcileReport(0, 0, 0, 0, Skipped: true);
            }
        }

        var healed = 0;
        var backstopped = 0;
        var repaired = 0;
        var checkedZones = 0;

        // Paso 1: huérfanos (hold activo en Redis sin fila en PG).
        foreach (var entry in await _redis.SetMembersAsync(RedisKeys.Events))
        {
            var eventId = Guid.Parse(entry.ToString());
            var members = await _redis.SortedSetRangeByScoreAsync(RedisKeys.Holds(eventId));
            foreach (var member in members)
            {
                var holdId = Guid.Parse(member.ToString());
                if (await _db.Holds.FindAsync([holdId], ct) is not null)
                {
                    continue;
                }

                var hash = await _redis.StringGetAsync(RedisKeys.Hold(holdId));
                if (!hash.HasValue)
                {
                    _log.LogError("hold {HoldId} en ZSET sin hash ni registro: pérdida de datos, requiere revisión manual", holdId);
                    continue;
                }

                using var doc = JsonDocument.Parse((string)hash!);
                var root = doc.RootElement;
                var qty = root.GetProperty("qty").GetInt32();
                var user = root.GetProperty("user").GetString() ?? "unknown";
                var zoneId = root.GetProperty("zone").GetGuid();
                var score = await _redis.SortedSetScoreAsync(RedisKeys.Holds(eventId), member);
                var expiresAt = score.HasValue
                    ? DateTimeOffset.FromUnixTimeSeconds((long)score.Value)
                    : DateTimeOffset.UtcNow;

                _db.Holds.Add(new HoldRecord
                {
                    HoldId = holdId, EventId = eventId, ZoneId = zoneId,
                    UserId = user, Qty = qty, CreatedAt = expiresAt.AddMinutes(-8), ExpiresAt = expiresAt,
                });
                _db.Ledger.Add(new LedgerEntry
                {
                    EventId = eventId, ZoneId = zoneId,
                    EntryType = LedgerEntryType.Hold, Delta = -qty, HoldId = holdId, Actor = "reconciler(heal)",
                });
                await _db.SaveChangesAsync(ct);
                healed++;
                _log.LogWarning("hold huérfano {HoldId} reconstruido en ledger desde Redis", holdId);
            }
        }

        // Paso 2: backstop de holds vencidos que el sweeper no cerró.
        var cutoff = DateTimeOffset.UtcNow - BackstopGrace;
        var stale = await _db.Holds.Where(h => h.Outcome == null && h.ExpiresAt < cutoff).ToListAsync(ct);
        foreach (var record in stale)
        {
            var result = await _holds.ReleaseHoldAsync(record.HoldId, userId: null, HoldOutcome.Expired, actor: "reconciler(backstop)", ct);
            if (result is HoldReleased)
            {
                backstopped++;
                _log.LogWarning("backstop: hold {HoldId} cerrado por el reconciliador", record.HoldId);
            }
        }

        // Paso 3: comparar y reparar por zona.
        await foreach (var key in ScanAsync("stock:*", ct))
        {
            var parts = key.Split(':');
            if (parts.Length != 3 || !Guid.TryParseExact(parts[1], "N", out var eventId) || !Guid.TryParseExact(parts[2], "N", out var zoneId))
            {
                continue;
            }

            var capacityRaw = await _redis.HashGetAsync(RedisKeys.Zones(eventId), zoneId.ToString("N"));
            if (!capacityRaw.HasValue || !int.TryParse((string)capacityRaw!, out var capacity))
            {
                continue;
            }

            var delta = await _db.Ledger
                .Where(l => l.EventId == eventId && l.ZoneId == zoneId)
                .SumAsync(l => (int?)l.Delta, ct) ?? 0;
            var expected = capacity + delta;
            var actualRaw = await _redis.StringGetAsync(key);
            var actual = actualRaw.HasValue ? (int)actualRaw : 0;

            checkedZones++;
            if (actual != expected)
            {
                await _redis.StringSetAsync(key, expected);
                DivergenceCounter.Add(1,
                    new KeyValuePair<string, object?>("event", eventId.ToString()),
                    new KeyValuePair<string, object?>("zone", zoneId.ToString()));
                repaired++;
                _log.LogError(
                    "DIVERGENCIA zona {EventId}/{ZoneId}: redis={Actual} ledger={Expected} (capacidad {Capacity}). Reparado desde ledger.",
                    eventId, zoneId, actual, expected, capacity);
            }
        }

        return new ReconcileReport(checkedZones, healed, backstopped, repaired, Skipped: false);
    }

    private async IAsyncEnumerable<string> ScanAsync(string pattern, CancellationToken ct)
    {
        // SCAN sobre el servidor (una sola instancia en Fase 2; con cluster, por nodo).
        foreach (var endpoint in _redis.Multiplexer.GetServers())
        {
            await foreach (var key in endpoint.KeysAsync(pattern: pattern))
            {
                ct.ThrowIfCancellationRequested();
                yield return key.ToString();
            }
        }
    }
}
