using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using MatBill.Configuration;
using MatBill.Data;
using MatBill.Data.Entities;
using MatBill.Localization;

namespace MatBill.Storage;

/// <summary>Where the bytes of the files are kept. A store knows keys, nothing else.</summary>
public interface IFileStore
{
    Task SaveAsync(string key, Stream content, CancellationToken ct = default);

    Task<Stream> OpenReadAsync(string key, CancellationToken ct = default);

    Task DeleteAsync(string key, CancellationToken ct = default);

    Task<bool> ExistsAsync(string key, CancellationToken ct = default);
}

/// <summary>Files in the data volume (<c>{data}/files</c>).</summary>
public sealed class LocalFileStore(string root) : IFileStore
{
    private string PathOf(string key)
    {
        var full = Path.GetFullPath(Path.Combine(root, key.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new StorageException(Text.T("Storage.InvalidKey"));
        }

        return full;
    }

    public async Task SaveAsync(string key, Stream content, CancellationToken ct = default)
    {
        var path = PathOf(key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".partial";
        await using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
        {
            await content.CopyToAsync(output, ct);
        }

        File.Move(temp, path, overwrite: true);
    }

    public Task<Stream> OpenReadAsync(string key, CancellationToken ct = default)
    {
        var path = PathOf(key);
        if (!File.Exists(path))
        {
            throw new StorageException(Text.T("Storage.FileMissing"));
        }

        return Task.FromResult<Stream>(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true));
    }

    public Task DeleteAsync(string key, CancellationToken ct = default)
    {
        var path = PathOf(key);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return Task.CompletedTask;
    }

    public Task<bool> ExistsAsync(string key, CancellationToken ct = default) => Task.FromResult(File.Exists(PathOf(key)));
}

/// <summary>Files on a share of a NAS.</summary>
public sealed class SmbFileStore(SmbShare share) : IFileStore
{
    public Task SaveAsync(string key, Stream content, CancellationToken ct = default) => share.WriteAsync(key, content, ct);

    public Task<Stream> OpenReadAsync(string key, CancellationToken ct = default) => share.OpenReadAsync(key, ct);

    public Task DeleteAsync(string key, CancellationToken ct = default) => share.DeleteAsync(key, ct);

    public Task<bool> ExistsAsync(string key, CancellationToken ct = default) => share.ExistsAsync(key, ct);
}

/// <summary>
/// The files of the application: documents that came in, exports, logos, issued invoices. The key of a file names its store (<c>local:2026/10/…</c>,
/// <c>smb:2026/10/…</c>), so switching the setting from the volume to a NAS does not strand the files that were written before.
/// </summary>
public sealed class FileStoreService(ConfigStore config, SecretProtector secrets, MbDbContext db, TimeProvider clock)
{
    public const string LocalScheme = "local";

    public const string SmbScheme = "smb";

    public IFileStore Local => new LocalFileStore(Path.Combine(config.DataDir, "files"));

    /// <summary>A share with the settings of the configuration (the NAS for attachments), or null when it is not set up.</summary>
    public SmbShare? ConfiguredShare()
    {
        var s = config.Current.Storage;
        return string.IsNullOrWhiteSpace(s.Host) || string.IsNullOrWhiteSpace(s.Share)
            ? null
            : new SmbShare(new SmbOptions(s.Host, s.Share, s.Path, s.Domain, s.UserName, secrets.Unprotect(s.PasswordProtected)));
    }

    private IFileStore StoreFor(string scheme) => scheme switch
    {
        LocalScheme => Local,
        SmbScheme => new SmbFileStore(ConfiguredShare() ?? throw new StorageException(Text.T("Storage.NasNotSetUp"))),
        _ => throw new StorageException(Text.T("Storage.Unknown"))
    };

    private static (string Scheme, string Path) Split(string key)
    {
        var colon = key.IndexOf(':');
        return colon < 0 ? (LocalScheme, key) : (key[..colon], key[(colon + 1)..]);
    }

    /// <summary>Stores the bytes and writes the row of the file (saved with the next SaveChanges of the caller's context).</summary>
    public async Task<StoredFile> SaveAsync(byte[] bytes, string fileName, string contentType, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var scheme = config.Current.Storage.Mode == StorageMode.Smb && ConfiguredShare() is not null ? SmbScheme : LocalScheme;
        var path = $"{now:yyyy}/{now:MM}/{Guid.NewGuid():N}";
        using (var stream = new MemoryStream(bytes, writable: false))
        {
            await StoreFor(scheme).SaveAsync(path, stream, ct);
        }

        var file = new StoredFile
        {
            StorageKey = $"{scheme}:{path}",
            FileName = SafeFileName(fileName),
            ContentType = contentType,
            SizeBytes = bytes.LongLength,
            Sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
        };
        db.StoredFile.Add(file);
        return file;
    }

    /// <summary>
    /// Writes content under a key that is already known (a restore): into the store of its scheme, or, when that store is not set up here
    /// (a backup restored on another machine), into the volume. Returns the key the file is under now.
    /// </summary>
    public async Task<string> RestoreAsync(string storageKey, Stream content, CancellationToken ct = default)
    {
        var (scheme, path) = Split(storageKey);
        IFileStore store;
        try
        {
            store = StoreFor(scheme);
        }
        catch (StorageException)
        {
            store = Local;
            scheme = LocalScheme;
        }

        await store.SaveAsync(path, content, ct);
        return $"{scheme}:{path}";
    }

    public Task<Stream> OpenAsync(StoredFile file, CancellationToken ct = default)
    {
        var (scheme, path) = Split(file.StorageKey);
        return StoreFor(scheme).OpenReadAsync(path, ct);
    }

    public async Task<byte[]> ReadAllAsync(StoredFile file, CancellationToken ct = default)
    {
        await using var stream = await OpenAsync(file, ct);
        using var buffer = new MemoryStream(file.SizeBytes > 0 && file.SizeBytes < int.MaxValue ? (int)file.SizeBytes : 0);
        await stream.CopyToAsync(buffer, ct);
        return buffer.ToArray();
    }

    public async Task DeleteAsync(StoredFile file, CancellationToken ct = default)
    {
        var (scheme, path) = Split(file.StorageKey);
        await StoreFor(scheme).DeleteAsync(path, ct);
    }

    public sealed record MoveResult(int Moved, int Failed, string? FirstError);

    /// <summary>
    /// Moves every file that is not in the store of <paramref name="toScheme"/> into it: copy, write the new key, only then remove the old file.
    /// A file that cannot be moved stays where it is and works as before.
    /// </summary>
    public async Task<MoveResult> MoveAllAsync(string toScheme, CancellationToken ct = default)
    {
        var target = StoreFor(toScheme);
        var moved = 0;
        var failed = 0;
        string? firstError = null;
        var files = (await db.StoredFile.ToListAsync(ct)).Where(f => Split(f.StorageKey).Scheme != toScheme).ToList();
        foreach (var file in files)
        {
            var (scheme, path) = Split(file.StorageKey);
            try
            {
                await using (var source = await StoreFor(scheme).OpenReadAsync(path, ct))
                {
                    await target.SaveAsync(path, source, ct);
                }

                file.StorageKey = $"{toScheme}:{path}";
                await db.SaveChangesAsync(ct);
                await StoreFor(scheme).DeleteAsync(path, ct);
                moved++;
            }
            catch (Exception ex) when (ex is StorageException or IOException)
            {
                failed++;
                firstError ??= $"{file.FileName}: {ex.Message}";
            }
        }

        return new MoveResult(moved, failed, firstError);
    }

    /// <summary>A file name that is safe in a download header and on any file system: no path, no control characters.</summary>
    public static string SafeFileName(string name)
    {
        var clean = Path.GetFileName(name.Replace('\\', '/'));
        foreach (var c in Path.GetInvalidFileNameChars().Concat(['"', ';']))
        {
            clean = clean.Replace(c, '_');
        }

        clean = new string(clean.Where(c => !char.IsControl(c)).ToArray()).Trim();
        return clean.Length == 0 ? "file" : clean.Length > 150 ? clean[^150..] : clean;
    }
}
