using MatBill.Configuration;
using MatBill.Core.Billing;
using MatBill.Data;
using MatBill.Data.Entities;
using MatBill.Services;
using MatBill.UiKit;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MatBill.Pages.Invoices;

/// <summary>
/// The invoice editor: the document is the interface. A draft is edited in place and saved as often as wanted; "Ausstellen" gives it its number and fixes it.
/// An issued invoice is shown read-only with what can still happen to it: send, mark as paid, cancel, duplicate, repeat.
/// </summary>
public sealed class EditModel(MbDbContext db, InvoiceService invoices, RecurringService recurring, NumberService numbers, ConfigStore config, ICurrentUser current,
    NavCounts nav, TimeProvider clock) : PageModelBase
{
    [BindProperty(SupportsGet = true)]
    public long? Id { get => _id; set => _id = value > 0 ? value : null; }

    // A form of something new posts id=0: that is "new", not an invoice with the number 0.
    private long? _id;

    [BindProperty]
    public DocumentInput Input { get; set; } = new();

    public Invoice Invoice { get; private set; } = null!;

    public DocumentEditorModel? Editor { get; private set; }

    public DocumentViewModel? View { get; private set; }

    /// <summary>The series this invoice started or belongs to.</summary>
    public RecurringInvoice? Series { get; private set; }

    public bool SeriesFromThis => Series is not null && Series.SourceInvoiceId == Invoice.Id && Invoice.Id > 0;

    public List<MailDelivery> Deliveries { get; private set; } = [];

    public string EditorView { get; private set; } = "document";

    public string EditorJson { get; private set; } = "{}";

    public DateOnly Today => Fmt.Today(clock);

    public bool IsNew => Invoice.Id == 0;

    public async Task<IActionResult> OnGetAsync(long? customer, long? from)
    {
        if (Id is { } id)
        {
            var found = await invoices.FindAsync(id);
            if (found is null)
            {
                return NotFound();
            }

            Invoice = found;
            Input = DocumentInputs.From(found);
        }
        else
        {
            var forCustomer = customer is { } c ? await db.Customer.AsNoTracking().FirstOrDefaultAsync(x => x.Id == c) : null;
            Invoice = invoices.NewDraft(forCustomer);
            Input = DocumentInputs.From(Invoice);
            if (Input.Lines.Count == 0)
            {
                Input.Lines.Add(new LineInput { Quantity = "1", TaxRate = Amount.Quantity(config.Current.Invoicing.DefaultTaxRate) });
            }
        }

        await LoadSeriesAsync();
        if (Series is not null && SeriesFromThis)
        {
            Input.Repeat = true;
            Input.Interval = Series.Interval;
            Input.RepeatStart = Series.StartDate;
            Input.RepeatRuns = Series.EndDate is null ? "open" : "until";
            Input.RepeatEnd = Series.EndDate;
            Input.AutoSend = Series.AutoSend;
        }

        await BuildAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostSaveAsync() => await SaveAsync(issue: false);

    public async Task<IActionResult> OnPostIssueAsync() => await SaveAsync(issue: true);

    private async Task<IActionResult> SaveAsync(bool issue)
    {
        var invoice = Id is { } id ? await invoices.FindAsync(id) : invoices.NewDraft();
        if (invoice is null)
        {
            return NotFound();
        }

        Invoice = invoice;
        if (!invoice.IsDraft)
        {
            FlashError(L["Invoices.Edit.AlreadyIssued"]);
            return RedirectToPage(new { id = invoice.Id });
        }

        var problems = invoices.Apply(invoice, Input);
        await invoices.LinkCustomerAsync(invoice);
        if (Input.Repeat && invoice.Customer is null)
        {
            problems.Add(new("Input.RecipientName", L["Invoices.Edit.RepeatNeedsCustomer"]));
        }

        if (Input.Repeat && Input.RepeatRuns == "until" && Input.RepeatEnd is { } end && end < (Input.RepeatStart ?? invoice.InvoiceDate))
        {
            problems.Add(new("Input.RepeatEnd", L["Invoices.Edit.RepeatEndBeforeStart"]));
        }

        if (issue)
        {
            problems.AddRange(InvoiceService.CheckIssuable(invoice));
        }

        if (problems.Count > 0)
        {
            foreach (var p in problems.DistinctBy(p => p.Message))
            {
                ModelState.AddModelError(p.Key, p.Message);
            }

            await LoadSeriesAsync();
            await BuildAsync();
            return Page();
        }

        if (invoice.Id == 0)
        {
            db.Invoice.Add(invoice);
        }

        await db.SaveChangesAsync();
        await SyncSeriesAsync(invoice);

        if (issue)
        {
            try
            {
                await invoices.IssueAsync(invoice);
            }
            catch (InvoiceException ex)
            {
                FlashError(ex.Message);
                return RedirectToPage(new { id = invoice.Id });
            }

            nav.Invalidate();
            Flash(L["Invoices.Edit.Issued", invoice.Number!]);
            return RedirectToPage(new { id = invoice.Id });
        }

        nav.Invalidate();
        Flash(Id is null ? L["Invoices.Edit.DraftCreated"] : L["Invoices.Edit.DraftSaved"]);
        return RedirectToPage(new { id = invoice.Id });
    }

    /// <summary>"Wiederholung einrichten" of a draft: the series follows the draft (made, updated, or removed again while it has made nothing).</summary>
    private async Task SyncSeriesAsync(Invoice invoice)
    {
        var existing = await db.RecurringInvoice.Include(s => s.Lines).FirstOrDefaultAsync(s => s.SourceInvoiceId == invoice.Id);
        if (!Input.Repeat)
        {
            if (existing is { CreatedCount: 0 })
            {
                db.RecurringInvoice.Remove(existing);
                await db.SaveChangesAsync();
            }

            return;
        }

        var start = Input.RepeatStart ?? Recurrence.Occurrence(invoice.InvoiceDate, Input.Interval, 1);
        var end = Input.RepeatRuns == "until" ? Input.RepeatEnd : null;
        if (existing is null)
        {
            var series = recurring.FromInvoice(invoice, Input.Interval, start, end, Input.AutoSend);
            db.RecurringInvoice.Add(series);
            await db.SaveChangesAsync();
            return;
        }

        // The series takes the draft as its template again: what comes next looks like this invoice.
        existing.CustomerId = invoice.CustomerId ?? existing.CustomerId;
        existing.Interval = Input.Interval;
        existing.StartDate = start;
        existing.EndDate = end;
        existing.AutoSend = Input.AutoSend;
        existing.Title = invoice.Title;
        existing.IntroText = invoice.IntroText;
        existing.PaymentTerms = invoice.PaymentTerms;
        existing.ClosingText = invoice.ClosingText;
        existing.PaymentTermDays = invoice.PaymentTermDays;
        existing.Lines.Clear();
        existing.Lines.AddRange(invoice.Lines.OrderBy(l => l.Position).Select(l => InvoiceService.CopyLine(l, new RecurringInvoiceLine())));
        await recurring.RescheduleAsync(existing);
        await db.SaveChangesAsync();
    }

    /// <summary>"Wiederholung einrichten" of an issued invoice: a new series with this invoice as its template.</summary>
    public async Task<IActionResult> OnPostRepeatAsync()
    {
        var invoice = Id is { } id ? await invoices.FindAsync(id) : null;
        if (invoice is null)
        {
            return NotFound();
        }

        if (invoice.CustomerId is null)
        {
            FlashError(L["Invoices.Edit.RepeatNeedsCustomer"]);
            return RedirectToPage(new { id = invoice.Id });
        }

        var start = Input.RepeatStart ?? Recurrence.Occurrence(invoice.InvoiceDate, Input.Interval, 1);
        var end = Input.RepeatRuns == "until" ? Input.RepeatEnd : null;
        if (end is { } e && e < start)
        {
            FlashError(L["Invoices.Edit.RepeatEndBeforeStart"]);
            return RedirectToPage(new { id = invoice.Id });
        }

        var series = recurring.FromInvoice(invoice, Input.Interval, start, end, Input.AutoSend);
        db.RecurringInvoice.Add(series);
        await db.SaveChangesAsync();
        Flash(L["Invoices.Edit.RepeatCreated", Labels.Of(series.Interval).ToLowerInvariant(), Fmt.Date(series.NextRunDate)]);
        return RedirectToPage("/Recurring/Edit", new { id = series.Id });
    }

    public async Task<IActionResult> OnPostDeleteAsync()
    {
        var invoice = Id is { } id ? await invoices.FindAsync(id) : null;
        if (invoice is null)
        {
            return RedirectToPage("/Invoices/Index");
        }

        try
        {
            var series = await db.RecurringInvoice.FirstOrDefaultAsync(s => s.SourceInvoiceId == invoice.Id && s.CreatedCount == 0);
            if (series is not null)
            {
                db.RecurringInvoice.Remove(series);
            }

            await invoices.DeleteDraftAsync(invoice);
        }
        catch (InvoiceException ex)
        {
            FlashError(ex.Message);
            return RedirectToPage(new { id = invoice.Id });
        }

        nav.Invalidate();
        Flash(L["Invoices.Edit.DraftDeleted"]);
        return RedirectToPage("/Invoices/Index");
    }

    public async Task<IActionResult> OnPostPaidAsync(DateOnly? paidOn) => await ChangeAsync(i => invoices.MarkPaidAsync(i, paidOn ?? Today), i => L["Invoices.Edit.Paid", i.Number!].Value);

    public async Task<IActionResult> OnPostReopenAsync() => await ChangeAsync(i => invoices.ReopenAsync(i), i => L["Invoices.Edit.Reopened", i.Number!].Value);

    /// <summary>Cancels the invoice with a cancellation invoice of its own number; the page of the cancellation invoice opens.</summary>
    public async Task<IActionResult> OnPostCancelAsync(string? reason)
    {
        var invoice = Id is { } id ? await invoices.FindAsync(id) : null;
        if (invoice is null)
        {
            return NotFound();
        }

        try
        {
            var storno = await invoices.CancelAsync(invoice, reason);
            nav.Invalidate();
            Flash(L["Invoices.Edit.Cancelled", invoice.Number!, storno.Number!]);
            return RedirectToPage(new { id = storno.Id });
        }
        catch (InvoiceException ex)
        {
            FlashError(ex.Message);
            return RedirectToPage(new { id = invoice.Id });
        }
    }

    public async Task<IActionResult> OnPostDuplicateAsync()
    {
        var invoice = Id is { } id ? await invoices.FindAsync(id) : null;
        if (invoice is null)
        {
            return NotFound();
        }

        var copy = await invoices.DuplicateAsync(invoice);
        nav.Invalidate();
        Flash(L["Invoices.Edit.Duplicated"]);
        return RedirectToPage(new { id = copy.Id });
    }

    private async Task<IActionResult> ChangeAsync(Func<Invoice, Task> change, Func<Invoice, string> message)
    {
        var invoice = Id is { } id ? await invoices.FindAsync(id) : null;
        if (invoice is null)
        {
            return NotFound();
        }

        try
        {
            await change(invoice);
            nav.Invalidate();
            Flash(message(invoice));
        }
        catch (InvoiceException ex)
        {
            FlashError(ex.Message);
        }

        return RedirectToPage(new { id = invoice.Id });
    }

    private async Task LoadSeriesAsync()
    {
        if (Invoice.Id == 0)
        {
            return;
        }

        Series = await db.RecurringInvoice.AsNoTracking().Include(s => s.Customer)
            .FirstOrDefaultAsync(s => s.SourceInvoiceId == Invoice.Id || (Invoice.RecurringInvoiceId != null && s.Id == Invoice.RecurringInvoiceId));
    }

    private async Task BuildAsync()
    {
        var company = config.Current.Company;
        var me = current.UserId is { } uid ? await db.User.AsNoTracking().FirstOrDefaultAsync(u => u.Id == uid) : null;
        EditorView = me?.EditorView ?? "document";
        ViewData["OwnHead"] = true;
        ViewData["Width"] = "wide";
        ViewData["ActiveHref"] = IsNew ? "/Invoices/Edit" : "/Invoices";
        string title = IsNew ? L["Invoices.New"]
            : Invoice.Number is { } number ? L[Invoice.Kind == InvoiceKind.Cancellation ? "Invoices.Edit.TitleCancellation" : "Invoices.Invoice", number]
            : L["Invoices.Edit.TitleDraft"];
        Head(title, null, (L["Invoices.Title"], "/Invoices"), (IsNew ? L["Common.New"] : Invoice.Number ?? L["Invoices.Draft"], null));

        if (!Invoice.IsDraft)
        {
            View = new DocumentViewModel(InvoiceDocument.Of(Invoice, config.Current.Invoicing), company, Brand.LogoUrl(company));
            Deliveries = await db.MailDelivery.AsNoTracking().Where(m => m.InvoiceId == Invoice.Id).OrderByDescending(m => m.AttemptDate).Take(20).ToListAsync();
            EditorJson = "{}";
            return;
        }

        Editor = new DocumentEditorModel
        {
            Input = Input,
            Mode = EditorMode.Invoice,
            Company = company,
            Invoicing = config.Current.Invoicing,
            NumberPreview = await numbers.PeekInvoiceNumberAsync(Input.InvoiceDate ?? Today),
            LogoUrl = Brand.LogoUrl(company),
            CanEditBranding = current.IsAdmin,
            Stamp = "ENTWURF",
        };

        // the pickers ask /api/customers and /api/products as one types: the page only says whether there is anything to find
        var hasCustomers = await db.Customer.AnyAsync();
        var hasProducts = await db.Product.AnyAsync(p => p.IsActive);
        EditorJson = EditorData.Serialize(new
        {
            hasCustomers,
            hasProducts,
            taxExempt = company.SmallBusiness,
            defaultTaxRate = Amount.Quantity(config.Current.Invoicing.DefaultTaxRate),
            draftKey = "invoice-" + (Invoice.Id == 0 ? "new" : Invoice.Id.ToString()),
            savedAt = Invoice.Id == 0 ? 0 : new DateTimeOffset(DateTime.SpecifyKind(Invoice.UpdateDate, DateTimeKind.Utc)).ToUnixTimeMilliseconds(),
        });
    }
}
