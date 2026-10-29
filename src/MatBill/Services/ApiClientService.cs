using System.Security.Cryptography;
using System.Text;
using MatBill.Configuration;
using MatBill.Core.Billing;
using MatBill.Data;
using MatBill.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace MatBill.Services;

/// <summary>
/// The clients of the API and their keys. A key is "mbk_" and 43 random characters (256 bits); it is shown once when it is made, the database keeps its
/// SHA-256 and its first characters only, so a key that got lost is replaced, never recovered. A presented key is found by its first characters and then
/// compared in constant time.
/// </summary>
public sealed class ApiClientService(MbDbContext db, ConfigStore config, TimeProvider clock)
{
    public const string Prefix = "mbk_";

    /// <summary>How many characters of a key are kept in the clear: enough to tell keys apart in the list, far too few to guess the rest.</summary>
    public const int PrefixLength = 12;

    public static string Hash(string key) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();

    public static string NewKey() => Prefix + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Makes a client with a new key. The plain key is returned here and nowhere else.</summary>
    public async Task<(ApiClient Client, string Key)> CreateAsync(string name, bool canCreate, bool canRead, bool canIssue, string? numberPattern, CancellationToken ct = default)
    {
        var key = NewKey();
        var client = new ApiClient
        {
            Name = name.Trim(),
            KeyPrefix = key[..PrefixLength],
            KeyHash = Hash(key),
            CanCreate = canCreate,
            CanRead = canRead,
            CanIssue = canIssue,
            NumberPattern = string.IsNullOrWhiteSpace(numberPattern) ? null : numberPattern.Trim(),
        };
        db.ApiClient.Add(client);
        await db.SaveChangesAsync(ct);
        return (client, key);
    }

    /// <summary>A new key for a client (the old one stops working at once); a revoked client is switched on again with it.</summary>
    public async Task<string> RenewKeyAsync(ApiClient client, CancellationToken ct = default)
    {
        var key = NewKey();
        client.KeyPrefix = key[..PrefixLength];
        client.KeyHash = Hash(key);
        client.RevokedDate = null;
        await db.SaveChangesAsync(ct);
        return key;
    }

    public async Task RevokeAsync(ApiClient client, CancellationToken ct = default)
    {
        client.RevokedDate ??= clock.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>The client of a presented key, when the key is known and the client not revoked. Its last use is kept (at most once a minute).</summary>
    public async Task<ApiClient?> ValidateAsync(string? presented, string? ip, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(presented))
        {
            return null;
        }

        presented = presented.Trim();
        if (!presented.StartsWith(Prefix, StringComparison.Ordinal) || presented.Length is < PrefixLength or > 200)
        {
            return null;
        }

        var hash = Encoding.ASCII.GetBytes(Hash(presented));
        var prefix = presented[..PrefixLength];
        var candidates = await db.ApiClient.Where(c => c.KeyPrefix == prefix).ToListAsync(ct);
        var client = candidates.FirstOrDefault(c => CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(c.KeyHash), hash));
        if (client is null || !client.IsActive)
        {
            return null;
        }

        var now = clock.GetUtcNow().UtcDateTime;
        if (client.LastUsedDate is null || now - client.LastUsedDate > TimeSpan.FromMinutes(1) || client.LastUsedIp != ip)
        {
            client.LastUsedDate = now;
            client.LastUsedIp = ip;
            await db.SaveChangesAsync(ct);
        }

        return client;
    }

    /// <summary>The key of a request: "Authorization: Bearer mbk_…" or "X-Api-Key: mbk_…".</summary>
    public static string? Presented(HttpRequest request)
    {
        var authorization = request.Headers.Authorization.ToString();
        if (authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return authorization["Bearer ".Length..].Trim();
        }

        return request.Headers["X-Api-Key"].FirstOrDefault()?.Trim();
    }

    /// <summary>
    /// What is wrong with the pattern of a client's own range, as the key of a text, or null when it can be used (empty = the normal range).
    /// It must not be the pattern of the normal invoice range or of another client: two ranges counting the same numbers would skip each other's.
    /// </summary>
    public async Task<string?> PatternProblemAsync(string? pattern, long? clientId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return null;
        }

        pattern = pattern.Trim();
        if (NumberPattern.Problem(pattern) is { } problem)
        {
            return problem;
        }

        if (string.Equals(pattern, config.Current.Invoicing.InvoiceNumberPattern.Trim(), StringComparison.Ordinal))
        {
            return "Api.Pattern.SameAsInvoices";
        }

        return await db.ApiClient.AnyAsync(c => c.Id != clientId && c.NumberPattern == pattern, ct) ? "Api.Pattern.SameAsOther" : null;
    }
}
