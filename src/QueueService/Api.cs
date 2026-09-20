using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using TicketNow.ServiceDefaults;

namespace TicketNow.QueueService;

public static class Api
{
    public static void MapQueueEndpoints(this WebApplication app)
    {
        var queue = app.MapGroup("/api/queue");

        // Entrar a la fila (o admisión inmediata si el evento no requiere fila, ADR-010).
        queue.MapPost("/enter", async (
            EnterQueueRequest request,
            [FromHeader(Name = "X-User-Id")] string? userId,
            QueueStore store,
            OnsaleConfigProvider config,
            AdmissionTokenService tokens,
            IHubContext<QueueHub> hub,
            CancellationToken ct) =>
        {
            var cfg = await config.ResolveAsync(request.OnsaleId, ct);
            if (cfg is null || !cfg.Configured)
            {
                return Results.Problem(title: "onsale sin fila configurada", statusCode: StatusCodes.Status404NotFound);
            }

            // Proof-of-work (Fase 6): sin prueba válida se emite un challenge
            // de un solo uso (428). El cliente resuelve y reintenta.
            if (cfg.PowDifficulty > 0)
            {
                var ok = request.ChallengeId is not null && request.Nonce is not null
                    && await store.ConsumeChallengeAsync(request.OnsaleId, request.ChallengeId, request.Nonce, cfg.PowDifficulty);
                if (!ok)
                {
                    var challengeId = await store.IssueChallengeAsync(request.OnsaleId);
                    return Results.Json(
                        new PowChallengeResponse(challengeId, cfg.PowDifficulty, QueueStore.PowChallengeTtlSeconds),
                        statusCode: StatusCodes.Status428PreconditionRequired);
                }
            }

            var sessionId = Guid.NewGuid().ToString("N");
            await store.EnterAsync(request.OnsaleId, sessionId, userId);

            if (!cfg.RequiresQueue)
            {
                // Free-pass (ADR-010): sin fila, admisión inmediata con token.
                var token = tokens.Issue(sessionId, request.OnsaleId, request.OnsaleId, TimeSpan.FromMinutes(5));
                await store.AdmitAsync(request.OnsaleId, sessionId, leaseSeconds: 60);
                return Results.Ok(new EnterQueueResponse(sessionId, 0, 0, true, token, DateTimeOffset.UtcNow.AddMinutes(5)));
            }

            var position = await store.PositionAsync(request.OnsaleId, sessionId) ?? 0;
            var rate = await store.GetRateAsync(request.OnsaleId, cfg.InitialRate);
            return Results.Ok(new EnterQueueResponse(sessionId, position + 1, Estimate(position + 1, rate), false, null, null));
        });

        // Snapshot para polling-REST (fallback sin SignalR). Si ya está admitido
        // se re-emite el token: el cliente que perdió el push lo recupera acá.
        queue.MapGet("/me", async (
            [FromQuery] string sessionId,
            QueueStore store,
            OnsaleConfigProvider config,
            AdmissionTokenService tokens,
            CancellationToken ct) =>
        {
            var onsaleId = await store.SessionOnsaleAsync(sessionId);
            if (onsaleId is null)
            {
                return Results.Problem(title: "sesión desconocida o expirada", statusCode: StatusCodes.Status404NotFound);
            }
            if (await store.IsAdmittedAsync(sessionId))
            {
                var ttl = TimeSpan.FromMinutes(5);
                return Results.Ok(new QueueStatusResponse(onsaleId, 0, 0, true, tokens.Issue(sessionId, onsaleId, onsaleId, ttl), DateTimeOffset.UtcNow.Add(ttl)));
            }
            var position = await store.PositionAsync(onsaleId, sessionId);
            if (position is null)
            {
                return Results.Problem(title: "fuera de la fila (abandono o admisión consumida)", statusCode: StatusCodes.Status404NotFound);
            }
            var cfg = await config.ResolveAsync(onsaleId, ct);
            var rate = cfg is null ? 10 : await store.GetRateAsync(onsaleId, cfg.InitialRate);
            return Results.Ok(new QueueStatusResponse(onsaleId, position.Value + 1, Estimate(position.Value + 1, rate), false));
        });

        queue.MapPost("/heartbeat", async (HeartbeatRequest request, QueueStore store) =>
        {
            var onsaleId = await store.SessionOnsaleAsync(request.SessionId);
            if (onsaleId is null)
            {
                return Results.Problem(title: "sesión desconocida o expirada", statusCode: StatusCodes.Status404NotFound);
            }
            await store.TouchAsync(onsaleId, request.SessionId);
            return Results.Ok();
        });

        queue.MapDelete("/me", async ([FromQuery] string sessionId, QueueStore store) =>
        {
            var onsaleId = await store.SessionOnsaleAsync(sessionId);
            if (onsaleId is null)
            {
                return Results.NotFound();
            }
            await store.LeaveAsync(onsaleId, sessionId, await store.IsAdmittedAsync(sessionId));
            return Results.NoContent();
        });

        var admin = queue.MapGroup("/admin");

        admin.MapGet("/{onsaleId}", async (string onsaleId, QueueStore store, OnsaleConfigProvider config, CancellationToken ct) =>
        {
            var cfg = await config.ResolveAsync(onsaleId, ct);
            return Results.Ok(new QueueStatsResponse(
                onsaleId,
                await store.WaitingCountAsync(onsaleId),
                await store.AdmittedCountAsync(onsaleId),
                await store.GetRateAsync(onsaleId, cfg?.InitialRate ?? 0),
                cfg?.MaxConcurrentInside ?? 0));
        });

        // Override manual de config (incidentes): precede al catálogo hasta DELETE.
        admin.MapPost("/{onsaleId}/config", async (string onsaleId, ConfigureQueueRequest request, QueueStore store, OnsaleConfigProvider config) =>
        {
            await store.SetOverrideAsync(onsaleId, request.MaxConcurrentInside, request.InitialRate, request.MinRate, request.MaxRate, request.RequiresQueue, request.PowDifficulty);
            config.Invalidate(onsaleId);
            return Results.Ok();
        });

        admin.MapDelete("/{onsaleId}/config", async (string onsaleId, QueueStore store, OnsaleConfigProvider config) =>
        {
            await store.ClearOverrideAsync(onsaleId);
            config.Invalidate(onsaleId);
            return Results.NoContent();
        });

        // Ping de Fase 0
        queue.MapGet("/ping", () => Results.Ok(new { service = "queue-service", status = "pong", utc = DateTime.UtcNow }));
    }

    private static int Estimate(long position, int perSecond) =>
        (int)Math.Ceiling((double)Math.Max(0, position - 1) / Math.Max(1, perSecond));
}
