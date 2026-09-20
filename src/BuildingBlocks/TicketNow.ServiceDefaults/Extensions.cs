using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Events;

namespace TicketNow.ServiceDefaults;

/// <summary>
/// Configuración compartida por todos los servicios de TicketNow:
/// logging estructurado (Serilog JSON), telemetría (OpenTelemetry) y health checks.
/// Ver docs/02-arquitectura.md y docs/05-despliegue-y-observabilidad.md.
/// </summary>
public static class Extensions
{
    public static WebApplicationBuilder AddTicketNowDefaults(this WebApplicationBuilder builder, string serviceName)
    {
        builder.Host.UseSerilog((context, configuration) => configuration
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.Extensions.Diagnostics", LogEventLevel.Warning)
            .Enrich.WithProperty("Service", serviceName)
            .WriteTo.Console(new Serilog.Formatting.Compact.CompactJsonFormatter()));

        var openTelemetry = builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(
                serviceName: serviceName,
                serviceVersion: typeof(Extensions).Assembly.GetName().Version?.ToString() ?? "dev"))
            .WithTracing(tracing => tracing
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation())
            .WithMetrics(metrics => metrics
                .AddRuntimeInstrumentation()
                .AddMeter(
                    "Microsoft.AspNetCore.Hosting",
                    "Microsoft.AspNetCore.Server.Kestrel",
                    "Microsoft.AspNetCore.Http.Connections",
                    "TicketNow.Inventory")
                .AddPrometheusExporter());

        // OTLP (Jaeger) solo si el entorno define el endpoint estandar (profile `obs`)
        var otlpEndpoint = builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];
        if (!string.IsNullOrWhiteSpace(otlpEndpoint))
        {
            openTelemetry
                .WithTracing(tracing => tracing.AddOtlpExporter())
                .WithMetrics(metrics => metrics.AddOtlpExporter());
        }

        builder.Services.AddHealthChecks();

        return builder;
    }

    public static WebApplication MapTicketNowDefaults(this WebApplication app)
    {
        // Liveness: el proceso vive (sin dependencias). Compose usa este endpoint.
        app.MapGet("/health/live", () => Results.Ok(new { status = "alive" }));

        // Readiness: listo para tráfico = todos los checks taggeados "ready" en verde.
        app.MapGet("/health/ready", async (HealthCheckService health, CancellationToken cancellationToken) =>
        {
            var report = await health.CheckHealthAsync(check => check.Tags.Contains("ready"), cancellationToken);
            return report.Status == HealthStatus.Healthy
                ? Results.Ok(new { status = "ready", checks = report.Entries.Count })
                : Results.Problem(
                    title: "dependencias no listas",
                    detail: string.Join("; ", report.Entries.Where(e => e.Value.Status != HealthStatus.Healthy).Select(e => $"{e.Key}: {e.Value.Description}")),
                    statusCode: StatusCodes.Status503ServiceUnavailable);
        });

        app.MapGet("/info", () => Results.Ok(new
        {
            service = app.Environment.ApplicationName,
            environment = app.Environment.EnvironmentName,
            version = typeof(Extensions).Assembly.GetName().Version?.ToString(),
        }));

        // Métricas Prometheus (scrapeadas según deploy/prometheus/prometheus.yml).
        app.MapPrometheusScrapingEndpoint();

        return app;
    }

    public static void AddRedisHealth(this WebApplicationBuilder builder, string name = "redis") =>
        builder.Services.AddHealthChecks().AddCheck<RedisHealthCheck>(name, tags: ["ready"]);

    public static void AddPostgresHealth(this WebApplicationBuilder builder, string name = "postgres") =>
        builder.Services.AddHealthChecks().AddCheck<PostgresHealthCheck>(name, tags: ["ready"]);

    public static void AddRabbitMqHealth(this WebApplicationBuilder builder, string name = "rabbitmq") =>
        builder.Services.AddHealthChecks().AddCheck<RabbitMqHealthCheck>(name, tags: ["ready"]);

    public static void AddGatewayDestinationsHealth(this WebApplicationBuilder builder)
    {
        builder.Services.AddHttpClient("gateway-health");
        builder.Services.AddHealthChecks().AddCheck<GatewayDestinationsHealthCheck>("destinations", tags: ["ready"]);
    }
}
