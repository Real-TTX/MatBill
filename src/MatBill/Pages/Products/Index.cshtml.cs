using MatBill.Core.Billing;
using MatBill.Data;
using MatBill.Data.Entities;
using MatBill.Services;
using MatBill.UiKit;
using Microsoft.EntityFrameworkCore;

namespace MatBill.Pages.Products;

/// <summary>Products and services: what a position of an invoice can be taken from (name, price, unit, tax rate).</summary>
public sealed class IndexModel(MbDbContext db) : PageModelBase
{
    public ToolbarModel Toolbar { get; private set; } = null!;

    public PagerModel Pager { get; private set; } = null!;

    public TableModel Table { get; private set; } = null!;

    public async Task OnGetAsync()
    {
        Head("Produkte", "Leistungen und Produkte mit Preis, Einheit und Steuersatz.", ("Produkte", null));
        var spec = new ListSpec<Product>
        {
            Id = "products",
            Action = "/Products",
            Label = "Produkte",
            Noun = "Produkte",
            SearchPlaceholder = "Name, Beschreibung oder Artikelnummer ...",
            Search = (q, text) =>
            {
                var like = Like.Contains(text);
                return q.Where(p => EF.Functions.ILike(p.Name, like) || EF.Functions.ILike(p.Description ?? "", like) || EF.Functions.ILike(p.Sku ?? "", like));
            },
            Filters =
            [
                new FilterSpec<Product>("art", "Art", ListBuilder.EnumOptions<ProductKind>(Labels.Of), (q, v) => q.Where(p => p.Kind == Enum.Parse<ProductKind>(v))),
                new FilterSpec<Product>("status", "Status", [new("aktiv", "Aktiv"), new("inaktiv", "Inaktiv")], (q, v) => v == "aktiv" ? q.Where(p => p.IsActive) : q.Where(p => !p.IsActive)),
                new FilterSpec<Product>("ust", "Steuersatz", [new("19", "19 %"), new("7", "7 %"), new("0", "0 %")], (q, v) => decimal.TryParse(v, out var r) ? q.Where(p => p.TaxRate == r) : q),
            ],
            Sorts = new Dictionary<string, (string, Func<IQueryable<Product>, bool, IOrderedQueryable<Product>>)>
            {
                ["name"] = ("Name", (q, d) => d ? q.OrderByDescending(p => p.Name) : q.OrderBy(p => p.Name)),
                ["preis"] = ("Preis", (q, d) => d ? q.OrderByDescending(p => p.UnitPrice) : q.OrderBy(p => p.UnitPrice)),
                ["neu"] = ("Angelegt", (q, d) => d ? q.OrderBy(p => p.CreateDate) : q.OrderByDescending(p => p.CreateDate)),
            },
            DefaultSort = "name",
        };

        var result = await ListBuilder.RunAsync(db.Product.AsNoTracking(), Request, spec, HttpContext.RequestAborted);
        Toolbar = result.Toolbar;
        Pager = result.Pager;
        Table = TableModel.Of("product-table", "Produkte", result.Items,
        [
            (new TableColumn("Name"), p => Ui.NameWithSub(p.Name, $"/Products/Edit?id={p.Id}", p.Description)),
            (new TableColumn("Art", HideOnPhone: true), p => Ui.Text(Labels.Of(p.Kind))),
            (new TableColumn("Preis (netto)", Numeric: true), p => Ui.Concat(Ui.Money(p.UnitPrice), Ui.Text(p.Unit is null ? "" : " / " + p.Unit))),
            (new TableColumn("USt.", Numeric: true, HideOnPhone: true), p => Ui.Text(Amount.Quantity(p.TaxRate) + " %")),
            (new TableColumn("Status", HideOnPhone: true), p => p.IsActive ? Ui.Pill("aktiv", "ok") : Ui.Pill("inaktiv", "plain")),
        ],
        p => [new RowAction("edit", "Bearbeiten", $"/Products/Edit?id={p.Id}")]);
        Table = Table with
        {
            EmptyTitle = Toolbar.Total == 0 ? "Noch keine Produkte" : "Nichts gefunden",
            EmptyText = Toolbar.Total == 0 ? "Lege deine Leistungen an: Im Editor wählst du sie dann mit ein paar Buchstaben aus." : "Ändere die Suche oder den Filter.",
            EmptyIcon = Toolbar.Total == 0 ? "box" : "search",
        };
    }
}
