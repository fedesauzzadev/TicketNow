using Microsoft.AspNetCore.SignalR;

namespace TicketNow.QueueService;

/// <summary>
/// Hub de la fila virtual (docs/04 §6): JoinQueue/Heartbeat/Leave + push de
/// posición y admisión. La verdad vive en Redis (QueueStore); el hub es solo
/// transporte (escala horizontal con backplane).
/// </summary>
public sealed class QueueHub(QueueStore store, OnsaleConfigProvider config, ILogger<QueueHub> log) : Hub
{
    public async Task<object> JoinQueue(string onsaleId, string? sessionId)
    {
        var configResolved = await config.ResolveAsync(onsaleId, Context.ConnectionAborted);
        if (configResolved is null || !configResolved.Configured)
        {
            throw new HubException("onsale sin fila configurada");
        }

        string session;
        if (!string.IsNullOrEmpty(sessionId) && await store.SessionOnsaleAsync(sessionId) == onsaleId)
        {
            session = sessionId; // reconexión dentro de la gracia: se conserva lugar
        }
        else
        {
            session = Guid.NewGuid().ToString("N");
            await store.EnterAsync(onsaleId, session, userId: Context.UserIdentifier);
        }

        await store.SetConnectionAsync(session, Context.ConnectionId);
        await store.TouchAsync(onsaleId, session);
        await Groups.AddToGroupAsync(Context.ConnectionId, $"onsale:{onsaleId}");

        var position = await store.PositionAsync(onsaleId, session);
        var admitted = await store.IsAdmittedAsync(session);
        log.LogInformation("sesión {Session} unida a {Onsale} (posición {Position})", session, onsaleId, position);

        return new { sessionId = session, position = position is null ? 0 : position.Value + 1, admitted };
    }

    public async Task Heartbeat(string sessionId)
    {
        var onsaleId = await store.SessionOnsaleAsync(sessionId);
        if (onsaleId is null)
        {
            throw new HubException("sesión desconocida o expirada");
        }
        await store.SetConnectionAsync(sessionId, Context.ConnectionId);
        await store.TouchAsync(onsaleId, sessionId);
    }

    public async Task LeaveQueue(string sessionId)
    {
        var onsaleId = await store.SessionOnsaleAsync(sessionId);
        if (onsaleId is null)
        {
            return;
        }
        await store.LeaveAsync(onsaleId, sessionId, await store.IsAdmittedAsync(sessionId));
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"onsale:{onsaleId}");
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        // Desconexión ≠ abandono: la gracia (60 s) la maneja el reaper por lastSeen.
        // Se limpia el connectionId para no pushear a una conexión muerta.
        log.LogInformation("conexión {Connection} caída (gracia de reconexión vigente)", Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }
}
