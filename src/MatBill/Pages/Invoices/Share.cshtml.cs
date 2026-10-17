using System.ComponentModel.DataAnnotations;
using MatBill.Data;
using MatBill.Data.Entities;
using MatBill.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MatBill.Pages.Invoices;

/// <summary>A link to an issued invoice for somebody without an account (the role "Anonym"): the invoice and its PDF, read only, for a limited time.</summary>
public sealed class ShareModel(MbDbContext db, InvoiceService invoices, ShareService shares, ActivityLogger log, TimeProvider clock) : PageModelBase
{
    public sealed class LinkInput
    {
        [Range(1, 365, ErrorMessage = "Zwischen 1 und 365 Tagen.")]
        public int Days { get; set; } = 30;

        public string? Password { get; set; }

        public string? Message { get; set; }

        public string? RecipientName { get; set; }
    }

    [BindProperty(SupportsGet = true)]
    public long Id { get; set; }

    [BindProperty]
    public LinkInput Input { get; set; } = new();

    public Invoice Invoice { get; private set; } = null!;

    public List<ShareLink> Links { get; private set; } = [];

    public DateTime Now => clock.GetUtcNow().UtcDateTime;

    public string UrlOf(ShareLink link) => shares.LinkOf(link);

    public async Task<IActionResult> OnGetAsync()
    {
        if (await LoadAsync() is { } problem)
        {
            return problem;
        }

        Input = new LinkInput { Days = shares.DefaultDays, RecipientName = Invoice.RecipientName };
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (await LoadAsync() is { } problem)
        {
            return problem;
        }

        if (!string.IsNullOrEmpty(Input.Password) && Input.Password.Length < 6)
        {
            ModelState.AddModelError("Input.Password", "Ein Passwort braucht mindestens 6 Zeichen.");
        }

        if (!ModelState.IsValid)
        {
            return Page();
        }

        var link = await shares.CreateAsync(Invoice, $"Rechnung {Invoice.Number}", Input.Message, Input.Days, Input.Password, Input.RecipientName);
        await log.LogAsync("Freigabe-Link angelegt", nameof(ShareLink), link.Id, $"Rechnung {Invoice.Number}");
        Flash("Der Link ist angelegt. Kopiere ihn und gib ihn weiter.");
        return RedirectToPage(new { id = Id });
    }

    private async Task<IActionResult?> LoadAsync()
    {
        var invoice = await invoices.FindAsync(Id);
        if (invoice is null)
        {
            return NotFound();
        }

        if (invoice.IsDraft)
        {
            FlashError("Ein Entwurf kann nicht freigegeben werden. Stelle die Rechnung zuerst aus.");
            return RedirectToPage("/Invoices/Edit", new { id = invoice.Id });
        }

        Invoice = invoice;
        Links = await db.ShareLink.AsNoTracking().Where(l => l.InvoiceId == invoice.Id).OrderByDescending(l => l.CreateDate).ToListAsync();
        Head($"Rechnung {invoice.Number} freigeben", "Ein Link, mit dem der Kunde die Rechnung ohne Konto ansehen und herunterladen kann.",
            ("Rechnungen", "/Invoices"), (invoice.Number!, $"/Invoices/Edit?id={invoice.Id}"), ("Freigabelink", null));
        ViewData["Width"] = "narrow";
        ViewData["ActiveHref"] = "/Invoices";
        return null;
    }
}
