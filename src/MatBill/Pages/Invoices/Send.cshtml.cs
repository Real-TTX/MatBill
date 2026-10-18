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
        [Required(ErrorMessage = "Invoices.Send.To.Required")]
        [Display(Name = "Invoices.Send.To")]
        public string To { get; set; } = "";

        [Display(Name = "Invoices.Send.Cc")]
        public string? Cc { get; set; }

        [Required(ErrorMessage = "Invoices.Send.Subject.Required")]
        [Display(Name = "Invoices.Send.Subject")]
        public string Subject { get; set; } = "";

        [Required(ErrorMessage = "Invoices.Send.Body.Required")]
        [Display(Name = "Invoices.Send.Body")]
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

        Flash(L["Invoices.Send.Sent", Invoice.Number!, Input.To]);
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
            FlashError(invoice.IsDraft ? L["Invoices.Send.NotIssued"] : L["Invoices.Send.IsCancelled"]);
            return RedirectToPage("/Invoices/Edit", new { id = invoice.Id });
        }

        Invoice = invoice;
        Head(L["Invoices.Send.Title", invoice.Number!], $"{invoice.RecipientName} · {Fmt.Money(invoice.GrossTotal)}", (L["Invoices.Title"], "/Invoices"), (invoice.Number!, $"/Invoices/Edit?id={invoice.Id}"), (L["Invoices.Send.Send"], null));
        ViewData["Width"] = "narrow";
        return null;
    }
}
