using Npgsql;
using Testcontainers.PostgreSql;

namespace TicketNow.UnitTests;

// PostgreSQL REAL: el outbox de MassTransit usa SQL específico de Postgres
// (SKIP LOCKED) que InMemory no soporta.
//
// Dos modos (por estabilidad del entorno):
//  - Si TICKETNOW_TEST_POSTGRES está definido (connection string a un PG existente,
//    p.ej. el de compose), se crea una DATABASE única por corrida de tests.
//  - Si no, se levanta un contenedor efímero con Testcontainers.
public sealed class PostgresFixture : IAsyncLifetime
{
    private PostgreSqlContainer? _container;
    private string _adminConnectionString = null!;
    private readonly List<string> _createdDatabases = new();

    public string DatabaseName { get; } = $"ticketnow_test_{Guid.NewGuid():N}";

    public string ConnectionString { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var shared = Environment.GetEnvironmentVariable("TICKETNOW_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(shared))
        {
            _container = new PostgreSqlBuilder()
                .WithImage("postgres:16-alpine")
                .WithDatabase("ticketnow")
                .WithUsername("ticketnow")
                .WithPassword("ticketnow")
                .Build();
            await _container.StartAsync();
            _adminConnectionString = _container.GetConnectionString();
        }
        else
        {
            _adminConnectionString = shared;
        }

        ConnectionString = await CreateDatabaseAsync("shared");
    }

    /// <summary>
    /// Crea una database adicional (una por servicio en tests multi-contexto, para
    /// que las tablas compartidas no colisionen entre migraciones).
    /// </summary>
    public async Task<string> CreateDatabaseAsync(string suffix)
    {
        var name = $"{DatabaseName}_{suffix}";
        await using (var admin = new NpgsqlConnection(_adminConnectionString))
        {
            await admin.OpenAsync();
            await using var cmd = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", admin);
            await cmd.ExecuteNonQueryAsync();
        }
        _createdDatabases.Add(name);

        return new NpgsqlConnectionStringBuilder(_adminConnectionString)
        {
            Database = name,
            // Resiliencia ante RSTs espurios del port-forward de Docker Desktop:
            // keepalive + reciclado agresivo de conexiones idle + timeouts amplios.
            KeepAlive = 5,
            ConnectionIdleLifetime = 30,
            Timeout = 30,
            CommandTimeout = 60,
        }.ToString();
    }

    public async Task DisposeAsync()
    {
        try
        {
            await using var admin = new NpgsqlConnection(_adminConnectionString);
            await admin.OpenAsync();
            foreach (var name in _createdDatabases.Append(DatabaseName).Distinct())
            {
                try
                {
                    await using var cmd = new NpgsqlCommand($"DROP DATABASE \"{name}\" WITH (FORCE)", admin);
                    await cmd.ExecuteNonQueryAsync();
                }
                catch
                {
                    // best effort por DB
                }
            }
        }
        catch
        {
            // best effort: las DBs huérfanas no rompen nada
        }
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }
}
