using System.Security.Cryptography;
using MatBill.Configuration;
using MatBill.Core.Security;
using MatBill.Data;
using MatBill.Data.Entities;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace MatBill.Services;

public enum ShareState
{
    Active,
    Expired,
    Revoked,
    Unknown,
}

public sealed record ShareResolution(ShareState State, ShareLink? Link);

/// <summary>
/// Anonymous links to one invoice (the role "Anonym"). The visitor has no account and sees only what the link allows, for as long as it is valid.
/// A link can have a password (checked once, remembered in a signed cookie for a few hours); wrong passwords are counted per link, so a link cannot be
/// guessed at: after ten wrong ones it is shut for fifteen minutes.
/// </summary>
public sealed class ShareService(MbDbContext db, ConfigStore config, TimeProvider clock, IDataProtectionProvider protection, IMemoryCache cache, PublicUrl urls)
{
    public const int MaxWrongPasswords = 10;

    private static readonly TimeSpan GateLifetime = TimeSpan.FromHours(4);

    private static readonly TimeSpan LockTime = TimeSpan.FromMinutes(15);

    private readonly IDataProtector _gate = protection.CreateProtector("MatBill.ShareGate.v1");

    public string LinkOf(ShareLink link) => urls.Absolute($"/s/{link.Token}");

    public string CompanyName => config.Current.Company.Name;

    public int DefaultDays => config.Current.Security.ShareLinkDays;

    public async Task<ShareLink> CreateAsync(Invoice invoice, string title, string? message, int days, string? password, string? recipientName, CancellationToken ct = default)
    {
        if (invoice.IsDraft)
        {
            throw new InvoiceException("Ein Entwurf kann nicht freigegeben werden. Stelle die Rechnung zuerst aus.");
        }

        var link = new ShareLink
        {
            InvoiceId = invoice.Id,
            Title = title.Trim(),
            Message = string.IsNullOrWhiteSpace(message) ? null : message.Trim(),
            ExpiresDate = clock.GetUtcNow().UtcDateTime.AddDays(Math.Clamp(days, 1, 365)),
            PasswordHash = string.IsNullOrEmpty(password) ? null : PasswordHasher.Hash(password),
            RecipientName = string.IsNullOrWhiteSpace(recipientName) ? null : recipientName.Trim(),
        };
        db.ShareLink.Add(link);
        await db.SaveChangesAsync(ct);
        return link;
    }

    public async Task<ShareResolution> ResolveAsync(Guid token, CancellationToken ct = default)
    {
        var link = await db.ShareLink.Include(l => l.Invoice).ThenInclude(i => i!.Lines).Include(l => l.Invoice).ThenInclude(i => i!.Customer)
            .FirstOrDefaultAsync(l => l.Token == token, ct);
        if (link?.Invoice is null || link.Invoice.IsDraft)
        {
            return new ShareResolution(ShareState.Unknown, null);
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var state = link.RevokedDate is not null ? ShareState.Revoked : link.ExpiresDate <= now ? ShareState.Expired : ShareState.Active;
        return new ShareResolution(state, link);
    }

    // ------------------------------------------------------------------------------------------------ the password

    public bool NeedsPassword(ShareLink link, HttpContext http) => link.PasswordHash is not null && !HasPassedGate(link, http);

    public bool HasPassedGate(ShareLink link, HttpContext http)
    {
        if (!http.Request.Cookies.TryGetValue(CookieName(link), out var value))
        {
            return false;
        }

        try
        {
            var parts = _gate.Unprotect(value).Split('|');
            return parts.Length == 2 && parts[0] == link.Token.ToString() && long.TryParse(parts[1], out var until) && until > clock.GetUtcNow().ToUnixTimeSeconds();
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    public bool IsLocked(ShareLink link) => cache.TryGetValue(WrongKey(link), out int wrong) && wrong >= MaxWrongPasswords;

    public bool TryPassword(ShareLink link, string? password, HttpContext http)
    {
        if (IsLocked(link))
        {
            return false;
        }

        if (!PasswordHasher.Verify(password ?? string.Empty, link.PasswordHash))
        {
            cache.Set(WrongKey(link), (cache.TryGetValue(WrongKey(link), out int wrong) ? wrong : 0) + 1, LockTime);
            return false;
        }

        var until = clock.GetUtcNow().Add(GateLifetime).ToUnixTimeSeconds();
        http.Response.Cookies.Append(CookieName(link), _gate.Protect($"{link.Token}|{until}"), new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            Secure = http.Request.IsHttps,
            Path = "/s/" + link.Token,
            MaxAge = GateLifetime,
        });
        return true;
    }

    private static string WrongKey(ShareLink link) => "share-wrong-" + link.Token;

    private static string CookieName(ShareLink link) => ".mb.share." + link.Token.ToString("N")[..12];

    public async Task CountViewAsync(ShareLink link, CancellationToken ct = default)
    {
        link.ViewCount++;
        link.LastViewDate = clock.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);
    }

    public static string Remaining(ShareLink link, DateTime now)
    {
        var span = link.ExpiresDate - now;
        return span.TotalDays >= 2 ? $"noch {(int)span.TotalDays} Tage" : span.TotalHours >= 1 ? $"noch {(int)span.TotalHours} Stunden" : "läuft bald ab";
    }
}
