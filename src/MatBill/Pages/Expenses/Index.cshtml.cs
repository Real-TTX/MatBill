using MatBill.Data;
using MatBill.Data.Entities;
using MatBill.Services;
using MatBill.UiKit;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MatBill.Pages.Expenses;

/// <summary>The expenses: receipts dropped onto the page become expenses at once; search, filter by category, status and year; the sums above.</summary>
public sealed class IndexModel(MbDbContext db, ExpenseService expenses, NavCounts nav, TimeProvider clock) : PageModelBase
{
    public ToolbarModel Toolbar { get; private set; } = null!;

    public PagerModel Pager { get; private set; } = null!;

    public TableModel Table { get; private set; } = null!;

    public decimal YearNet { get; private set; }

    public decimal YearTax { get; private set; }

    public decimal OpenSum { get; private set; }

    public int ToReview { get; private set; }

    [BindProperty]
    public List<IFormFile> Files { get; set; } = [];

    public async Task OnGetAsync()
    {
        Head(L["Expenses.Title"], L["Expenses.Sub"], (L["Expenses.Title"], null));
        var today = Fmt.Today(clock);
        var years = await db.Expense.Select(e => e.ExpenseDate.Year).Distinct().OrderByDescending(y => y).ToListAsync();
        var spec = new ListSpec<Expense>
        {
            Id = "expenses",
            Action = "/Expenses",
            Label = L["Expenses.Title"],
            Noun = L["Expenses.Noun"],
            SearchPlaceholder = L["Expenses.Search"],
            Search = (q, text) =>
            {
                var like = Like.Contains(text);
                return q.Where(e => EF.Functions.ILike(e.Supplier, like) || EF.Functions.ILike(e.Number, like) || EF.Functions.ILike(e.Description ?? "", like) || EF.Functions.ILike(e.SupplierInvoiceNumber ?? "", like));
            },
            Filters =
            [
                new FilterSpec<Expense>("status", L["Common.Status"], [new("review", L["Expenses.Status.Review"]), new("open", L["Expenses.Status.Open"]), new("paid", L["Expenses.Status.Paid"]), new("noreceipt", L["Expenses.Status.NoReceipt"])], (q, v) => v switch
                {
                    "review" => q.Where(e => e.NeedsReview),
                    "open" => q.Where(e => e.Status == ExpenseStatus.Open),
                    "paid" => q.Where(e => e.Status == ExpenseStatus.Paid),
                    _ => q.Where(e => e.ReceiptFileId == null),
                }),
                new FilterSpec<Expense>("category", L["Expenses.Category"], [.. expenses.Categories.Select(c => new FilterOption(c, c))], (q, v) => q.Where(e => e.Category == v)),
                new FilterSpec<Expense>("year", L["Expenses.Year"], [.. years.Select(y => new FilterOption(y.ToString(), y.ToString()))], (q, v) => int.TryParse(v, out var y) ? q.Where(e => e.ExpenseDate.Year == y) : q),
            ],
            Sorts = new Dictionary<string, (string, Func<IQueryable<Expense>, bool, IOrderedQueryable<Expense>>)>
            {
                ["date"] = (L["Expenses.Date"].Value, (q, d) => d ? q.OrderByDescending(e => e.ExpenseDate).ThenByDescending(e => e.Id) : q.OrderBy(e => e.ExpenseDate).ThenBy(e => e.Id)),
                ["supplier"] = (L["Expenses.Supplier"].Value, (q, d) => d ? q.OrderByDescending(e => e.Supplier) : q.OrderBy(e => e.Supplier)),
                ["amount"] = (L["Common.Amount"].Value, (q, d) => d ? q.OrderByDescending(e => e.GrossAmount) : q.OrderBy(e => e.GrossAmount)),
                ["number"] = (L["Expenses.Number"].Value, (q, d) => d ? q.OrderByDescending(e => e.Number) : q.OrderBy(e => e.Number)),
            },
            DefaultSort = "date",
            DefaultDescending = true,
        };

        var result = await ListBuilder.RunAsync(db.Expense.AsNoTracking(), Request, spec, HttpContext.RequestAborted);
        Toolbar = result.Toolbar;
        Pager = result.Pager;
        YearNet = SmallBusiness
            ? await db.Expense.Where(e => e.ExpenseDate.Year == today.Year).SumAsync(e => e.GrossAmount)
            : await db.Expense.Where(e => e.ExpenseDate.Year == today.Year).SumAsync(e => e.NetAmount);
        YearTax = await db.Expense.Where(e => e.ExpenseDate.Year == today.Year).SumAsync(e => e.TaxAmount);
        OpenSum = await db.Expense.Where(e => e.Status == ExpenseStatus.Open).SumAsync(e => e.GrossAmount);
        ToReview = await db.Expense.CountAsync(e => e.NeedsReview);

        var review = L["Expenses.Pill.Review"].Value;
        var paid = L["Expenses.Pill.Paid"].Value;
        var open = L["Expenses.Pill.Open"].Value;
        var eInvoice = " " + L["Expenses.File.EInvoice"].Value;
        var receipt = " " + L["Expenses.File.Receipt"].Value;
        var edit = L["Common.Edit"].Value;
        var view = L["Expenses.ViewReceipt"].Value;
        Table = TableModel.Of("expense-table", L["Expenses.Title"], result.Items,
        [
            (new TableColumn(L["Expenses.Supplier"]), e => Ui.NameWithSub(e.Supplier, $"/Expenses/Edit?id={e.Id}", e.Description)),
            (new TableColumn(L["Expenses.Column.Receipt"], HideOnPhone: true, Mono: true), e => Ui.Text(e.Number)),
            (new TableColumn(L["Common.Date"], HideOnPhone: true), e => Ui.Text(Fmt.Date(e.ExpenseDate))),
            (new TableColumn(L["Expenses.Category"], HideOnPhone: true), e => Ui.Text(e.Category)),
            (new TableColumn(L["Expenses.Column.Gross"], Numeric: true), e => Ui.Money(e.GrossAmount)),
            (new TableColumn(L["Common.Status"]), e => e.NeedsReview ? Ui.Pill(review, "warn") : e.Status == ExpenseStatus.Paid ? Ui.Pill(paid, "ok") : Ui.Pill(open, "info")),
            (new TableColumn(L["Expenses.Column.File"], HideOnPhone: true), e => e.ReceiptFileId is null ? Ui.Text("–") : Ui.Concat(Ui.Icon(e.FromEInvoice ? "check-circle" : "paperclip"), Ui.Text(e.FromEInvoice ? eInvoice : receipt))),
        ],
        e => e.ReceiptFileId is null
            ? [new RowAction("edit", edit, $"/Expenses/Edit?id={e.Id}")]
            : [new RowAction("edit", edit, $"/Expenses/Edit?id={e.Id}"), new RowAction("eye", view, $"/expenses/{e.Id}/receipt")]);
        Table = Table with
        {
            EmptyTitle = Toolbar.Total == 0 ? L["Expenses.Empty.Title"] : L["Expenses.Empty.NothingFound"],
            EmptyText = Toolbar.Total == 0 ? L["Expenses.Empty.Text"] : L["Expenses.Empty.ChangeSearch"],
            EmptyIcon = Toolbar.Total == 0 ? "upload" : "search",
        };
    }

    /// <summary>The dropped receipts: one expense each, then the list of what is to be checked.</summary>
    public async Task<IActionResult> OnPostUploadAsync()
    {
        if (Files.Count == 0)
        {
            FlashError(L["Expenses.Upload.NoFile"]);
            return RedirectToPage();
        }

        var result = await expenses.CreateFromFilesAsync(Files);
        nav.Invalidate();
        if (result.Problems.Count > 0)
        {
            FlashError(string.Join(" ", result.Problems.Take(3)));
        }

        if (result.Created == 1)
        {
            Flash(L["Expenses.Upload.One"]);
            return RedirectToPage("/Expenses/Edit", new { id = result.Ids[0] });
        }

        if (result.Created > 1)
        {
            Flash(L["Expenses.Upload.Many", result.Created]);
        }

        return RedirectToPage(new { status = "review" });
    }
}
