using System.ComponentModel.DataAnnotations;
using MatBill.Configuration;
using MatBill.Services;
using Microsoft.AspNetCore.Mvc;

namespace MatBill.Pages.Admin;

/// <summary>The mail server that sends invoices, and the subject and text of an invoice mail; a test mail shows whether it works.</summary>
public sealed class MailModel(ConfigStore config, SecretProtector secrets, MailService mail, ICurrentUser current, ActivityLogger log) : PageModelBase
{
    public sealed class InputModel
    {
        public string? Host { get; set; }

        [Range(1, 65535, ErrorMessage = "Ein Port zwischen 1 und 65535.")]
        public int Port { get; set; } = 587;

        public MailSecurity Security { get; set; } = MailSecurity.Auto;

        public string? UserName { get; set; }

        public string? Password { get; set; }

        [EmailAddress(ErrorMessage = "Das ist keine E-Mail-Adresse.")]
        public string? FromAddress { get; set; }

        public string? FromName { get; set; }

        public string? Bcc { get; set; }

        [Required(ErrorMessage = "Bitte gib einen Betreff ein.")]
        public string Subject { get; set; } = "";

        [Required(ErrorMessage = "Bitte schreib einen Text.")]
        public string Body { get; set; } = "";

        public string? QuoteSubject { get; set; }

        public string? QuoteBody { get; set; }
    }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    [BindProperty]
    public string? TestTo { get; set; }

    public bool HasPassword => !string.IsNullOrEmpty(config.Current.Mail.PasswordProtected);

    public string? TestResult { get; private set; }

    public bool TestOk { get; private set; }

    public void OnGet()
    {
        var m = config.Current.Mail;
        Input = new InputModel
        {
            Host = m.Host, Port = m.Port, Security = m.Security, UserName = m.UserName, FromAddress = m.FromAddress, FromName = m.FromName, Bcc = m.Bcc, Subject = m.Subject, Body = m.Body, QuoteSubject = m.QuoteSubject, QuoteBody = m.QuoteBody,
        };
        TestTo = current.IsAuthenticated ? User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value : null;
        Prepare();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        Prepare();
        if (!string.IsNullOrWhiteSpace(Input.Host) && string.IsNullOrWhiteSpace(Input.FromAddress))
        {
            ModelState.AddModelError("Input.FromAddress", "Mit einem Server braucht es eine Absenderadresse.");
        }

        if (!ModelState.IsValid)
        {
            return Page();
        }

        Save();
        await log.LogAsync("E-Mail-Einstellungen geändert", "Mail");
        Flash("Die E-Mail-Einstellungen sind gespeichert.");
        return RedirectToPage();
    }

    /// <summary>Saves and sends a test mail with the settings of the form.</summary>
    public async Task<IActionResult> OnPostTestAsync()
    {
        Prepare();
        if (!ModelState.IsValid)
        {
            return Page();
        }

        if (string.IsNullOrWhiteSpace(TestTo))
        {
            ModelState.AddModelError(nameof(TestTo), "An welche Adresse soll die Testnachricht gehen?");
            return Page();
        }

        Save();
        var error = await mail.SendTestAsync(TestTo.Trim(), HttpContext.RequestAborted);
        TestOk = error is null;
        TestResult = error ?? $"Die Testnachricht ist an {TestTo} unterwegs. Die Einstellungen sind gespeichert.";
        return Page();
    }

    private void Save()
    {
        config.Update(c =>
        {
            c.Mail.Host = Input.Host?.Trim() ?? "";
            c.Mail.Port = Input.Port;
            c.Mail.Security = Input.Security;
            c.Mail.UserName = Input.UserName?.Trim() ?? "";
            if (!string.IsNullOrEmpty(Input.Password))
            {
                c.Mail.PasswordProtected = secrets.Protect(Input.Password);
            }

            c.Mail.FromAddress = Input.FromAddress?.Trim() ?? "";
            c.Mail.FromName = Input.FromName?.Trim() ?? "";
            c.Mail.Bcc = Input.Bcc?.Trim() ?? "";
            c.Mail.Subject = Input.Subject.Trim();
            c.Mail.Body = Input.Body.Replace("\r\n", "\n", StringComparison.Ordinal);
            c.Mail.QuoteSubject = Input.QuoteSubject?.Trim() ?? "";
            c.Mail.QuoteBody = (Input.QuoteBody ?? "").Replace("\r\n", "\n", StringComparison.Ordinal);
        });
    }

    private void Prepare()
    {
        Head("E-Mail", "Der Mailserver, über den Rechnungen als PDF versendet werden.", ("Einstellungen", null), ("E-Mail", null));
        ViewData["Width"] = "narrow";
    }
}
