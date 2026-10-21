using MatBill.Configuration;
using MatBill.Data;
using MatBill.Data.Entities;
using MatBill.Services;
using MatBill.UiKit;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MatBill.Pages.Banking;

/// <summary>The accounts and their bookings: what is assigned, what is open, what was ignored; import, fetch, assign automatically.</summary>
public sealed class IndexModel(MbDbContext db, MatchingService matching, BankSyncService sync, ConfigStore config, NavCounts nav) : PageModelBase
{
    public ToolbarModel Toolbar { get; private set; } = null!;

    public PagerModel Pager { get; private set; } = null!;

    public TableModel Table { get; private set; } = null!;

    public List<BankAccount> Accounts { get; private set; } = [];

    public Dictionary<long, decimal> Sums { get; private set; } = [];

    public int OpenCount { get; private set; }

    public bool OnlineConfigured => config.Current.Banking.IsOnlineConfigured;

    public async Task OnGetAsync()
    {
        Head(L["Banking.Title"], L["Banking.Sub"], (L["Banking.Title"], null));
        Accounts = await db.BankAccount.AsNoTracking().Include(a => a.BankConnection).Where(a => a.IsActive).OrderBy(a => a.Name).ToListAsync();
        Sums = await db.BankTransaction.GroupBy(t => t.BankAccountId).Select(g => new { g.Key, Sum = g.Sum(t => t.Amount) }).ToDictionaryAsync(x => x.Key, x => x.Sum);
        OpenCount = await db.BankTransaction.CountAsync(t => t.State == MatchState.Open);

        var spec = new ListSpec<BankTransaction>
        {
            Id = "transactions",
            Action = "/Banking",
            Label = L["Banking.Transactions"],
            Noun = L["Banking.Noun"],
            SearchPlaceholder = L["Banking.Search"],
            Search = (q, text) =>
            {
                var like = Like.Contains(text);
                var amount = Core.Billing.Amount.TryParse(text, out var a) ? Math.Abs(a) : (decimal?)null;
                return q.Where(t => EF.Functions.ILike(t.CounterpartyName ?? "", like) || EF.Functions.ILike(t.Purpose ?? "", like) || EF.Functions.ILike(t.CounterpartyIban ?? "", like)
                    || (amount != null && (t.Amount == amount || t.Amount == -amount)));
            },
            Filters =
            [
                new FilterSpec<BankTransaction>("status", L["Banking.Match"], [new("open", L["Banking.State.Open"]), new("matched", L["Banking.State.Matched"]), new("auto", L["Banking.State.Auto"]), new("ignored", L["Banking.State.Ignored"])], (q, v) => v switch
                {
                    "open" => q.Where(t => t.State == MatchState.Open),
                    "matched" => q.Where(t => t.State == MatchState.Matched),
                    "auto" => q.Where(t => t.State == MatchState.Matched && t.MatchedAutomatically),
                    _ => q.Where(t => t.State == MatchState.Ignored),
                }),
                new FilterSpec<BankTransaction>("direction", L["Banking.Direction"], [new("in", L["Banking.Direction.In"]), new("out", L["Banking.Direction.Out"])], (q, v) => v == "in" ? q.Where(t => t.Amount > 0) : q.Where(t => t.Amount < 0)),
                new FilterSpec<BankTransaction>("account", L["Banking.Account"], [.. Accounts.Select(a => new FilterOption(a.Id.ToString(), a.Name))], (q, v) => long.TryParse(v, out var id) ? q.Where(t => t.BankAccountId == id) : q),
            ],
            Sorts = new Dictionary<string, (string, Func<IQueryable<BankTransaction>, bool, IOrderedQueryable<BankTransaction>>)>
            {
                ["date"] = (L["Banking.BookingDate"].Value, (q, d) => d ? q.OrderByDescending(t => t.BookingDate).ThenByDescending(t => t.Id) : q.OrderBy(t => t.BookingDate).ThenBy(t => t.Id)),
                ["amount"] = (L["Common.Amount"].Value, (q, d) => d ? q.OrderByDescending(t => t.Amount) : q.OrderBy(t => t.Amount)),
                ["name"] = (L["Common.Name"].Value, (q, d) => d ? q.OrderByDescending(t => t.CounterpartyName) : q.OrderBy(t => t.CounterpartyName)),
            },
            DefaultSort = "date",
            DefaultDescending = true,
            PageSize = 30,
        };

        var result = await ListBuilder.RunAsync(db.BankTransaction.AsNoTracking().Include(t => t.Invoice).Include(t => t.Expense).Include(t => t.BankAccount), Request, spec, HttpContext.RequestAborted);
        Toolbar = result.Toolbar;
        Pager = result.Pager;
        var noName = L["Banking.NoName"].Value;
        var auto = L["Banking.Pill.Auto"].Value;
        var matched = L["Banking.Pill.Matched"].Value;
        var ignored = L["Banking.Pill.Ignored"].Value;
        var open = L["Banking.Pill.Open"].Value;
        var invoice = L["Banking.Invoice"].Value;
        Table = TableModel.Of("transaction-table", L["Banking.Transactions"], result.Items,
        [
            (new TableColumn(L["Common.Date"]), t => Ui.Text(Fmt.Date(t.BookingDate))),
            (new TableColumn(L["Banking.Column.NamePurpose"], Primary: true), t => Ui.NameWithSub(t.CounterpartyName ?? noName, $"/Banking/Transaction?id={t.Id}", t.Purpose is { Length: > 90 } p ? p[..90] + " …" : t.Purpose)),
            (new TableColumn(L["Banking.Account"], HideOnPhone: true), t => Ui.Text(t.BankAccount?.Name)),
            (new TableColumn(L["Common.Amount"], Numeric: true), t => Ui.Money(t.Amount)),
            (new TableColumn(L["Banking.Match"]), t => t.State switch
            {
                MatchState.Matched when t.Invoice is not null => Ui.Concat(Ui.Pill(t.MatchedAutomatically ? auto : matched, "ok"), Ui.Text(" "), Ui.Link(t.Invoice.Number ?? invoice, $"/Invoices/Edit?id={t.InvoiceId}")),
                MatchState.Matched when t.Expense is not null => Ui.Concat(Ui.Pill(t.MatchedAutomatically ? auto : matched, "ok"), Ui.Text(" "), Ui.Link(t.Expense.Number, $"/Expenses/Edit?id={t.ExpenseId}")),
                MatchState.Ignored => Ui.Pill(ignored, "plain"),
                _ => Ui.Pill(open, "warn"),
            }),
        ],
        t => [new RowAction("edit", L["Banking.Assign"], $"/Banking/Transaction?id={t.Id}")]);
        Table = Table with
        {
            EmptyTitle = Toolbar.Total == 0 ? L["Banking.Empty.Title"] : L["Banking.Empty.NothingFound"],
            EmptyText = Toolbar.Total == 0 ? L["Banking.Empty.Text"] : L["Banking.Empty.ChangeSearch"],
            EmptyIcon = Toolbar.Total == 0 ? "credit-card" : "search",
        };
    }

    public async Task<IActionResult> OnPostMatchAsync()
    {
        var matched = await matching.AutoMatchAsync();
        nav.Invalidate();
        Flash(matched == 0 ? L["Banking.AutoMatch.None"] : L["Banking.AutoMatch.Done", matched]);
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostSyncAsync(long accountId)
    {
        var account = await db.BankAccount.FirstOrDefaultAsync(a => a.Id == accountId);
        if (account is null)
        {
            return NotFound();
        }

        var import = await sync.SyncAsync(account);
        nav.Invalidate();
        if (import is null)
        {
            FlashError(account.LastSyncError ?? L["Banking.Sync.Failed"]);
        }
        else
        {
            Flash(L["Banking.Sync.Done", account.Name, import.Added, import.Matched]);
        }

        return RedirectToPage();
    }
}
