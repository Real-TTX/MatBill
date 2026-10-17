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
        Head("Banking", "Kontoumsätze importieren oder abrufen und automatisch Rechnungen und Ausgaben zuordnen.", ("Banking", null));
        Accounts = await db.BankAccount.AsNoTracking().Include(a => a.BankConnection).Where(a => a.IsActive).OrderBy(a => a.Name).ToListAsync();
        Sums = await db.BankTransaction.GroupBy(t => t.BankAccountId).Select(g => new { g.Key, Sum = g.Sum(t => t.Amount) }).ToDictionaryAsync(x => x.Key, x => x.Sum);
        OpenCount = await db.BankTransaction.CountAsync(t => t.State == MatchState.Open);

        var spec = new ListSpec<BankTransaction>
        {
            Id = "transactions",
            Action = "/Banking",
            Label = "Umsätze",
            Noun = "Umsätze",
            SearchPlaceholder = "Name, Verwendungszweck, IBAN, Betrag ...",
            Search = (q, text) =>
            {
                var like = Like.Contains(text);
                var amount = Core.Billing.Amount.TryParse(text, out var a) ? Math.Abs(a) : (decimal?)null;
                return q.Where(t => EF.Functions.ILike(t.CounterpartyName ?? "", like) || EF.Functions.ILike(t.Purpose ?? "", like) || EF.Functions.ILike(t.CounterpartyIban ?? "", like)
                    || (amount != null && (t.Amount == amount || t.Amount == -amount)));
            },
            Filters =
            [
                new FilterSpec<BankTransaction>("status", "Zuordnung", [new("offen", "Offen"), new("zugeordnet", "Zugeordnet"), new("automatisch", "Automatisch zugeordnet"), new("ignoriert", "Ignoriert")], (q, v) => v switch
                {
                    "offen" => q.Where(t => t.State == MatchState.Open),
                    "zugeordnet" => q.Where(t => t.State == MatchState.Matched),
                    "automatisch" => q.Where(t => t.State == MatchState.Matched && t.MatchedAutomatically),
                    _ => q.Where(t => t.State == MatchState.Ignored),
                }),
                new FilterSpec<BankTransaction>("richtung", "Richtung", [new("ein", "Eingang"), new("aus", "Ausgang")], (q, v) => v == "ein" ? q.Where(t => t.Amount > 0) : q.Where(t => t.Amount < 0)),
                new FilterSpec<BankTransaction>("konto", "Konto", [.. Accounts.Select(a => new FilterOption(a.Id.ToString(), a.Name))], (q, v) => long.TryParse(v, out var id) ? q.Where(t => t.BankAccountId == id) : q),
            ],
            Sorts = new Dictionary<string, (string, Func<IQueryable<BankTransaction>, bool, IOrderedQueryable<BankTransaction>>)>
            {
                ["datum"] = ("Buchungstag", (q, d) => d ? q.OrderByDescending(t => t.BookingDate).ThenByDescending(t => t.Id) : q.OrderBy(t => t.BookingDate).ThenBy(t => t.Id)),
                ["betrag"] = ("Betrag", (q, d) => d ? q.OrderByDescending(t => t.Amount) : q.OrderBy(t => t.Amount)),
                ["name"] = ("Name", (q, d) => d ? q.OrderByDescending(t => t.CounterpartyName) : q.OrderBy(t => t.CounterpartyName)),
            },
            DefaultSort = "datum",
            DefaultDescending = true,
            PageSize = 30,
        };

        var result = await ListBuilder.RunAsync(db.BankTransaction.AsNoTracking().Include(t => t.Invoice).Include(t => t.Expense).Include(t => t.BankAccount), Request, spec, HttpContext.RequestAborted);
        Toolbar = result.Toolbar;
        Pager = result.Pager;
        Table = TableModel.Of("transaction-table", "Umsätze", result.Items,
        [
            (new TableColumn("Datum"), t => Ui.Text(Fmt.Date(t.BookingDate))),
            (new TableColumn("Name / Verwendungszweck"), t => Ui.NameWithSub(t.CounterpartyName ?? "(ohne Namen)", $"/Banking/Transaction?id={t.Id}", t.Purpose is { Length: > 90 } p ? p[..90] + " …" : t.Purpose)),
            (new TableColumn("Konto", HideOnPhone: true), t => Ui.Text(t.BankAccount?.Name)),
            (new TableColumn("Betrag", Numeric: true), t => Ui.Money(t.Amount)),
            (new TableColumn("Zuordnung"), t => t.State switch
            {
                MatchState.Matched when t.Invoice is not null => Ui.Concat(Ui.Pill(t.MatchedAutomatically ? "automatisch" : "zugeordnet", "ok"), Ui.Text(" "), Ui.Link(t.Invoice.Number ?? "Rechnung", $"/Invoices/Edit?id={t.InvoiceId}")),
                MatchState.Matched when t.Expense is not null => Ui.Concat(Ui.Pill(t.MatchedAutomatically ? "automatisch" : "zugeordnet", "ok"), Ui.Text(" "), Ui.Link(t.Expense.Number, $"/Expenses/Edit?id={t.ExpenseId}")),
                MatchState.Ignored => Ui.Pill("ignoriert", "plain"),
                _ => Ui.Pill("offen", "warn"),
            }),
        ],
        t => [new RowAction("edit", "Zuordnen", $"/Banking/Transaction?id={t.Id}")]);
        Table = Table with
        {
            EmptyTitle = Toolbar.Total == 0 ? "Noch keine Umsätze" : "Nichts gefunden",
            EmptyText = Toolbar.Total == 0 ? "Importiere einen Kontoauszug (CSV, CAMT, MT940) oder verbinde ein Konto online." : "Ändere die Suche oder den Filter.",
            EmptyIcon = Toolbar.Total == 0 ? "credit-card" : "search",
        };
    }

    public async Task<IActionResult> OnPostMatchAsync()
    {
        var matched = await matching.AutoMatchAsync();
        nav.Invalidate();
        Flash(matched == 0 ? "Keine eindeutige Zuordnung gefunden. Offene Umsätze zeigen Vorschläge." : $"{matched} Umsätze zugeordnet.");
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
            FlashError(account.LastSyncError ?? "Das Konto konnte nicht abgerufen werden.");
        }
        else
        {
            Flash($"{account.Name}: {import.Added} neue Umsätze, {import.Matched} zugeordnet.");
        }

        return RedirectToPage();
    }
}
