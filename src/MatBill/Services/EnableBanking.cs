using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MatBill.Configuration;
using MatBill.Core.Banking;
using MatBill.Data;
using MatBill.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace MatBill.Services;

public sealed record BankChoice(string Name, string Country, string? Logo, string? Bic);

public sealed record ConnectedAccount(string Uid, string? Iban, string? Name, string? Currency);

/// <summary>
/// Enable Banking (enablebanking.com): one PSD2 interface to the banks of Europe. The application authenticates with a JWT signed with its own RSA key (RS256,
/// the id of the application as <c>kid</c>); the person confirms the access at the bank (redirect), then the bookings can be fetched for the time of the consent.
/// </summary>
public sealed class EnableBankingClient(HttpClient http, ConfigStore config, SecretProtector secrets)
{
    public const string BaseUrl = "https://api.enablebanking.com";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public bool IsConfigured => config.Current.Banking.IsOnlineConfigured;

    /// <summary>The JWT of a request (valid for an hour; the API refuses more than a day).</summary>
    public string Token()
    {
        var b = config.Current.Banking;
        var pem = secrets.Unprotect(b.EnableBankingKeyProtected);
        if (string.IsNullOrWhiteSpace(pem) || string.IsNullOrWhiteSpace(b.EnableBankingAppId))
        {
            throw new BankingException("Enable Banking ist nicht eingerichtet (Einstellungen › Banking).");
        }

        using var rsa = RSA.Create();
        try
        {
            rsa.ImportFromPem(pem);
        }
        catch (ArgumentException)
        {
            throw new BankingException("Der private Schlüssel ist kein gültiger RSA-Schlüssel im PEM-Format.");
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var header = B64(JsonSerializer.SerializeToUtf8Bytes(new { typ = "JWT", alg = "RS256", kid = b.EnableBankingAppId }));
        var payload = B64(JsonSerializer.SerializeToUtf8Bytes(new { iss = "enablebanking.com", aud = "api.enablebanking.com", iat = now, exp = now + 3600 }));
        var signature = rsa.SignData(Encoding.ASCII.GetBytes(header + "." + payload), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return header + "." + payload + "." + B64(signature);
    }

    private static string B64(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private async Task<JsonElement> SendAsync(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, BaseUrl + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token());
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: Json);
        }

        using var response = await http.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            var message = text;
            try
            {
                using var doc = JsonDocument.Parse(text);
                message = doc.RootElement.TryGetProperty("message", out var m) ? m.GetString() ?? text : doc.RootElement.TryGetProperty("detail", out var d) ? d.ToString() : text;
            }
            catch (JsonException)
            {
                // not JSON: the text as it is
            }

            throw new BankingException($"Enable Banking antwortet {(int)response.StatusCode}: {(message.Length > 300 ? message[..300] : message)}");
        }

        using var result = JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text);
        return result.RootElement.Clone();
    }

    public async Task<List<BankChoice>> BanksAsync(string country, CancellationToken ct = default)
    {
        var root = await SendAsync(HttpMethod.Get, $"/aspsps?country={Uri.EscapeDataString(country)}&psu_type=business", null, ct);
        var list = new List<BankChoice>();
        if (root.TryGetProperty("aspsps", out var aspsps))
        {
            foreach (var a in aspsps.EnumerateArray())
            {
                list.Add(new BankChoice(a.GetProperty("name").GetString() ?? "", a.GetProperty("country").GetString() ?? country, Str(a, "logo"), Str(a, "bic")));
            }
        }

        return [.. list.OrderBy(b => b.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    /// <summary>Starts the consent at a bank; the answer is the address the person is sent to.</summary>
    public async Task<string> StartAsync(string bank, string country, Guid state, string redirectUrl, DateTime validUntil, CancellationToken ct = default)
    {
        var root = await SendAsync(HttpMethod.Post, "/auth", new
        {
            access = new { valid_until = validUntil.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture) },
            aspsp = new { name = bank, country },
            state = state.ToString(),
            redirect_url = redirectUrl,
            psu_type = "business",
            language = "de",
        }, ct);
        return root.GetProperty("url").GetString() ?? throw new BankingException("Enable Banking hat keine Adresse für die Anmeldung geliefert.");
    }

    public async Task<(string SessionId, DateTime? ValidUntil, List<ConnectedAccount> Accounts)> CreateSessionAsync(string code, CancellationToken ct = default)
    {
        var root = await SendAsync(HttpMethod.Post, "/sessions", new { code }, ct);
        var accounts = new List<ConnectedAccount>();
        foreach (var a in root.GetProperty("accounts").EnumerateArray())
        {
            var iban = a.TryGetProperty("account_id", out var id) && id.ValueKind == JsonValueKind.Object ? Str(id, "iban") : null;
            accounts.Add(new ConnectedAccount(a.GetProperty("uid").GetString() ?? "", iban, Str(a, "name"), Str(a, "currency")));
        }

        DateTime? valid = root.TryGetProperty("access", out var access) && DateTime.TryParse(Str(access, "valid_until"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var v) ? v : null;
        return (root.GetProperty("session_id").GetString() ?? "", valid, accounts);
    }

    /// <summary>The booked transactions of an account from <paramref name="from"/> on, all pages.</summary>
    public async Task<List<StatementLine>> TransactionsAsync(string uid, DateOnly from, CancellationToken ct = default)
    {
        var lines = new List<StatementLine>();
        string? continuation = null;
        for (var page = 0; page < 50; page++)
        {
            var query = $"/accounts/{Uri.EscapeDataString(uid)}/transactions?date_from={from:yyyy-MM-dd}" + (continuation is null ? "" : "&continuation_key=" + Uri.EscapeDataString(continuation));
            var root = await SendAsync(HttpMethod.Get, query, null, ct);
            foreach (var t in root.GetProperty("transactions").EnumerateArray())
            {
                if (Str(t, "status") is { } status && status != "BOOK")
                {
                    continue;
                }

                var amount = decimal.Parse(t.GetProperty("transaction_amount").GetProperty("amount").GetString() ?? "0", CultureInfo.InvariantCulture);
                var credit = Str(t, "credit_debit_indicator") != "DBIT";
                var party = credit ? "debtor" : "creditor";
                var name = t.TryGetProperty(party, out var p) && p.ValueKind == JsonValueKind.Object ? Str(p, "name") : null;
                var iban = t.TryGetProperty(party + "_account", out var pa) && pa.ValueKind == JsonValueKind.Object ? Str(pa, "iban") : null;
                var purpose = t.TryGetProperty("remittance_information", out var r) && r.ValueKind == JsonValueKind.Array ? string.Join(" ", r.EnumerateArray().Select(x => x.GetString())) : null;
                var booking = DateOnly.TryParse(Str(t, "booking_date"), CultureInfo.InvariantCulture, out var bd) ? bd : DateOnly.TryParse(Str(t, "value_date"), CultureInfo.InvariantCulture, out var vd) ? vd : DateOnly.FromDateTime(DateTime.UtcNow);
                DateOnly? value = DateOnly.TryParse(Str(t, "value_date"), CultureInfo.InvariantCulture, out var vv) ? vv : null;
                lines.Add(new StatementLine(booking, value, credit ? Math.Abs(amount) : -Math.Abs(amount), t.GetProperty("transaction_amount").GetProperty("currency").GetString() ?? "EUR",
                    name, iban, purpose is null ? null : SepaPurpose.Squeeze(purpose), Str(t, "entry_reference"), SepaPurpose.EndToEnd(purpose)));
            }

            continuation = Str(root, "continuation_key");
            if (string.IsNullOrEmpty(continuation))
            {
                break;
            }
        }

        return lines;
    }

    public async Task<decimal?> BalanceAsync(string uid, CancellationToken ct = default)
    {
        var root = await SendAsync(HttpMethod.Get, $"/accounts/{Uri.EscapeDataString(uid)}/balances", null, ct);
        if (!root.TryGetProperty("balances", out var balances))
        {
            return null;
        }

        var list = balances.EnumerateArray().ToList();
        var best = list.FirstOrDefault(b => Str(b, "balance_type") is "CLBD" or "ITBD" or "XPCD" or "CLAV");
        var chosen = best.ValueKind == JsonValueKind.Undefined ? list.FirstOrDefault() : best;
        return chosen.ValueKind == JsonValueKind.Object && decimal.TryParse(chosen.GetProperty("balance_amount").GetProperty("amount").GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    private static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

/// <summary>Fetches the bookings of connected accounts (by hand and on schedule), and says when a consent runs out.</summary>
public sealed class BankSyncService(MbDbContext db, EnableBankingClient client, BankingService banking, NotificationService notifications, TimeProvider clock, ILogger<BankSyncService> logger)
{
    public async Task<BankImport?> SyncAsync(BankAccount account, CancellationToken ct = default)
    {
        if (account.Provider != BankProvider.EnableBanking || string.IsNullOrWhiteSpace(account.ExternalId))
        {
            return null;
        }

        var now = clock.GetUtcNow().UtcDateTime;
        try
        {
            var from = account.LastSyncDate is { } last ? DateOnly.FromDateTime(last).AddDays(-7) : DateOnly.FromDateTime(now).AddDays(-90);
            var lines = await client.TransactionsAsync(account.ExternalId, from, ct);
            var import = await banking.ImportAsync(account, lines, "Online (Enable Banking)", null, null, ct: ct);
            if (await client.BalanceAsync(account.ExternalId, ct) is { } balance)
            {
                account.Balance = balance;
                account.BalanceDate = DateOnly.FromDateTime(now);
            }

            account.LastSyncDate = now;
            account.LastSyncError = null;
            await db.SaveChangesAsync(ct);
            return import;
        }
        catch (Exception ex) when (ex is BankingException or HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            logger.LogWarning(ex, "Bank account {Id} could not be fetched.", account.Id);
            account.LastSyncError = ex is BankingException ? ex.Message : "Die Bank war nicht erreichbar: " + ex.Message;
            await db.SaveChangesAsync(ct);
            await notifications.NotifyAllAsync(NotificationTone.Error, $"Konto „{account.Name}“ nicht abgerufen", account.LastSyncError, $"/Banking/Accounts/Edit?id={account.Id}", $"sync-{account.Id}-{now:yyyyMMddHH}", ct);
            return null;
        }
    }

    public async Task<int> SyncDueAsync(int everyHours, CancellationToken ct = default)
    {
        var due = clock.GetUtcNow().UtcDateTime.AddHours(-Math.Max(1, everyHours));
        var accounts = await db.BankAccount.Include(a => a.BankConnection)
            .Where(a => a.IsActive && a.Provider == BankProvider.EnableBanking && a.ExternalId != null && (a.LastSyncDate == null || a.LastSyncDate < due)
                && a.BankConnection != null && a.BankConnection.Status == BankConnectionStatus.Active)
            .ToListAsync(ct);
        foreach (var a in accounts)
        {
            await SyncAsync(a, ct);
        }

        return accounts.Count;
    }

    /// <summary>Notes a week before a consent runs out, and marks the ones that did.</summary>
    public async Task CheckConsentsAsync(CancellationToken ct = default)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        foreach (var c in await db.BankConnection.Where(c => c.Status == BankConnectionStatus.Active && c.ValidUntil != null).ToListAsync(ct))
        {
            if (c.ValidUntil <= now)
            {
                c.Status = BankConnectionStatus.Expired;
                await notifications.NotifyAllAsync(NotificationTone.Warning, $"Bankzugang {c.BankName} abgelaufen", "Verbinde das Konto neu, damit die Umsätze wieder kommen.", "/Banking", $"consent-expired-{c.Id}", ct);
            }
            else if (c.ValidUntil <= now.AddDays(7))
            {
                await notifications.NotifyAllAsync(NotificationTone.Warning, $"Bankzugang {c.BankName} läuft bald ab", $"Gültig bis {Fmt.Date(c.ValidUntil)}. Verbinde das Konto rechtzeitig neu.", "/Banking", $"consent-soon-{c.Id}", ct);
            }
        }

        await db.SaveChangesAsync(ct);
    }
}

/// <summary>Fetches connected bank accounts on schedule and watches the consents.</summary>
public sealed class BankingScheduler(IServiceScopeFactory scopes, ConfigStore config, ILogger<BankingScheduler> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                if (config.Current.Banking.IsOnlineConfigured)
                {
                    try
                    {
                        using var scope = scopes.CreateScope();
                        using (scope.ServiceProvider.GetRequiredService<ICurrentUser>().RunAs(null, "Automatik"))
                        {
                            var sync = scope.ServiceProvider.GetRequiredService<BankSyncService>();
                            await sync.CheckConsentsAsync(stoppingToken);
                            await sync.SyncDueAsync(config.Current.Banking.SyncHours, stoppingToken);
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        logger.LogError(ex, "The banking run failed.");
                    }
                }

                await Task.Delay(TimeSpan.FromMinutes(15), stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // the application is stopping
        }
    }
}

public sealed class BankingException(string message) : Exception(message);
