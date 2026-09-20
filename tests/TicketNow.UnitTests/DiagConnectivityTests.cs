using Npgsql;

namespace TicketNow.UnitTests;

public class DiagConnectivityTests
{
    private static string Cs => Environment.GetEnvironmentVariable("TICKETNOW_TEST_POSTGRES")
        ?? "Host=localhost;Port=5435;Database=ticketnow;Username=ticketnow;Password=ticketnow";

    [Fact]
    public async Task Diag_pg_transacciones_concurrentes()
    {
        var fails = 0;
        for (var round = 0; round < 10; round++)
        {
            await Task.WhenAll(Enumerable.Range(0, 20).Select(async _ =>
            {
                try
                {
                    await using var connection = new NpgsqlConnection(Cs);
                    await connection.OpenAsync();
                    await using var tx = await connection.BeginTransactionAsync();
                    await using var cmd = new NpgsqlCommand("SELECT pg_sleep(0.05)", connection, tx);
                    await cmd.ExecuteScalarAsync();
                    await tx.CommitAsync();
                }
                catch
                {
                    Interlocked.Increment(ref fails);
                }
            }));
        }
        Assert.True(fails == 0, $"abortos en transacciones: {fails}/200");
    }
}
