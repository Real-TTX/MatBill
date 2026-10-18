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
        Head(L["Customers.Title"], L["Customers.Sub"], (L["Customers.Title"], null));
        var spec = new ListSpec<Customer>
        {
            Id = "customers",
            Action = "/Customers",
            Label = L["Customers.Title"],
            Noun = L["Customers.Noun"],
            SearchPlaceholder = L["Customers.List.Search"],
            Search = (q, text) =>
            {
                var like = Like.Contains(text);
                return q.Where(c => EF.Functions.ILike(c.Name, like) || EF.Functions.ILike(c.Number, like) || EF.Functions.ILike(c.City ?? "", like)
                    || EF.Functions.ILike(c.Email ?? "", like) || EF.Functions.ILike(c.ContactName ?? "", like));
            },
            Filters =
            [
                new FilterSpec<Customer>("kind", L["Customers.Filter.Kind"], ListBuilder.EnumOptions<CustomerKind>(Labels.Of), (q, v) => q.Where(c => c.Kind == Enum.Parse<CustomerKind>(v))),
                new FilterSpec<Customer>("open", L["Customers.Filter.Open"], [new("true", L["Customers.Filter.Open.Yes"]), new("false", L["Customers.Filter.Open.No"])], (q, v) => v == "true"
                    ? q.Where(c => db.Invoice.Any(i => i.CustomerId == c.Id && i.Status == InvoiceStatus.Open))
                    : q.Where(c => !db.Invoice.Any(i => i.CustomerId == c.Id && i.Status == InvoiceStatus.Open))),
                new FilterSpec<Customer>("email", L["Common.Email"], [new("true", L["Customers.Filter.Email.Yes"]), new("false", L["Customers.Filter.Email.No"])], (q, v) => v == "true" ? q.Where(c => c.Email != null && c.Email != "") : q.Where(c => c.Email == null || c.Email == "")),
            ],
            Sorts = new Dictionary<string, (string, Func<IQueryable<Customer>, bool, IOrderedQueryable<Customer>>)>
            {
                ["name"] = (L["Common.Name"], (q, d) => d ? q.OrderByDescending(c => c.Name) : q.OrderBy(c => c.Name)),
                ["number"] = (L["Customers.Sort.Number"], (q, d) => d ? q.OrderByDescending(c => c.Number) : q.OrderBy(c => c.Number)),
                ["city"] = (L["Customers.Column.City"], (q, d) => d ? q.OrderByDescending(c => c.City) : q.OrderBy(c => c.City)),
                ["created"] = (L["Customers.Sort.Created"], (q, d) => d ? q.OrderBy(c => c.CreateDate) : q.OrderByDescending(c => c.CreateDate)),
            },
            DefaultSort = "name",
        };

        var result = await ListBuilder.RunAsync(db.Customer.AsNoTracking(), Request, spec, HttpContext.RequestAborted);
        Toolbar = result.Toolbar;
        Pager = result.Pager;
        var ids = result.Items.Select(c => c.Id).ToList();
        var open = await db.Invoice.Where(i => i.CustomerId != null && ids.Contains(i.CustomerId.Value) && i.Status == InvoiceStatus.Open)
            .GroupBy(i => i.CustomerId!.Value).Select(g => new { g.Key, Sum = g.Sum(i => i.GrossTotal) }).ToDictionaryAsync(x => x.Key, x => x.Sum);

        var edit = L["Common.Edit"].Value;
        var newInvoice = L["Customers.NewInvoice"].Value;
        Table = TableModel.Of("customer-table", L["Customers.Title"], result.Items,
        [
            (new TableColumn(L["Common.Name"]), c => Ui.NameWithSub(c.Name, $"/Customers/Edit?id={c.Id}", c.ContactName)),
            (new TableColumn(L["Customers.Column.Number"], HideOnPhone: true, Mono: true), c => Ui.Text(c.Number)),
            (new TableColumn(L["Customers.Column.City"], HideOnPhone: true), c => Ui.Text(string.Join(" ", new[] { c.PostalCode, c.City }.Where(s => !string.IsNullOrWhiteSpace(s))))),
            (new TableColumn(L["Common.Email"], HideOnPhone: true), c => string.IsNullOrWhiteSpace(c.Email) ? Ui.None() : Ui.Text(c.Email)),
            (new TableColumn(L["Customers.Column.Open"], Numeric: true), c => open.TryGetValue(c.Id, out var sum) ? Ui.Money(sum) : Ui.None()),
        ],
        c => [new RowAction("edit", edit, $"/Customers/Edit?id={c.Id}"), new RowAction("file-plus", newInvoice, $"/Invoices/Edit?customer={c.Id}")]);
        Table = Table with
        {
            EmptyTitle = Toolbar.Total == 0 ? L["Customers.Empty.Title"] : L["Customers.Empty.NothingFound"],
            EmptyText = Toolbar.Total == 0 ? L["Customers.Empty.Text"] : L["Customers.Empty.ChangeSearch"],
            EmptyIcon = Toolbar.Total == 0 ? "users" : "search",
        };
    }
}
