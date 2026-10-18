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
        [Range(1, 365, ErrorMessage = "Invoices.Share.Days.Range")]
        [Display(Name = "Invoices.Share.Days")]
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
            ModelState.AddModelError("Input.Password", L["Invoices.Share.Password.Short"]);
        }

        if (!ModelState.IsValid)
        {
            return Page();
        }

        // The title is what the customer sees behind the link: German, like the invoice itself.
        var link = await shares.CreateAsync(Invoice, $"Rechnung {Invoice.Number}", Input.Message, Input.Days, Input.Password, Input.RecipientName);
        await log.LogAsync(L["Invoices.Share.Log"], nameof(ShareLink), link.Id, L["Invoices.Invoice", Invoice.Number!]);
        Flash(L["Invoices.Share.Created"]);
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
            FlashError(L["Invoices.Share.Draft"]);
            return RedirectToPage("/Invoices/Edit", new { id = invoice.Id });
        }

        Invoice = invoice;
        Links = await db.ShareLink.AsNoTracking().Where(l => l.InvoiceId == invoice.Id).OrderByDescending(l => l.CreateDate).ToListAsync();
        Head(L["Invoices.Share.Title", invoice.Number!], L["Invoices.Share.Sub"],
            (L["Invoices.Title"], "/Invoices"), (invoice.Number!, $"/Invoices/Edit?id={invoice.Id}"), (L["Invoices.Edit.ShareLink"], null));
        ViewData["Width"] = "narrow";
        ViewData["ActiveHref"] = "/Invoices";
        return null;
    }
}
