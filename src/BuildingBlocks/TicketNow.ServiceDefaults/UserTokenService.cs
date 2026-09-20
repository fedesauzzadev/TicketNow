using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace TicketNow.ServiceDefaults;

// JWT de usuario (Fase 6, docs/02 ADR-011): HS256 con sub=userId. Lo emite el
// gateway (/api/auth/token, mock didáctico: sin password real — en producción
// esto es un IdP); lo exige el gateway en rutas de compra (scheme "user") y
// propaga el sub como X-User-Id hacia los servicios (que no cambian).
// Claims: iss, aud, sub=userId, name, jti, iat, exp (TTL largo: sesión).
public sealed class UserTokenService
{
    public const string Issuer = "ticketnow-auth";
    public const string Audience = "ticketnow";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly byte[] _key;
    private readonly ILogger<UserTokenService> _log;

    public UserTokenService(IConfiguration config, ILogger<UserTokenService> log)
    {
        _log = log;
        var key = config["Auth:UserSigningKey"]
            ?? throw new InvalidOperationException("Auth:UserSigningKey no configurado");
        if (Encoding.UTF8.GetByteCount(key) < 32)
        {
            throw new InvalidOperationException("Auth:UserSigningKey debe tener al menos 32 bytes");
        }
        _key = Encoding.UTF8.GetBytes(key);
    }

    public string Issue(string userId, string displayName, TimeSpan ttl)
    {
        var now = DateTimeOffset.UtcNow;
        var header = Base64Url("""{"alg":"HS256","typ":"JWT"}""");
        var payload = Base64Url(JsonSerializer.Serialize(new
        {
            iss = Issuer,
            aud = Audience,
            sub = userId,
            name = displayName,
            jti = Guid.NewGuid().ToString("N"),
            iat = now.ToUnixTimeSeconds(),
            exp = now.Add(ttl).ToUnixTimeSeconds(),
        }, Json));
        return $"{header}.{payload}.{Base64Url(HMACSHA256.HashData(_key, Encoding.ASCII.GetBytes($"{header}.{payload}")))}";
    }

    public bool TryVerify(string token, out string? userId, out string? displayName)
    {
        userId = null;
        displayName = null;
        var parts = token.Split('.');
        if (parts.Length != 3)
        {
            return false;
        }
        var expected = Base64Url(HMACSHA256.HashData(_key, Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}")));
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(parts[2])))
        {
            _log.LogWarning("user token con firma inválida");
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
            userId = root.GetProperty("sub").GetString();
            displayName = root.TryGetProperty("name", out var n) ? n.GetString() : userId;
            return userId is not null;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "user token malformado");
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
