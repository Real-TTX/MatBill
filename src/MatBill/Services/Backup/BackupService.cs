using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using MatBill.Configuration;
using MatBill.Data;
using MatBill.Data.Entities;
using MatBill.Localization;
using MatBill.Services.Auth;
using MatBill.Storage;
using MatBill.UiKit;
using Microsoft.EntityFrameworkCore;

namespace MatBill.Services.Backup;

public sealed record BackupFileEntry(long Id, string StorageKey, string Entry, string Sha256, long Size);

/// <summary>What is inside a backup: the version of the application, the tables with their columns and the files with their checksums.</summary>
public sealed record BackupManifest(int Format, string App, DateTime CreatedUtc, BackupScope Scope, string? Migration, List<TableInfo> Tables, List<BackupFileEntry> Files, bool HasSettings, List<string> Problems);

public sealed record RestoreOptions(bool Files, bool Settings);

public sealed record RestoreResult(int Tables, long Rows, int Files, bool SettingsRestored, string? SafetyCopy, IReadOnlyList<string> Notes);

public sealed class BackupException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Makes backups and restores them. A backup is one zip: the tables of the database (binary COPY), the settings (without the database password) and the key
/// ring that protects the secrets in them, and, when the plan says so, every stored file. It can be encrypted with a passphrase. A restore first makes a safety
/// copy of the current system in the volume, then replaces the database in one transaction.
/// </summary>
public sealed class BackupService(
    ConfigStore config, SecretProtector secrets, BackupStoreFactory stores, FileStoreService files, MbDbContext db, DatabaseArchive database,
    TimeProvider clock, SessionCache sessions, ActivityLogger log, ILogger<BackupService> logger)
{
    public const int FormatVersion = 1;

    private static readonly SemaphoreSlim Busy = new(1, 1);

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static bool IsBusy => Busy.CurrentCount == 0;

    /// <summary>The marks of <see cref="BackupPlan.LastStatus"/>: a code, not a text (older versions wrote "Erfolgreich" and "Fehlgeschlagen: …").</summary>
    private const string StatusSuccess = "Success";

    private const string StatusFailed = "Failed: ";

    public static bool IsFailedStatus(string? status)
        => status is not null && (status.StartsWith(StatusFailed, StringComparison.Ordinal) || status.StartsWith("Fehl", StringComparison.Ordinal));

    /// <summary>
    /// The folder of the safety copies made before a restore. The name is on disk in existing installations, so it stays as it was
    /// (it means "before restore").
    /// </summary>
    public static string SafetyFolder(string dataDir) => Path.Combine(dataDir, "backups", "vor-wiederherstellung");

    private TimeZoneInfo Zone => Fmt.Zone;

    private string TempDirectory
    {
        get
        {
            var configured = config.Current.Backup.TempDirectory;
            var dir = string.IsNullOrWhiteSpace(configured) ? Path.Combine(config.DataDir, "tmp") : configured;
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    // ------------------------------------------------------------------------------------------------ making a backup

    /// <summary>Writes the backup to a file in the temporary folder (encrypted when there is a passphrase). The caller deletes the file.</summary>
    public async Task<string> CreateFileAsync(BackupScope scope, string? passphrase, CancellationToken ct = default)
    {
        var zipPath = Path.Combine(TempDirectory, $"mb-backup-{Guid.NewGuid():N}.zip");
        string? encryptedPath = null;
        try
        {
            await WriteArchiveAsync(zipPath, scope, ct);
            if (string.IsNullOrEmpty(passphrase))
            {
                return zipPath;
            }

            encryptedPath = zipPath + ".enc";
            await using (var input = File.OpenRead(zipPath))
            await using (var output = File.Create(encryptedPath))
            {
                await BackupCrypto.EncryptAsync(input, output, passphrase, ct);
            }

            File.Delete(zipPath);
            return encryptedPath;
        }
        catch
        {
            TryDelete(zipPath);
            if (encryptedPath is not null)
            {
                TryDelete(encryptedPath);
            }

            throw;
        }
    }

    private async Task WriteArchiveAsync(string path, BackupScope scope, CancellationToken ct)
    {
        var problems = new List<string>();
        List<TableInfo> tables;
        var fileEntries = new List<BackupFileEntry>();
        var hasSettings = false;

        await using (var stream = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 81920, useAsync: true))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            tables = await database.ExportAsync(zip, ct);
            hasSettings = AddSettings(zip);
            if (scope == BackupScope.SystemAndFiles)
            {
                await AddFilesAsync(zip, fileEntries, problems, ct);
            }

            var manifest = new BackupManifest(FormatVersion, AppVersion.Current, clock.GetUtcNow().UtcDateTime, scope, await database.LatestMigrationAsync(ct), tables, fileEntries, hasSettings, problems);
            var entry = zip.CreateEntry("manifest.json", CompressionLevel.Optimal);
            await using var writer = entry.Open();
            await JsonSerializer.SerializeAsync(writer, manifest, Json, ct);
        }
    }

    /// <summary>The settings without the section of the database (its password must not end up in a backup in clear text) and the key ring.</summary>
    private bool AddSettings(ZipArchive zip)
    {
        var configPath = Path.Combine(config.DataDir, "config", "app.json");
        var any = false;
        if (File.Exists(configPath))
        {
            var node = JsonNode.Parse(File.ReadAllText(configPath)) as JsonObject;
            node?.Remove("Database");
            node?.Remove("database");
            var entry = zip.CreateEntry("settings/app.json", CompressionLevel.Optimal);
            using var writer = new StreamWriter(entry.Open());
            writer.Write(node?.ToJsonString(Json));
            any = true;
        }

        var keys = Path.Combine(config.DataDir, "keys");
        if (Directory.Exists(keys))
        {
            foreach (var file in Directory.EnumerateFiles(keys))
            {
                zip.CreateEntryFromFile(file, "settings/keys/" + Path.GetFileName(file), CompressionLevel.Optimal);
                any = true;
            }
        }

        return any;
    }

    private async Task AddFilesAsync(ZipArchive zip, List<BackupFileEntry> entries, List<string> problems, CancellationToken ct)
    {
        var stored = await db.StoredFile.AsNoTracking().OrderBy(f => f.Id).ToListAsync(ct);
        foreach (var file in stored)
        {
            try
            {
                await using var source = await files.OpenAsync(file, ct);
                var entry = zip.CreateEntry($"files/{file.Id}", CompressionLevel.Optimal);
                await using var target = entry.Open();
                await source.CopyToAsync(target, ct);
                entries.Add(new BackupFileEntry(file.Id, file.StorageKey, entry.FullName, file.Sha256, file.SizeBytes));
            }
            catch (Exception ex) when (ex is StorageException or IOException)
            {
                problems.Add($"{file.FileName}: {ex.Message}");
                logger.LogWarning(ex, "The file {File} could not be added to the backup.", file.FileName);
            }
        }
    }

    // ------------------------------------------------------------------------------------------------ plans

    /// <summary>Runs a plan: makes the file, writes it to the target, applies the rules of retention and writes the run. A failure is a run too.</summary>
    public async Task<BackupRun> RunPlanAsync(BackupPlan plan, bool manual, CancellationToken ct = default)
    {
        if (!await Busy.WaitAsync(0, ct))
        {
            throw new BackupException(Text.T("Backup.Busy"));
        }

        var started = clock.GetUtcNow().UtcDateTime;
        var target = plan.Target ?? await db.BackupTarget.FirstOrDefaultAsync(t => t.Id == plan.TargetId, ct) ?? throw new BackupException(Text.T("Backup.Service.TargetGone"));
        var run = new BackupRun { PlanId = plan.Id, PlanName = plan.Name, TargetName = target.Name, StartDate = started, IsManual = manual, Scope = plan.Scope };
        string? file = null;
        try
        {
            var passphrase = secrets.Unprotect(plan.PassphraseProtected);
            file = await CreateFileAsync(plan.Scope, passphrase, ct);
            var name = BackupNames.Make(plan.Name, started, passphrase.Length > 0);
            var store = stores.Create(target);
            await using (var input = File.OpenRead(file))
            {
                run.SizeBytes = input.Length;
                await store.WriteAsync(name, input, ct);
            }

            run.FileName = name;
            var removed = await ApplyRetentionAsync(plan, store, ct);
            run.Status = BackupRunStatus.Success;
            run.Message = removed > 0 ? Text.T("Backup.Service.Removed", removed) : null;
            plan.FailedAttempts = 0;
            plan.LastStatus = StatusSuccess;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "The backup {Plan} failed.", plan.Name);
            run.Status = BackupRunStatus.Failed;
            run.Message = ex.Message;
            plan.FailedAttempts++;
            plan.LastStatus = StatusFailed + ex.Message;
        }
        finally
        {
            if (file is not null)
            {
                TryDelete(file);
            }

            Busy.Release();
        }

        run.EndDate = clock.GetUtcNow().UtcDateTime;
        plan.LastRunDate = run.EndDate;
        plan.NextRunDate = NextRunAfterRun(plan, run);
        db.BackupRun.Add(run);
        db.BackupPlan.Update(plan);
        await db.SaveChangesAsync(ct);
        await log.LogAsync(run.Status == BackupRunStatus.Success ? Text.T("Backup.Log.Created") : Text.T("Backup.Log.Failed"), "Backup", run.Id, $"{plan.Name} → {target.Name}");
        return run;
    }

    /// <summary>A failed run is tried again soon (three times); after that, or after a success, the plan goes on with its schedule.</summary>
    private DateTime NextRunAfterRun(BackupPlan plan, BackupRun run)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        return run.Status == BackupRunStatus.Failed && plan.FailedAttempts <= 3 ? now.AddMinutes(15 * plan.FailedAttempts) : BackupSchedule.Next(plan, now, Zone);
    }

    /// <summary>Runs every plan that is due. Called by the scheduler every half minute.</summary>
    public async Task RunDueAsync(CancellationToken ct = default)
    {
        if (!config.Current.Backup.Enabled || IsBusy)
        {
            return;
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var plans = await db.BackupPlan.Include(p => p.Target).Where(p => p.IsEnabled).ToListAsync(ct);
        foreach (var plan in plans)
        {
            if (plan.NextRunDate is null)
            {
                plan.NextRunDate = BackupSchedule.Next(plan, now, Zone);
                await db.SaveChangesAsync(ct);
                continue;
            }

            if (plan.NextRunDate <= now)
            {
                await RunPlanAsync(plan, manual: false, ct);
            }
        }
    }

    private async Task<int> ApplyRetentionAsync(BackupPlan plan, IBackupStore store, CancellationToken ct)
    {
        var label = BackupNames.LabelOf(BackupNames.Make(plan.Name, clock.GetUtcNow().UtcDateTime, false));
        var mine = (await store.ListAsync(ct)).Where(f => BackupNames.LabelOf(f.Name) == label).ToList();
        var delete = BackupRetention.ToDelete(mine, plan.KeepLast, plan.KeepDaily, plan.KeepWeekly, plan.KeepMonthly, Zone);
        foreach (var f in delete)
        {
            try
            {
                await store.DeleteAsync(f.Name, ct);
            }
            catch (Exception ex) when (ex is StorageException or IOException)
            {
                logger.LogWarning(ex, "The old backup {Name} could not be removed.", f.Name);
            }
        }

        return delete.Count;
    }

    // ------------------------------------------------------------------------------------------------ restoring

    /// <summary>Reads the manifest of a backup without changing anything: for the check before a restore. Decrypts into a temporary file when needed.</summary>
    public async Task<BackupManifest> ReadManifestAsync(string zipPath, CancellationToken ct = default)
    {
        await using var stream = File.OpenRead(zipPath);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        return await ReadManifestAsync(zip, ct);
    }

    private static async Task<BackupManifest> ReadManifestAsync(ZipArchive zip, CancellationToken ct)
    {
        var entry = zip.GetEntry("manifest.json") ?? throw new BackupException(Text.T("Backup.Service.NoManifest"));
        await using var reader = entry.Open();
        var manifest = await JsonSerializer.DeserializeAsync<BackupManifest>(reader, Json, ct) ?? throw new BackupException(Text.T("Backup.Service.EmptyManifest"));
        return manifest.Format > FormatVersion ? throw new BackupException(Text.T("Backup.Service.Newer")) : manifest;
    }

    /// <summary>
    /// Brings the file of a backup (plain or encrypted, from an upload or from a target) into a plain zip in the temporary folder: first the bytes are kept as they
    /// came (a share cannot be read twice), then decrypted when they are encrypted. The caller deletes the result. Returns the name of the staged file.
    /// </summary>
    public async Task<string> StageAsync(Stream source, string? passphrase, CancellationToken ct = default)
    {
        var raw = Path.Combine(TempDirectory, $"mb-raw-{Guid.NewGuid():N}.tmp");
        var plain = Path.Combine(TempDirectory, $"{StagedPrefix}{Guid.NewGuid():N}.zip");
        try
        {
            await using (var output = File.Create(raw))
            {
                await source.CopyToAsync(output, ct);
            }

            await using var input = File.OpenRead(raw);
            await using var result = File.Create(plain);
            if (await BackupCrypto.IsEncryptedAsync(input, ct))
            {
                if (string.IsNullOrEmpty(passphrase))
                {
                    throw new BackupException(Text.T("Backup.Service.Encrypted"));
                }

                try
                {
                    await BackupCrypto.DecryptAsync(input, result, passphrase, ct);
                }
                catch (System.Security.Cryptography.CryptographicException ex)
                {
                    throw new BackupException(ex.Message, ex);
                }
            }
            else
            {
                await input.CopyToAsync(result, ct);
            }

            return Path.GetFileName(plain);
        }
        catch
        {
            TryDelete(plain);
            throw;
        }
        finally
        {
            TryDelete(raw);
        }
    }

    public const string StagedPrefix = "mb-restore-";

    /// <summary>The path of a staged file, from the name that was given out; null when the name is not one of ours (it comes from a request).</summary>
    public string? StagedPath(string? name)
    {
        if (name is null || !name.StartsWith(StagedPrefix, StringComparison.Ordinal) || !name.EndsWith(".zip", StringComparison.Ordinal) || name != Path.GetFileName(name))
        {
            return null;
        }

        var path = Path.Combine(TempDirectory, name);
        return File.Exists(path) ? path : null;
    }

    /// <summary>Removes staged files that nobody went on with.</summary>
    public void CleanStaged()
    {
        foreach (var f in new DirectoryInfo(TempDirectory).GetFiles().Where(f => (f.Name.StartsWith(StagedPrefix, StringComparison.Ordinal) || f.Name.StartsWith("mb-raw-", StringComparison.Ordinal)) && f.LastWriteTimeUtc < clock.GetUtcNow().UtcDateTime.AddHours(-2)))
        {
            TryDelete(f.FullName);
        }
    }

    public void DeleteStaged(string path) => TryDelete(path);

    /// <summary>Replaces the database (and, on request, the files and the settings) with the content of the backup at <paramref name="zipPath"/>.</summary>
    public async Task<RestoreResult> RestoreAsync(string zipPath, RestoreOptions options, CancellationToken ct = default)
    {
        if (!await Busy.WaitAsync(0, ct))
        {
            throw new BackupException(Text.T("Backup.Busy"));
        }

        try
        {
            BackupManifest manifest;
            try
            {
                manifest = await ReadManifestAsync(zipPath, ct);
            }
            catch (InvalidDataException ex)
            {
                throw new BackupException(Text.T("Backup.Restore.Invalid"), ex);
            }

            var latest = await database.LatestMigrationAsync(ct);
            if (manifest.Migration is not null && latest is not null && string.CompareOrdinal(manifest.Migration, latest) > 0)
            {
                throw new BackupException(Text.T("Backup.Service.NewerUpdate"));
            }

            var safety = await MakeSafetyCopyAsync(ct);
            var notes = new List<string>();
            await using var stream = File.OpenRead(zipPath);
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
            try
            {
                await database.RestoreAsync(zip, manifest.Tables, ct);
            }
            catch (Exception ex) when (ex is Npgsql.NpgsqlException or InvalidOperationException)
            {
                throw new BackupException(Text.T("Backup.Service.DatabaseFailed", ex.Message), ex);
            }

            sessions.InvalidateAll();
            var restoredFiles = options.Files && manifest.Files.Count > 0 ? await RestoreFilesAsync(zip, manifest, notes, ct) : 0;
            if (options.Files && manifest.Scope == BackupScope.SystemOnly)
            {
                notes.Add(Text.T("Backup.Service.NoFiles"));
            }

            var settings = options.Settings && manifest.HasSettings && RestoreSettings(zip, notes);
            await log.LogAsync(Text.T("Backup.Log.Restored"), "Backup", null, Text.T("Backup.Log.Restored.Detail", Fmt.Moment(manifest.CreatedUtc), manifest.App));
            return new RestoreResult(manifest.Tables.Count, manifest.Tables.Sum(t => t.Rows), restoredFiles, settings, safety, notes);
        }
        finally
        {
            Busy.Release();
        }
    }

    private async Task<int> RestoreFilesAsync(ZipArchive zip, BackupManifest manifest, List<string> notes, CancellationToken ct)
    {
        var count = 0;
        foreach (var f in manifest.Files)
        {
            var entry = zip.GetEntry(f.Entry);
            if (entry is null)
            {
                notes.Add(Text.T("Backup.Service.FileMissing", f.Id));
                continue;
            }

            await using var source = entry.Open();
            var key = await files.RestoreAsync(f.StorageKey, source, ct);
            if (key != f.StorageKey)
            {
                await db.StoredFile.Where(x => x.Id == f.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.StorageKey, key), ct);
            }

            count++;
        }

        if (manifest.Problems.Count > 0)
        {
            notes.Add(Text.T("Backup.Service.FilesMissing", manifest.Problems.Count, manifest.Problems[0]));
        }

        return count;
    }

    /// <summary>
    /// The settings of the backup replace those of this installation (the database connection and the address stay), and the keys of the key ring that
    /// are not here yet are added. The key ring is only read at start-up: the secrets in the settings work after a restart.
    /// </summary>
    private bool RestoreSettings(ZipArchive zip, List<string> notes)
    {
        var restored = false;
        foreach (var entry in zip.Entries.Where(e => e.FullName.StartsWith("settings/keys/", StringComparison.Ordinal) && e.Name.Length > 0))
        {
            var target = Path.Combine(config.DataDir, "keys", Path.GetFileName(entry.Name));
            if (File.Exists(target))
            {
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target);
            restored = true;
        }

        if (zip.GetEntry("settings/app.json") is { } settings)
        {
            using var reader = new StreamReader(settings.Open());
            var restoredConfig = JsonSerializer.Deserialize<AppConfig>(reader.ReadToEnd(), new JsonSerializerOptions(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true });
            if (restoredConfig is not null)
            {
                config.Update(c =>
                {
                    c.Company = restoredConfig.Company;
                    c.Invoicing = restoredConfig.Invoicing;
                    c.Mail = restoredConfig.Mail;
                    c.Banking = restoredConfig.Banking;
                    c.Storage = restoredConfig.Storage;
                    c.Security = restoredConfig.Security;
                    c.Backup = restoredConfig.Backup;
                });
                restored = true;
            }
        }

        if (restored)
        {
            notes.Add(Text.T("Backup.Service.SettingsBack"));
        }

        return restored;
    }

    /// <summary>Before a restore the current system is saved into the volume, so that a restore of the wrong file can be taken back. The newest three are kept.</summary>
    private async Task<string?> MakeSafetyCopyAsync(CancellationToken ct)
    {
        var folder = SafetyFolder(config.DataDir);
        try
        {
            var file = await CreateFileAsync(BackupScope.SystemOnly, null, ct);
            Directory.CreateDirectory(folder);
            var target = Path.Combine(folder, BackupNames.Make("before-restore", clock.GetUtcNow().UtcDateTime, false));
            File.Move(file, target, overwrite: true);
            foreach (var old in new DirectoryInfo(folder).GetFiles().Where(f => BackupNames.IsBackup(f.Name)).OrderByDescending(f => f.LastWriteTimeUtc).Skip(3))
            {
                old.Delete();
            }

            return target;
        }
        catch (Exception ex) when (ex is IOException or Npgsql.NpgsqlException or UnauthorizedAccessException)
        {
            throw new BackupException(Text.T("Backup.Service.SafetyFailed", ex.Message), ex);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
    }
}
