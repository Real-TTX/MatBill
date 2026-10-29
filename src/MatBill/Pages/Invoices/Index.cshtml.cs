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

    /// <summary>"1 invoice", "3 invoices" in the language of the interface.</summary>
    public string CountText(long count) => count == 1 ? L["Invoices.Kpi.Count.One", Fmt.Integer(count)] : L["Invoices.Kpi.Count.Many", Fmt.Integer(count)];

    public async Task OnGetAsync()
    {
        Head(L["Invoices.Title"], L["Invoices.Sub"], (L["Invoices.Title"], null));
        var today = Fmt.Today(clock);
        var years = await db.Invoice.Select(i => i.InvoiceDate.Year).Distinct().OrderByDescending(y => y).ToListAsync();
        // the programs that ask for invoices through the API: a handful, their names mark the invoices they requested
        var apiClients = await db.ApiClient.AsNoTracking().OrderBy(c => c.Name).Select(c => new { c.Id, c.Name }).ToListAsync();
        var apiNames = apiClients.ToDictionary(c => c.Id, c => c.Name);

        var spec = new ListSpec<Invoice>
        {
            Id = "invoices",
            Action = "/Invoices",
            Label = L["Invoices.Title"],
            Noun = L["Invoices.Noun"],
            SearchPlaceholder = L["Invoices.Search"],
            Search = (q, text) =>
            {
                var like = Like.Contains(text);
                return q.Where(i => (i.Number != null && EF.Functions.ILike(i.Number, like)) || (i.RecipientName != null && EF.Functions.ILike(i.RecipientName, like))
                    || i.Lines.Any(l => EF.Functions.ILike(l.Name, like)));
            },
            Filters =
            [
                new FilterSpec<Invoice>("status", L["Common.Status"],
                [
                    new("draft", L["Invoices.Status.Draft"]), new("open", L["Invoices.Status.Open"]), new("overdue", L["Invoices.Status.Overdue"]),
                    new("paid", L["Invoices.Status.Paid"]), new("cancelled", L["Invoices.Status.Cancelled"]), new("sendfailed", L["Invoices.Status.SendFailed"]),
                ], (q, v) => v switch
                {
                    "draft" => q.Where(i => i.Status == InvoiceStatus.Draft),
                    "open" => q.Where(i => i.Status == InvoiceStatus.Open),
                    "overdue" => q.Where(i => i.Status == InvoiceStatus.Open && i.DueDate < today),
                    "paid" => q.Where(i => i.Status == InvoiceStatus.Paid),
                    "sendfailed" => q.Where(i => i.SendState == SendState.Failed && i.Status != InvoiceStatus.Cancelled),
                    _ => q.Where(i => i.Status == InvoiceStatus.Cancelled),
                }),
                new FilterSpec<Invoice>("year", L["Invoices.Filter.Year"], [.. years.Select(y => new FilterOption(y.ToString(), y.ToString()))], (q, v) => int.TryParse(v, out var y) ? q.Where(i => i.InvoiceDate.Year == y) : q),
                new FilterSpec<Invoice>("customer", L["Invoices.Customer"], [], (q, v) => long.TryParse(v, out var id) ? q.Where(i => i.CustomerId == id) : q, Search: "customers", Resolve: Lookup.CustomerName(db)),
                new FilterSpec<Invoice>("origin", L["Invoices.Filter.Origin"],
                    [new("series", L["Invoices.Origin.Series"]), new("api", L["Invoices.Origin.Api"]), new("single", L["Invoices.Origin.Single"])],
                    (q, v) => v switch
                    {
                        "series" => q.Where(i => i.RecurringInvoiceId != null),
                        "api" => q.Where(i => i.ApiClientId != null),
                        _ => q.Where(i => i.RecurringInvoiceId == null && i.ApiClientId == null),
                    }),
                .. apiClients.Count == 0 ? Array.Empty<FilterSpec<Invoice>>() :
                [
                    new FilterSpec<Invoice>("client", L["Invoices.Filter.ApiClient"], [.. apiClients.Select(c => new FilterOption(c.Id.ToString(), c.Name))],
                        (q, v) => long.TryParse(v, out var id) ? q.Where(i => i.ApiClientId == id) : q),
                ],
            ],
            Sorts = new Dictionary<string, (string, Func<IQueryable<Invoice>, bool, IOrderedQueryable<Invoice>>)>
            {
                ["date"] = (L["Invoices.Sort.Date"], (q, d) => d ? q.OrderByDescending(i => i.InvoiceDate).ThenByDescending(i => i.Id) : q.OrderBy(i => i.InvoiceDate).ThenBy(i => i.Id)),
                ["number"] = (L["Invoices.Number"], (q, d) => d ? q.OrderByDescending(i => i.Number) : q.OrderBy(i => i.Number)),
                ["customer"] = (L["Invoices.Customer"], (q, d) => d ? q.OrderByDescending(i => i.RecipientName) : q.OrderBy(i => i.RecipientName)),
                ["due"] = (L["Invoices.Sort.Due"], (q, d) => d ? q.OrderByDescending(i => i.DueDate) : q.OrderBy(i => i.DueDate)),
                ["amount"] = (L["Common.Amount"], (q, d) => d ? q.OrderByDescending(i => i.GrossTotal) : q.OrderBy(i => i.GrossTotal)),
            },
            DefaultSort = "date",
            DefaultDescending = true,
        };

        var result = await ListBuilder.RunAsync(db.Invoice.AsNoTracking(), Request, spec, HttpContext.RequestAborted);
        Toolbar = result.Toolbar;
        Pager = result.Pager;
        HasAny = result.Toolbar.Total > 0;
        ShownGross = await result.Filtered.Where(i => i.Status != InvoiceStatus.Draft).SumAsync(i => i.GrossTotal);
        OpenSum = await db.Invoice.Where(i => i.Status == InvoiceStatus.Open).SumAsync(i => i.GrossTotal);
        OverdueSum = await db.Invoice.Where(i => i.Status == InvoiceStatus.Open && i.DueDate < today).SumAsync(i => i.GrossTotal);
        OverdueCount = await db.Invoice.CountAsync(i => i.Status == InvoiceStatus.Open && i.DueDate < today);

        string draft = L["Invoices.Draft"], fromSeries = L["Invoices.FromSeries"], failed = L["Invoices.Send.State.Failed"], pending = L["Invoices.Send.State.Pending"];
        string edit = L["Common.Edit"], open = L["Invoices.Open"];
        Table = TableModel.Of("invoice-table", L["Invoices.Title"], result.Items,
        [
            (new TableColumn(L["Invoices.Number"]), i => Ui.NameWithSub(i.Number ?? draft, $"/Invoices/Edit?id={i.Id}",
                i.RecurringInvoiceId is not null ? fromSeries
                : i.ApiClientId is { } api ? L["Invoices.FromApi", apiNames.GetValueOrDefault(api) ?? "API"].Value
                : null)),
            (new TableColumn(L["Invoices.Customer"]), i => i.CustomerId is { } cid ? Ui.Link(i.RecipientName ?? "", $"/Customers/Edit?id={cid}&tab=invoices") : Ui.Text(i.RecipientName)),
            (new TableColumn(L["Common.Date"], HideOnPhone: true), i => Ui.Text(Fmt.Date(i.InvoiceDate))),
            (new TableColumn(L["Invoices.Col.Due"], HideOnPhone: true), i => i.Status == InvoiceStatus.Open ? Ui.Text(Fmt.Due(i.DueDate, today)) : Ui.None()),
            (new TableColumn(L["Common.Amount"], Numeric: true), i => Ui.Money(i.GrossTotal)),
            (new TableColumn(L["Common.Status"]), i => Labels.Pill(i, today)),
            (new TableColumn(L["Invoices.Sending"], HideOnPhone: true), i => i.SendState switch
            {
                SendState.Sent => Ui.Concat(Ui.Icon("check"), Ui.Text(" " + Fmt.Date(i.SentDate))),
                SendState.Failed => Ui.Pill(failed, "error"),
                SendState.Pending => Ui.Pill(pending, "info"),
                _ => Ui.None(),
            }),
        ],
        i => [new RowAction(i.IsDraft ? "edit" : "eye", i.IsDraft ? edit : open, $"/Invoices/Edit?id={i.Id}"), new RowAction("download", "PDF", $"/invoices/{i.Id}/pdf?download=1")]);
        Table = Table with
        {
            EmptyTitle = HasAny ? L["Invoices.Empty.NothingFound"] : L["Invoices.Empty.Title"],
            EmptyText = HasAny ? L["Invoices.Empty.ChangeSearch"] : L["Invoices.Empty.Text"],
            EmptyIcon = HasAny ? "search" : "file-text",
        };
    }
}
