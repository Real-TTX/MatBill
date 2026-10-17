using System.Data;
using System.IO.Compression;
using MatBill.Configuration;
using Npgsql;

namespace MatBill.Services.Backup;

/// <summary>A table in a backup: its columns (the order of the data), how many rows, and where its data is in the archive.</summary>
public sealed record TableInfo(string Name, string[] Columns, long Rows, string Entry);

/// <summary>
/// The database as a set of files in a zip: for every table the raw binary COPY of Postgres (fast, exact, keeps every type), read in one
/// repeatable-read transaction so that all tables are from the same moment. Restoring replaces the content of all tables in one transaction:
/// either everything is back, or nothing changed.
/// </summary>
public sealed class DatabaseArchive(ConfigStore config)
{
    private const string History = "__EFMigrationsHistory";

    public static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    public async Task<List<TableInfo>> ExportAsync(ZipArchive zip, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(config.ConnectionString());
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        var result = new List<TableInfo>();
        foreach (var (name, columns) in await ListTablesAsync(conn, tx, ct))
        {
            var list = string.Join(", ", columns.Select(Quote));
            long rows;
            await using (var count = new NpgsqlCommand($"SELECT count(*) FROM {Quote(name)}", conn, tx))
            {
                rows = (long)(await count.ExecuteScalarAsync(ct))!;
            }

            var entryName = $"database/{name}.bin";
            var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
            await using (var target = entry.Open())
            await using (var copy = await conn.BeginRawBinaryCopyAsync($"COPY (SELECT {list} FROM {Quote(name)} ORDER BY {Quote("Id")}) TO STDOUT (FORMAT BINARY)", ct))
            {
                await copy.CopyToAsync(target, ct);
            }

            result.Add(new TableInfo(name, columns, rows, entryName));
        }

        await tx.CommitAsync(ct);
        return result;
    }

    /// <summary>The newest migration the database has applied, for the check that a backup is not from a newer version than the application.</summary>
    public async Task<string?> LatestMigrationAsync(CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(config.ConnectionString());
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand($"SELECT max(\"MigrationId\") FROM {Quote(History)}", conn);
        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    /// <summary>Replaces the content of every table with the content of the backup. Throws without changing anything if it does not fit.</summary>
    public async Task RestoreAsync(ZipArchive zip, IReadOnlyList<TableInfo> tables, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(config.ConnectionString());
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var current = (await ListTablesAsync(conn, tx, ct)).ToDictionary(t => t.Name, t => t.Columns, StringComparer.Ordinal);

        foreach (var table in tables)
        {
            if (!current.TryGetValue(table.Name, out var columns))
            {
                continue;
            }

            var missing = table.Columns.Where(c => !columns.Contains(c, StringComparer.Ordinal)).ToList();
            if (missing.Count > 0)
            {
                throw new InvalidOperationException($"In der Sicherung hat die Tabelle {table.Name} die Spalte {missing[0]}, die es in dieser Version nicht mehr gibt.");
            }
        }

        var replica = await TryReplicaRoleAsync(conn, tx, ct);
        await Exec(conn, tx, "TRUNCATE TABLE " + string.Join(", ", current.Keys.Select(Quote)) + " RESTART IDENTITY CASCADE", ct);

        var ordered = replica ? tables.ToList() : await InDependencyOrderAsync(conn, tx, tables, ct);
        foreach (var table in ordered.Where(t => current.ContainsKey(t.Name)))
        {
            var entry = zip.GetEntry(table.Entry) ?? throw new InvalidOperationException($"Die Sicherung ist unvollständig: {table.Entry} fehlt.");
            if (table.Rows == 0)
            {
                continue;
            }

            await using var source = entry.Open();
            await using (var copy = await conn.BeginRawBinaryCopyAsync($"COPY {Quote(table.Name)} ({string.Join(", ", table.Columns.Select(Quote))}) FROM STDIN (FORMAT BINARY)", ct))
            {
                await source.CopyToAsync(copy, ct);
            }
        }

        foreach (var table in tables.Where(t => current.ContainsKey(t.Name) && t.Columns.Contains("Id")))
        {
            var q = Quote(table.Name);
            await Exec(conn, tx, $"SELECT setval(pg_get_serial_sequence('{q.Replace("'", "''", StringComparison.Ordinal)}', 'Id'), COALESCE((SELECT max(\"Id\") FROM {q}), 1), (SELECT max(\"Id\") IS NOT NULL FROM {q}))", ct);
        }

        await tx.CommitAsync(ct);
    }

    private static async Task<List<(string Name, string[] Columns)>> ListTablesAsync(NpgsqlConnection conn, NpgsqlTransaction tx, CancellationToken ct)
    {
        const string sql = """
            SELECT t.table_name, c.column_name
            FROM information_schema.tables t
            JOIN information_schema.columns c ON c.table_schema = t.table_schema AND c.table_name = t.table_name
            WHERE t.table_schema = 'public' AND t.table_type = 'BASE TABLE' AND t.table_name <> '__EFMigrationsHistory'
              AND c.is_generated = 'NEVER'
            ORDER BY t.table_name, c.ordinal_position
            """;
        var map = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var table = reader.GetString(0);
            if (!map.TryGetValue(table, out var list))
            {
                map[table] = list = [];
            }

            list.Add(reader.GetString(1));
        }

        return [.. map.Select(p => (p.Key, p.Value.ToArray()))];
    }

    /// <summary>With the replica role Postgres does not check the foreign keys while loading (needs a privileged user); without it the tables are loaded parents first.</summary>
    private static async Task<bool> TryReplicaRoleAsync(NpgsqlConnection conn, NpgsqlTransaction tx, CancellationToken ct)
    {
        await Exec(conn, tx, "SAVEPOINT mb_replica", ct);
        try
        {
            await Exec(conn, tx, "SET LOCAL session_replication_role = replica", ct);
            await Exec(conn, tx, "RELEASE SAVEPOINT mb_replica", ct);
            return true;
        }
        catch (PostgresException)
        {
            await Exec(conn, tx, "ROLLBACK TO SAVEPOINT mb_replica", ct);
            return false;
        }
    }

    private static async Task<List<TableInfo>> InDependencyOrderAsync(NpgsqlConnection conn, NpgsqlTransaction tx, IReadOnlyList<TableInfo> tables, CancellationToken ct)
    {
        const string sql = """
            SELECT child.relname, parent.relname
            FROM pg_constraint c
            JOIN pg_class child ON child.oid = c.conrelid
            JOIN pg_class parent ON parent.oid = c.confrelid
            JOIN pg_namespace n ON n.oid = c.connamespace
            WHERE c.contype = 'f' AND n.nspname = 'public' AND c.conrelid <> c.confrelid
            """;
        var parents = tables.ToDictionary(t => t.Name, _ => new HashSet<string>(StringComparer.Ordinal), StringComparer.Ordinal);
        await using (var cmd = new NpgsqlCommand(sql, conn, tx))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                if (parents.TryGetValue(reader.GetString(0), out var set) && parents.ContainsKey(reader.GetString(1)))
                {
                    set.Add(reader.GetString(1));
                }
            }
        }

        var done = new HashSet<string>(StringComparer.Ordinal);
        var ordered = new List<TableInfo>();
        var rest = tables.ToList();
        while (rest.Count > 0)
        {
            var ready = rest.Where(t => parents[t.Name].All(done.Contains)).ToList();
            if (ready.Count == 0)
            {
                throw new InvalidOperationException("Die Tabellen verweisen im Kreis aufeinander. Zum Wiederherstellen braucht der Datenbankbenutzer erweiterte Rechte (session_replication_role).");
            }

            foreach (var t in ready)
            {
                ordered.Add(t);
                done.Add(t.Name);
                rest.Remove(t);
            }
        }

        return ordered;
    }

    private static async Task Exec(NpgsqlConnection conn, NpgsqlTransaction tx, string sql, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
