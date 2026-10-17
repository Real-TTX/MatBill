using MatBill.Configuration;
using MatBill.Core.Billing;
using MatBill.Data;
using MatBill.Data.Entities;
using MatBill.Services;
using MatBill.UiKit;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MatBill.Pages.Recurring;

/// <summary>
/// One series: the template as a document (positions, texts, customer), the schedule beside it, and what it did. Changing the template changes only the invoices
/// that come next; the ones it made stay as they are.
/// </summary>
public sealed class EditModel(MbDbContext db, RecurringService recurring, ConfigStore config, ICurrentUser current, NavCounts nav, TimeProvider clock) : PageModelBase
{
    [BindProperty(SupportsGet = true)]
    public long? Id { get => _id; set => _id = value > 0 ? value : null; }

    // A form of something new posts id=0: that is "new", not an invoice with the number 0.
    private long? _id;

    [BindProperty]
    public DocumentInput Input { get; set; } = new();

    [BindProperty]
    public string? Name { get; set; }

    [BindProperty]
    public bool StatePeriod { get; set; } = true;

    public RecurringInvoice Series { get; private set; } = null!;

    public DocumentEditorModel Editor { get; private set; } = null!;

    public string EditorView { get; private set; } = "document";

    public string EditorJson { get; private set; } = "{}";

    public List<RecurringRun> Runs { get; private set; } = [];

    public List<Invoice> Invoices { get; private set; } = [];

    public IReadOnlyList<DateOnly> Upcoming { get; private set; } = [];

    public bool IsNew => Series.Id == 0;

    public DateOnly Today => Fmt.Today(clock);

    public async Task<IActionResult> OnGetAsync(long? customer)
    {
        if (Id is { } id)
        {
            var found = await recurring.LoadAsync(id);
            if (found is null)
            {
                return NotFound();
            }

            Series = found;
        }
        else
        {
            var defaults = config.Current.Invoicing;
            Series = new RecurringInvoice
            {
                StartDate = Today,
                IntroText = defaults.IntroText,
                PaymentTerms = defaults.PaymentTerms,
                ClosingText = defaults.ClosingText,
                PaymentTermDays = defaults.PaymentTermDays,
                Lines = [new RecurringInvoiceLine { Quantity = 1m, TaxRate = defaults.DefaultTaxRate }],
            };
            if (customer is { } c && await db.Customer.AsNoTracking().FirstOrDefaultAsync(x => x.Id == c) is { } chosen)
            {
                Series.CustomerId = chosen.Id;
                Series.Customer = chosen;
            }
        }

        Input = DocumentInputs.From(Series);
        if (IsNew)
        {
            Input.Lines = [new LineInput { Quantity = "1", TaxRate = Amount.Quantity(config.Current.Invoicing.DefaultTaxRate) }];
        }

        Name = Series.Name;
        StatePeriod = Series.StatePeriod;
        await BuildAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostSaveAsync()
    {
        var series = Id is { } id ? await recurring.LoadAsync(id) : new RecurringInvoice();
        if (series is null)
        {
            return NotFound();
        }

        Series = series;
        var problems = await recurring.ApplyAsync(series, Input, Name, StatePeriod);
        if (problems.Count > 0)
        {
            foreach (var p in problems.DistinctBy(p => p.Message))
            {
                ModelState.AddModelError(p.Key, p.Message);
            }

            await BuildAsync();
            return Page();
        }

        if (series.Status == RecurringStatus.Ended && Id is null)
        {
            series.Status = RecurringStatus.Active;
        }

        // Whoever saves the template has looked at it: a series that waited after failures may try again at once.
        series.FailedAttempts = 0;
        series.RetryAfterDate = null;
        await recurring.RescheduleAsync(series);
        if (series.Id == 0)
        {
            db.RecurringInvoice.Add(series);
        }

        await db.SaveChangesAsync();
        nav.Invalidate();
        Flash(Id is null
            ? $"Serie angelegt. Die erste Rechnung entsteht am {Fmt.Date(series.NextRunDate)}."
            : "Vorlage gespeichert. Sie gilt für die nächsten Rechnungen; bereits erstellte bleiben unverändert.");
        return RedirectToPage(new { id = series.Id });
    }

    public async Task<IActionResult> OnPostPauseAsync() => await ChangeAsync(s => recurring.PauseAsync(s), "Serie pausiert. Bis zum Fortsetzen entstehen keine Rechnungen.");

    public async Task<IActionResult> OnPostResumeAsync() => await ChangeAsync(s => recurring.ResumeAsync(s), "Serie fortgesetzt.");

    public async Task<IActionResult> OnPostEndAsync() => await ChangeAsync(s => recurring.EndAsync(s), "Serie beendet. Die erstellten Rechnungen bleiben erhalten.");

    /// <summary>Makes the invoice of the next date now ("Jetzt ausführen"), or tries a failed one again ("Erneut ausführen").</summary>
    public async Task<IActionResult> OnPostRunAsync()
    {
        var series = Id is { } id ? await recurring.LoadAsync(id) : null;
        if (series is null)
        {
            return NotFound();
        }

        if (series.Status != RecurringStatus.Active || series.NextRunDate is null)
        {
            FlashError("Nur eine aktive Serie mit einem nächsten Termin kann ausgeführt werden.");
            return RedirectToPage(new { id = series.Id });
        }

        var result = await recurring.RunAsync(series, manual: true);
        nav.Invalidate();
        if (result.Invoice is { } invoice)
        {
            Flash($"Rechnung {invoice.Number} erstellt{(series.AutoSend ? " und zum Versand vorgemerkt" : "")}.");
            return RedirectToPage("/Invoices/Edit", new { id = invoice.Id });
        }

        FlashError(result.Error ?? "Die Rechnung konnte nicht erstellt werden.");
        return RedirectToPage(new { id = series.Id });
    }

    public async Task<IActionResult> OnPostDeleteAsync()
    {
        var series = Id is { } id ? await db.RecurringInvoice.FirstOrDefaultAsync(s => s.Id == id) : null;
        if (series is not null)
        {
            db.RecurringInvoice.Remove(series);
            await db.SaveChangesAsync();
            nav.Invalidate();
            Flash("Serie gelöscht. Die erstellten Rechnungen bleiben erhalten.");
        }

        return RedirectToPage("/Recurring/Index");
    }

    private async Task<IActionResult> ChangeAsync(Func<RecurringInvoice, Task> change, string message)
    {
        var series = Id is { } id ? await recurring.LoadAsync(id) : null;
        if (series is null)
        {
            return NotFound();
        }

        await change(series);
        nav.Invalidate();
        Flash(message);
        return RedirectToPage(new { id = series.Id });
    }

    private async Task BuildAsync()
    {
        var company = config.Current.Company;
        var me = current.UserId is { } uid ? await db.User.AsNoTracking().FirstOrDefaultAsync(u => u.Id == uid) : null;
        EditorView = me?.EditorView ?? "document";
        ViewData["OwnHead"] = true;
        ViewData["Width"] = "wide";
        ViewData["ActiveHref"] = "/Recurring";
        Head(IsNew ? "Neue Serie" : Series.Name, null, ("Wiederkehrend", "/Recurring"), (IsNew ? "Neu" : Series.Name, null));

        Editor = new DocumentEditorModel
        {
            Input = Input,
            Mode = EditorMode.Template,
            Company = company,
            Invoicing = config.Current.Invoicing,
            LogoUrl = Brand.LogoUrl(company),
            CanEditBranding = current.IsAdmin,
            Stamp = "VORLAGE",
            StatePeriod = StatePeriod,
        };

        if (!IsNew)
        {
            Runs = await db.RecurringRun.AsNoTracking().Include(r => r.Invoice).Where(r => r.RecurringInvoiceId == Series.Id).OrderByDescending(r => r.RunDate).Take(30).ToListAsync();
            Invoices = await db.Invoice.AsNoTracking().Where(i => i.RecurringInvoiceId == Series.Id).OrderByDescending(i => i.InvoiceDate).Take(12).ToListAsync();
            Upcoming = Series.Status == RecurringStatus.Active && Series.NextRunDate is { } next
                ? Recurrence.Upcoming(Series.StartDate, Series.Interval, next, 6, Series.EndDate)
                : [];
        }

        var customers = await db.Customer.AsNoTracking().OrderBy(c => c.Name).Take(5000).ToListAsync();
        var products = await db.Product.AsNoTracking().Where(p => p.IsActive).OrderBy(p => p.Name).Take(5000).ToListAsync();
        EditorJson = EditorData.Serialize(new
        {
            customers = customers.Select(CustomerJson.Of),
            products = products.Select(ProductJson.Of),
            taxExempt = company.SmallBusiness,
            defaultTaxRate = Amount.Quantity(config.Current.Invoicing.DefaultTaxRate),
            requireCustomer = true,
            draftKey = "series-" + (Series.Id == 0 ? "new" : Series.Id.ToString()),
            savedAt = Series.Id == 0 ? 0 : new DateTimeOffset(DateTime.SpecifyKind(Series.UpdateDate, DateTimeKind.Utc)).ToUnixTimeMilliseconds(),
        });
    }
}
