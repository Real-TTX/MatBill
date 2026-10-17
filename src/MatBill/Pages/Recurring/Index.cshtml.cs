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

    public async Task OnGetAsync()
    {
        Head("Wiederkehrende Rechnungen", "Serien sind Vorlagen: Zu jedem Termin entsteht daraus eine eigene Rechnung mit eigener Nummer.", ("Wiederkehrend", null));
        var exempt = config.Current.Company.SmallBusiness;
        var customers = await db.Customer.AsNoTracking().OrderBy(c => c.Name).Select(c => new { c.Id, c.Name }).Take(500).ToListAsync();

        var spec = new ListSpec<RecurringInvoice>
        {
            Id = "series",
            Action = "/Recurring",
            Label = "Serien",
            Noun = "Serien",
            SearchPlaceholder = "Name oder Kunde ...",
            Search = (q, text) =>
            {
                var like = Like.Contains(text);
                return q.Where(s => EF.Functions.ILike(s.Name, like) || EF.Functions.ILike(s.Customer!.Name, like));
            },
            Filters =
            [
                new FilterSpec<RecurringInvoice>("status", "Status", [new("aktiv", "Aktiv"), new("pausiert", "Pausiert"), new("beendet", "Beendet"), new("fehler", "Mit Fehler")], (q, v) => v switch
                {
                    "aktiv" => q.Where(s => s.Status == RecurringStatus.Active),
                    "pausiert" => q.Where(s => s.Status == RecurringStatus.Paused),
                    "beendet" => q.Where(s => s.Status == RecurringStatus.Ended),
                    _ => q.Where(s => s.LastError != null && s.Status == RecurringStatus.Active),
                }),
                new FilterSpec<RecurringInvoice>("intervall", "Rhythmus", ListBuilder.EnumOptions<RecurrenceInterval>(Labels.Of), (q, v) => q.Where(s => s.Interval == Enum.Parse<RecurrenceInterval>(v))),
                new FilterSpec<RecurringInvoice>("kunde", "Kunde", [.. customers.Select(c => new FilterOption(c.Id.ToString(), c.Name))], (q, v) => long.TryParse(v, out var id) ? q.Where(s => s.CustomerId == id) : q),
            ],
            Sorts = new Dictionary<string, (string, Func<IQueryable<RecurringInvoice>, bool, IOrderedQueryable<RecurringInvoice>>)>
            {
                ["naechste"] = ("Nächste Rechnung", (q, d) => d ? q.OrderByDescending(s => s.NextRunDate) : q.OrderBy(s => s.NextRunDate == null).ThenBy(s => s.NextRunDate)),
                ["name"] = ("Name", (q, d) => d ? q.OrderByDescending(s => s.Name) : q.OrderBy(s => s.Name)),
                ["kunde"] = ("Kunde", (q, d) => d ? q.OrderByDescending(s => s.Customer!.Name) : q.OrderBy(s => s.Customer!.Name)),
                ["erstellt"] = ("Erstellte Rechnungen", (q, d) => d ? q.OrderByDescending(s => s.CreatedCount) : q.OrderBy(s => s.CreatedCount)),
            },
            DefaultSort = "naechste",
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

        Table = TableModel.Of("series-table", "Serien", result.Items,
        [
            (new TableColumn("Serie"), s => Ui.NameWithSub(s.Name, $"/Recurring/Edit?id={s.Id}", s.Customer?.Name)),
            (new TableColumn("Rhythmus", HideOnPhone: true), s => Ui.Text(Labels.Of(s.Interval))),
            (new TableColumn("Nächste Rechnung"), s => s.NextRunDate is { } next && s.Status == RecurringStatus.Active ? Ui.Text(Fmt.Date(next)) : Ui.None()),
            (new TableColumn("Betrag", Numeric: true, HideOnPhone: true), s => Ui.Money(InvoiceMath.Totals(s.Lines.Select(l => l.Amounts), exempt).Gross)),
            (new TableColumn("Status"), s => Labels.Pill(s)),
            (new TableColumn("Erstellt", Numeric: true, HideOnPhone: true), s => Ui.Text(s.CreatedCount.ToString())),
            (new TableColumn("Versand", HideOnPhone: true), s => s.AutoSend ? Ui.Concat(Ui.Icon("send"), Ui.Text(" automatisch")) : Ui.Text("von Hand")),
        ],
        s => [new RowAction("edit", "Bearbeiten", $"/Recurring/Edit?id={s.Id}")]);
        Table = Table with
        {
            EmptyTitle = Toolbar.Total == 0 ? "Noch keine Serie" : "Nichts gefunden",
            EmptyText = Toolbar.Total == 0 ? "Öffne eine Rechnung und wähle „Wiederholung einrichten“, oder lege eine Serie neu an." : "Ändere die Suche oder den Filter.",
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
