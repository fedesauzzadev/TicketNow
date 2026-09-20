namespace TicketNow.InventoryService;

// Claves Redis del hot path (docs/04-datos-y-apis.md §3) y scripts Lua atómicos (docs/03 §2).

internal static class RedisKeys
{
    public static string Stock(Guid eventId, Guid zoneId) => $"stock:{eventId:N}:{zoneId:N}";
    public static string Holds(Guid eventId) => $"holds:{eventId:N}";
    public static string Hold(Guid holdId) => $"hold:{holdId:N}";
    public static string Zones(Guid eventId) => $"event:{eventId:N}:zones";
    public const string Events = "inventory:events";
    public static string Idem(string key) => $"idem:{key.Trim()}";
}

internal static class LuaScripts
{
    /// <summary>
    /// Reserva atómica: chequeo + decremento + registro del hold, todo o nada.
    /// KEYS: stock, holds zset, hold hash. ARGV: holdId, qty, expiraAtUnix, ttlSeg, payloadJson.
    /// Devuelve 1 (otorgado) o 0 (sin stock).
    /// </summary>
    public const string Hold = """
        local stock = tonumber(redis.call('GET', KEYS[1]) or '-1')
        local qty = tonumber(ARGV[2])
        if stock < qty then return 0 end
        redis.call('DECRBY', KEYS[1], qty)
        redis.call('ZADD', KEYS[2], ARGV[3], ARGV[1])
        redis.call('SET', KEYS[3], ARGV[5], 'EX', ARGV[4])
        return 1
        """;

    /// <summary>
    /// Liberación: si el hold estaba activo lo saca del set y restaura stock.
    /// KEYS: stock, holds zset, hold hash. ARGV: holdId, qty.
    /// Devuelve 1 (stock restaurado) o 0 (ya no estaba: idempotente).
    /// </summary>
    public const string Release = """
        local removed = redis.call('ZREM', KEYS[2], ARGV[1])
        if removed == 0 then return 0 end
        redis.call('INCRBY', KEYS[1], ARGV[2])
        redis.call('DEL', KEYS[3])
        return 1
        """;

    /// <summary>
    /// Confirmación: si el hold sigue activo lo saca del set (puerta autoritativa).
    /// KEYS: holds zset, hold hash. ARGV: holdId.
    /// Devuelve 1 (confirmado) o 0 (ya no estaba activo).
    /// </summary>
    public const string Confirm = """
        if redis.call('ZREM', KEYS[1], ARGV[1]) == 0 then return 0 end
        redis.call('DEL', KEYS[2])
        return 1
        """;
}
