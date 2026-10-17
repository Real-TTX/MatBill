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
        Head("Ausgaben", "Belege hochladen, prüfen und zuordnen. E-Rechnungen von Lieferanten füllen sich selbst aus.", ("Ausgaben", null));
        var today = Fmt.Today(clock);
        var years = await db.Expense.Select(e => e.ExpenseDate.Year).Distinct().OrderByDescending(y => y).ToListAsync();
        var spec = new ListSpec<Expense>
        {
            Id = "expenses",
            Action = "/Expenses",
            Label = "Ausgaben",
            Noun = "Ausgaben",
            SearchPlaceholder = "Lieferant, Belegnummer, Beschreibung ...",
            Search = (q, text) =>
            {
                var like = Like.Contains(text);
                return q.Where(e => EF.Functions.ILike(e.Supplier, like) || EF.Functions.ILike(e.Number, like) || EF.Functions.ILike(e.Description ?? "", like) || EF.Functions.ILike(e.SupplierInvoiceNumber ?? "", like));
            },
            Filters =
            [
                new FilterSpec<Expense>("status", "Status", [new("pruefen", "Zu prüfen"), new("offen", "Offen"), new("bezahlt", "Bezahlt"), new("ohnebeleg", "Ohne Beleg")], (q, v) => v switch
                {
                    "pruefen" => q.Where(e => e.NeedsReview),
                    "offen" => q.Where(e => e.Status == ExpenseStatus.Open),
                    "bezahlt" => q.Where(e => e.Status == ExpenseStatus.Paid),
                    _ => q.Where(e => e.ReceiptFileId == null),
                }),
                new FilterSpec<Expense>("kategorie", "Kategorie", [.. expenses.Categories.Select(c => new FilterOption(c, c))], (q, v) => q.Where(e => e.Category == v)),
                new FilterSpec<Expense>("jahr", "Jahr", [.. years.Select(y => new FilterOption(y.ToString(), y.ToString()))], (q, v) => int.TryParse(v, out var y) ? q.Where(e => e.ExpenseDate.Year == y) : q),
            ],
            Sorts = new Dictionary<string, (string, Func<IQueryable<Expense>, bool, IOrderedQueryable<Expense>>)>
            {
                ["datum"] = ("Belegdatum", (q, d) => d ? q.OrderByDescending(e => e.ExpenseDate).ThenByDescending(e => e.Id) : q.OrderBy(e => e.ExpenseDate).ThenBy(e => e.Id)),
                ["lieferant"] = ("Lieferant", (q, d) => d ? q.OrderByDescending(e => e.Supplier) : q.OrderBy(e => e.Supplier)),
                ["betrag"] = ("Betrag", (q, d) => d ? q.OrderByDescending(e => e.GrossAmount) : q.OrderBy(e => e.GrossAmount)),
                ["nummer"] = ("Belegnummer", (q, d) => d ? q.OrderByDescending(e => e.Number) : q.OrderBy(e => e.Number)),
            },
            DefaultSort = "datum",
            DefaultDescending = true,
        };

        var result = await ListBuilder.RunAsync(db.Expense.AsNoTracking(), Request, spec, HttpContext.RequestAborted);
        Toolbar = result.Toolbar;
        Pager = result.Pager;
        YearNet = await db.Expense.Where(e => e.ExpenseDate.Year == today.Year).SumAsync(e => e.NetAmount);
        YearTax = await db.Expense.Where(e => e.ExpenseDate.Year == today.Year).SumAsync(e => e.TaxAmount);
        OpenSum = await db.Expense.Where(e => e.Status == ExpenseStatus.Open).SumAsync(e => e.GrossAmount);
        ToReview = await db.Expense.CountAsync(e => e.NeedsReview);

        Table = TableModel.Of("expense-table", "Ausgaben", result.Items,
        [
            (new TableColumn("Lieferant"), e => Ui.NameWithSub(e.Supplier, $"/Expenses/Edit?id={e.Id}", e.Description)),
            (new TableColumn("Beleg", HideOnPhone: true, Mono: true), e => Ui.Text(e.Number)),
            (new TableColumn("Datum", HideOnPhone: true), e => Ui.Text(Fmt.Date(e.ExpenseDate))),
            (new TableColumn("Kategorie", HideOnPhone: true), e => Ui.Text(e.Category)),
            (new TableColumn("Brutto", Numeric: true), e => Ui.Money(e.GrossAmount)),
            (new TableColumn("Status"), e => e.NeedsReview ? Ui.Pill("zu prüfen", "warn") : e.Status == ExpenseStatus.Paid ? Ui.Pill("bezahlt", "ok") : Ui.Pill("offen", "info")),
            (new TableColumn("Datei", HideOnPhone: true), e => e.ReceiptFileId is null ? Ui.Text("–") : Ui.Concat(Ui.Icon(e.FromEInvoice ? "check-circle" : "paperclip"), Ui.Text(e.FromEInvoice ? " E-Rechnung" : " Beleg"))),
        ],
        e => e.ReceiptFileId is null
            ? [new RowAction("edit", "Bearbeiten", $"/Expenses/Edit?id={e.Id}")]
            : [new RowAction("edit", "Bearbeiten", $"/Expenses/Edit?id={e.Id}"), new RowAction("eye", "Beleg ansehen", $"/expenses/{e.Id}/receipt")]);
        Table = Table with
        {
            EmptyTitle = Toolbar.Total == 0 ? "Noch keine Ausgaben" : "Nichts gefunden",
            EmptyText = Toolbar.Total == 0 ? "Zieh Belege (PDF, Fotos, E-Rechnungen) auf die Fläche oben, oder erfasse eine Ausgabe von Hand." : "Ändere die Suche oder den Filter.",
            EmptyIcon = Toolbar.Total == 0 ? "upload" : "search",
        };
    }

    /// <summary>The dropped receipts: one expense each, then the list of what is to be checked.</summary>
    public async Task<IActionResult> OnPostUploadAsync()
    {
        if (Files.Count == 0)
        {
            FlashError("Es kam keine Datei an.");
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
            Flash("Beleg hochgeladen. Prüfe die Angaben.");
            return RedirectToPage("/Expenses/Edit", new { id = result.Ids[0] });
        }

        if (result.Created > 1)
        {
            Flash($"{result.Created} Belege hochgeladen. Sie stehen unter „zu prüfen“.");
        }

        return RedirectToPage(new { status = "pruefen" });
    }
}
