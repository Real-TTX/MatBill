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
        Head(L["Products.Title"], L["Products.Sub"], (L["Products.Title"], null));
        var spec = new ListSpec<Product>
        {
            Id = "products",
            Action = "/Products",
            Label = L["Products.Title"],
            Noun = L["Products.Noun"],
            SearchPlaceholder = L["Products.List.Search"],
            Search = (q, text) =>
            {
                var like = Like.Contains(text);
                return q.Where(p => EF.Functions.ILike(p.Name, like) || EF.Functions.ILike(p.Description ?? "", like) || EF.Functions.ILike(p.Sku ?? "", like));
            },
            Filters =
            [
                new FilterSpec<Product>("kind", L["Products.Filter.Kind"], ListBuilder.EnumOptions<ProductKind>(Labels.Of), (q, v) => q.Where(p => p.Kind == Enum.Parse<ProductKind>(v))),
                new FilterSpec<Product>("status", L["Common.Status"], [new("active", L["Products.Status.Active"]), new("inactive", L["Products.Status.Inactive"])], (q, v) => v == "active" ? q.Where(p => p.IsActive) : q.Where(p => !p.IsActive)),
                new FilterSpec<Product>("vat", L["Products.Filter.TaxRate"], [new("19", "19 %"), new("7", "7 %"), new("0", "0 %")], (q, v) => decimal.TryParse(v, out var r) ? q.Where(p => p.TaxRate == r) : q),
            ],
            Sorts = new Dictionary<string, (string, Func<IQueryable<Product>, bool, IOrderedQueryable<Product>>)>
            {
                ["name"] = (L["Common.Name"], (q, d) => d ? q.OrderByDescending(p => p.Name) : q.OrderBy(p => p.Name)),
                ["price"] = (L["Products.Sort.Price"], (q, d) => d ? q.OrderByDescending(p => p.UnitPrice) : q.OrderBy(p => p.UnitPrice)),
                ["created"] = (L["Products.Sort.Created"], (q, d) => d ? q.OrderBy(p => p.CreateDate) : q.OrderByDescending(p => p.CreateDate)),
            },
            DefaultSort = "name",
        };

        var result = await ListBuilder.RunAsync(db.Product.AsNoTracking(), Request, spec, HttpContext.RequestAborted);
        Toolbar = result.Toolbar;
        Pager = result.Pager;
        var active = L["Products.Pill.Active"].Value;
        var inactive = L["Products.Pill.Inactive"].Value;
        var edit = L["Common.Edit"].Value;
        Table = TableModel.Of("product-table", L["Products.Title"], result.Items,
        [
            (new TableColumn(L["Common.Name"]), p => Ui.NameWithSub(p.Name, $"/Products/Edit?id={p.Id}", p.Description)),
            (new TableColumn(L["Products.Column.Kind"], HideOnPhone: true), p => Ui.Text(Labels.Of(p.Kind))),
            (new TableColumn(L["Products.Column.Price"], Numeric: true), p => Ui.Concat(Ui.Money(p.UnitPrice), Ui.Text(p.Unit is null ? "" : " / " + p.Unit))),
            (new TableColumn(L["Products.Column.Vat"], Numeric: true, HideOnPhone: true), p => Ui.Text(Amount.Quantity(p.TaxRate) + " %")),
            (new TableColumn(L["Common.Status"], HideOnPhone: true), p => p.IsActive ? Ui.Pill(active, "ok") : Ui.Pill(inactive, "plain")),
        ],
        p => [new RowAction("edit", edit, $"/Products/Edit?id={p.Id}")]);
        Table = Table with
        {
            EmptyTitle = Toolbar.Total == 0 ? L["Products.Empty.Title"] : L["Products.Empty.NothingFound"],
            EmptyText = Toolbar.Total == 0 ? L["Products.Empty.Text"] : L["Products.Empty.ChangeSearch"],
            EmptyIcon = Toolbar.Total == 0 ? "box" : "search",
        };
    }
}
