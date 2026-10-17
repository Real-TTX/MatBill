using System.Security.Cryptography;
using MatBill.Configuration;
using MatBill.Services;
using Microsoft.AspNetCore.Mvc;

namespace MatBill.Pages.Admin;

/// <summary>The settings of banking: automatic assignment, how often accounts are fetched, and the access to Enable Banking for the online connection.</summary>
public sealed class BankingModel(ConfigStore config, SecretProtector secrets, EnableBankingClient client, PublicUrl urls, ActivityLogger log) : PageModelBase
{
    public sealed class InputModel
    {
        public bool AutoMatch { get; set; } = true;

        public int SyncHours { get; set; } = 6;

        public string Country { get; set; } = "DE";

        public string? AppId { get; set; }

        /// <summary>The private key in PEM; empty keeps the stored one.</summary>
        public string? PrivateKey { get; set; }
    }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public bool HasKey => !string.IsNullOrEmpty(config.Current.Banking.EnableBankingKeyProtected);

    public string CallbackUrl => urls.Absolute("/Banking/Callback");

    public string? TestResult { get; private set; }

    public bool TestOk { get; private set; }

    public void OnGet()
    {
        var b = config.Current.Banking;
        Input = new InputModel { AutoMatch = b.AutoMatch, SyncHours = b.SyncHours, Country = b.Country, AppId = b.EnableBankingAppId };
        Prepare();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        Prepare();
        if (!Validate())
        {
            return Page();
        }

        Save();
        await log.LogAsync("Banking-Einstellungen geändert", "Banking");
        Flash("Die Einstellungen sind gespeichert.");
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostTestAsync()
    {
        Prepare();
        if (!Validate())
        {
            return Page();
        }

        Save();
        try
        {
            var banks = await client.BanksAsync(config.Current.Banking.Country, HttpContext.RequestAborted);
            TestOk = true;
            TestResult = $"Die Verbindung zu Enable Banking steht: {banks.Count} Banken in {config.Current.Banking.Country} verfügbar. Die Einstellungen sind gespeichert.";
        }
        catch (Exception ex) when (ex is BankingException or HttpRequestException or TaskCanceledException)
        {
            TestResult = ex.Message;
        }

        return Page();
    }

    private bool Validate()
    {
        if (Input.SyncHours is < 1 or > 48)
        {
            ModelState.AddModelError("Input.SyncHours", "Zwischen 1 und 48 Stunden.");
        }

        if (!string.IsNullOrWhiteSpace(Input.PrivateKey))
        {
            try
            {
                using var rsa = RSA.Create();
                rsa.ImportFromPem(Input.PrivateKey);
            }
            catch (ArgumentException)
            {
                ModelState.AddModelError("Input.PrivateKey", "Das ist kein privater RSA-Schlüssel im PEM-Format (-----BEGIN PRIVATE KEY----- …).");
            }
        }

        return ModelState.IsValid;
    }

    private void Save() => config.Update(c =>
    {
        c.Banking.AutoMatch = Input.AutoMatch;
        c.Banking.SyncHours = Input.SyncHours;
        c.Banking.Country = string.IsNullOrWhiteSpace(Input.Country) ? "DE" : Input.Country.Trim().ToUpperInvariant();
        c.Banking.EnableBankingAppId = Input.AppId?.Trim() ?? "";
        if (!string.IsNullOrWhiteSpace(Input.PrivateKey))
        {
            c.Banking.EnableBankingKeyProtected = secrets.Protect(Input.PrivateKey.Trim());
        }
    });

    private void Prepare()
    {
        Head("Banking", "Automatische Zuordnung und der Online-Abruf der Konten.", ("Einstellungen", null), ("Banking", null));
        ViewData["Width"] = "narrow";
    }
}
