using MatBill.Configuration;
using MatBill.Data;
using MatBill.Data.Entities;
using MatBill.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MatBill.Pages.Banking;

/// <summary>Connects accounts online: the bank is chosen, the person confirms the access at the bank (PSD2, 90 or 180 days), then MatBill fetches the bookings by itself.</summary>
public sealed class ConnectModel(MbDbContext db, EnableBankingClient client, ConfigStore config, PublicUrl urls, ActivityLogger log) : PageModelBase
{
    [BindProperty(SupportsGet = true)]
    public string? Country { get; set; }

    [BindProperty]
    public string? Bank { get; set; }

    public List<BankChoice> Banks { get; private set; } = [];

    public string? Problem { get; private set; }

    public bool Configured => client.IsConfigured;

    public string CallbackUrl => urls.Absolute("/Banking/Callback");

    public async Task OnGetAsync()
    {
        Prepare();
        if (!Configured)
        {
            return;
        }

        try
        {
            Banks = await client.BanksAsync(Country!, HttpContext.RequestAborted);
        }
        catch (Exception ex) when (ex is BankingException or HttpRequestException or TaskCanceledException)
        {
            Problem = ex.Message;
        }
    }

    public async Task<IActionResult> OnPostAsync()
    {
        Prepare();
        if (string.IsNullOrWhiteSpace(Bank))
        {
            FlashError("Bitte wähle deine Bank.");
            return RedirectToPage(new { country = Country });
        }

        var connection = new BankConnection { BankName = Bank, Country = Country! };
        db.BankConnection.Add(connection);
        await db.SaveChangesAsync();
        try
        {
            var url = await client.StartAsync(Bank, Country!, connection.Token, CallbackUrl, DateTime.UtcNow.AddDays(90), HttpContext.RequestAborted);
            await log.LogAsync("Bankverbindung gestartet", nameof(BankConnection), connection.Id, Bank);
            return Redirect(url);
        }
        catch (Exception ex) when (ex is BankingException or HttpRequestException or TaskCanceledException)
        {
            connection.Status = BankConnectionStatus.Failed;
            connection.Error = ex.Message;
            await db.SaveChangesAsync();
            FlashError(ex.Message);
            return RedirectToPage(new { country = Country });
        }
    }

    private void Prepare()
    {
        Country = string.IsNullOrWhiteSpace(Country) ? config.Current.Banking.Country : Country.Trim().ToUpperInvariant()[..Math.Min(2, Country.Trim().Length)];
        Head("Bank online verbinden", "Über Enable Banking (PSD2): Umsätze aller Konten kommen automatisch, ohne Datei-Export.", ("Banking", "/Banking"), ("Verbinden", null));
        ViewData["ActiveHref"] = "/Banking";
        ViewData["Width"] = "narrow";
    }
}
