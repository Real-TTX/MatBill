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
        Head(L["Quotes.Title"], L["Quotes.Sub"], (L["Quotes.Title"], null));
        var today = Fmt.Today(clock);
        var years = await db.Quote.Select(q => q.QuoteDate.Year).Distinct().OrderByDescending(y => y).ToListAsync();
        var spec = new ListSpec<Quote>
        {
            Id = "quotes",
            Action = "/Quotes",
            Label = L["Quotes.Title"],
            Noun = L["Quotes.Noun"],
            SearchPlaceholder = L["Quotes.List.Search"],
            Search = (q, text) =>
            {
                var like = Like.Contains(text);
                return q.Where(x => (x.Number != null && EF.Functions.ILike(x.Number, like)) || (x.RecipientName != null && EF.Functions.ILike(x.RecipientName, like)) || x.Lines.Any(l => EF.Functions.ILike(l.Name, like)));
            },
            Filters =
            [
                new FilterSpec<Quote>("status", L["Common.Status"],
                    [
                        new("draft", L["Quotes.Status.Draft"]), new("open", L["Quotes.Status.Open"]), new("expired", L["Quotes.Status.Expired"]),
                        new("accepted", L["Quotes.Status.Accepted"]), new("declined", L["Quotes.Status.Declined"]), new("invoiced", L["Quotes.Status.Invoiced"]),
                    ],
                    (q, v) => v switch
                    {
                        "draft" => q.Where(x => x.Status == QuoteStatus.Draft),
                        "open" => q.Where(x => x.Status == QuoteStatus.Open && x.ValidUntil >= today),
                        "expired" => q.Where(x => x.Status == QuoteStatus.Open && x.ValidUntil < today),
                        "accepted" => q.Where(x => x.Status == QuoteStatus.Accepted),
                        "declined" => q.Where(x => x.Status == QuoteStatus.Declined),
                        _ => q.Where(x => x.Status == QuoteStatus.Invoiced),
                    }),
                new FilterSpec<Quote>("year", L["Quotes.Filter.Year"], [.. years.Select(y => new FilterOption(y.ToString(), y.ToString()))], (q, v) => int.TryParse(v, out var y) ? q.Where(x => x.QuoteDate.Year == y) : q),
            ],
            Sorts = new Dictionary<string, (string, Func<IQueryable<Quote>, bool, IOrderedQueryable<Quote>>)>
            {
                ["date"] = (L["Common.Date"], (q, d) => d ? q.OrderByDescending(x => x.QuoteDate).ThenByDescending(x => x.Id) : q.OrderBy(x => x.QuoteDate).ThenBy(x => x.Id)),
                ["number"] = (L["Quotes.Column.Number"], (q, d) => d ? q.OrderByDescending(x => x.Number) : q.OrderBy(x => x.Number)),
                ["customer"] = (L["Quotes.Column.Customer"], (q, d) => d ? q.OrderByDescending(x => x.RecipientName) : q.OrderBy(x => x.RecipientName)),
                ["amount"] = (L["Common.Amount"], (q, d) => d ? q.OrderByDescending(x => x.GrossTotal) : q.OrderBy(x => x.GrossTotal)),
            },
            DefaultSort = "date",
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

        var draft = L["Quotes.Draft"].Value;
        var edit = L["Common.Edit"].Value;
        var open = L["Quotes.Open"].Value;
        Table = TableModel.Of("quote-table", L["Quotes.Title"], result.Items,
        [
            (new TableColumn(L["Quotes.Column.Number"]), q => Ui.NameWithSub(q.Number ?? draft, $"/Quotes/Edit?id={q.Id}", null)),
            (new TableColumn(L["Quotes.Column.Customer"]), q => Ui.Text(q.RecipientName)),
            (new TableColumn(L["Common.Date"], HideOnPhone: true), q => Ui.Text(Fmt.Date(q.QuoteDate))),
            (new TableColumn(L["Quotes.Column.ValidUntil"], HideOnPhone: true), q => q.IsDraft ? Ui.None() : Ui.Text(Fmt.Date(q.ValidUntil))),
            (new TableColumn(L["Common.Amount"], Numeric: true), q => Ui.Money(q.GrossTotal)),
            (new TableColumn(L["Common.Status"]), q => Labels.Pill(q, today)),
        ],
        q => [new RowAction(q.IsDraft ? "edit" : "eye", q.IsDraft ? edit : open, $"/Quotes/Edit?id={q.Id}"), new RowAction("download", "PDF", $"/quotes/{q.Id}/pdf?download=1")]);
        Table = Table with
        {
            EmptyTitle = Toolbar.Total == 0 ? L["Quotes.Empty.Title"] : L["Quotes.Empty.NothingFound"],
            EmptyText = Toolbar.Total == 0 ? L["Quotes.Empty.Text"] : L["Quotes.Empty.ChangeSearch"],
            EmptyIcon = Toolbar.Total == 0 ? "file-document" : "search",
        };
    }
}
