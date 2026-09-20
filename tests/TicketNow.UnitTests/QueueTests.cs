using System.Net;
using System.Net.Http.Json;
using MassTransit;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using TicketNow.Contracts.Events;
using TicketNow.QueueService;
using TicketNow.ServiceDefaults;

namespace TicketNow.UnitTests;

// Fila virtual: FIFO, reconexión con gracia, tokens y backpressure.
// Sin PG (la fila vive 100% en Redis) y sin SignalR real (se prueba el store
// y la API REST; SignalR se verifica en smoke de Docker).
public class QueueTests : IClassFixture<RedisFixture>
{
    private readonly RedisFixture _redis;

    public QueueTests(RedisFixture redis) => _redis = redis;

    private WebApplicationFactory<TicketNow.QueueService.Marker> CreateFactory()
    {
        return new WebApplicationFactory<TicketNow.QueueService.Marker>().WithWebHostBuilder(web =>
        {
            web.UseEnvironment("Testing");
            web.UseSetting("ConnectionStrings:Redis", _redis.ConnectionString);
            web.UseSetting("RabbitMq:Host", Environment.GetEnvironmentVariable("TICKETNOW_TEST_RABBITMQ_HOST") ?? "localhost");
            web.UseSetting("RabbitMq:Port", Environment.GetEnvironmentVariable("TICKETNOW_TEST_RABBITMQ_PORT") ?? "5672");
            web.UseSetting("RabbitMq:User", Environment.GetEnvironmentVariable("TICKETNOW_TEST_RABBITMQ_USER") ?? "guest");
            web.UseSetting("RabbitMq:Pass", Environment.GetEnvironmentVariable("TICKETNOW_TEST_RABBITMQ_PASS") ?? "guest");
            web.UseSetting("Messaging:EndpointSuffix", Guid.NewGuid().ToString("N")[..8]);
            web.ConfigureTestServices(services =>
            {
                // Loop y reaper manuales en tests (determinismo).
                foreach (var worker in new[] { typeof(AdmissionLoop), typeof(QueueReaper) })
                {
                    var doomed = services
                        .Where(s => s.ServiceType == typeof(IHostedService) && s.ImplementationType == worker)
                        .ToList();
                    foreach (var descriptor in doomed)
                    {
                        services.Remove(descriptor);
                    }
                }
            });
        });
    }

    private static async Task ConfigureAsync(HttpClient client, string onsaleId, int maxConcurrent = 2)
    {
        var response = await client.PostAsJsonAsync($"/api/queue/admin/{onsaleId}/config", new
        {
            maxConcurrentInside = maxConcurrent,
            initialRate = 50,
            minRate = 1,
            maxRate = 50,
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task<string> EnterAsync(HttpClient client, string onsaleId)
    {
        var response = await client.PostAsJsonAsync("/api/queue/enter", new { onsaleId });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<EnterDto>())!.SessionId;
    }

    private sealed record EnterDto(string SessionId, long Position, int EtaSeconds, bool Admitted, string? AdmissionToken);
    private sealed record StatusDto(string OnsaleId, long Position, int EtaSeconds, bool Admitted);

    // INV-4: el orden de admisión respeta el orden de llegada (FIFO).
    [Fact]
    public async Task Admision_respeta_FIFO()
    {
        using var factory = CreateFactory();
        var client = factory.CreateClient();
        var onsaleId = $"onsale-{Guid.NewGuid():N}";
        await ConfigureAsync(client, onsaleId, maxConcurrent: 2);

        var sessions = new List<string>();
        for (var i = 0; i < 5; i++)
        {
            sessions.Add(await EnterAsync(client, onsaleId));
            await Task.Delay(5); // scores de llegada distintos
        }

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<AdmissionService>().RunCycleAsync(CancellationToken.None);
        }

        var admitted = new List<string>();
        var waiting = new List<(string Session, long Position)>();
        foreach (var session in sessions)
        {
            var status = await client.GetFromJsonAsync<StatusDto>($"/api/queue/me?sessionId={session}");
            if (status!.Admitted) admitted.Add(session);
            else waiting.Add((session, status.Position));
        }

        // Los 2 primeros admitidos, en orden; el resto en fila 1..3 en orden.
        Assert.Equal(new[] { sessions[0], sessions[1] }, admitted);
        Assert.Equal(3, waiting.Count);
        Assert.Equal(sessions[2], waiting[0].Session);
        Assert.Equal(1, waiting[0].Position);
        Assert.Equal(sessions[4], waiting[2].Session);
        Assert.Equal(3, waiting[2].Position);
    }

    // INV-4 (2da parte): reingresar con la misma sesión conserva el lugar.
    [Fact]
    public async Task Reingreso_con_misma_sesion_conserva_lugar()
    {
        using var factory = CreateFactory();
        await using var scope = factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<QueueStore>();
        var onsaleId = $"onsale-{Guid.NewGuid():N}";
        var sessionId = Guid.NewGuid().ToString("N");

        var (created1, arrival1) = await store.EnterAsync(onsaleId, sessionId, userId: "u1");
        await Task.Delay(5);
        var (created2, arrival2) = await store.EnterAsync(onsaleId, sessionId, userId: "u1");

        Assert.True(created1);
        Assert.False(created2); // no es una sesión nueva
        Assert.Equal(arrival1, arrival2); // conserva timestamp original
        Assert.Equal(1, await store.WaitingCountAsync(onsaleId)); // una sola entrada en la fila
    }

    // Free-pass (ADR-010): evento sin fila admite de inmediato con token.
    [Fact]
    public async Task Sin_fila_requerida_admite_de_inmediato()
    {
        using var factory = CreateFactory();
        var client = factory.CreateClient();
        var onsaleId = $"onsale-{Guid.NewGuid():N}";
        var configured = await client.PostAsJsonAsync($"/api/queue/admin/{onsaleId}/config", new
        {
            maxConcurrentInside = 100,
            initialRate = 50,
            minRate = 1,
            maxRate = 50,
            requiresQueue = false,
        });
        Assert.Equal(HttpStatusCode.OK, configured.StatusCode);

        var response = await client.PostAsJsonAsync("/api/queue/enter", new { onsaleId });
        var entered = (await response.Content.ReadFromJsonAsync<EnterDto>())!;

        Assert.True(entered.Admitted);
        Assert.NotNull(entered.AdmissionToken);

        await using var scope = factory.Services.CreateAsyncScope();
        var tokens = scope.ServiceProvider.GetRequiredService<AdmissionTokenService>();
        Assert.True(tokens.TryVerify(entered.AdmissionToken!, out var session, out var onsale));
        Assert.Equal(entered.SessionId, session);
        Assert.Equal(onsaleId, onsale);
    }

    [Fact]
    public async Task Token_manipulado_o_expirado_no_valida()
    {
        using var factory = CreateFactory();
        await using var scope = factory.Services.CreateAsyncScope();
        var tokens = scope.ServiceProvider.GetRequiredService<AdmissionTokenService>();

        var valid = tokens.Issue("s1", "o1", "e1", TimeSpan.FromMinutes(5));
        Assert.True(tokens.TryVerify(valid, out _, out _));

        // Manipulado: cambia un caracter del payload.
        var parts = valid.Split('.');
        var tampered = parts[0] + "." + parts[1].Replace(parts[1][10], parts[1][10] == 'A' ? 'B' : 'A') + "." + parts[2];
        Assert.False(tokens.TryVerify(tampered, out _, out _));

        // Expirado.
        var expired = tokens.Issue("s1", "o1", "e1", TimeSpan.FromSeconds(-1));
        Assert.False(tokens.TryVerify(expired, out _, out _));

        // Basura.
        Assert.False(tokens.TryVerify("no-es-un-token", out _, out _));
    }

    [Theory]
    [InlineData(50, true, 1, 100, 55)]   // sano: +10%
    [InlineData(100, true, 1, 100, 100)] // sano en el tope: se queda
    [InlineData(50, false, 1, 100, 25)]  // degradado: mitad
    [InlineData(3, false, 1, 100, 1)]    // degradado con piso en mínimo
    [InlineData(1, false, 1, 100, 1)]    // ya en mínimo: no baja más
    public void Tasa_adaptativa_con_histéresis(int current, bool healthy, int min, int max, int expected)
    {
        Assert.Equal(expected, AdmissionRate.Compute(current, healthy, min, max));
    }

    // Fase 5: OnsaleOpened llega por el bus → la fila queda habilitada con la
    // tasa inicial de la config (arranque != 0) y enter funciona sin más pasos.
    [Fact]
    public async Task OnsaleOpened_habilita_la_fila_con_tasa_inicial()
    {
        using var factory = CreateFactory();
        var client = factory.CreateClient();
        var eventId = Guid.NewGuid();
        var onsaleId = eventId.ToString();

        var cfg = await client.PostAsJsonAsync($"/api/queue/admin/{onsaleId}/config", new
        {
            maxConcurrentInside = 50,
            initialRate = 7,
        });
        Assert.Equal(HttpStatusCode.OK, cfg.StatusCode);

        // El evento viaja por el bus REAL → consumer del queue-service.
        var bus = factory.Services.GetRequiredService<IBus>();
        await bus.Publish(new OnsaleOpened(
            eventId, DateTimeOffset.UtcNow,
            new[] { new OnsaleZone(Guid.NewGuid(), "General", 100, 50m) }, 4));

        StatsDto? stats = null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!timeout.IsCancellationRequested)
        {
            stats = await client.GetFromJsonAsync<StatsDto>($"/api/queue/admin/{onsaleId}", timeout.Token);
            if (stats?.Rate == 7)
            {
                break;
            }
            await Task.Delay(100, timeout.Token);
        }
        Assert.NotNull(stats);
        Assert.Equal(7, stats!.Rate);

        // La fila acepta entrada (está "abierta": la config se resuelve).
        var enter = await client.PostAsJsonAsync("/api/queue/enter", new { onsaleId });
        Assert.Equal(HttpStatusCode.OK, enter.StatusCode);
    }

    private sealed record StatsDto(string OnsaleId, long Waiting, long Admitted, int Rate, int Capacity);

    // Fase 6: PoW exigido — sin prueba hay challenge (428); con mala prueba
    // otro challenge (un solo uso); con prueba válida se entra.
    [Fact]
    public async Task Pow_exigido_bloquea_y_luego_admite_con_prueba_valida()
    {
        using var factory = CreateFactory();
        var client = factory.CreateClient();
        var onsaleId = Guid.NewGuid().ToString();

        var cfg = await client.PostAsJsonAsync($"/api/queue/admin/{onsaleId}/config", new
        {
            maxConcurrentInside = 50,
            initialRate = 50,
            powDifficulty = 12,
        });
        Assert.Equal(HttpStatusCode.OK, cfg.StatusCode);

        var bare = await client.PostAsJsonAsync("/api/queue/enter", new { onsaleId });
        Assert.Equal((HttpStatusCode)428, bare.StatusCode);
        var challenge = (await bare.Content.ReadFromJsonAsync<PowDto>())!;
        Assert.Equal(12, challenge.Difficulty);

        // Mala prueba: se consume el challenge y llega OTRO (sigue 428).
        var bad = await client.PostAsJsonAsync("/api/queue/enter", new
        {
            onsaleId,
            challengeId = challenge.ChallengeId,
            nonce = "0",
        });
        Assert.Equal((HttpStatusCode)428, bad.StatusCode);
        var retry = (await bad.Content.ReadFromJsonAsync<PowDto>())!;
        Assert.NotEqual(challenge.ChallengeId, retry.ChallengeId);

        // Prueba válida: se entra.
        var nonce = ProofOfWork.Solve(retry.ChallengeId, 12);
        var good = await client.PostAsJsonAsync("/api/queue/enter", new
        {
            onsaleId,
            challengeId = retry.ChallengeId,
            nonce,
        });
        Assert.Equal(HttpStatusCode.OK, good.StatusCode);
    }

    [Theory]
    [InlineData(8, true)]
    [InlineData(16, true)]
    public void ProofOfWork_resuelve_y_verifica(int difficulty, bool expected)
    {
        var nonce = ProofOfWork.Solve("challenge-test", difficulty);
        Assert.Equal(expected, ProofOfWork.Verify("challenge-test", nonce, difficulty));
        Assert.False(ProofOfWork.Verify("challenge-test", "0", difficulty));
        Assert.False(ProofOfWork.Verify("otro-challenge", nonce, difficulty));
    }

    private sealed record PowDto(string ChallengeId, int Difficulty, int ExpiresInSeconds);
}
