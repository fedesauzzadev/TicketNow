using System.Text.Json;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using TicketNow.Contracts.Events;

namespace TicketNow.InventoryService;

public sealed record HoldCreated(Guid HoldId, Guid EventId, Guid ZoneId, int Qty, DateTimeOffset ExpiresAt, bool Replayed);

public abstract record CreateHoldResult;
public sealed record HoldAccepted(HoldCreated Hold) : CreateHoldResult;
public sealed record HoldRejected(string Reason) : CreateHoldResult; // 409: sin stock
public sealed record HoldInvalid(string Reason) : CreateHoldResult; // 400/404: validación

public abstract record ReleaseHoldResult;
public sealed record HoldReleased(Guid HoldId, HoldOutcome Outcome) : ReleaseHoldResult;
public sealed record HoldNotFound(Guid HoldId) : ReleaseHoldResult;
public sealed record HoldForbidden(Guid HoldId) : ReleaseHoldResult;

/// <summary>
/// Corazón del inventario: reservas atómicas en Redis + ledger en PostgreSQL.
///
/// Orden de operaciones (deliberado, docs/03 §2.1): Redis primero (puerta rápida),
/// ledger después. Si PG falla tras el Lua, se compensa en Redis y se falla limpio:
/// jamás queda stock decrementado sin su fila de ledger (el sweeper y el
/// reconciliador cubren la ventana residual de un crash entre ambas escrituras).
/// </summary>
public sealed class HoldService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan IdempotencyTtl = TimeSpan.FromHours(24);

    // Margen: el hash de Redis sobrevive al TTL del hold para que el sweeper
    // siempre pueda leer qty/dueño al expirar (docs/03 §5).
    private const int HashTtlMarginSeconds = 300;

    private readonly InventoryDbContext _db;
    private readonly IDatabase _redis;
    private readonly IConfiguration _config;
    private readonly IPublishEndpoint _publish;
    private readonly ILogger<HoldService> _log;

    public HoldService(InventoryDbContext db, ConnectionMultiplexer redis, IConfiguration config, IPublishEndpoint publish, ILogger<HoldService> log)
    {
        _db = db;
        _redis = redis.GetDatabase();
        _config = config;
        _publish = publish;
        _log = log;
    }

    private int HoldTtlSeconds => _config.GetValue("Inventory:HoldTtlSeconds", 480);
    private int MaxQtyPerHold => _config.GetValue("Inventory:MaxQtyPerHold", 4);

    /// <summary>
    /// Siembra el stock de un evento (idempotente: replay-safe). En Fase 3 esto lo
    /// dispara el evento OnsaleOpened vía MassTransit; en Fase 2 es un endpoint admin.
    /// </summary>
    public async Task<(int Seeded, int Skipped)> SeedAsync(Guid eventId, IReadOnlyList<(Guid ZoneId, int Capacity)> zones, CancellationToken ct)
    {
        var seeded = 0;
        foreach (var (zoneId, capacity) in zones)
        {
            if (await _redis.StringSetAsync(RedisKeys.Stock(eventId, zoneId), capacity, when: When.NotExists))
            {
                seeded++;
            }
            await _redis.HashSetAsync(RedisKeys.Zones(eventId), zoneId.ToString("N"), capacity);
        }
        await _redis.SetAddAsync(RedisKeys.Events, eventId.ToString("N"));
        _log.LogInformation("stock sembrado para evento {EventId}: {Seeded} zonas nuevas", eventId, seeded);
        return (seeded, zones.Count - seeded);
    }

    public async Task<CreateHoldResult> CreateHoldAsync(
        Guid eventId, Guid zoneId, string userId, int qty, string? idempotencyKey, CancellationToken ct)
    {
        if (qty < 1 || qty > MaxQtyPerHold)
        {
            return new HoldInvalid($"qty debe estar entre 1 y {MaxQtyPerHold}");
        }

        if (!string.IsNullOrWhiteSpace(idempotencyKey))
        {
            var prior = await _redis.StringGetAsync(RedisKeys.Idem(idempotencyKey));
            if (prior.HasValue && Guid.TryParse((string)prior!, out var priorHoldId))
            {
                var priorHold = await ReadHoldAsync(priorHoldId, ct);
                if (priorHold is not null)
                {
                    return new HoldAccepted(priorHold with { Replayed = true });
                }
                await _redis.KeyDeleteAsync(RedisKeys.Idem(idempotencyKey));
            }
        }

        var capacity = await _redis.HashGetAsync(RedisKeys.Zones(eventId), zoneId.ToString("N"));
        if (!capacity.HasValue)
        {
            return new HoldInvalid("zona desconocida para el evento (falta seed de stock)");
        }

        var holdId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var expiresAt = now.AddSeconds(HoldTtlSeconds);
        var payload = JsonSerializer.Serialize(new { qty, user = userId, zone = zoneId }, Json);

        var granted = (long)await _redis.ScriptEvaluateAsync(LuaScripts.Hold,
            keys: new RedisKey[] { RedisKeys.Stock(eventId, zoneId), RedisKeys.Holds(eventId), RedisKeys.Hold(holdId) },
            values: new RedisValue[] { holdId.ToString("N"), qty, expiresAt.ToUnixTimeSeconds(), HoldTtlSeconds + HashTtlMarginSeconds, payload });

        if (granted == 0)
        {
            return new HoldRejected("sin stock suficiente en la zona");
        }

        try
        {
            _db.Ledger.Add(new LedgerEntry
            {
                EventId = eventId, ZoneId = zoneId, EntryType = LedgerEntryType.Hold,
                Delta = -qty, HoldId = holdId, Actor = "inventory",
            });
            _db.Holds.Add(new HoldRecord
            {
                HoldId = holdId, EventId = eventId, ZoneId = zoneId, UserId = userId,
                Qty = qty, CreatedAt = now, ExpiresAt = expiresAt,
            });
            await _db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "PG falló tras hold {HoldId} en Redis: compensando (stock devuelto)", holdId);
            await _redis.ScriptEvaluateAsync(LuaScripts.Release,
                keys: new RedisKey[] { RedisKeys.Stock(eventId, zoneId), RedisKeys.Holds(eventId), RedisKeys.Hold(holdId) },
                values: new RedisValue[] { holdId.ToString("N"), qty });
            throw;
        }

        if (!string.IsNullOrWhiteSpace(idempotencyKey))
        {
            await _redis.StringSetAsync(RedisKeys.Idem(idempotencyKey), holdId.ToString("N"), IdempotencyTtl);
        }

        // Con outbox (UseBusOutbox) la entrega es confiable; el catálogo actualiza
        // su proyección de disponibilidad (docs/03 §8).
        await _publish.Publish(new HoldGranted(holdId, eventId, zoneId, qty, expiresAt, userId), ct);

        return new HoldAccepted(new HoldCreated(holdId, eventId, zoneId, qty, expiresAt, Replayed: false));
    }

    /// <summary>
    /// Cierra un hold (liberación voluntaria, expiración del sweeper o backstop del
    /// reconciliador). Seguro ante carreras: el Lua restaura stock una sola vez
    /// (ZREM); si ya estaba cerrado se escribe fila de delta 0 para no desbalancear
    /// el ledger (docs/03 §2.1 y §11).
    /// </summary>
    public async Task<ReleaseHoldResult> ReleaseHoldAsync(
        Guid holdId, string? userId, HoldOutcome outcome, string actor, CancellationToken ct)
    {
        var record = await _db.Holds.FindAsync([holdId], ct);
        if (record is null || record.Outcome is not null)
        {
            return new HoldNotFound(holdId);
        }
        if (userId is not null && record.UserId != userId)
        {
            return new HoldForbidden(holdId);
        }

        var released = (long)await _redis.ScriptEvaluateAsync(LuaScripts.Release,
            keys: new RedisKey[] { RedisKeys.Stock(record.EventId, record.ZoneId), RedisKeys.Holds(record.EventId), RedisKeys.Hold(holdId) },
            values: new RedisValue[] { holdId.ToString("N"), record.Qty });

        var entryType = outcome == HoldOutcome.Expired ? LedgerEntryType.Expire : LedgerEntryType.Release;
        record.Outcome = outcome;
        _db.Ledger.Add(new LedgerEntry
        {
            EventId = record.EventId, ZoneId = record.ZoneId, EntryType = entryType,
            Delta = released == 1 ? record.Qty : 0, HoldId = holdId, Actor = actor,
        });
        await _db.SaveChangesAsync(ct);

        var orderId = record.OrderId ?? Guid.Empty;
        if (outcome == HoldOutcome.Expired)
        {
            await _publish.Publish(new HoldExpired(holdId, record.EventId, record.ZoneId, record.Qty, orderId), ct);
        }
        else
        {
            await _publish.Publish(new global::TicketNow.Contracts.Events.HoldReleased(holdId, record.EventId, record.ZoneId, record.Qty, "released"), ct);
        }

        return new HoldReleased(holdId, outcome);
    }

    /// <summary>
    /// Reclamo ligero de un hold para una orden (Fase 3): el primero gana.
    /// La puerta autoritativa sigue siendo el Lua de confirmación.
    /// </summary>
    public async Task<HoldRecord?> TryClaimAsync(Guid holdId, Guid orderId, CancellationToken ct)
    {
        var record = await _db.Holds.FindAsync([holdId], ct);
        if (record is null)
        {
            _log.LogWarning("TryClaim {HoldId}: inexistente", holdId);
            return null;
        }
        if (record.Outcome is not null)
        {
            _log.LogWarning("TryClaim {HoldId}: ya cerrado ({Outcome})", holdId, record.Outcome);
            return null;
        }
        if (record.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            _log.LogWarning("TryClaim {HoldId}: expirado", holdId);
            return null;
        }
        if (record.OrderId is not null && record.OrderId != orderId)
        {
            _log.LogWarning("TryClaim {HoldId}: reclamado por {Owner} (pide {OrderId})", holdId, record.OrderId, orderId);
            return null;
        }
        if (orderId != Guid.Empty && record.OrderId is null)
        {
            record.OrderId = orderId;
            await _db.SaveChangesAsync(ct);
        }
        return record;
    }

    /// <summary>
    /// Confirmación autoritativa (puerta de la saga): si el hold sigue activo en
    /// Redis se cementa (ledger CONFIRM delta 0); si no, falla limpio y la saga
    /// compensa (reembolso). Cubre la ventana entre validación y cobro.
    /// </summary>
    public async Task<bool> ConfirmHoldAsync(Guid orderId, Guid holdId, CancellationToken ct)
    {
        var record = await _db.Holds.FindAsync([holdId], ct);
        if (record is null || record.Outcome is not null || (record.OrderId is not null && record.OrderId != orderId))
        {
            await _publish.Publish(new HoldConfirmFailed(orderId, holdId, "hold no confirmable"), ct);
            return false;
        }

        var confirmed = (long)await _redis.ScriptEvaluateAsync(LuaScripts.Confirm,
            keys: new RedisKey[] { RedisKeys.Holds(record.EventId), RedisKeys.Hold(holdId) },
            values: new RedisValue[] { holdId.ToString("N") });

        if (confirmed == 0)
        {
            await _publish.Publish(new HoldConfirmFailed(orderId, holdId, "el hold ya no está activo"), ct);
            return false;
        }

        record.Outcome = HoldOutcome.Confirmed;
        record.OrderId ??= orderId;
        _db.Ledger.Add(new LedgerEntry
        {
            EventId = record.EventId, ZoneId = record.ZoneId, EntryType = LedgerEntryType.Confirm,
            Delta = 0, HoldId = holdId, OrderId = orderId, Actor = "orders-saga",
        });
        await _db.SaveChangesAsync(ct);

        await _publish.Publish(new HoldConfirmed(orderId, holdId, record.EventId, record.ZoneId, record.Qty), ct);
        return true;
    }

    public async Task<HoldCreated?> ReadHoldAsync(Guid holdId, CancellationToken ct)
    {
        var hash = await _redis.StringGetAsync(RedisKeys.Hold(holdId));
        if (hash.HasValue)
        {
            using var doc = JsonDocument.Parse((string)hash!);
            var root = doc.RootElement;
            var record = await _db.Holds.FindAsync([holdId], ct);
            if (record is null)
            {
                return null;
            }
            return new HoldCreated(holdId, record.EventId, record.ZoneId, root.GetProperty("qty").GetInt32(), record.ExpiresAt, Replayed: false);
        }

        var closed = await _db.Holds.FindAsync([holdId], ct);
        return closed is null ? null : new HoldCreated(closed.HoldId, closed.EventId, closed.ZoneId, closed.Qty, closed.ExpiresAt, Replayed: false);
    }

    public async Task<HoldRecord?> ReadRecordAsync(Guid holdId, CancellationToken ct) =>
        await _db.Holds.FindAsync([holdId], ct);

    /// <summary>
    /// Sweeper: libera los holds cuyo TTL venció (docs/03 §5). El TTL de Redis es
    /// red de seguridad; este job es el mecanismo primario (avisa y audita).
    /// </summary>
    public async Task<int> SweepExpiredAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var released = 0;

        foreach (var entry in await _redis.SetMembersAsync(RedisKeys.Events))
        {
            var eventId = Guid.Parse(entry.ToString());
            var expired = await _redis.SortedSetRangeByScoreAsync(RedisKeys.Holds(eventId), stop: now);
            foreach (var member in expired)
            {
                var holdId = Guid.Parse(member.ToString());
                var result = await ReleaseHoldAsync(holdId, userId: null, HoldOutcome.Expired, actor: "sweeper", ct);
                if (result is HoldReleased)
                {
                    released++;
                    _log.LogInformation("hold {HoldId} expirado: stock devuelto", holdId);
                }
            }
        }

        return released;
    }
}
