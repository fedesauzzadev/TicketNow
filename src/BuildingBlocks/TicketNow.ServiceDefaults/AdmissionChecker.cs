using StackExchange.Redis;

namespace TicketNow.ServiceDefaults;

/// <summary>Resultado del bindeo turno→evento + revocación (ADR-012).</summary>
public enum AdmissionCheckResult
{
    Ok,
    Missing,
    Invalid,
    WrongEvent,
    Revoked,
}

/// <summary>
/// Verificación de admisión del lado del servicio que VENDE (ADR-012).
/// El gateway valida firma/vigencia, pero no conoce el evento comprado:
/// solo acá se puede exigir que el turno sea PARA ESE evento, y solo Redis
/// sabe si la sesión salió (LeaveAsync revoca).
///
/// Fail-open en Missing a propósito: sin token no hay nada que bindear. El
/// gateway garantiza presencia en producción; directo al servicio = tests/dev.
/// </summary>
public sealed class AdmissionChecker(AdmissionTokenService tokens, IDatabase redis)
{
    public static string RevokedKey(string sessionId) => $"revoked-session:{sessionId}";

    /// <summary>TTL del marcador = TTL máximo de un token (5 min): revocar más
    /// que eso es inútil porque el token ya expiró solo.</summary>
    public static readonly TimeSpan RevokedTtl = TimeSpan.FromMinutes(5);

    public async Task<AdmissionCheckResult> CheckAsync(string? token, string expectedEventId)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return AdmissionCheckResult.Missing;
        }
        if (!tokens.TryVerifyEvent(token, out var sessionId, out var eventId) || sessionId is null)
        {
            return AdmissionCheckResult.Invalid;
        }
        if (!SameEvent(eventId, expectedEventId))
        {
            return AdmissionCheckResult.WrongEvent;
        }
        if (await redis.KeyExistsAsync(RevokedKey(sessionId)))
        {
            return AdmissionCheckResult.Revoked;
        }
        return AdmissionCheckResult.Ok;
    }

    public Task RevokeSessionAsync(string sessionId) =>
        redis.StringSetAsync(RevokedKey(sessionId), "1", RevokedTtl);

    public Task ClearRevocationAsync(string sessionId) =>
        redis.KeyDeleteAsync(RevokedKey(sessionId));

    // El evento viaja como string con formato libre ("D" o "N"): comparar por Guid.
    private static bool SameEvent(string? a, string b)
    {
        if (Guid.TryParse(a, out var ga) && Guid.TryParse(b, out var gb))
        {
            return ga == gb;
        }
        return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }
}
