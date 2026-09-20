using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;
using RabbitMQ.Client;
using StackExchange.Redis;

namespace TicketNow.ServiceDefaults;

/// <summary>Health check de Redis: ping con medición de latencia (hot path).</summary>
public sealed class RedisHealthCheck(IConfiguration configuration) : IHealthCheck
{
    private static ConnectionMultiplexer? _connection;
    private static readonly object SyncLock = new();

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var connectionString = configuration.GetConnectionString("Redis")
                ?? throw new InvalidOperationException("ConnectionStrings:Redis no configurado");

            lock (SyncLock)
            {
                _connection ??= ConnectionMultiplexer.Connect(connectionString);
            }

            var latency = _connection.GetDatabase().Ping();
            return Task.FromResult(HealthCheckResult.Healthy($"redis ping {latency.TotalMilliseconds:F1} ms"));
        }
        catch (Exception ex)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy("redis inaccesible", ex));
        }
    }
}

/// <summary>Health check de PostgreSQL: abrir conexión (valida red + credenciales).</summary>
public sealed class PostgresHealthCheck(IConfiguration configuration) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var connectionString = configuration.GetConnectionString("Postgres")
                ?? throw new InvalidOperationException("ConnectionStrings:Postgres no configurado");

            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            return HealthCheckResult.Healthy("postgres conectado");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("postgres inaccesible", ex);
        }
    }
}

/// <summary>Health check de RabbitMQ: crear conexión AMQP (RabbitMQ.Client v6, API sync).</summary>
public sealed class RabbitMqHealthCheck(IConfiguration configuration) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var factory = new ConnectionFactory
            {
                HostName = configuration["RabbitMq:Host"] ?? "localhost",
                Port = configuration.GetValue("RabbitMq:Port", 5672),
                UserName = configuration["RabbitMq:User"] ?? "guest",
                Password = configuration["RabbitMq:Pass"] ?? "guest",
            };

            using var connection = factory.CreateConnection();
            return Task.FromResult(connection.IsOpen
                ? HealthCheckResult.Healthy("rabbitmq conectado")
                : HealthCheckResult.Unhealthy("conexion rabbitmq cerrada"));
        }
        catch (Exception ex)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy("rabbitmq inaccesible", ex));
        }
    }
}

/// <summary>
/// Health check del gateway: verifica /health/live de cada destino YARP
/// (config: Gateway:Health:Targets). Readiness del borde = readiness de sus upstreams.
/// </summary>
public sealed class GatewayDestinationsHealthCheck(IHttpClientFactory httpClientFactory, IConfiguration configuration) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var targets = configuration.GetSection("Gateway:Health:Targets").Get<string[]>() ?? [];
        if (targets.Length == 0)
        {
            return HealthCheckResult.Healthy("sin destinos configurados");
        }

        var client = httpClientFactory.CreateClient("gateway-health");
        var failures = new List<string>();

        foreach (var target in targets)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(2));

                var response = await client.GetAsync(target, timeout.Token);
                if (!response.IsSuccessStatusCode)
                {
                    failures.Add($"{target} -> {(int)response.StatusCode}");
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                failures.Add($"{target} -> timeout");
            }
            catch (Exception ex)
            {
                failures.Add($"{target} -> {ex.GetType().Name}");
            }
        }

        return failures.Count == 0
            ? HealthCheckResult.Healthy($"{targets.Length} destinos OK")
            : HealthCheckResult.Unhealthy(string.Join("; ", failures));
    }
}
