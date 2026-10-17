using System.ComponentModel.DataAnnotations;
using MatBill.Data;
using MatBill.Data.Entities;
using MatBill.Services;
using MatBill.UiKit;
using Microsoft.AspNetCore.Mvc;

namespace MatBill.Pages.Invoices;

/// <summary>Sends an issued invoice as a PDF by mail: to the address of the invoice, with the text of the settings, both changeable before it goes out.</summary>
public sealed class SendModel(InvoiceService invoices, MailService mail, NavCounts nav, ICurrentUser current) : PageModelBase
{
    public sealed class MailInput
    {
        [Required(ErrorMessage = "Bitte gib eine E-Mail-Adresse ein.")]
        public string To { get; set; } = "";

        public string? Cc { get; set; }

        [Required(ErrorMessage = "Bitte gib einen Betreff ein.")]
        public string Subject { get; set; } = "";

        [Required(ErrorMessage = "Bitte schreib einen Text.")]
        public string Body { get; set; } = "";
    }

    [BindProperty(SupportsGet = true)]
    public long Id { get; set; }

    [BindProperty]
    public MailInput Input { get; set; } = new();

    public Invoice Invoice { get; private set; } = null!;

    public bool Configured => mail.IsConfigured;

    public bool IsAdmin => current.IsAdmin;

    public async Task<IActionResult> OnGetAsync()
    {
        if (await LoadAsync() is { } problem)
        {
            return problem;
        }

        var m = mail.DefaultMail(Invoice);
        Input = new MailInput { To = m.To, Cc = m.Cc, Subject = m.Subject, Body = m.Body };
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (await LoadAsync() is { } problem)
        {
            return problem;
        }

        if (!ModelState.IsValid)
        {
            return Page();
        }

        var error = await mail.SendAsync(Invoice, new InvoiceMail(Input.To.Trim(), string.IsNullOrWhiteSpace(Input.Cc) ? null : Input.Cc.Trim(), Input.Subject.Trim(), Input.Body), automatic: false);
        nav.Invalidate();
        if (error is not null)
        {
            ModelState.AddModelError(string.Empty, error);
            return Page();
        }

        Flash($"Rechnung {Invoice.Number} an {Input.To} versendet.");
        return RedirectToPage("/Invoices/Edit", new { id = Invoice.Id });
    }

    private async Task<IActionResult?> LoadAsync()
    {
        var invoice = await invoices.FindAsync(Id);
        if (invoice is null)
        {
            return NotFound();
        }

        if (invoice.IsDraft || invoice.Status == InvoiceStatus.Cancelled)
        {
            FlashError(invoice.IsDraft ? "Stelle die Rechnung zuerst aus, dann kann sie versendet werden." : "Eine stornierte Rechnung wird nicht versendet.");
            return RedirectToPage("/Invoices/Edit", new { id = invoice.Id });
        }

        Invoice = invoice;
        Head($"Rechnung {invoice.Number} senden", $"{invoice.RecipientName} · {Fmt.Money(invoice.GrossTotal)}", ("Rechnungen", "/Invoices"), (invoice.Number!, $"/Invoices/Edit?id={invoice.Id}"), ("Senden", null));
        ViewData["Width"] = "narrow";
        return null;
    }
}
