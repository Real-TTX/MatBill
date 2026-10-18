using MatBill.Configuration;
using MatBill.Core.Billing;
using MatBill.Data;
using MatBill.Data.Entities;
using MatBill.Services;
using MatBill.UiKit;
using Microsoft.EntityFrameworkCore;

namespace MatBill.Pages.Recurring;

/// <summary>All series: the next date, the rhythm, the status and the customer; the problems first when there are any.</summary>
public sealed class IndexModel(MbDbContext db, ConfigStore config, TimeProvider clock) : PageModelBase
{
    public ToolbarModel Toolbar { get; private set; } = null!;

    public PagerModel Pager { get; private set; } = null!;

    public TableModel Table { get; private set; } = null!;

    public int Active { get; private set; }

    public int Problems { get; private set; }

    public decimal MonthlyVolume { get; private set; }

    public DateOnly? Next { get; private set; }

    public DateOnly Today => Fmt.Today(clock);

    /// <summary>"1 series with errors", "3 series with errors" in the language of the interface.</summary>
    public string ProblemsText => Problems == 1 ? L["Recurring.Kpi.Problems.One", Fmt.Integer(Problems)] : L["Recurring.Kpi.Problems.Many", Fmt.Integer(Problems)];

    public async Task OnGetAsync()
    {
        Head(L["Recurring.Title"], L["Recurring.Sub"], (L["Recurring.Crumb"], null));
        var exempt = config.Current.Company.SmallBusiness;
        var customers = await db.Customer.AsNoTracking().OrderBy(c => c.Name).Select(c => new { c.Id, c.Name }).Take(500).ToListAsync();

        var spec = new ListSpec<RecurringInvoice>
        {
            Id = "series",
            Action = "/Recurring",
            Label = L["Recurring.Title"],
            Noun = L["Recurring.Noun"],
            SearchPlaceholder = L["Recurring.Search"],
            Search = (q, text) =>
            {
                var like = Like.Contains(text);
                return q.Where(s => EF.Functions.ILike(s.Name, like) || EF.Functions.ILike(s.Customer!.Name, like));
            },
            Filters =
            [
                new FilterSpec<RecurringInvoice>("status", L["Common.Status"],
                [
                    new("active", L["Recurring.Status.Active"]), new("paused", L["Recurring.Status.Paused"]), new("ended", L["Recurring.Status.Ended"]), new("failed", L["Recurring.Status.Failed"]),
                ], (q, v) => v switch
                {
                    "active" => q.Where(s => s.Status == RecurringStatus.Active),
                    "paused" => q.Where(s => s.Status == RecurringStatus.Paused),
                    "ended" => q.Where(s => s.Status == RecurringStatus.Ended),
                    _ => q.Where(s => s.LastError != null && s.Status == RecurringStatus.Active),
                }),
                new FilterSpec<RecurringInvoice>("interval", L["Recurring.Field.Interval"], ListBuilder.EnumOptions<RecurrenceInterval>(Labels.Of), (q, v) => q.Where(s => s.Interval == Enum.Parse<RecurrenceInterval>(v))),
                new FilterSpec<RecurringInvoice>("customer", L["Invoices.Customer"], [.. customers.Select(c => new FilterOption(c.Id.ToString(), c.Name))], (q, v) => long.TryParse(v, out var id) ? q.Where(s => s.CustomerId == id) : q),
            ],
            Sorts = new Dictionary<string, (string, Func<IQueryable<RecurringInvoice>, bool, IOrderedQueryable<RecurringInvoice>>)>
            {
                ["next"] = (L["Recurring.NextInvoice"], (q, d) => d ? q.OrderByDescending(s => s.NextRunDate) : q.OrderBy(s => s.NextRunDate == null).ThenBy(s => s.NextRunDate)),
                ["name"] = (L["Common.Name"], (q, d) => d ? q.OrderByDescending(s => s.Name) : q.OrderBy(s => s.Name)),
                ["customer"] = (L["Invoices.Customer"], (q, d) => d ? q.OrderByDescending(s => s.Customer!.Name) : q.OrderBy(s => s.Customer!.Name)),
                ["created"] = (L["Recurring.CreatedInvoices"], (q, d) => d ? q.OrderByDescending(s => s.CreatedCount) : q.OrderBy(s => s.CreatedCount)),
            },
            DefaultSort = "next",
        };

        var all = db.RecurringInvoice.AsNoTracking().Include(s => s.Customer).Include(s => s.Lines);
        var result = await ListBuilder.RunAsync(all, Request, spec, HttpContext.RequestAborted);
        Toolbar = result.Toolbar;
        Pager = result.Pager;

        var active = await db.RecurringInvoice.AsNoTracking().Include(s => s.Lines).Where(s => s.Status == RecurringStatus.Active).ToListAsync();
        Active = active.Count;
        Problems = active.Count(s => s.LastError != null);
        Next = active.Where(s => s.NextRunDate != null).Min(s => s.NextRunDate);
        MonthlyVolume = active.Sum(s => Monthly(InvoiceMath.Totals(s.Lines.Select(l => l.Amounts), exempt).Gross, s.Interval));

        string automatic = " " + L["Recurring.Automatic"], manual = L["Recurring.Manual"], edit = L["Common.Edit"];
        Table = TableModel.Of("series-table", L["Recurring.Title"], result.Items,
        [
            (new TableColumn(L["Recurring.Series"]), s => Ui.NameWithSub(s.Name, $"/Recurring/Edit?id={s.Id}", s.Customer?.Name)),
            (new TableColumn(L["Recurring.Field.Interval"], HideOnPhone: true), s => Ui.Text(Labels.Of(s.Interval))),
            (new TableColumn(L["Recurring.NextInvoice"]), s => s.NextRunDate is { } next && s.Status == RecurringStatus.Active ? Ui.Text(Fmt.Date(next)) : Ui.None()),
            (new TableColumn(L["Common.Amount"], Numeric: true, HideOnPhone: true), s => Ui.Money(InvoiceMath.Totals(s.Lines.Select(l => l.Amounts), exempt).Gross)),
            (new TableColumn(L["Common.Status"]), s => Labels.Pill(s)),
            (new TableColumn(L["Recurring.Created"], Numeric: true, HideOnPhone: true), s => Ui.Text(s.CreatedCount.ToString())),
            (new TableColumn(L["Invoices.Sending"], HideOnPhone: true), s => s.AutoSend ? Ui.Concat(Ui.Icon("send"), Ui.Text(automatic)) : Ui.Text(manual)),
        ],
        s => [new RowAction("edit", edit, $"/Recurring/Edit?id={s.Id}")]);
        Table = Table with
        {
            EmptyTitle = Toolbar.Total == 0 ? L["Recurring.Empty.Title"] : L["Invoices.Empty.NothingFound"],
            EmptyText = Toolbar.Total == 0 ? L["Recurring.Empty.Text"] : L["Invoices.Empty.ChangeSearch"],
            EmptyIcon = Toolbar.Total == 0 ? "repeat" : "search",
        };
    }

    /// <summary>What a series brings per month on average (a weekly one about 4.35 times its amount).</summary>
    private static decimal Monthly(decimal amount, RecurrenceInterval interval) => interval switch
    {
        RecurrenceInterval.Weekly => amount * 52m / 12m,
        RecurrenceInterval.Monthly => amount,
        RecurrenceInterval.Quarterly => amount / 3m,
        RecurrenceInterval.HalfYearly => amount / 6m,
        _ => amount / 12m,
    };
}
