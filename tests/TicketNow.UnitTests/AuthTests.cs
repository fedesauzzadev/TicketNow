using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using TicketNow.ServiceDefaults;

namespace TicketNow.UnitTests;

// JWT de usuario (Fase 6): emisión, verificación, tamper, expiración y clave corta.
public class AuthTests
{
    private const string Key = "clave-de-test-para-jwt-de-usuario-32b!!";

    private static UserTokenService CreateService(string key = Key)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Auth:UserSigningKey"] = key })
            .Build();
        return new UserTokenService(config, NullLogger<UserTokenService>.Instance);
    }

    [Fact]
    public void Emite_y_verifica_identidad()
    {
        var tokens = CreateService();
        var token = tokens.Issue("fan@example.com", "Fan", TimeSpan.FromHours(12));
        Assert.True(tokens.TryVerify(token, out var userId, out var name));
        Assert.Equal("fan@example.com", userId);
        Assert.Equal("Fan", name);
    }

    [Fact]
    public void Manipulado_o_expirado_no_valida()
    {
        var tokens = CreateService();
        var valid = tokens.Issue("u1", "U", TimeSpan.FromHours(1));
        var parts = valid.Split('.');
        var tampered = parts[0] + "." + parts[1].Replace(parts[1][10], parts[1][10] == 'A' ? 'B' : 'A') + "." + parts[2];
        Assert.False(tokens.TryVerify(tampered, out _, out _));

        var expired = tokens.Issue("u1", "U", TimeSpan.FromSeconds(-1));
        Assert.False(tokens.TryVerify(expired, out _, out _));

        Assert.False(tokens.TryVerify("basura", out _, out _));
    }

    [Fact]
    public void Otra_clave_no_valida()
    {
        var tokens = CreateService();
        var other = CreateService("otra-clave-distinta-de-32-bytes-minimo!");
        var token = tokens.Issue("u1", "U", TimeSpan.FromHours(1));
        Assert.False(other.TryVerify(token, out _, out _));
    }

    [Fact]
    public void Clave_corta_se_rechaza_al_construir()
    {
        Assert.Throws<InvalidOperationException>(() => CreateService("corta"));
    }
}
