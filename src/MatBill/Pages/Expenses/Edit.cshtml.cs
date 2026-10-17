using System.ComponentModel.DataAnnotations;
using MatBill.Core.Billing;
using MatBill.Data;
using MatBill.Data.Entities;
using MatBill.Services;
using MatBill.UiKit;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MatBill.Pages.Expenses;

/// <summary>One expense: the receipt beside the form, supplier, category, amounts (gross or net, the rest is worked out), paid or open, the booking that paid it.</summary>
public sealed class EditModel(MbDbContext db, ExpenseService expenses, MatchingService matching, NavCounts nav, ActivityLogger log) : PageModelBase
{
    public sealed class InputModel
    {
        [Required(ErrorMessage = "Bitte gib den Lieferanten ein."), StringLength(200)]
        public string Supplier { get; set; } = "";

        public DateOnly? ExpenseDate { get; set; }

        [StringLength(100)]
        public string? SupplierInvoiceNumber { get; set; }

        public string Category { get; set; } = "Sonstiges";

        [StringLength(500)]
        public string? Description { get; set; }

        /// <summary>"brutto" or "netto": which amount was typed.</summary>
        public string Entry { get; set; } = "brutto";

        public string? Amount { get; set; }

        public string TaxRate { get; set; } = "19";

        public bool Paid { get; set; }

        public DateOnly? PaidDate { get; set; }

        public DateOnly? DueDate { get; set; }

        [StringLength(4000)]
        public string? Note { get; set; }
    }

    private long? _id;

    [BindProperty(SupportsGet = true)]
    public long? Id { get => _id; set => _id = value > 0 ? value : null; }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    [BindProperty]
    public IFormFile? Receipt { get; set; }

    public Expense? Expense { get; private set; }

    public BankTransaction? Payment { get; private set; }

    public IReadOnlyList<string> Categories => expenses.Categories;

    public List<string> Suppliers { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync()
    {
        if (Id is { } id)
        {
            var e = await expenses.FindAsync(id);
            if (e is null)
            {
                return NotFound();
            }

            Expense = e;
            Input = new InputModel
            {
                Supplier = e.Supplier, ExpenseDate = e.ExpenseDate, SupplierInvoiceNumber = e.SupplierInvoiceNumber, Category = e.Category, Description = e.Description,
                Entry = "brutto", Amount = e.GrossAmount == 0 ? null : Amount.Edit(e.GrossAmount), TaxRate = Amount.Quantity(e.TaxRate),
                Paid = e.Status == ExpenseStatus.Paid, PaidDate = e.PaidDate, DueDate = e.DueDate, Note = e.Note,
            };
        }
        else
        {
            var fresh = expenses.New();
            Input = new InputModel { ExpenseDate = fresh.ExpenseDate, Category = fresh.Category, TaxRate = Amount.Quantity(fresh.TaxRate) };
        }

        await LoadAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var expense = Id is { } id ? await expenses.FindAsync(id) : expenses.New();
        if (expense is null)
        {
            return NotFound();
        }

        Expense = expense.Id == 0 ? null : expense;
        if (!Amount.TryParse(Input.Amount, out var amount))
        {
            ModelState.AddModelError("Input.Amount", "Bitte gib den Betrag ein.");
        }

        if (!Amount.TryParse(Input.TaxRate, out var rate) || rate is < 0 or > 100)
        {
            ModelState.AddModelError("Input.TaxRate", "Der Steuersatz liegt zwischen 0 und 100 %.");
        }

        if (Receipt is { Length: > 0 })
        {
            var problem = await expenses.AttachAsync(expense, Receipt);
            if (problem is not null)
            {
                ModelState.AddModelError(nameof(Receipt), problem);
            }
        }

        if (!ModelState.IsValid)
        {
            await LoadAsync();
            return Page();
        }

        var (net, tax, gross) = ExpenseService.Amounts(Input.Entry == "netto" ? amount : null, Input.Entry == "netto" ? null : amount, rate);
        expense.Supplier = Input.Supplier.Trim();
        expense.ExpenseDate = Input.ExpenseDate ?? expenses.Today;
        expense.SupplierInvoiceNumber = string.IsNullOrWhiteSpace(Input.SupplierInvoiceNumber) ? null : Input.SupplierInvoiceNumber.Trim();
        expense.Category = Categories.Contains(Input.Category) ? Input.Category : "Sonstiges";
        expense.Description = string.IsNullOrWhiteSpace(Input.Description) ? null : Input.Description.Trim();
        expense.TaxRate = rate;
        expense.NetAmount = net;
        expense.TaxAmount = tax;
        expense.GrossAmount = gross;
        expense.DueDate = Input.DueDate;
        expense.Note = string.IsNullOrWhiteSpace(Input.Note) ? null : Input.Note.Trim();
        expense.Status = Input.Paid ? ExpenseStatus.Paid : ExpenseStatus.Open;
        expense.PaidDate = Input.Paid ? Input.PaidDate ?? expense.PaidDate ?? expenses.Today : null;
        var isNew = expense.Id == 0;
        await expenses.SaveAsync(expense);
        nav.Invalidate();
        await log.LogAsync(isNew ? "Ausgabe erfasst" : "Ausgabe geändert", nameof(Expense), expense.Id, $"{expense.Number} · {expense.Supplier} · {Fmt.Money(expense.GrossAmount)}");
        Flash(isNew ? $"Ausgabe {expense.Number} erfasst." : "Ausgabe gespeichert.");
        var next = await db.Expense.Where(e => e.NeedsReview && e.Id != expense.Id).OrderBy(e => e.Id).Select(e => (long?)e.Id).FirstOrDefaultAsync();
        return next is { } nextId ? RedirectToPage(new { id = nextId }) : RedirectToPage("/Expenses/Index");
    }

    public async Task<IActionResult> OnPostDeleteAsync()
    {
        var expense = Id is { } id ? await expenses.FindAsync(id) : null;
        if (expense is not null)
        {
            foreach (var t in await db.BankTransaction.Where(t => t.ExpenseId == expense.Id).ToListAsync())
            {
                await matching.UnassignAsync(t);
            }

            db.Expense.Remove(expense);
            await db.SaveChangesAsync();
            nav.Invalidate();
            await log.LogAsync("Ausgabe gelöscht", nameof(Expense), expense.Id, expense.Number);
            Flash("Ausgabe gelöscht.");
        }

        return RedirectToPage("/Expenses/Index");
    }

    private async Task LoadAsync()
    {
        var title = Expense is null ? "Neue Ausgabe" : $"{Expense.Supplier}";
        Head(title, Expense is null ? null : $"Beleg {Expense.Number}{(Expense.FromEInvoice ? " · aus E-Rechnung" : "")}", ("Ausgaben", "/Expenses"), (Expense?.Number ?? "Neu", null));
        ViewData["ActiveHref"] = "/Expenses";
        ViewData["Width"] = "wide";
        Suppliers = await expenses.SuppliersAsync();
        if (Expense is not null)
        {
            Payment = await db.BankTransaction.AsNoTracking().Include(t => t.BankAccount).FirstOrDefaultAsync(t => t.ExpenseId == Expense.Id);
        }
    }

    public bool ReceiptIsImage => Expense?.ReceiptFile?.ContentType is "image/png" or "image/jpeg" or "image/webp";

    public bool ReceiptIsPdf => Expense?.ReceiptFile?.ContentType == "application/pdf";
}
