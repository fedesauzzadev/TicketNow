using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using StackExchange.Redis;
using TicketNow.ServiceDefaults;

namespace TicketNow.InventoryService;

public static class Api
{
    public static void MapInventoryEndpoints(this WebApplication app)
    {
        var inventory = app.MapGroup("/api/inventory");

        // POST /holds — reserva atómica (docs/03 §2). HU-02: 201 con hold o 409 sin stock.
        inventory.MapPost("/holds", async (
            CreateHoldRequest request,
            [FromHeader(Name = "X-User-Id")] string? userId,
            [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
            [FromHeader(Name = "X-Admission-Token")] string? admissionToken,
            HoldService holds,
            AdmissionChecker admission,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(userId))
            {
                return Results.Problem(title: "falta el header X-User-Id (hasta Fase 6, sin JWT)", statusCode: StatusCodes.Status400BadRequest);
            }

            // Turno atado al evento (ADR-012): ver OrdersService/Api.cs.
            var admissionCheck = await admission.CheckAsync(admissionToken, request.EventId.ToString());
            if (admissionCheck is AdmissionCheckResult.WrongEvent)
            {
                return Results.Problem(
                    title: "este turno es para otro evento: hacé la fila del evento que querés comprar",
                    statusCode: StatusCodes.Status403Forbidden,
                    extensions: new Dictionary<string, object?> { ["error"] = "admission_for_other_event" });
            }
            if (admissionCheck is AdmissionCheckResult.Revoked)
            {
                return Results.Problem(
                    title: "saliste de la fila: volvé a entrar para comprar",
                    statusCode: StatusCodes.Status403Forbidden,
                    extensions: new Dictionary<string, object?> { ["error"] = "admission_revoked" });
            }
            if (admissionCheck is AdmissionCheckResult.Invalid)
            {
                return Results.Problem(
                    title: "turno inválido o vencido: volvé a entrar a la fila",
                    statusCode: StatusCodes.Status401Unauthorized,
                    extensions: new Dictionary<string, object?> { ["error"] = "admission_invalid" });
            }

            var result = await holds.CreateHoldAsync(request.EventId, request.ZoneId, userId.Trim(), request.Qty, idempotencyKey, ct);

            return result switch
            {
                HoldAccepted g => Results.Created(
                    $"/api/inventory/holds/{g.Hold.HoldId}",
                    new HoldResponse(g.Hold.HoldId, g.Hold.EventId, g.Hold.ZoneId, g.Hold.Qty, g.Hold.ExpiresAt, g.Hold.Replayed)),
                HoldRejected r => Results.Problem(title: r.Reason, statusCode: StatusCodes.Status409Conflict),
                HoldInvalid i => Results.Problem(title: i.Reason, statusCode: StatusCodes.Status400BadRequest),
                _ => Results.Problem(title: "resultado desconocido", statusCode: StatusCodes.Status500InternalServerError),
            };
        });

        inventory.MapGet("/holds/{id:guid}", async (Guid id, HoldService holds, CancellationToken ct) =>
        {
            var record = await holds.ReadRecordAsync(id, ct);
            if (record is null)
            {
                return Results.Problem(title: "hold inexistente", statusCode: StatusCodes.Status404NotFound);
            }

            return Results.Ok(new HoldStatusResponse(
                record.HoldId, record.EventId, record.ZoneId, record.UserId,
                record.Qty, record.CreatedAt, record.ExpiresAt, record.Outcome?.ToString()));
        });

        // DELETE /holds/{id} — liberación voluntaria (FR-32 parcial; el resto lo hace el sweeper).
        inventory.MapDelete("/holds/{id:guid}", async (
            Guid id,
            [FromHeader(Name = "X-User-Id")] string? userId,
            HoldService holds,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(userId))
            {
                return Results.Problem(title: "falta el header X-User-Id", statusCode: StatusCodes.Status400BadRequest);
            }

            var result = await holds.ReleaseHoldAsync(id, userId.Trim(), HoldOutcome.Released, actor: "user", ct);

            return result switch
            {
                HoldReleased => Results.NoContent(),
                HoldForbidden => Results.Problem(title: "el hold pertenece a otro usuario", statusCode: StatusCodes.Status403Forbidden),
                HoldNotFound => Results.Problem(title: "hold inexistente o ya cerrado", statusCode: StatusCodes.Status404NotFound),
                _ => Results.Problem(title: "resultado desconocido", statusCode: StatusCodes.Status500InternalServerError),
            };
        });

        var admin = inventory.MapGroup("/admin");

        // Siembra de stock (Fase 2: explícita; Fase 3: la dispara OnsaleOpened vía MassTransit).
        admin.MapPost("/seed", async (SeedStockRequest request, HoldService holds, CancellationToken ct) =>
        {
            if (request.Zones.Count == 0 || request.Zones.Any(z => z.Capacity < 1))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["zones"] = ["al menos una zona con capacidad > 0"] });
            }

            var (seeded, skipped) = await holds.SeedAsync(request.EventId, request.Zones.Select(z => (z.ZoneId, z.Capacity)).ToList(), ct);
            return Results.Ok(new SeedStockResponse(request.EventId, seeded, skipped));
        });

        // Expiración forzada (herramienta operativa para holds atascados; usa el
        // mismo camino que el sweeper: ReleaseHoldAsync + evento HoldExpired).
        admin.MapPost("/holds/{id:guid}/expire", async (Guid id, HoldService holds, CancellationToken ct) =>
        {
            var result = await holds.ReleaseHoldAsync(id, userId: null, HoldOutcome.Expired, actor: "admin", ct);
            return result switch
            {
                HoldReleased => Results.NoContent(),
                HoldNotFound => Results.Problem(title: "hold inexistente o ya cerrado", statusCode: StatusCodes.Status404NotFound),
                _ => Results.Problem(title: "resultado desconocido", statusCode: StatusCodes.Status500InternalServerError),
            };
        });

        // Ping de Fase 0 (smoke tests del borde)
        inventory.MapGet("/ping", () => Results.Ok(new { service = "inventory-service", status = "pong", utc = DateTime.UtcNow }));
    }
}
