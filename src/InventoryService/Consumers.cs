using MassTransit;
using Microsoft.Extensions.Logging;
using TicketNow.Contracts.Commands;
using TicketNow.Contracts.Events;

namespace TicketNow.InventoryService;

/// <summary>
/// Validación de holds para la saga: responde (request/response del API) y publica
/// (correlación de la saga). Con inbox+outbox del endpoint: idempotente y atómico.
/// </summary>
public sealed class ValidateHoldConsumer(HoldService holds) : IConsumer<ValidateHold>
{
    public async Task Consume(ConsumeContext<ValidateHold> context)
    {
        var message = context.Message;
        var ct = context.CancellationToken;

        var record = await holds.TryClaimAsync(message.HoldId, message.OrderId, ct);
        if (record is null)
        {
            var invalid = new global::TicketNow.Contracts.Events.HoldInvalid(message.OrderId, message.HoldId, "hold inexistente, vencido o en uso");
            await context.RespondAsync(invalid);
            if (message.OrderId != Guid.Empty)
            {
                await context.Publish(invalid, ct);
            }
            return;
        }

        var valid = new HoldValidated(message.OrderId, message.HoldId, record.EventId, record.ZoneId, record.Qty);
        await context.RespondAsync(valid);
        if (message.OrderId != Guid.Empty)
        {
            await context.Publish(valid, ct);
        }
    }
}

/// <summary>Compensación de la saga: devolver el stock (fire-and-forget).</summary>
public sealed class ReleaseHoldConsumer(HoldService holds) : IConsumer<ReleaseHold>
{
    public async Task Consume(ConsumeContext<ReleaseHold> context)
    {
        await holds.ReleaseHoldAsync(context.Message.HoldId, userId: null, HoldOutcome.Released, actor: "saga", context.CancellationToken);
    }
}

/// <summary>Puerta autoritativa de la saga: cementar o fallar (la saga compensa).</summary>
public sealed class ConfirmHoldConsumer(HoldService holds) : IConsumer<ConfirmHold>
{
    public async Task Consume(ConsumeContext<ConfirmHold> context)
    {
        await holds.ConfirmHoldAsync(context.Message.OrderId, context.Message.HoldId, context.CancellationToken);
    }
}

/// <summary>
/// Fase 5: el onsale abre → siembra el stock de todas las zonas (idempotente:
/// SETNX por zona; replays y reenvíos no duplican). Reemplaza el seed manual
/// de admin como camino principal (el endpoint queda para tests/incidentes).
/// </summary>
public sealed class OnsaleOpenedConsumer(HoldService holds, ILogger<OnsaleOpenedConsumer> log) : IConsumer<OnsaleOpened>
{
    public async Task Consume(ConsumeContext<OnsaleOpened> context)
    {
        var message = context.Message;
        var (seeded, skipped) = await holds.SeedAsync(
            message.EventId,
            message.Zones.Select(z => (z.ZoneId, z.Capacity)).ToList(),
            context.CancellationToken);
        log.LogInformation(
            "OnsaleOpened {EventId}: {Seeded} zonas sembradas, {Skipped} ya existentes (idempotente)",
            message.EventId, seeded, skipped);
    }
}
