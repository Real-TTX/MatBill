using System.Text;
using MatBill.Configuration;
using MatBill.Data;
using MatBill.Localization;
using MatBill.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace MatBill.Services;

/// <summary>The logo of the company: uploaded once, stored like any file, read for every PDF (cached by its token, which changes with every new logo).</summary>
public sealed class BrandingService(ConfigStore config, MbDbContext db, FileStoreService files, IMemoryCache cache)
{
    public const long MaxLogoBytes = 2 * 1024 * 1024;

    public CompanyConfig Company => config.Current.Company;

    public async Task<LogoImage?> GetLogoAsync(CancellationToken ct = default)
    {
        if (!Guid.TryParse(Company.LogoToken, out var token))
        {
            return null;
        }

        return await cache.GetOrCreateAsync("logo-" + token, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(1);
            var file = await db.StoredFile.AsNoTracking().FirstOrDefaultAsync(f => f.Token == token, ct);
            if (file is null)
            {
                return null;
            }

            try
            {
                return new LogoImage(await files.ReadAllAsync(file, ct), file.ContentType == "image/svg+xml");
            }
            catch (Exception ex) when (ex is StorageException or IOException)
            {
                return null;
            }
        });
    }

    /// <summary>Stores a new logo and makes it the one in force. Returns a message when the file is not a logo.</summary>
    public async Task<string?> SaveLogoAsync(IFormFile upload, CancellationToken ct = default)
    {
        if (upload.Length == 0 || upload.Length > MaxLogoBytes)
        {
            return Text.T("System.Logo.TooLarge");
        }

        using var buffer = new MemoryStream();
        await upload.CopyToAsync(buffer, ct);
        var bytes = buffer.ToArray();
        var type = Sniff(bytes);
        if (type is null)
        {
            return Text.T("System.Logo.NotImage");
        }

        var file = await files.SaveAsync(bytes, "logo" + (type == "image/svg+xml" ? ".svg" : type == "image/png" ? ".png" : ".jpg"), type, ct);
        await db.SaveChangesAsync(ct);
        config.Update(c => c.Company.LogoToken = file.Token.ToString());
        return null;
    }

    public void RemoveLogo() => config.Update(c => c.Company.LogoToken = null);

    /// <summary>The type of an image by its content, never by its name: PNG and JPEG by their magic bytes, SVG by its root element (no script, no foreign object).</summary>
    public static string? Sniff(byte[] bytes)
    {
        if (bytes.Length > 8 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
        {
            return "image/png";
        }

        if (bytes.Length > 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
        {
            return "image/jpeg";
        }

        var text = Encoding.UTF8.GetString(bytes, 0, Math.Min(bytes.Length, 64 * 1024));
        var lower = text.ToLowerInvariant();
        var isSvg = lower.Contains("<svg", StringComparison.Ordinal) && !lower.Contains("<script", StringComparison.Ordinal) && !lower.Contains("<foreignobject", StringComparison.Ordinal)
            && !lower.Contains("<!entity", StringComparison.Ordinal) && !lower.Contains("javascript:", StringComparison.Ordinal) && !System.Text.RegularExpressions.Regex.IsMatch(lower, @"\son[a-z]+\s*=");
        return isSvg ? "image/svg+xml" : null;
    }
}
