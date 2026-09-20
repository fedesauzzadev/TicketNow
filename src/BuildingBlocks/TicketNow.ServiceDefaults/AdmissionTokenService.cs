using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace TicketNow.ServiceDefaults;

// Token de admisión (docs/02 ADR-004/005, docs/03 §1): JWT HS256 estándar que
// acredita haber pasado el control de admisión (o que no había fila).
// Lo emite queue-service; lo exige el gateway en rutas de compra (JwtBearer).
// Claims: iss, aud, sub=sessionId, onsale, event, jti, iat, exp (TTL corto).
public sealed class AdmissionTokenService
{
    public const string Issuer = "ticketnow-queue";
    public const string Audience = "ticketnow-store";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly byte[] _key;
    private readonly ILogger<AdmissionTokenService> _log;

    public AdmissionTokenService(IConfiguration config, ILogger<AdmissionTokenService> log)
    {
        _log = log;
        var key = config["Queue:AdmissionSigningKey"]
            ?? throw new InvalidOperationException("Queue:AdmissionSigningKey no configurado");
        if (Encoding.UTF8.GetByteCount(key) < 32)
        {
            throw new InvalidOperationException("Queue:AdmissionSigningKey debe tener al menos 32 bytes");
        }
        _key = Encoding.UTF8.GetBytes(key);
    }

    public string Issue(string sessionId, string onsaleId, string eventId, TimeSpan ttl)
    {
        var now = DateTimeOffset.UtcNow;
        var header = Base64Url("""{"alg":"HS256","typ":"JWT"}""");
        var payload = Base64Url(JsonSerializer.Serialize(new
        {
            iss = Issuer,
            aud = Audience,
            sub = sessionId,
            onsale = onsaleId,
            @event = eventId,
            jti = Guid.NewGuid().ToString("N"),
            iat = now.ToUnixTimeSeconds(),
            exp = now.Add(ttl).ToUnixTimeSeconds(),
        }, Json));
        return $"{header}.{payload}.{Base64Url(HMACSHA256.HashData(_key, Encoding.ASCII.GetBytes($"{header}.{payload}")))}";
    }

    public bool TryVerify(string token, out string? sessionId, out string? onsaleId)
    {
        sessionId = null;
        onsaleId = null;
        var parts = token.Split('.');
        if (parts.Length != 3)
        {
            return false;
        }
        var expected = Base64Url(HMACSHA256.HashData(_key, Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}")));
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(parts[2])))
        {
            _log.LogWarning("admission token con firma inválida");
            return false;
        }
        try
        {
            using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(Base64UrlDecode(parts[1])));
            var root = doc.RootElement;
            if (root.GetProperty("iss").GetString() != Issuer
                || root.GetProperty("aud").GetString() != Audience
                || DateTimeOffset.FromUnixTimeSeconds(root.GetProperty("exp").GetInt64()) < DateTimeOffset.UtcNow)
            {
                return false;
            }
            sessionId = root.GetProperty("sub").GetString();
            onsaleId = root.GetProperty("onsale").GetString();
            return sessionId is not null && onsaleId is not null;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "admission token malformado");
            return false;
        }
    }

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
