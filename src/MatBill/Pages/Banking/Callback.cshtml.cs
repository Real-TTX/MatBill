using MatBill.Data;
using MatBill.Data.Entities;
using MatBill.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MatBill.Pages.Banking;

/// <summary>Where the bank sends the person back: the code becomes a session, its accounts become (or join) bank accounts, and they are fetched for the first time.</summary>
public sealed class CallbackModel(MbDbContext db, EnableBankingClient client, BankSyncService sync, ActivityLogger log) : PageModelBase
{
    public async Task<IActionResult> OnGetAsync(string? code, string? state, string? error, string? error_description)
    {
        var connection = Guid.TryParse(state, out var token) ? await db.BankConnection.FirstOrDefaultAsync(c => c.Token == token && c.Status == BankConnectionStatus.Pending) : null;
        if (connection is null)
        {
            FlashError("Diese Rückmeldung der Bank gehört zu keiner offenen Verbindung. Bitte verbinde noch einmal.");
            return RedirectToPage("/Banking/Index");
        }

        if (!string.IsNullOrEmpty(error) || string.IsNullOrEmpty(code))
        {
            connection.Status = BankConnectionStatus.Failed;
            connection.Error = error_description ?? error ?? "abgebrochen";
            await db.SaveChangesAsync();
            FlashError($"Die Bank hat den Zugang nicht freigegeben ({connection.Error}).");
            return RedirectToPage("/Banking/Index");
        }

        try
        {
            var (sessionId, validUntil, accounts) = await client.CreateSessionAsync(code, HttpContext.RequestAborted);
            connection.SessionId = sessionId;
            connection.ValidUntil = validUntil ?? DateTime.UtcNow.AddDays(90);
            connection.Status = BankConnectionStatus.Active;
            var linked = new List<BankAccount>();
            foreach (var a in accounts)
            {
                var account = (a.Iban is null ? null : await db.BankAccount.FirstOrDefaultAsync(x => x.Iban == a.Iban))
                    ?? new BankAccount { Name = a.Name ?? $"{connection.BankName} {a.Iban?[^4..]}", Iban = a.Iban, BankName = connection.BankName, Currency = a.Currency ?? "EUR" };
                account.Provider = BankProvider.EnableBanking;
                account.ExternalId = a.Uid;
                account.BankConnection = connection;
                account.IsActive = true;
                if (account.Id == 0)
                {
                    db.BankAccount.Add(account);
                }

                linked.Add(account);
            }

            await db.SaveChangesAsync();
            await log.LogAsync("Bankverbindung hergestellt", nameof(BankConnection), connection.Id, $"{connection.BankName}: {linked.Count} Konten");
            var added = 0;
            foreach (var account in linked)
            {
                added += (await sync.SyncAsync(account, HttpContext.RequestAborted))?.Added ?? 0;
            }

            Flash($"{connection.BankName} ist verbunden: {linked.Count} Konten, {added} Umsätze abgerufen.");
        }
        catch (Exception ex) when (ex is BankingException or HttpRequestException or TaskCanceledException)
        {
            connection.Status = BankConnectionStatus.Failed;
            connection.Error = ex.Message;
            await db.SaveChangesAsync();
            FlashError(ex.Message);
        }

        return RedirectToPage("/Banking/Index");
    }
}
