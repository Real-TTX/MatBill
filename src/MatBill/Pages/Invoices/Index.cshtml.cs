using MatBill.Data;
using MatBill.Data.Entities;
using MatBill.Services;
using MatBill.UiKit;
using Microsoft.EntityFrameworkCore;

namespace MatBill.Pages.Invoices;

/// <summary>All invoices: search, filter by status, year and customer, sort; the sums of what the filter shows above the list.</summary>
public sealed class IndexModel(MbDbContext db, TimeProvider clock) : PageModelBase
{
    public ToolbarModel Toolbar { get; private set; } = null!;

    public PagerModel Pager { get; private set; } = null!;

    public TableModel Table { get; private set; } = null!;

    public decimal OpenSum { get; private set; }

    public decimal OverdueSum { get; private set; }

    public int OverdueCount { get; private set; }

    public decimal ShownGross { get; private set; }

    public bool HasAny { get; private set; }

    public async Task OnGetAsync()
    {
        Head("Rechnungen", "Alle Rechnungen und Entwürfe.", ("Rechnungen", null));
        var today = Fmt.Today(clock);
        var years = await db.Invoice.Select(i => i.InvoiceDate.Year).Distinct().OrderByDescending(y => y).ToListAsync();
        var customers = await db.Customer.AsNoTracking().OrderBy(c => c.Name).Select(c => new { c.Id, c.Name }).Take(500).ToListAsync();

        var spec = new ListSpec<Invoice>
        {
            Id = "invoices",
            Action = "/Invoices",
            Label = "Rechnungen",
            Noun = "Rechnungen",
            SearchPlaceholder = "Nummer, Kunde oder Leistung ...",
            Search = (q, text) =>
            {
                var like = Like.Contains(text);
                return q.Where(i => (i.Number != null && EF.Functions.ILike(i.Number, like)) || (i.RecipientName != null && EF.Functions.ILike(i.RecipientName, like))
                    || i.Lines.Any(l => EF.Functions.ILike(l.Name, like)));
            },
            Filters =
            [
                new FilterSpec<Invoice>("status", "Status",
                [
                    new("entwurf", "Entwurf"), new("offen", "Offen"), new("ueberfaellig", "Überfällig"), new("bezahlt", "Bezahlt"), new("storniert", "Storniert"),
                    new("versand", "Versand fehlgeschlagen"),
                ], (q, v) => v switch
                {
                    "entwurf" => q.Where(i => i.Status == InvoiceStatus.Draft),
                    "offen" => q.Where(i => i.Status == InvoiceStatus.Open),
                    "ueberfaellig" => q.Where(i => i.Status == InvoiceStatus.Open && i.DueDate < today),
                    "bezahlt" => q.Where(i => i.Status == InvoiceStatus.Paid),
                    "versand" => q.Where(i => i.SendState == SendState.Failed && i.Status != InvoiceStatus.Cancelled),
                    _ => q.Where(i => i.Status == InvoiceStatus.Cancelled),
                }),
                new FilterSpec<Invoice>("jahr", "Jahr", [.. years.Select(y => new FilterOption(y.ToString(), y.ToString()))], (q, v) => int.TryParse(v, out var y) ? q.Where(i => i.InvoiceDate.Year == y) : q),
                new FilterSpec<Invoice>("kunde", "Kunde", [.. customers.Select(c => new FilterOption(c.Id.ToString(), c.Name))], (q, v) => long.TryParse(v, out var id) ? q.Where(i => i.CustomerId == id) : q),
                new FilterSpec<Invoice>("herkunft", "Herkunft", [new("serie", "Aus einer Serie"), new("einzeln", "Einzeln geschrieben")],
                    (q, v) => v == "serie" ? q.Where(i => i.RecurringInvoiceId != null) : q.Where(i => i.RecurringInvoiceId == null)),
            ],
            Sorts = new Dictionary<string, (string, Func<IQueryable<Invoice>, bool, IOrderedQueryable<Invoice>>)>
            {
                ["datum"] = ("Rechnungsdatum", (q, d) => d ? q.OrderByDescending(i => i.InvoiceDate).ThenByDescending(i => i.Id) : q.OrderBy(i => i.InvoiceDate).ThenBy(i => i.Id)),
                ["nummer"] = ("Nummer", (q, d) => d ? q.OrderByDescending(i => i.Number) : q.OrderBy(i => i.Number)),
                ["kunde"] = ("Kunde", (q, d) => d ? q.OrderByDescending(i => i.RecipientName) : q.OrderBy(i => i.RecipientName)),
                ["faellig"] = ("Fälligkeit", (q, d) => d ? q.OrderByDescending(i => i.DueDate) : q.OrderBy(i => i.DueDate)),
                ["betrag"] = ("Betrag", (q, d) => d ? q.OrderByDescending(i => i.GrossTotal) : q.OrderBy(i => i.GrossTotal)),
            },
            DefaultSort = "datum",
            DefaultDescending = true,
        };

        var result = await ListBuilder.RunAsync(db.Invoice.AsNoTracking(), Request, spec, HttpContext.RequestAborted);
        Toolbar = result.Toolbar;
        Pager = result.Pager;
        HasAny = result.Toolbar.Total > 0;
        ShownGross = await result.Filtered.Where(i => i.Status != InvoiceStatus.Cancelled && i.Status != InvoiceStatus.Draft).SumAsync(i => i.GrossTotal);
        OpenSum = await db.Invoice.Where(i => i.Status == InvoiceStatus.Open).SumAsync(i => i.GrossTotal);
        OverdueSum = await db.Invoice.Where(i => i.Status == InvoiceStatus.Open && i.DueDate < today).SumAsync(i => i.GrossTotal);
        OverdueCount = await db.Invoice.CountAsync(i => i.Status == InvoiceStatus.Open && i.DueDate < today);

        Table = TableModel.Of("invoice-table", "Rechnungen", result.Items,
        [
            (new TableColumn("Nummer"), i => Ui.NameWithSub(i.Number ?? "Entwurf", $"/Invoices/Edit?id={i.Id}", i.RecurringInvoiceId is null ? null : "aus Serie")),
            (new TableColumn("Kunde"), i => Ui.Text(i.RecipientName)),
            (new TableColumn("Datum", HideOnPhone: true), i => Ui.Text(Fmt.Date(i.InvoiceDate))),
            (new TableColumn("Fällig", HideOnPhone: true), i => i.Status == InvoiceStatus.Open ? Ui.Text(Fmt.Due(i.DueDate, today)) : Ui.None()),
            (new TableColumn("Betrag", Numeric: true), i => Ui.Money(i.GrossTotal)),
            (new TableColumn("Status"), i => Labels.Pill(i, today)),
            (new TableColumn("Versand", HideOnPhone: true), i => i.SendState switch
            {
                SendState.Sent => Ui.Concat(Ui.Icon("check"), Ui.Text(" " + Fmt.Date(i.SentDate))),
                SendState.Failed => Ui.Pill("fehlgeschlagen", "error"),
                SendState.Pending => Ui.Pill("wartet", "info"),
                _ => Ui.None(),
            }),
        ],
        i => [new RowAction(i.IsDraft ? "edit" : "eye", i.IsDraft ? "Bearbeiten" : "Öffnen", $"/Invoices/Edit?id={i.Id}"), new RowAction("download", "PDF", $"/invoices/{i.Id}/pdf?download=1")]);
        Table = Table with
        {
            EmptyTitle = HasAny ? "Nichts gefunden" : "Noch keine Rechnung",
            EmptyText = HasAny ? "Ändere die Suche oder den Filter." : "Schreib deine erste Rechnung: direkt auf dem Dokument, mit Kunde und Positionen.",
            EmptyIcon = HasAny ? "search" : "file-text",
        };
    }
}
