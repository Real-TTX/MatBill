using MatBill.Data;
using MatBill.Data.Entities;
using MatBill.Services;
using MatBill.UiKit;
using Microsoft.EntityFrameworkCore;

namespace MatBill.Pages.Quotes;

/// <summary>All quotes: search, status, year, sort; what is open and what was accepted above the list.</summary>
public sealed class IndexModel(MbDbContext db, TimeProvider clock) : PageModelBase
{
    public ToolbarModel Toolbar { get; private set; } = null!;

    public PagerModel Pager { get; private set; } = null!;

    public TableModel Table { get; private set; } = null!;

    public decimal OpenSum { get; private set; }

    public int OpenCount { get; private set; }

    public decimal AcceptedSum { get; private set; }

    public int Rate { get; private set; }

    public async Task OnGetAsync()
    {
        Head("Angebote", "Angebote schreiben, versenden und in Rechnungen umwandeln.", ("Angebote", null));
        var today = Fmt.Today(clock);
        var years = await db.Quote.Select(q => q.QuoteDate.Year).Distinct().OrderByDescending(y => y).ToListAsync();
        var spec = new ListSpec<Quote>
        {
            Id = "quotes",
            Action = "/Quotes",
            Label = "Angebote",
            Noun = "Angebote",
            SearchPlaceholder = "Nummer, Kunde oder Leistung ...",
            Search = (q, text) =>
            {
                var like = Like.Contains(text);
                return q.Where(x => (x.Number != null && EF.Functions.ILike(x.Number, like)) || (x.RecipientName != null && EF.Functions.ILike(x.RecipientName, like)) || x.Lines.Any(l => EF.Functions.ILike(l.Name, like)));
            },
            Filters =
            [
                new FilterSpec<Quote>("status", "Status", [new("entwurf", "Entwurf"), new("offen", "Offen"), new("abgelaufen", "Abgelaufen"), new("angenommen", "Angenommen"), new("abgelehnt", "Abgelehnt"), new("rechnung", "In Rechnung")],
                    (q, v) => v switch
                    {
                        "entwurf" => q.Where(x => x.Status == QuoteStatus.Draft),
                        "offen" => q.Where(x => x.Status == QuoteStatus.Open && x.ValidUntil >= today),
                        "abgelaufen" => q.Where(x => x.Status == QuoteStatus.Open && x.ValidUntil < today),
                        "angenommen" => q.Where(x => x.Status == QuoteStatus.Accepted),
                        "abgelehnt" => q.Where(x => x.Status == QuoteStatus.Declined),
                        _ => q.Where(x => x.Status == QuoteStatus.Invoiced),
                    }),
                new FilterSpec<Quote>("jahr", "Jahr", [.. years.Select(y => new FilterOption(y.ToString(), y.ToString()))], (q, v) => int.TryParse(v, out var y) ? q.Where(x => x.QuoteDate.Year == y) : q),
            ],
            Sorts = new Dictionary<string, (string, Func<IQueryable<Quote>, bool, IOrderedQueryable<Quote>>)>
            {
                ["datum"] = ("Datum", (q, d) => d ? q.OrderByDescending(x => x.QuoteDate).ThenByDescending(x => x.Id) : q.OrderBy(x => x.QuoteDate).ThenBy(x => x.Id)),
                ["nummer"] = ("Nummer", (q, d) => d ? q.OrderByDescending(x => x.Number) : q.OrderBy(x => x.Number)),
                ["kunde"] = ("Kunde", (q, d) => d ? q.OrderByDescending(x => x.RecipientName) : q.OrderBy(x => x.RecipientName)),
                ["betrag"] = ("Betrag", (q, d) => d ? q.OrderByDescending(x => x.GrossTotal) : q.OrderBy(x => x.GrossTotal)),
            },
            DefaultSort = "datum",
            DefaultDescending = true,
        };

        var result = await ListBuilder.RunAsync(db.Quote.AsNoTracking(), Request, spec, HttpContext.RequestAborted);
        Toolbar = result.Toolbar;
        Pager = result.Pager;
        OpenSum = await db.Quote.Where(q => q.Status == QuoteStatus.Open && q.ValidUntil >= today).SumAsync(q => q.GrossTotal);
        OpenCount = await db.Quote.CountAsync(q => q.Status == QuoteStatus.Open && q.ValidUntil >= today);
        AcceptedSum = await db.Quote.Where(q => (q.Status == QuoteStatus.Accepted || q.Status == QuoteStatus.Invoiced) && q.QuoteDate.Year == today.Year).SumAsync(q => q.GrossTotal);
        var answered = await db.Quote.CountAsync(q => q.Status == QuoteStatus.Accepted || q.Status == QuoteStatus.Invoiced || q.Status == QuoteStatus.Declined);
        var won = await db.Quote.CountAsync(q => q.Status == QuoteStatus.Accepted || q.Status == QuoteStatus.Invoiced);
        Rate = answered == 0 ? 0 : (int)Math.Round(100.0 * won / answered);

        Table = TableModel.Of("quote-table", "Angebote", result.Items,
        [
            (new TableColumn("Nummer"), q => Ui.NameWithSub(q.Number ?? "Entwurf", $"/Quotes/Edit?id={q.Id}", null)),
            (new TableColumn("Kunde"), q => Ui.Text(q.RecipientName)),
            (new TableColumn("Datum", HideOnPhone: true), q => Ui.Text(Fmt.Date(q.QuoteDate))),
            (new TableColumn("Gültig bis", HideOnPhone: true), q => q.IsDraft ? Ui.None() : Ui.Text(Fmt.Date(q.ValidUntil))),
            (new TableColumn("Betrag", Numeric: true), q => Ui.Money(q.GrossTotal)),
            (new TableColumn("Status"), q => Labels.Pill(q, today)),
        ],
        q => [new RowAction(q.IsDraft ? "edit" : "eye", q.IsDraft ? "Bearbeiten" : "Öffnen", $"/Quotes/Edit?id={q.Id}"), new RowAction("download", "PDF", $"/quotes/{q.Id}/pdf?download=1")]);
        Table = Table with
        {
            EmptyTitle = Toolbar.Total == 0 ? "Noch kein Angebot" : "Nichts gefunden",
            EmptyText = Toolbar.Total == 0 ? "Schreib ein Angebot im Dokument-Editor; angenommen wird es mit einem Klick zur Rechnung." : "Ändere die Suche oder den Filter.",
            EmptyIcon = Toolbar.Total == 0 ? "file-document" : "search",
        };
    }
}
