using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TicketNow.OrdersService;

/// <summary>
/// Entradas como JWT firmado HMAC-SHA256 (docs/06 §6): sin PII, nonce único (jti),
/// expiración larga configurable. Implementación manual a propósito (didáctica):
/// firmar y verificar son ~40 líneas y se entienden de punta a punta.
/// </summary>
public sealed class QrService(IConfiguration config, ILogger<QrService> log)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private byte[] Key =>
        Encoding.UTF8.GetBytes(config["Tickets:QrKey"]
            ?? throw new InvalidOperationException("Tickets:QrKey no configurado"));

    public string Issue(Guid ticketId, Guid orderId, Guid eventId, Guid zoneId, int qty)
    {
        var header = Base64Url("""{"alg":"HS256","typ":"JWT"}""");
        var payload = Base64Url(JsonSerializer.Serialize(new
        {
            ticketId,
            orderId,
            @event = eventId,
            zone = zoneId,
            qty,
            nonce = Guid.NewGuid().ToString("N"),
            iat = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            exp = DateTimeOffset.UtcNow.AddDays(config.GetValue("Tickets:QrValidityDays", 180)).ToUnixTimeSeconds(),
        }, Json));

        var signature = Base64Url(Hmac($"{header}.{payload}"));
        return $"{header}.{payload}.{signature}";
    }

    public bool TryVerify(string token, out QrClaims claims)
    {
        claims = null!;
        var parts = token.Split('.');
        if (parts.Length != 3)
        {
            return false;
        }

        var expected = Base64Url(Hmac($"{parts[0]}.{parts[1]}"));
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(parts[2])))
        {
            log.LogWarning("QR con firma inválida");
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(Base64UrlDecode(parts[1])));
            var root = doc.RootElement;
            var exp = DateTimeOffset.FromUnixTimeSeconds(root.GetProperty("exp").GetInt64());
            if (exp < DateTimeOffset.UtcNow)
            {
                return false;
            }
            claims = new QrClaims(
                root.GetProperty("ticketId").GetGuid(),
                root.GetProperty("orderId").GetGuid(),
                root.GetProperty("nonce").GetString() ?? string.Empty,
                exp);
            return true;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "QR malformado");
            return false;
        }
    }

    private byte[] Hmac(string data) => HMACSHA256.HashData(Key, Encoding.ASCII.GetBytes(data));

    private static string Base64Url(string text) => Base64Url(Encoding.UTF8.GetBytes(text));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string text)
    {
        var padded = text.Replace('-', '+').Replace('_', '/');
        padded += new string('=', (4 - padded.Length % 4) % 4);
        return Convert.FromBase64String(padded);
    }
}

public sealed record QrClaims(Guid TicketId, Guid OrderId, string Nonce, DateTimeOffset ExpiresAt);
