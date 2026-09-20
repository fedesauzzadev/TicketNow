using MassTransit;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace TicketNow.ServiceDefaults;

/// <summary>
/// Alta de MassTransit con transporte conmutable (ADR-003, docs/05 §5):
/// RabbitMQ en docker/producción (y en tests e2e: la topología real se prueba
/// de verdad). El modo in-memory existe como fallback para un único bus en
/// proceso (no garantiza ruteo entre varios buses del mismo proceso).
/// Los endpoints se declaran explícitos por servicio (docs/02 §6).
/// </summary>
public static class MessagingExtensions
{
    public static void AddTicketNowMessaging(
        this WebApplicationBuilder builder,
        Action<IBusRegistrationConfigurator> configure,
        Action<IBusRegistrationContext, IBusFactoryConfigurator>? configureEndpoints = null)
    {
        builder.Services.AddMassTransit(bus =>
        {
            configure(bus);

            var transport = builder.Configuration.GetValue("Messaging:Transport", "rabbitmq");
            if (string.Equals(transport, "inmemory", StringComparison.OrdinalIgnoreCase))
            {
                bus.UsingInMemory((context, cfg) => configureEndpoints?.Invoke(context, cfg));
            }
            else
            {
                bus.UsingRabbitMq((context, cfg) =>
                {
                    // En tests: purgar colas al arrancar para no consumir mensajes
                    // huérfanos de corridas anteriores (poison entre runs).
                    if (builder.Environment.IsEnvironment("Testing"))
                    {
                        cfg.PurgeOnStartup = true;
                    }
                    var rabbitHost = builder.Configuration["RabbitMq:Host"] ?? "localhost";
                    var rabbitPort = builder.Configuration.GetValue("RabbitMq:Port", 5672);
                    cfg.Host(new Uri($"rabbitmq://{rabbitHost}:{rabbitPort}"), h =>
                    {
                        h.Username(builder.Configuration["RabbitMq:User"] ?? "guest");
                        h.Password(builder.Configuration["RabbitMq:Pass"] ?? "guest");
                    });
                    configureEndpoints?.Invoke(context, cfg);
                });
            }
        });
    }

    /// <summary>
    /// Nombre de cola con sufijo opcional por entorno (docs/02 §6).
    /// En tests cada fixture usa un sufijo único: los buses de distintas corridas
    /// o clases JAMÁS compiten por las mismas colas (ni entre sí ni con compose).
    /// En producción el sufijo está vacío: nombres estables.
    /// </summary>
    public static string QueueName(string baseName, IConfiguration config)
    {
        var suffix = config.GetValue<string>("Messaging:EndpointSuffix");
        return string.IsNullOrWhiteSpace(suffix) ? baseName : $"{baseName}-{suffix}";
    }

    public static Uri QueueUri(string baseName, IConfiguration config) =>
        new($"queue:{QueueName(baseName, config)}");

    /// <summary>
    /// Retry con backoff exponencial (docs/02 §6: 5 reintentos → DLQ).
    /// Sin esto, cualquier fallo transitorio (red, PG) manda el mensaje a error
    /// y la saga queda trabada. Con inbox (endpoints con outbox), el redelivery
    /// es seguro: duplicados se descartan, consumers idempotentes no duplican efecto.
    /// </summary>
    public static void UseTicketNowRetry(IReceiveEndpointConfigurator endpoint)
    {
        endpoint.UseMessageRetry(r => r.Exponential(5, TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(200)));
    }

    /// <summary>
    /// Outbox transaccional (docs/03 §6, ADR-007): estado en PG y eventos atomizados.
    /// Los consumers de cada endpoint agregan inbox/dedupe con
    /// <c>e.UseEntityFrameworkOutbox&lt;TDbContext&gt;(context)</c>.
    /// </summary>
    public static void AddTicketNowOutbox<TDbContext>(
        IBusRegistrationConfigurator bus,
        IConfiguration config)
        where TDbContext : DbContext
    {
        bus.AddEntityFrameworkOutbox<TDbContext>(o =>
        {
            o.UsePostgres();
            // Sin UseBusOutbox a propósito: combinado con el filtro de endpoint
            // duplica la persistencia (mismo MessageId en dos filas de outbox) y los
            // redeliveries chocan en AK_InboxState. Los publishes fuera de consumers
            // van directos (fail fast; el cliente reintenta con Idempotency-Key y el
            // sweeper reintenta en el ciclo siguiente). Ver ADR-007 (ajuste Fase 3).
            o.QueryDelay = TimeSpan.FromSeconds(config.GetValue("Messaging:OutboxQueryDelaySeconds", 1.0));
            o.QueryTimeout = TimeSpan.FromSeconds(30);
            o.DuplicateDetectionWindow = TimeSpan.FromMinutes(30);
        });
    }
}
