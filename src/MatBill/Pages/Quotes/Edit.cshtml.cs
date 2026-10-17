using MatBill.Configuration;
using MatBill.Core.Billing;
using MatBill.Data;
using MatBill.Data.Entities;
using MatBill.Services;
using MatBill.UiKit;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MatBill.Pages.Quotes;

/// <summary>The quote editor: the same document as an invoice, with a validity instead of a payment term. An issued quote is answered or turned into an invoice.</summary>
public sealed class EditModel(MbDbContext db, QuoteService quotes, NumberService numbers, ConfigStore config, ICurrentUser current, TimeProvider clock) : PageModelBase
{
    private long? _id;

    [BindProperty(SupportsGet = true)]
    public long? Id { get => _id; set => _id = value > 0 ? value : null; }

    [BindProperty]
    public DocumentInput Input { get; set; } = new();

    public Quote Quote { get; private set; } = null!;

    public DocumentEditorModel? Editor { get; private set; }

    public DocumentViewModel? View { get; private set; }

    public string EditorView { get; private set; } = "document";

    public string EditorJson { get; private set; } = "{}";

    public bool IsNew => Quote.Id == 0;

    public DateOnly Today => Fmt.Today(clock);

    public async Task<IActionResult> OnGetAsync(long? customer)
    {
        if (Id is { } id)
        {
            var found = await quotes.FindAsync(id);
            if (found is null)
            {
                return NotFound();
            }

            Quote = found;
        }
        else
        {
            var forCustomer = customer is { } c ? await db.Customer.AsNoTracking().FirstOrDefaultAsync(x => x.Id == c) : null;
            Quote = quotes.NewDraft(forCustomer);
        }

        Input = DocumentInputs.From(Quote);
        if (Input.Lines.Count == 0)
        {
            Input.Lines.Add(new LineInput { Quantity = "1", TaxRate = Amount.Quantity(config.Current.Invoicing.DefaultTaxRate) });
        }

        await BuildAsync();
        return Page();
    }

    public Task<IActionResult> OnPostSaveAsync() => SaveAsync(issue: false);

    public Task<IActionResult> OnPostIssueAsync() => SaveAsync(issue: true);

    private async Task<IActionResult> SaveAsync(bool issue)
    {
        var quote = Id is { } id ? await quotes.FindAsync(id) : quotes.NewDraft();
        if (quote is null)
        {
            return NotFound();
        }

        Quote = quote;
        if (!quote.IsDraft)
        {
            FlashError("Dieses Angebot ist bereits ausgestellt.");
            return RedirectToPage(new { id = quote.Id });
        }

        var problems = quotes.Apply(quote, Input);
        await quotes.LinkCustomerAsync(quote);
        if (issue && (string.IsNullOrWhiteSpace(quote.RecipientName) || quote.Lines.Count == 0))
        {
            problems.Add(new("", "Ein Angebot braucht einen Empfänger und mindestens eine Position."));
        }

        if (problems.Count > 0)
        {
            foreach (var p in problems.DistinctBy(p => p.Message))
            {
                ModelState.AddModelError(p.Key, p.Message);
            }

            await BuildAsync();
            return Page();
        }

        if (quote.Id == 0)
        {
            db.Quote.Add(quote);
        }

        await db.SaveChangesAsync();
        if (issue)
        {
            try
            {
                await quotes.IssueAsync(quote);
            }
            catch (InvoiceException ex)
            {
                FlashError(ex.Message);
                return RedirectToPage(new { id = quote.Id });
            }

            Flash($"Angebot {quote.Number} ausgestellt.");
        }
        else
        {
            Flash(Id is null ? "Angebotsentwurf angelegt." : "Angebotsentwurf gespeichert.");
        }

        return RedirectToPage(new { id = quote.Id });
    }

    public Task<IActionResult> OnPostAcceptAsync() => ChangeAsync(q => quotes.AnswerAsync(q, true), q => $"Angebot {q.Number} ist angenommen.");

    public Task<IActionResult> OnPostDeclineAsync() => ChangeAsync(q => quotes.AnswerAsync(q, false), q => $"Angebot {q.Number} ist abgelehnt.");

    public async Task<IActionResult> OnPostInvoiceAsync()
    {
        var quote = Id is { } id ? await quotes.FindAsync(id) : null;
        if (quote is null)
        {
            return NotFound();
        }

        try
        {
            var invoice = await quotes.ToInvoiceAsync(quote);
            Flash($"Rechnungsentwurf aus Angebot {quote.Number} angelegt. Prüfe ihn und stelle ihn aus.");
            return RedirectToPage("/Invoices/Edit", new { id = invoice.Id });
        }
        catch (InvoiceException ex)
        {
            FlashError(ex.Message);
            return RedirectToPage(new { id = quote.Id });
        }
    }

    public async Task<IActionResult> OnPostDuplicateAsync()
    {
        var quote = Id is { } id ? await quotes.FindAsync(id) : null;
        if (quote is null)
        {
            return NotFound();
        }

        var copy = await quotes.DuplicateAsync(quote);
        Flash("Kopie als neuer Angebotsentwurf angelegt.");
        return RedirectToPage(new { id = copy.Id });
    }

    public async Task<IActionResult> OnPostDeleteAsync()
    {
        var quote = Id is { } id ? await quotes.FindAsync(id) : null;
        if (quote is null)
        {
            return RedirectToPage("/Quotes/Index");
        }

        try
        {
            await quotes.DeleteDraftAsync(quote);
            Flash("Angebotsentwurf gelöscht.");
            return RedirectToPage("/Quotes/Index");
        }
        catch (InvoiceException ex)
        {
            FlashError(ex.Message);
            return RedirectToPage(new { id = quote.Id });
        }
    }

    private async Task<IActionResult> ChangeAsync(Func<Quote, Task> change, Func<Quote, string> message)
    {
        var quote = Id is { } id ? await quotes.FindAsync(id) : null;
        if (quote is null)
        {
            return NotFound();
        }

        try
        {
            await change(quote);
            Flash(message(quote));
        }
        catch (InvoiceException ex)
        {
            FlashError(ex.Message);
        }

        return RedirectToPage(new { id = quote.Id });
    }

    private async Task BuildAsync()
    {
        var company = config.Current.Company;
        var me = current.UserId is { } uid ? await db.User.AsNoTracking().FirstOrDefaultAsync(u => u.Id == uid) : null;
        EditorView = me?.EditorView ?? "document";
        ViewData["OwnHead"] = true;
        ViewData["Width"] = "wide";
        ViewData["ActiveHref"] = "/Quotes";
        var title = IsNew ? "Neues Angebot" : Quote.Number is { } n ? $"Angebot {n}" : "Angebotsentwurf";
        Head(title, null, ("Angebote", "/Quotes"), (IsNew ? "Neu" : Quote.Number ?? "Entwurf", null));

        if (!Quote.IsDraft)
        {
            View = new DocumentViewModel(InvoiceDocument.Of(Quote, config.Current.Invoicing), company, Brand.LogoUrl(company));
            return;
        }

        Editor = new DocumentEditorModel
        {
            Input = Input,
            Mode = EditorMode.Quote,
            Company = company,
            Invoicing = config.Current.Invoicing,
            NumberPreview = await numbers.PeekAsync(QuoteService.QuoteRange, config.Current.Invoicing.QuoteNumberPattern, Input.InvoiceDate ?? Today),
            LogoUrl = Brand.LogoUrl(company),
            CanEditBranding = current.IsAdmin,
            Stamp = "ENTWURF",
        };
        var customers = await db.Customer.AsNoTracking().OrderBy(c => c.Name).Take(5000).ToListAsync();
        var products = await db.Product.AsNoTracking().Where(p => p.IsActive).OrderBy(p => p.Name).Take(5000).ToListAsync();
        EditorJson = EditorData.Serialize(new
        {
            customers = customers.Select(CustomerJson.Of),
            products = products.Select(ProductJson.Of),
            taxExempt = company.SmallBusiness,
            defaultTaxRate = Amount.Quantity(config.Current.Invoicing.DefaultTaxRate),
            draftKey = "quote-" + (Quote.Id == 0 ? "new" : Quote.Id.ToString()),
            savedAt = Quote.Id == 0 ? 0 : new DateTimeOffset(DateTime.SpecifyKind(Quote.UpdateDate, DateTimeKind.Utc)).ToUnixTimeMilliseconds(),
        });
    }

}

