using MatBill.Data;
using MatBill.Data.Entities;
using MatBill.Services;
using MatBill.UiKit;
using Microsoft.EntityFrameworkCore;

namespace MatBill.Pages.Customers;

/// <summary>The customers: search, filter, sort, and what is open with each of them.</summary>
public sealed class IndexModel(MbDbContext db) : PageModelBase
{
    public ToolbarModel Toolbar { get; private set; } = null!;

    public PagerModel Pager { get; private set; } = null!;

    public TableModel Table { get; private set; } = null!;

    public async Task OnGetAsync()
    {
        Head("Kunden", "An wen du Rechnungen schreibst.", ("Kunden", null));
        var spec = new ListSpec<Customer>
        {
            Id = "customers",
            Action = "/Customers",
            Label = "Kunden",
            Noun = "Kunden",
            SearchPlaceholder = "Name, Nummer, Ort oder E-Mail ...",
            Search = (q, text) =>
            {
                var like = Like.Contains(text);
                return q.Where(c => EF.Functions.ILike(c.Name, like) || EF.Functions.ILike(c.Number, like) || EF.Functions.ILike(c.City ?? "", like)
                    || EF.Functions.ILike(c.Email ?? "", like) || EF.Functions.ILike(c.ContactName ?? "", like));
            },
            Filters =
            [
                new FilterSpec<Customer>("art", "Art", ListBuilder.EnumOptions<CustomerKind>(Labels.Of), (q, v) => q.Where(c => c.Kind == Enum.Parse<CustomerKind>(v))),
                new FilterSpec<Customer>("offen", "Offene Rechnungen", [new("ja", "Mit offenen Rechnungen"), new("nein", "Ohne offene Rechnungen")], (q, v) => v == "ja"
                    ? q.Where(c => db.Invoice.Any(i => i.CustomerId == c.Id && i.Status == InvoiceStatus.Open))
                    : q.Where(c => !db.Invoice.Any(i => i.CustomerId == c.Id && i.Status == InvoiceStatus.Open))),
                new FilterSpec<Customer>("email", "E-Mail", [new("ja", "Mit E-Mail-Adresse"), new("nein", "Ohne E-Mail-Adresse")], (q, v) => v == "ja" ? q.Where(c => c.Email != null && c.Email != "") : q.Where(c => c.Email == null || c.Email == "")),
            ],
            Sorts = new Dictionary<string, (string, Func<IQueryable<Customer>, bool, IOrderedQueryable<Customer>>)>
            {
                ["name"] = ("Name", (q, d) => d ? q.OrderByDescending(c => c.Name) : q.OrderBy(c => c.Name)),
                ["nummer"] = ("Kundennummer", (q, d) => d ? q.OrderByDescending(c => c.Number) : q.OrderBy(c => c.Number)),
                ["ort"] = ("Ort", (q, d) => d ? q.OrderByDescending(c => c.City) : q.OrderBy(c => c.City)),
                ["neu"] = ("Angelegt", (q, d) => d ? q.OrderBy(c => c.CreateDate) : q.OrderByDescending(c => c.CreateDate)),
            },
            DefaultSort = "name",
        };

        var result = await ListBuilder.RunAsync(db.Customer.AsNoTracking(), Request, spec, HttpContext.RequestAborted);
        Toolbar = result.Toolbar;
        Pager = result.Pager;
        var ids = result.Items.Select(c => c.Id).ToList();
        var open = await db.Invoice.Where(i => i.CustomerId != null && ids.Contains(i.CustomerId.Value) && i.Status == InvoiceStatus.Open)
            .GroupBy(i => i.CustomerId!.Value).Select(g => new { g.Key, Sum = g.Sum(i => i.GrossTotal) }).ToDictionaryAsync(x => x.Key, x => x.Sum);

        Table = TableModel.Of("customer-table", "Kunden", result.Items,
        [
            (new TableColumn("Name"), c => Ui.NameWithSub(c.Name, $"/Customers/Edit?id={c.Id}", c.ContactName)),
            (new TableColumn("Nummer", HideOnPhone: true, Mono: true), c => Ui.Text(c.Number)),
            (new TableColumn("Ort", HideOnPhone: true), c => Ui.Text(string.Join(" ", new[] { c.PostalCode, c.City }.Where(s => !string.IsNullOrWhiteSpace(s))))),
            (new TableColumn("E-Mail", HideOnPhone: true), c => string.IsNullOrWhiteSpace(c.Email) ? Ui.None() : Ui.Text(c.Email)),
            (new TableColumn("Offen", Numeric: true), c => open.TryGetValue(c.Id, out var sum) ? Ui.Money(sum) : Ui.None()),
        ],
        c => [new RowAction("edit", "Bearbeiten", $"/Customers/Edit?id={c.Id}"), new RowAction("file-plus", "Neue Rechnung", $"/Invoices/Edit?customer={c.Id}")]);
        Table = Table with
        {
            EmptyTitle = Toolbar.Total == 0 ? "Noch keine Kunden" : "Nichts gefunden",
            EmptyText = Toolbar.Total == 0 ? "Lege deinen ersten Kunden an, oder tippe beim Schreiben einer Rechnung einfach einen neuen Namen." : "Ändere die Suche oder den Filter.",
            EmptyIcon = Toolbar.Total == 0 ? "users" : "search",
        };
    }
}
