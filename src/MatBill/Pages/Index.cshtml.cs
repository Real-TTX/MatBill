using MatBill.Configuration;
using MatBill.Core.Billing;
using MatBill.Data;
using MatBill.Data.Entities;
using MatBill.Services;
using MatBill.UiKit;
using Microsoft.EntityFrameworkCore;

namespace MatBill.Pages;

/// <summary>The overview: what is open and overdue, what came in, what the series do next, and what needs a look.</summary>
public sealed class IndexModel(MbDbContext db, ConfigStore config, ICurrentUser current, TimeProvider clock) : PageModelBase
{
    public DateOnly Today { get; private set; }

    public decimal Open { get; private set; }

    public int OpenCount { get; private set; }

    public decimal Overdue { get; private set; }

    public int OverdueCount { get; private set; }

    public decimal PaidThisMonth { get; private set; }

    public decimal RevenueThisYear { get; private set; }

    public int Drafts { get; private set; }

    public decimal ExpensesThisYear { get; private set; }

    public int OpenBookings { get; private set; }

    public int ExpensesToReview { get; private set; }

    public int OpenQuotes { get; private set; }

    public List<Invoice> Recent { get; private set; } = [];

    public List<Invoice> OverdueList { get; private set; } = [];

    public List<(RecurringInvoice Series, decimal Gross)> Upcoming { get; private set; } = [];

    public List<RecurringInvoice> SeriesProblems { get; private set; } = [];

    public List<Invoice> SendProblems { get; private set; } = [];

    public bool NeedsSetup { get; private set; }

    public bool IsAdmin => current.IsAdmin;

    public bool IsEmpty { get; private set; }

    public string Greeting { get; private set; } = "";

    public async Task OnGetAsync()
    {
        Today = Fmt.Today(clock);
        var hour = Fmt.Local(clock.GetUtcNow().UtcDateTime).Hour;
        Greeting = (hour < 11 ? "Guten Morgen" : hour < 18 ? "Hallo" : "Guten Abend") + (current.Name is { Length: > 0 } name ? ", " + name.Split(' ')[0] : "");
        Head("Übersicht", Greeting, ("Übersicht", null));

        var monthStart = new DateOnly(Today.Year, Today.Month, 1);
        var yearStart = new DateOnly(Today.Year, 1, 1);
        Open = await db.Invoice.Where(i => i.Status == InvoiceStatus.Open).SumAsync(i => i.GrossTotal);
        OpenCount = await db.Invoice.CountAsync(i => i.Status == InvoiceStatus.Open);
        Overdue = await db.Invoice.Where(i => i.Status == InvoiceStatus.Open && i.DueDate < Today).SumAsync(i => i.GrossTotal);
        OverdueCount = await db.Invoice.CountAsync(i => i.Status == InvoiceStatus.Open && i.DueDate < Today);
        PaidThisMonth = await db.Invoice.Where(i => i.Status == InvoiceStatus.Paid && i.Kind == InvoiceKind.Invoice && i.PaidDate >= monthStart).SumAsync(i => i.GrossTotal);
        RevenueThisYear = await db.Invoice.Where(i => i.Status != InvoiceStatus.Draft && i.InvoiceDate >= yearStart).SumAsync(i => i.NetTotal);
        ExpensesThisYear = await db.Expense.Where(e => e.ExpenseDate >= yearStart).SumAsync(e => e.NetAmount);
        OpenBookings = await db.BankTransaction.CountAsync(t => t.State == MatchState.Open);
        ExpensesToReview = await db.Expense.CountAsync(e => e.NeedsReview);
        OpenQuotes = await db.Quote.CountAsync(q => q.Status == QuoteStatus.Open && q.ValidUntil >= Today);
        Drafts = await db.Invoice.CountAsync(i => i.Status == InvoiceStatus.Draft);
        IsEmpty = !await db.Invoice.AnyAsync();

        Recent = await db.Invoice.AsNoTracking().OrderByDescending(i => i.UpdateDate).Take(6).ToListAsync();
        OverdueList = await db.Invoice.AsNoTracking().Where(i => i.Status == InvoiceStatus.Open && i.DueDate < Today).OrderBy(i => i.DueDate).Take(5).ToListAsync();
        var exempt = config.Current.Company.SmallBusiness;
        var upcoming = await db.RecurringInvoice.AsNoTracking().Include(s => s.Customer).Include(s => s.Lines)
            .Where(s => s.Status == RecurringStatus.Active && s.NextRunDate != null).OrderBy(s => s.NextRunDate).Take(5).ToListAsync();
        Upcoming = [.. upcoming.Select(s => (s, InvoiceMath.Totals(s.Lines.Select(l => l.Amounts), exempt).Gross))];
        SeriesProblems = await db.RecurringInvoice.AsNoTracking().Where(s => s.Status == RecurringStatus.Active && s.LastError != null).OrderBy(s => s.Name).Take(5).ToListAsync();
        SendProblems = await db.Invoice.AsNoTracking().Where(i => i.SendState == SendState.Failed && i.Status != InvoiceStatus.Cancelled).OrderByDescending(i => i.UpdateDate).Take(5).ToListAsync();

        var company = config.Current.Company;
        NeedsSetup = string.IsNullOrWhiteSpace(company.Name) || (string.IsNullOrWhiteSpace(company.TaxNumber) && string.IsNullOrWhiteSpace(company.VatId)) || string.IsNullOrWhiteSpace(company.Iban);
    }
}
