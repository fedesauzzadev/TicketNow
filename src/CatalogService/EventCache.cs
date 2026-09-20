using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace TicketNow.CatalogService;

/// <summary>
/// Cache-aside sobre Redis con single-flight (docs/03-patrones.md §8):
/// una sola reconstrucción por clave; el resto espera. Si Redis no está
/// disponible, las lecturas caen directo al origen (degradación elegante).
/// </summary>
public interface IEventCache
{
    Task<T?> GetOrSetAsync<T>(string key, TimeSpan ttl, Func<CancellationToken, Task<T?>> factory, CancellationToken cancellationToken = default) where T : class;

    Task InvalidateEventAsync(Guid eventId);

    Task InvalidateListingsAsync();

    /// <summary>Proyección de disponibilidad (docs/03 §8): libres por zona (-1 = sin datos).</summary>
    Task<Dictionary<Guid, int>> GetFreeAsync(Guid eventId, IEnumerable<Guid> zoneIds, CancellationToken cancellationToken = default);

    Task InitializeAvailabilityAsync(Guid eventId, IReadOnlyList<(Guid ZoneId, int Capacity)> zones, CancellationToken cancellationToken = default);

    Task ApplyAvailabilityDeltaAsync(Guid eventId, Guid zoneId, int delta, CancellationToken cancellationToken = default);

    Task<bool> HasAvailabilityAsync(Guid eventId, CancellationToken cancellationToken = default);
}

public sealed class NullEventCache : IEventCache
{
    public Task<T?> GetOrSetAsync<T>(string key, TimeSpan ttl, Func<CancellationToken, Task<T?>> factory, CancellationToken cancellationToken = default)
        where T : class => factory(cancellationToken);

    public Task InvalidateEventAsync(Guid eventId) => Task.CompletedTask;

    public Task InvalidateListingsAsync() => Task.CompletedTask;

    public Task<Dictionary<Guid, int>> GetFreeAsync(Guid eventId, IEnumerable<Guid> zoneIds, CancellationToken cancellationToken = default) =>
        Task.FromResult(zoneIds.ToDictionary(z => z, _ => -1));

    public Task InitializeAvailabilityAsync(Guid eventId, IReadOnlyList<(Guid ZoneId, int Capacity)> zones, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task ApplyAvailabilityDeltaAsync(Guid eventId, Guid zoneId, int delta, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<bool> HasAvailabilityAsync(Guid eventId, CancellationToken cancellationToken = default) => Task.FromResult(false);
}

public sealed class RedisEventCache : IEventCache
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly string? _connectionString;
    private readonly ILogger<RedisEventCache> _logger;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _singleFlightGates = new();
    private readonly object _connectionLock = new();
    private ConnectionMultiplexer? _multiplexer;

    public RedisEventCache(IConfiguration configuration, ILogger<RedisEventCache> logger)
    {
        _logger = logger;
        _connectionString = configuration.GetConnectionString("Redis");
        if (string.IsNullOrWhiteSpace(_connectionString))
        {
            _logger.LogWarning("ConnectionStrings:Redis no configurado: cache deshabilitada (solo origen)");
        }
    }

    /// <summary>
    /// Conexión perezosa con auto-recuperación: si Redis cae o aún no está listo
    /// al arrancar, cada operación reintenta conectar en vez de quedar muerta.
    /// </summary>
    private ConnectionMultiplexer? Multiplexer
    {
        get
        {
            if (_multiplexer is not null && _multiplexer.IsConnected)
            {
                return _multiplexer;
            }
            lock (_connectionLock)
            {
                if (_multiplexer is not null && _multiplexer.IsConnected)
                {
                    return _multiplexer;
                }
                if (string.IsNullOrWhiteSpace(_connectionString))
                {
                    return null;
                }
                try
                {
                    try { _multiplexer?.Dispose(); } catch { /* best effort */ }
                    _multiplexer = ConnectionMultiplexer.Connect(_connectionString);
                    _logger.LogInformation("conectado a Redis");
                    return _multiplexer;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Redis no disponible: se reintentará en la próxima operación");
                    return null;
                }
            }
        }
    }

    public async Task<T?> GetOrSetAsync<T>(string key, TimeSpan ttl, Func<CancellationToken, Task<T?>> factory, CancellationToken cancellationToken = default)
        where T : class
    {
        var mux = Multiplexer;
        if (mux is null)
        {
            return await factory(cancellationToken);
        }

        var database = mux.GetDatabase();

        try
        {
            var cached = await database.StringGetAsync(key);
            if (cached.HasValue)
            {
                return JsonSerializer.Deserialize<T>((string)cached!, Json);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "lectura de cache falló para {CacheKey}; yendo al origen", key);
            return await factory(cancellationToken);
        }

        // Single-flight por clave: dentro de esta instancia, solo un request reconstruye.
        var gate = _singleFlightGates.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            try
            {
                var again = await database.StringGetAsync(key);
                if (again.HasValue)
                {
                    return JsonSerializer.Deserialize<T>((string)again!, Json);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "re-lectura de cache falló para {CacheKey}", key);
            }

            var value = await factory(cancellationToken);

            if (value is not null)
            {
                try
                {
                    await database.StringSetAsync(key, JsonSerializer.Serialize(value, Json), ttl);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "escritura de cache falló para {CacheKey} (el valor se devuelve igual)", key);
                }
            }

            return value;
        }
        finally
        {
            gate.Release();
        }
    }

    public Task InvalidateEventAsync(Guid eventId)
    {
        _logger.LogInformation("invalidando cache del evento {EventId}", eventId);
        return DeletePatternAsync($"catalog:event:{eventId}*");
    }

    public Task InvalidateListingsAsync() => DeletePatternAsync("catalog:events:*");

    private static string AvailabilityKey(Guid eventId) => $"catalog:availability:{eventId:N}";

    public async Task<Dictionary<Guid, int>> GetFreeAsync(Guid eventId, IEnumerable<Guid> zoneIds, CancellationToken cancellationToken = default)
    {
        var ids = zoneIds.ToList();
        var mux = Multiplexer;
        if (mux is null)
        {
            return ids.ToDictionary(z => z, _ => -1);
        }

        try
        {
            var fields = ids.Select(z => (RedisValue)z.ToString("N")).ToArray();
            var values = await mux.GetDatabase().HashGetAsync(AvailabilityKey(eventId), fields);
            return ids.Zip(values, (id, value) => (id, free: value.HasValue && int.TryParse((string?)value, out var n) ? n : -1))
                .ToDictionary(x => x.id, x => x.free);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "lectura de disponibilidad falló para {EventId}", eventId);
            return ids.ToDictionary(z => z, _ => -1);
        }
    }

    public async Task InitializeAvailabilityAsync(Guid eventId, IReadOnlyList<(Guid ZoneId, int Capacity)> zones, CancellationToken cancellationToken = default)
    {
        var mux = Multiplexer;
        if (mux is null)
        {
            return;
        }
        try
        {
            var database = mux.GetDatabase();
            foreach (var (zoneId, capacity) in zones)
            {
                await database.HashSetAsync(AvailabilityKey(eventId), zoneId.ToString("N"), capacity, when: When.NotExists);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "init de disponibilidad falló para {EventId}", eventId);
        }
    }

    public async Task ApplyAvailabilityDeltaAsync(Guid eventId, Guid zoneId, int delta, CancellationToken cancellationToken = default)
    {
        var mux = Multiplexer;
        if (mux is null)
        {
            return;
        }
        try
        {
            // INCREMENT con delta signado (granted=-qty, released/expired=+qty).
            // Ojo: HashDecrementAsync INVIERTE el signo (bug clásico de doble negación).
            await mux.GetDatabase().HashIncrementAsync(AvailabilityKey(eventId), zoneId.ToString("N"), delta);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "delta de disponibilidad falló para {EventId}/{ZoneId}", eventId, zoneId);
        }
    }

    public async Task<bool> HasAvailabilityAsync(Guid eventId, CancellationToken cancellationToken = default)
    {
        var mux = Multiplexer;
        if (mux is null)
        {
            return false;
        }
        try
        {
            return await mux.GetDatabase().KeyExistsAsync(AvailabilityKey(eventId));
        }
        catch
        {
            return false;
        }
    }

    private async Task DeletePatternAsync(string pattern)
    {
        var mux = Multiplexer;
        if (mux is null)
        {
            return;
        }

        try
        {
            var database = mux.GetDatabase();
            long cursor = 0;
            do
            {
                var scan = await database.ExecuteAsync("SCAN", cursor.ToString(), "MATCH", pattern, "COUNT", "100");
                var parts = (RedisResult[])scan!;
                cursor = long.Parse((string)parts[0]!);
                var keys = (RedisResult[])parts[1]!;
                if (keys.Length > 0)
                {
                    await database.KeyDeleteAsync(keys.Select(k => (RedisKey)(string)k!).ToArray());
                }
            }
            while (cursor != 0);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "invalidación de cache ({Pattern}) falló; expirará por TTL", pattern);
        }
    }
}
