using MatBill.Configuration;
using MatBill.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace MatBill.Services;

/// <summary>
/// Waits for PostgreSQL, creates the database when it is missing and applies the migrations. The stack therefore needs no setup script: the release
/// compose file is one app, one database and two volumes.
/// </summary>
public static class DatabaseInitializer
{
    public static async Task InitializeAsync(IServiceProvider services, ConfigStore config, ILogger logger, CancellationToken ct = default)
    {
        await EnsureDatabaseAsync(config, logger, ct);

        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MbDbContext>();
        await db.Database.MigrateAsync(ct);
    }

    private static async Task EnsureDatabaseAsync(ConfigStore config, ILogger logger, CancellationToken ct)
    {
        var target = config.Current.Database.Database;
        var deadline = DateTime.UtcNow.AddSeconds(90);
        Exception? last = null;

        while (DateTime.UtcNow < deadline)
        {
            try
            {
                await using var connection = new NpgsqlConnection(config.ConnectionString("postgres"));
                await connection.OpenAsync(ct);
                await using var exists = new NpgsqlCommand("SELECT 1 FROM pg_database WHERE datname = @name", connection);
                exists.Parameters.AddWithValue("name", target);
                if (await exists.ExecuteScalarAsync(ct) is null)
                {
                    logger.LogInformation("Creating the database {Database}.", target);
                    await using var create = new NpgsqlCommand($"CREATE DATABASE \"{target.Replace("\"", "\"\"")}\"", connection);
                    await create.ExecuteNonQueryAsync(ct);
                }

                return;
            }
            catch (Exception ex) when (ex is NpgsqlException or System.Net.Sockets.SocketException or TimeoutException)
            {
                last = ex;
                logger.LogInformation("Waiting for PostgreSQL ({Message}) ...", ex.Message);
                await Task.Delay(2000, ct);
            }
        }

        throw new InvalidOperationException("PostgreSQL did not become available.", last);
    }
}
