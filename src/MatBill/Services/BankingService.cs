using System.Text.Json;
using MatBill.Configuration;
using MatBill.Core.Banking;
using MatBill.Data;
using MatBill.Data.Entities;
using MatBill.Localization;
using Microsoft.EntityFrameworkCore;

namespace MatBill.Services;

/// <summary>A statement file as it was read, before it is imported: the format, the bookings and, for a CSV, the table and the columns it was read with.</summary>
public sealed record ImportPreview(StatementFormat Format, CsvTable? Table, CsvMapping? Mapping, List<StatementLine> Lines, StatementInfo Info, List<string> Problems);

/// <summary>The mapping of a CSV kept per account, with the headers it belongs to (a different file of the bank gets a new guess).</summary>
public sealed record SavedMapping(string[] Headers, CsvMapping Mapping);

/// <summary>
/// Bank accounts and their bookings: statement files (CSV of any bank, CAMT, MT940) are read, compared with what is there (nothing comes twice) and imported;
/// then <see cref="MatchingService"/> assigns what it can.
/// </summary>
public sealed class BankingService(MbDbContext db, MatchingService matching, ConfigStore config, ActivityLogger activity, TimeProvider clock)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public ImportPreview Read(byte[] bytes, BankAccount? account, CsvMapping? mapping = null)
    {
        var text = CsvStatement.Decode(bytes);
        var problems = new List<string>();
        switch (StatementFormats.Detect(text))
        {
            case StatementFormat.Camt:
                var (camt, camtInfo) = CamtStatement.Read(text);
                return new ImportPreview(StatementFormat.Camt, null, null, camt, camtInfo, problems);
            case StatementFormat.Mt940:
                var (mt, mtInfo) = Mt940Statement.Read(text);
                return new ImportPreview(StatementFormat.Mt940, null, null, mt, mtInfo, problems);
            default:
                var table = CsvStatement.Read(text);
                if (table.Headers.Count == 0)
                {
                    problems.Add(Text.T("Finance.Import.NoTable"));
                    return new ImportPreview(StatementFormat.Csv, table, null, [], new StatementInfo(null, null, null), problems);
                }

                mapping ??= Saved(account, table) ?? CsvStatement.Guess(table);
                var lines = CsvStatement.Lines(table, mapping, problems, account?.Currency ?? "EUR");
                return new ImportPreview(StatementFormat.Csv, table, mapping, lines, new StatementInfo(null, null, null), problems);
        }
    }

    private static CsvMapping? Saved(BankAccount? account, CsvTable table)
    {
        if (string.IsNullOrWhiteSpace(account?.CsvMapping))
        {
            return null;
        }

        try
        {
            var saved = JsonSerializer.Deserialize<SavedMapping>(account.CsvMapping, Json);
            return saved is not null && saved.Headers.SequenceEqual(table.Headers) ? saved.Mapping : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Imports the bookings into <paramref name="account"/>: new ones are added, known ones are counted as duplicates; then the matcher runs.</summary>
    public async Task<BankImport> ImportAsync(BankAccount account, IReadOnlyList<StatementLine> lines, string source, string? fileName, StatementInfo? info, CsvTable? table = null, CsvMapping? mapping = null, CancellationToken ct = default)
    {
        var keys = StatementDedup.Keys(lines);
        var known = (await db.BankTransaction.Where(t => t.BankAccountId == account.Id && keys.Contains(t.ImportKey)).Select(t => t.ImportKey).ToListAsync(ct)).ToHashSet(StringComparer.Ordinal);
        var import = new BankImport { BankAccountId = account.Id, Source = source, FileName = fileName, ImportDate = clock.GetUtcNow().UtcDateTime, Total = lines.Count };
        db.BankImport.Add(import);
        await db.SaveChangesAsync(ct);

        var added = new List<BankTransaction>();
        for (var i = 0; i < lines.Count; i++)
        {
            if (!known.Add(keys[i]))
            {
                import.Duplicates++;
                continue;
            }

            var l = lines[i];
            var t = new BankTransaction
            {
                BankAccountId = account.Id,
                BookingDate = l.BookingDate,
                ValueDate = l.ValueDate,
                Amount = l.Amount,
                Currency = l.Currency,
                CounterpartyName = Cut(l.CounterpartyName, 300),
                CounterpartyIban = Cut(l.CounterpartyIban?.Replace(" ", "", StringComparison.Ordinal), 40),
                Purpose = Cut(l.Purpose, 2000),
                EndToEndId = Cut(l.EndToEndId, 100),
                ImportKey = keys[i],
                BankImportId = import.Id,
            };
            db.BankTransaction.Add(t);
            added.Add(t);
        }

        import.Added = added.Count;
        if (info?.Balance is { } balance && (account.BalanceDate is null || info.BalanceDate >= account.BalanceDate))
        {
            account.Balance = balance;
            account.BalanceDate = info.BalanceDate;
        }

        if (string.IsNullOrWhiteSpace(account.Iban) && info?.Iban is { } iban)
        {
            account.Iban = iban;
        }

        if (table is not null && mapping is not null)
        {
            account.CsvMapping = JsonSerializer.Serialize(new SavedMapping([.. table.Headers], mapping), Json);
        }

        await db.SaveChangesAsync(ct);
        if (config.Current.Banking.AutoMatch && added.Count > 0)
        {
            import.Matched = await matching.AutoMatchAsync([.. added.Select(a => a.Id)], ct);
            await db.SaveChangesAsync(ct);
        }

        await activity.LogAsync(Text.T("Finance.Activity.TransactionsImported"), nameof(BankAccount), account.Id,
            Text.T("Finance.Activity.ImportSummary", account.Name, import.Added, import.Duplicates, import.Matched));
        return import;
    }

    /// <summary>The balance of an account that has none from its bank: the sum of its bookings.</summary>
    public async Task<decimal> SumAsync(long accountId, CancellationToken ct = default)
        => await db.BankTransaction.Where(t => t.BankAccountId == accountId).SumAsync(t => t.Amount, ct);

    private static string? Cut(string? text, int max) => text is null ? null : text.Length > max ? text[..max] : text;
}

/// <summary>
/// Assigns bookings: money that came in to open invoices, money that went out to open expenses (<see cref="PaymentMatcher"/> scores them). An assignment marks the
/// invoice or the expense as paid on the booking date; taking it back opens it again.
/// </summary>
public sealed class MatchingService(MbDbContext db, NumberService numbers, ExpenseService expenses, NotificationService notifications, ActivityLogger activity)
{
    public async Task<List<OpenItem>> OpenInvoicesAsync(CancellationToken ct = default)
        => (await db.Invoice.AsNoTracking().Include(i => i.Customer)
                .Where(i => i.Status == InvoiceStatus.Open && i.Kind == InvoiceKind.Invoice)
                .Select(i => new { i.Id, i.Number, i.GrossTotal, i.RecipientName, i.InvoiceDate, Iban = (string?)null })
                .ToListAsync(ct))
            .Select(i => new OpenItem(i.Id, "invoice", i.Number, i.GrossTotal, i.RecipientName, i.InvoiceDate, i.Iban)).ToList();

    public async Task<List<OpenItem>> OpenExpensesAsync(CancellationToken ct = default)
        => (await db.Expense.AsNoTracking().Where(e => e.Status == ExpenseStatus.Open).ToListAsync(ct))
            .Select(e => new OpenItem(e.Id, "expense", e.SupplierInvoiceNumber, e.GrossAmount, e.Supplier, e.ExpenseDate, e.SupplierIban)).ToList();

    public async Task<IReadOnlyList<MatchCandidate>> CandidatesAsync(BankTransaction t, CancellationToken ct = default)
    {
        var items = t.Amount >= 0 ? await OpenInvoicesAsync(ct) : await OpenExpensesAsync(ct);
        return PaymentMatcher.Candidates(Info(t), items);
    }

    private static PaymentInfo Info(BankTransaction t) => new(t.Amount, t.BookingDate, t.CounterpartyName, t.CounterpartyIban, $"{t.Purpose} {t.EndToEndId}");

    /// <summary>Assigns every open booking (or the given ones) whose best match is clear. Returns how many were assigned.</summary>
    public async Task<int> AutoMatchAsync(IReadOnlyCollection<long>? ids = null, CancellationToken ct = default)
    {
        var open = await db.BankTransaction.Where(t => t.State == MatchState.Open && (ids == null || ids.Contains(t.Id))).OrderBy(t => t.BookingDate).ToListAsync(ct);
        if (open.Count == 0)
        {
            return 0;
        }

        var invoices = await OpenInvoicesAsync(ct);
        var expensesOpen = await OpenExpensesAsync(ct);
        var matched = 0;
        foreach (var t in open)
        {
            var pool = t.Amount >= 0 ? invoices : expensesOpen;
            if (PaymentMatcher.Auto(PaymentMatcher.Candidates(Info(t), pool)) is not { } best)
            {
                continue;
            }

            await AssignAsync(t, best.Item.Kind, best.Item.Id, automatic: true, best.Score, ct);
            pool.Remove(best.Item);
            matched++;
        }

        if (matched > 0)
        {
            await notifications.NotifyAllAsync(NotificationTone.Success,
                () => (matched == 1 ? Text.T("Notify.Matched.TitleOne", matched) : Text.T("Notify.Matched.TitleMany", matched), Text.T("Notify.Matched.Text")),
                "/Banking?status=matched", null, ct);
        }

        return matched;
    }

    public async Task AssignAsync(BankTransaction t, string kind, long id, bool automatic, int score, CancellationToken ct = default)
    {
        if (t.State == MatchState.Matched)
        {
            await UnassignAsync(t, ct);
        }

        if (kind == "invoice")
        {
            var invoice = await db.Invoice.FirstOrDefaultAsync(i => i.Id == id, ct) ?? throw new InvoiceException(Text.T("Finance.Match.InvoiceGone"));
            if (invoice.Status == InvoiceStatus.Open)
            {
                invoice.Status = InvoiceStatus.Paid;
                invoice.PaidDate = t.BookingDate;
            }

            t.InvoiceId = id;
            t.ExpenseId = null;
        }
        else
        {
            var expense = await db.Expense.FirstOrDefaultAsync(e => e.Id == id, ct) ?? throw new InvoiceException(Text.T("Finance.Match.ExpenseGone"));
            expense.Status = ExpenseStatus.Paid;
            expense.PaidDate = t.BookingDate;
            if (string.IsNullOrWhiteSpace(expense.SupplierIban) && !string.IsNullOrWhiteSpace(t.CounterpartyIban))
            {
                expense.SupplierIban = t.CounterpartyIban;
            }

            t.ExpenseId = id;
            t.InvoiceId = null;
        }

        t.State = MatchState.Matched;
        t.MatchedAutomatically = automatic;
        t.MatchScore = score;
        await db.SaveChangesAsync(ct);
        await activity.LogAsync(automatic ? Text.T("Finance.Activity.PaymentMatchedAutomatically") : Text.T("Finance.Activity.PaymentMatched"), nameof(BankTransaction), t.Id, $"{Fmt.Money(t.Amount)} {t.CounterpartyName} → {kind} {id}");
    }

    /// <summary>Takes an assignment back: the invoice or the expense is open again (if no other booking pays it).</summary>
    public async Task UnassignAsync(BankTransaction t, CancellationToken ct = default)
    {
        if (t.InvoiceId is { } invoiceId && !await db.BankTransaction.AnyAsync(o => o.Id != t.Id && o.InvoiceId == invoiceId, ct))
        {
            var invoice = await db.Invoice.FirstOrDefaultAsync(i => i.Id == invoiceId, ct);
            if (invoice is { Status: InvoiceStatus.Paid })
            {
                invoice.Status = InvoiceStatus.Open;
                invoice.PaidDate = null;
            }
        }

        if (t.ExpenseId is { } expenseId && !await db.BankTransaction.AnyAsync(o => o.Id != t.Id && o.ExpenseId == expenseId, ct))
        {
            var expense = await db.Expense.FirstOrDefaultAsync(e => e.Id == expenseId, ct);
            if (expense is not null)
            {
                expense.Status = ExpenseStatus.Open;
                expense.PaidDate = null;
            }
        }

        t.InvoiceId = null;
        t.ExpenseId = null;
        t.State = MatchState.Open;
        t.MatchedAutomatically = false;
        t.MatchScore = 0;
        await db.SaveChangesAsync(ct);
    }

    public async Task IgnoreAsync(BankTransaction t, string? note, CancellationToken ct = default)
    {
        if (t.State == MatchState.Matched)
        {
            await UnassignAsync(t, ct);
        }

        t.State = MatchState.Ignored;
        t.Note = string.IsNullOrWhiteSpace(note) ? t.Note : note.Trim();
        await db.SaveChangesAsync(ct);
    }

    /// <summary>An expense from a booking that went out (a card payment, a direct debit without a receipt yet), assigned to it.</summary>
    public async Task<Expense> CreateExpenseAsync(BankTransaction t, string category, CancellationToken ct = default)
    {
        var expense = expenses.New();
        expense.ExpenseDate = t.BookingDate;
        expense.Supplier = string.IsNullOrWhiteSpace(t.CounterpartyName) ? Text.T("Finance.Expense.UnknownSupplier") : t.CounterpartyName;
        expense.SupplierIban = t.CounterpartyIban;
        expense.Description = t.Purpose is { Length: > 200 } p ? p[..200] : t.Purpose;
        expense.Category = category;
        var (net, tax, gross) = ExpenseService.Amounts(null, Math.Abs(t.Amount), expense.TaxRate);
        expense.NetAmount = net;
        expense.TaxAmount = tax;
        expense.GrossAmount = gross;
        expense.NeedsReview = true;
        expense.Number = await numbers.NextExpenseNumberAsync(expense.ExpenseDate, ct);
        db.Expense.Add(expense);
        await db.SaveChangesAsync(ct);
        await AssignAsync(t, "expense", expense.Id, automatic: false, 100, ct);
        return expense;
    }
}
