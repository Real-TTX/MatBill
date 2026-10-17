using System.IO.Compression;
using MatBill.Data;
using MatBill.Data.Entities;
using MatBill.Services;
using MatBill.Services.Backup;
using MatBill.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MatBill.Tests.Integration;

/// <summary>A clock the test moves by hand, so that backups made one after the other have different names.</summary>
public sealed class ManualClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}

[Collection(IntegrationCollection.Name)]
public sealed class BackupIntegrationTests : IClassFixture<AppFixture>
{
    private readonly AppFixture _app;

    public BackupIntegrationTests(AppFixture app) => _app = app;

    private static BackupService ServiceOf(IServiceProvider sp, TimeProvider clock) => ActivatorUtilities.CreateInstance<BackupService>(sp, clock);

    private static string NewFolder()
    {
        var folder = Path.Combine(Path.GetTempPath(), "mb-backup-it-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        return folder;
    }

    private async Task<(BackupTarget Target, BackupPlan Plan)> NewPlanAsync(string folder, int keepLast, BackupScope scope = BackupScope.SystemOnly, string? passphrase = null)
        => await _app.WithScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<MbDbContext>();
            var target = new BackupTarget { Name = "Test " + Guid.NewGuid().ToString("N")[..4], Kind = BackupTargetKind.Local, Path = folder };
            db.BackupTarget.Add(target);
            await db.SaveChangesAsync();
            var plan = new BackupPlan
            {
                Name = "Plan " + Guid.NewGuid().ToString("N")[..4], TargetId = target.Id, Scope = scope, Schedule = ScheduleKind.Daily, KeepLast = keepLast,
                PassphraseProtected = passphrase is null ? null : sp.GetRequiredService<Configuration.SecretProtector>().Protect(passphrase),
            };
            db.BackupPlan.Add(plan);
            await db.SaveChangesAsync();
            return (target, plan);
        });

    [DatabaseFact]
    public async Task A_plan_writes_to_its_target_and_removes_the_backups_it_no_longer_keeps()
    {
        await _app.EnsureAdminAsync();
        var folder = NewFolder();
        var (_, plan) = await NewPlanAsync(folder, keepLast: 2);
        var clock = new ManualClock(new DateTimeOffset(2026, 10, 12, 1, 0, 0, TimeSpan.Zero));

        for (var i = 0; i < 4; i++)
        {
            var run = await _app.WithScopeAsync(async sp =>
            {
                var db = sp.GetRequiredService<MbDbContext>();
                var current = await db.BackupPlan.Include(p => p.Target).FirstAsync(p => p.Id == plan.Id);
                return await ServiceOf(sp, clock).RunPlanAsync(current, manual: true);
            });
            Assert.Equal(BackupRunStatus.Success, run.Status);
            Assert.True(run.SizeBytes > 0);
            clock.Advance(TimeSpan.FromMinutes(5));
        }

        var files = new DirectoryInfo(folder).GetFiles("*.mbbackup").Select(f => f.Name).OrderBy(n => n).ToList();
        Assert.Equal(2, files.Count);
        Assert.Contains("20261012-011000", files[0]);
        Assert.Contains("20261012-011500", files[1]);

        var runs = await _app.WithScopeAsync(sp => sp.GetRequiredService<MbDbContext>().BackupRun.AsNoTracking().Where(r => r.PlanId == plan.Id).CountAsync());
        Assert.Equal(4, runs);
        Directory.Delete(folder, recursive: true);
    }

    [DatabaseFact]
    public async Task A_plan_that_is_due_runs_by_itself_and_is_planned_again()
    {
        await _app.EnsureAdminAsync();
        var folder = NewFolder();
        var (_, plan) = await NewPlanAsync(folder, keepLast: 3);
        await _app.WithScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<MbDbContext>();
            (await db.BackupPlan.FirstAsync(p => p.Id == plan.Id)).NextRunDate = DateTime.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync();
        });

        await _app.WithScopeAsync(sp => sp.GetRequiredService<BackupService>().RunDueAsync());

        var after = await _app.WithScopeAsync(sp => sp.GetRequiredService<MbDbContext>().BackupPlan.AsNoTracking().FirstAsync(p => p.Id == plan.Id));
        Assert.NotNull(after.LastRunDate);
        Assert.True(after.NextRunDate > DateTime.UtcNow, "the next run is in the future");
        Assert.Single(new DirectoryInfo(folder).GetFiles("*.mbbackup"));
        Directory.Delete(folder, recursive: true);
    }

    [DatabaseFact]
    public async Task A_restore_brings_back_data_and_files_and_the_numbers_go_on()
    {
        await _app.EnsureAdminAsync();
        var (kept, _, keptFile) = await _app.CreateInvoiceWithPdfAsync("Behalten");
        var storedBefore = new List<StoredFile> { keptFile };
        Assert.NotEmpty(storedBefore);

        // the backup: encrypted, with the files
        string archive;
        await using (var scope = _app.Services.CreateAsyncScope())
        {
            archive = await ServiceOf(scope.ServiceProvider, TimeProvider.System).CreateFileAsync(BackupScope.SystemAndFiles, "Geheim-2026");
        }

        await using (var stream = File.OpenRead(archive))
        {
            Assert.True(await BackupCrypto.IsEncryptedAsync(stream));
        }

        // afterwards: one more customer and invoice, and the stored PDFs are gone
        var (lost, _, _) = await _app.CreateInvoiceWithPdfAsync("Verloren");
        foreach (var f in Directory.EnumerateFiles(Path.Combine(_app.DataDir, "files"), "*", SearchOption.AllDirectories))
        {
            File.Delete(f);
        }

        string staged;
        RestoreResult result;
        await using (var scope = _app.Services.CreateAsyncScope())
        {
            var backups = ServiceOf(scope.ServiceProvider, TimeProvider.System);

            // a wrong passphrase is refused before anything is touched
            await using (var wrong = File.OpenRead(archive))
            {
                await Assert.ThrowsAsync<BackupException>(() => backups.StageAsync(wrong, "falsch"));
            }

            await using (var noPassphrase = File.OpenRead(archive))
            {
                await Assert.ThrowsAsync<BackupException>(() => backups.StageAsync(noPassphrase, null));
            }

            await using var input = File.OpenRead(archive);
            staged = await backups.StageAsync(input, "Geheim-2026");
            var path = backups.StagedPath(staged)!;
            var manifest = await backups.ReadManifestAsync(path);
            Assert.Equal(BackupScope.SystemAndFiles, manifest.Scope);
            Assert.True(manifest.Tables.Sum(t => t.Rows) > 0);
            Assert.NotEmpty(manifest.Files);

            result = await backups.RestoreAsync(path, new RestoreOptions(Files: true, Settings: false));
            backups.DeleteStaged(path);
        }

        File.Delete(archive);
        Assert.True(result.Tables > 10);
        Assert.True(result.Files >= 1);
        Assert.NotNull(result.SafetyCopy);
        Assert.True(File.Exists(result.SafetyCopy));

        await _app.WithScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<MbDbContext>();
            Assert.True(await db.Customer.AnyAsync(c => c.Id == kept.Id));
            Assert.False(await db.Customer.AnyAsync(c => c.Id == lost.Id));

            // the file is readable again, byte for byte
            var stored = await db.StoredFile.AsNoTracking().FirstAsync(f => f.Id == storedBefore[0].Id);
            var bytes = await sp.GetRequiredService<FileStoreService>().ReadAllAsync(stored);
            Assert.Equal(storedBefore[0].Sha256, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant());

            // new rows get numbers after the restored ones: the sequences were set
            var next = new Customer { Number = "K-NACH", Name = "Nach der Wiederherstellung" };
            db.Customer.Add(next);
            await db.SaveChangesAsync();
            Assert.True(next.Id > kept.Id);
        });
    }

    [DatabaseFact]
    public async Task A_backup_from_a_newer_version_or_a_damaged_file_is_refused_and_changes_nothing()
    {
        await _app.EnsureAdminAsync();
        var (kept, _, _) = await _app.CreateInvoiceWithPdfAsync("Unverändert");

        string archive;
        await using (var scope = _app.Services.CreateAsyncScope())
        {
            archive = await ServiceOf(scope.ServiceProvider, TimeProvider.System).CreateFileAsync(BackupScope.SystemOnly, null);
        }

        // from the future: the manifest names a migration this version does not know
        string manifestText;
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Update))
        {
            var entry = zip.GetEntry("manifest.json")!;
            using (var reader = new StreamReader(entry.Open()))
            {
                manifestText = await reader.ReadToEndAsync();
            }

            entry.Delete();
            var changed = System.Text.RegularExpressions.Regex.Replace(manifestText, "\"migration\": \"[^\"]+\"", "\"migration\": \"99999999999999_Zukunft\"");
            await using var writer = new StreamWriter(zip.CreateEntry("manifest.json").Open());
            await writer.WriteAsync(changed);
        }

        await using (var scope = _app.Services.CreateAsyncScope())
        {
            var backups = ServiceOf(scope.ServiceProvider, TimeProvider.System);
            var error = await Assert.ThrowsAsync<BackupException>(() => backups.RestoreAsync(archive, new RestoreOptions(true, false)));
            Assert.Contains("neueren Version", error.Message);

            var notAZip = Path.Combine(Path.GetTempPath(), "mb-not-a-zip-" + Guid.NewGuid().ToString("N")[..6] + ".zip");
            await File.WriteAllTextAsync(notAZip, "das ist keine Sicherung");
            await Assert.ThrowsAsync<BackupException>(() => backups.RestoreAsync(notAZip, new RestoreOptions(true, false)));
            File.Delete(notAZip);
        }

        File.Delete(archive);
        Assert.True(await _app.WithScopeAsync(sp => sp.GetRequiredService<MbDbContext>().Customer.AnyAsync(c => c.Id == kept.Id)));
    }

    [DatabaseFact]
    public async Task The_settings_in_a_backup_have_no_database_password()
    {
        await _app.EnsureAdminAsync();
        await using var scope = _app.Services.CreateAsyncScope();
        var archive = await ServiceOf(scope.ServiceProvider, TimeProvider.System).CreateFileAsync(BackupScope.SystemOnly, null);

        using var zip = ZipFile.OpenRead(archive);
        var settings = zip.GetEntry("settings/app.json");
        if (settings is not null)
        {
            using var reader = new StreamReader(settings.Open());
            var text = await reader.ReadToEndAsync();
            Assert.DoesNotContain("\"Database\"", text);
            Assert.DoesNotContain(Environment.GetEnvironmentVariable(AppFixture.Variable)!.Split("Password=")[^1].Split(';')[0], text);
        }

        zip.Dispose();
        File.Delete(archive);
    }
}
