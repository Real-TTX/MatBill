using MatBill.Data;
using MatBill.Data.Entities;
using MatBill.Services;
using MatBill.Services.Backup;
using MatBill.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MatBill.Tests.Integration;

/// <summary>A test that needs a Samba server: <c>MATBILL_TEST_SMB</c> is <c>host;share=name;user=name;password=secret</c> (the CI starts one on port 445).</summary>
public sealed class SmbFactAttribute : FactAttribute
{
    public const string Variable = "MATBILL_TEST_SMB";

    public SmbFactAttribute()
    {
        if (Settings is null)
        {
            Skip = $"{Variable} is not set: no SMB server to talk to.";
        }
    }

    public static (string Host, string Share, string User, string Password)? Settings
    {
        get
        {
            var raw = Environment.GetEnvironmentVariable(Variable);
            if (string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }

            var parts = raw.Split(';');
            string Part(string key) => parts.Skip(1).Select(p => p.Split('=', 2)).First(p => p[0] == key)[1];
            return (parts[0], Part("share"), Part("user"), Part("password"));
        }
    }
}

public sealed class SmbBackupTests
{
    [SmbFact]
    public async Task A_share_of_a_nas_takes_backups_lists_them_returns_them_and_deletes_them()
    {
        var s = SmbFactAttribute.Settings!.Value;
        var folder = "mb-test-" + Guid.NewGuid().ToString("N")[..8];
        IBackupStore store = new SmbBackupStore(new SmbShare(new SmbOptions(s.Host, s.Share, folder, null, s.User, s.Password)));

        var (ok, message) = await store.CheckAsync();
        Assert.True(ok, message);

        var name = BackupNames.Make("smb", new DateTime(2026, 10, 12, 1, 0, 0, DateTimeKind.Utc), encrypted: false);
        var content = new byte[3 * 1024 * 1024 + 7];
        Random.Shared.NextBytes(content);
        await store.WriteAsync(name, new MemoryStream(content));

        var listed = await store.ListAsync();
        Assert.Contains(listed, f => f.Name == name && f.Size == content.Length);

        await using (var read = await store.OpenReadAsync(name))
        {
            using var buffer = new MemoryStream();
            await read.CopyToAsync(buffer);
            Assert.Equal(content, buffer.ToArray());
        }

        await store.DeleteAsync(name);
        Assert.DoesNotContain(await store.ListAsync(), f => f.Name == name);
    }

    [SmbFact]
    public async Task A_wrong_password_is_reported_in_words()
    {
        var s = SmbFactAttribute.Settings!.Value;
        IBackupStore store = new SmbBackupStore(new SmbShare(new SmbOptions(s.Host, s.Share, "mb-x", null, s.User, "nicht-das-passwort")));

        var (ok, message) = await store.CheckAsync();
        Assert.False(ok);
        Assert.False(string.IsNullOrWhiteSpace(message));
    }
}

/// <summary>A test that needs the application's PostgreSQL and a Samba server.</summary>
public sealed class SmbDatabaseFactAttribute : FactAttribute
{
    public SmbDatabaseFactAttribute()
    {
        if (!AppFixture.Available || SmbFactAttribute.Settings is null)
        {
            Skip = $"{AppFixture.Variable} and {SmbFactAttribute.Variable} are needed: PostgreSQL and an SMB server.";
        }
    }
}

[Collection(IntegrationCollection.Name)]
public sealed class NasStorageTests : IClassFixture<AppFixture>
{
    private readonly AppFixture _app;

    public NasStorageTests(AppFixture app) => _app = app;

    [SmbDatabaseFact]
    public async Task Files_go_to_the_nas_when_it_is_chosen_move_over_and_back_and_stay_readable()
    {
        await _app.EnsureAdminAsync();
        var s = SmbFactAttribute.Settings!.Value;
        var folder = "mb-it-" + Guid.NewGuid().ToString("N")[..8];
        var (_, _, first) = await _app.CreateInvoiceWithPdfAsync("Nas");
        var known = new List<long> { first.Id };

        async Task<List<StoredFile>> StoredAsync() => await _app.WithScopeAsync(sp => sp.GetRequiredService<MbDbContext>().StoredFile.AsNoTracking().Where(f => known.Contains(f.Id)).ToListAsync());

        var before = await StoredAsync();
        Assert.All(before, f => Assert.StartsWith("local:", f.StorageKey));
        var original = before[0];

        // the NAS is set up (the password is kept protected, as the page does it)
        await _app.WithScopeAsync(sp =>
        {
            var secrets = sp.GetRequiredService<Configuration.SecretProtector>();
            sp.GetRequiredService<Configuration.ConfigStore>().Update(c =>
            {
                c.Storage.Mode = Configuration.StorageMode.Smb;
                c.Storage.Host = s.Host;
                c.Storage.Share = s.Share;
                c.Storage.Path = folder;
                c.Storage.UserName = s.User;
                c.Storage.PasswordProtected = secrets.Protect(s.Password);
            });
            return Task.CompletedTask;
        });

        // a file saved now lands on the NAS; the old one stays where it was and still works
        var added = await _app.WithScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<MbDbContext>();
            var saved = await sp.GetRequiredService<FileStoreService>().SaveAsync("Neu auf dem NAS"u8.ToArray(), "neu.txt", "text/plain");
            await db.SaveChangesAsync();
            return saved.Id;
        });
        known.Add(added);
        var stored = await StoredAsync();
        Assert.StartsWith("smb:", stored.Single(f => f.Id == added).StorageKey);
        Assert.StartsWith("local:", stored.Single(f => f.Id == original.Id).StorageKey);

        // everything over to the NAS
        var toNas = await _app.WithScopeAsync(sp => sp.GetRequiredService<FileStoreService>().MoveAllAsync(FileStoreService.SmbScheme));
        Assert.Equal(0, toNas.Failed);
        Assert.True(toNas.Moved >= 1);
        Assert.All(await StoredAsync(), f => Assert.StartsWith("smb:", f.StorageKey));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_app.DataDir, "files"), "*", SearchOption.AllDirectories));

        async Task<string> HashOfAsync(long id) => await _app.WithScopeAsync(async sp =>
        {
            var file = await sp.GetRequiredService<MbDbContext>().StoredFile.AsNoTracking().FirstAsync(f => f.Id == id);
            return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(await sp.GetRequiredService<FileStoreService>().ReadAllAsync(file))).ToLowerInvariant();
        });

        Assert.Equal(original.Sha256, await HashOfAsync(original.Id));

        // and back into the volume
        var back = await _app.WithScopeAsync(sp => sp.GetRequiredService<FileStoreService>().MoveAllAsync(FileStoreService.LocalScheme));
        Assert.Equal(0, back.Failed);
        Assert.All(await StoredAsync(), f => Assert.StartsWith("local:", f.StorageKey));
        Assert.Equal(original.Sha256, await HashOfAsync(original.Id));

        // nothing is left on the NAS
        var share = new SmbShare(new SmbOptions(s.Host, s.Share, folder, null, s.User, s.Password));
        Assert.Empty(await share.ListAsync(DateTime.UtcNow.ToString("yyyy/MM")));
    }
}
