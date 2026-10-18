using MatBill.Core.Banking;
using MatBill.Data;
using MatBill.Data.Entities;
using MatBill.Services;
using MatBill.UiKit;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MatBill.Pages.Banking;

/// <summary>One booking: what it is, what it settles (or what it could settle, with the reasons), and the ways to assign it by hand.</summary>
public sealed class TransactionModel(MbDbContext db, MatchingService matching, ExpenseService expenses, NavCounts nav) : PageModelBase
{
    [BindProperty(SupportsGet = true)]
    public long Id { get; set; }

    public BankTransaction Transaction { get; private set; } = null!;

    public IReadOnlyList<MatchCandidate> Candidates { get; private set; } = [];

    public List<OpenItem> AllOpen { get; private set; } = [];

    public IReadOnlyList<string> Categories => expenses.Categories;

    public async Task<IActionResult> OnGetAsync()
    {
        if (!await LoadAsync())
        {
            return NotFound();
        }

        return Page();
    }

    public async Task<IActionResult> OnPostAssignAsync(string kind, long itemId)
    {
        var t = await db.BankTransaction.FirstOrDefaultAsync(x => x.Id == Id);
        if (t is null || kind is not ("invoice" or "expense"))
        {
            return NotFound();
        }

        try
        {
            await matching.AssignAsync(t, kind, itemId, automatic: false, 100);
            nav.Invalidate();
            Flash(kind == "invoice" ? L["Banking.Transaction.Assigned.Invoice"] : L["Banking.Transaction.Assigned.Expense"]);
        }
        catch (InvoiceException ex)
        {
            FlashError(ex.Message);
        }

        return await NextAsync();
    }

    public async Task<IActionResult> OnPostUnassignAsync()
    {
        var t = await db.BankTransaction.FirstOrDefaultAsync(x => x.Id == Id);
        if (t is null)
        {
            return NotFound();
        }

        await matching.UnassignAsync(t);
        nav.Invalidate();
        Flash(L["Banking.Transaction.Unassigned"]);
        return RedirectToPage(new { id = Id });
    }

    public async Task<IActionResult> OnPostIgnoreAsync(string? note)
    {
        var t = await db.BankTransaction.FirstOrDefaultAsync(x => x.Id == Id);
        if (t is null)
        {
            return NotFound();
        }

        await matching.IgnoreAsync(t, note);
        nav.Invalidate();
        Flash(L["Banking.Transaction.Ignored"]);
        return await NextAsync();
    }

    public async Task<IActionResult> OnPostExpenseAsync(string category)
    {
        var t = await db.BankTransaction.FirstOrDefaultAsync(x => x.Id == Id);
        if (t is null || t.Amount >= 0)
        {
            return NotFound();
        }

        var expense = await matching.CreateExpenseAsync(t, expenses.Categories.Contains(category) ? category : "Sonstiges");
        nav.Invalidate();
        Flash(L["Banking.Transaction.ExpenseCreated", expense.Number]);
        return RedirectToPage("/Expenses/Edit", new { id = expense.Id });
    }

    /// <summary>After an assignment the next open booking comes, so that a stack is worked off in one go.</summary>
    private async Task<IActionResult> NextAsync()
    {
        var next = await db.BankTransaction.Where(x => x.State == MatchState.Open && x.Id != Id).OrderByDescending(x => x.BookingDate).Select(x => (long?)x.Id).FirstOrDefaultAsync();
        return next is { } nextId ? RedirectToPage(new { id = nextId }) : RedirectToPage("/Banking/Index");
    }

    private async Task<bool> LoadAsync()
    {
        var t = await db.BankTransaction.AsNoTracking().Include(x => x.BankAccount).Include(x => x.Invoice).Include(x => x.Expense).FirstOrDefaultAsync(x => x.Id == Id);
        if (t is null)
        {
            return false;
        }

        Transaction = t;
        Head(t.CounterpartyName ?? L["Banking.Transaction"].Value, $"{Fmt.Date(t.BookingDate)} · {Fmt.Money(t.Amount)} · {t.BankAccount?.Name}", (L["Banking.Title"], "/Banking"), (L["Banking.Transaction"], null));
        ViewData["ActiveHref"] = "/Banking";
        if (t.State != MatchState.Matched)
        {
            Candidates = await matching.CandidatesAsync(t);
            AllOpen = t.Amount >= 0 ? await matching.OpenInvoicesAsync() : await matching.OpenExpensesAsync();
        }

        return true;
    }
}
