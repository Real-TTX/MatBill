using MatBill.Configuration;
using MatBill.Core.Billing;
using MatBill.Data;
using Microsoft.EntityFrameworkCore;

namespace MatBill.Services;

/// <summary>
/// The number ranges of invoices and customers. A number is taken with one statement that counts up in the database (insert or update, returning the new value),
/// inside the caller's transaction: two invoices issued at the same moment never get the same number, and a failed save gives its number back.
/// </summary>
public sealed class NumberService(MbDbContext db, ConfigStore config)
{
    public const string InvoiceRange = "invoice";

    public const string CustomerRange = "customer";

    public const string ExpenseRange = "expense";

    public Task<string> NextExpenseNumberAsync(DateOnly date, CancellationToken ct = default)
        => NextAsync(ExpenseRange, config.Current.Invoicing.ExpenseNumberPattern, date, ct);

    public Task<string> NextInvoiceNumberAsync(DateOnly date, CancellationToken ct = default)
        => NextAsync(InvoiceRange, config.Current.Invoicing.InvoiceNumberPattern, date, ct);

    /// <summary>The range of the invoices an API client requested: "invoice:client:&lt;id&gt;", with the pattern of the client.</summary>
    public static string ClientRange(long apiClientId) => $"{InvoiceRange}:client:{apiClientId}";

    /// <summary>
    /// The range an invoice takes its number from: the own range of the API client that requested it when the client has a pattern, else the invoice range.
    /// A client's range counts on its own (its own counter, the same yearly or monthly restart as any pattern with the year or the month).
    /// </summary>
    public async Task<(string Name, string Pattern)> InvoiceRangeOfAsync(long? apiClientId, CancellationToken ct = default)
    {
        if (apiClientId is { } id
            && await db.ApiClient.AsNoTracking().Where(c => c.Id == id).Select(c => c.NumberPattern).FirstOrDefaultAsync(ct) is { Length: > 0 } pattern)
        {
            return (ClientRange(id), pattern);
        }

        return (InvoiceRange, config.Current.Invoicing.InvoiceNumberPattern);
    }

    /// <summary>The next number of an invoice, from the range of its API client (<see cref="InvoiceRangeOfAsync"/>).</summary>
    public async Task<string> NextInvoiceNumberAsync(DateOnly date, long? apiClientId, CancellationToken ct = default)
    {
        var (name, pattern) = await InvoiceRangeOfAsync(apiClientId, ct);
        return await NextAsync(name, pattern, date, ct);
    }

    /// <summary>The number the invoice would get now (the preview of a draft), from the range of its API client.</summary>
    public async Task<string> PeekInvoiceNumberAsync(DateOnly date, long? apiClientId, CancellationToken ct = default)
    {
        var (name, pattern) = await InvoiceRangeOfAsync(apiClientId, ct);
        return await PeekAsync(name, pattern, date, ct);
    }

    /// <summary>The next customer number of the range. A number typed by hand may already stand where the range arrives: that one is skipped.</summary>
    public Task<string> NextCustomerNumberAsync(DateOnly date, CancellationToken ct = default)
        => NextAsync(CustomerRange, config.Current.Invoicing.CustomerNumberPattern, date, ct);

    /// <summary>Whether a customer (also a deleted one, whose number is on old invoices) has the number.</summary>
    public Task<bool> CustomerNumberTakenAsync(string number, CancellationToken ct = default)
        => db.Customer.IgnoreQueryFilters().AnyAsync(c => c.Number == number, ct);

    /// <summary>The customer number the next customer would get, without taking it (the placeholder of the field).</summary>
    public Task<string> PeekCustomerNumberAsync(DateOnly date, CancellationToken ct = default)
        => PeekAsync(CustomerRange, config.Current.Invoicing.CustomerNumberPattern, date, ct);

    /// <summary>The number the next invoice of <paramref name="date"/> would get, without taking it (the preview in a draft).</summary>
    public Task<string> PeekInvoiceNumberAsync(DateOnly date, CancellationToken ct = default)
        => PeekAsync(InvoiceRange, config.Current.Invoicing.InvoiceNumberPattern, date, ct);

    public async Task<string> PeekAsync(string name, string pattern, DateOnly date, CancellationToken ct = default)
    {
        var period = NumberPattern.PeriodKey(pattern, date);
        var last = await db.NumberSequence.AsNoTracking().Where(s => s.Name == name && s.Period == period).Select(s => (long?)s.LastValue).FirstOrDefaultAsync(ct) ?? 0;
        return await FirstFreeAsync(name, pattern, date, last + 1, ct);
    }

    /// <summary>The number of <paramref name="counter"/>, or of the first counter after it whose number is on no document yet (what <see cref="NextAsync"/> will give).</summary>
    public async Task<string> FirstFreeAsync(string name, string pattern, DateOnly date, long counter, CancellationToken ct = default)
    {
        var number = NumberPattern.Format(pattern, date, counter);
        for (var tries = 0; tries < MaxSkip && IsKnown(name) && await InUseAsync(name, [number], ct); tries++)
        {
            number = NumberPattern.Format(pattern, date, ++counter);
        }

        return number;
    }

    /// <summary>The counter the next number of the period of <paramref name="date"/> gets (1 when the period has none yet).</summary>
    public async Task<long> NextCounterAsync(string name, string pattern, DateOnly date, CancellationToken ct = default)
    {
        var period = NumberPattern.PeriodKey(pattern, date);
        return (await db.NumberSequence.AsNoTracking().Where(s => s.Name == name && s.Period == period).Select(s => (long?)s.LastValue).FirstOrDefaultAsync(ct) ?? 0) + 1;
    }

    /// <summary>
    /// Lets the range of the period of <paramref name="date"/> continue at <paramref name="next"/> (an invoice program that is replaced, a counter that goes on).
    /// Check with <see cref="CanContinueAtAsync"/> first.
    /// </summary>
    public async Task SetNextCounterAsync(string name, string pattern, DateOnly date, long next, CancellationToken ct = default)
    {
        if (next == await NextCounterAsync(name, pattern, date, ct))
        {
            return;
        }

        var period = NumberPattern.PeriodKey(pattern, date);
        var now = DateTime.UtcNow;
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO "NumberSequence" ("Name", "Period", "LastValue", "CreateDate", "UpdateDate")
            VALUES ({name}, {period}, {next - 1}, {now}, {now})
            ON CONFLICT ("Name", "Period") DO UPDATE SET "LastValue" = {next - 1}, "UpdateDate" = {now}
            """, ct);
    }

    /// <summary>Forward always; back only when none of the numbers in between is on a document (also a deleted one), so a number is never given twice.</summary>
    public async Task<bool> CanContinueAtAsync(string name, string pattern, DateOnly date, long next, CancellationToken ct = default)
    {
        var current = await NextCounterAsync(name, pattern, date, ct);
        return next >= current || (next >= 1 && current - next <= 5000
            && !await InUseAsync(name, [.. Enumerable.Range(0, (int)(current - next)).Select(k => NumberPattern.Format(pattern, date, next + k))], ct));
    }

    /// <summary>Whether a document carries one of the numbers.</summary>
    private async Task<bool> InUseAsync(string name, List<string> numbers, CancellationToken ct) => name switch
    {
        // every invoice range (the normal one and those of the API clients) checks all invoices: the number is unique across them
        _ when IsInvoiceRange(name) => await db.Invoice.IgnoreQueryFilters().AnyAsync(i => i.Number != null && numbers.Contains(i.Number), ct),
        CustomerRange => await db.Customer.IgnoreQueryFilters().AnyAsync(c => numbers.Contains(c.Number), ct),
        ExpenseRange => await db.Expense.IgnoreQueryFilters().AnyAsync(e => numbers.Contains(e.Number), ct),
        QuoteService.QuoteRange => await db.Quote.IgnoreQueryFilters().AnyAsync(q => q.Number != null && numbers.Contains(q.Number), ct),
        _ => true,
    };

    /// <summary>
    /// The next number of any range (quotes, receipts of expenses ...). A number that is already on a document (typed by hand, or left by an earlier
    /// pattern) is skipped, so taking a number never fails on it; this leaves no gap, the skipped number exists.
    /// </summary>
    public async Task<string> NextAsync(string name, string pattern, DateOnly date, CancellationToken ct = default)
    {
        for (var tries = 0; ; tries++)
        {
            var number = await TakeAsync(name, pattern, date, ct);
            if (tries >= MaxSkip || !IsKnown(name) || !await InUseAsync(name, [number], ct))
            {
                return number;
            }
        }
    }

    private const int MaxSkip = 1000;

    private static bool IsKnown(string name) => IsInvoiceRange(name) || name is CustomerRange or ExpenseRange or QuoteService.QuoteRange;

    private static bool IsInvoiceRange(string name) => name == InvoiceRange || name.StartsWith(InvoiceRange + ":", StringComparison.Ordinal);

    private async Task<string> TakeAsync(string name, string pattern, DateOnly date, CancellationToken ct)
    {
        var period = NumberPattern.PeriodKey(pattern, date);
        var now = DateTime.UtcNow;
        var value = await db.Database.SqlQuery<long>($"""
            INSERT INTO "NumberSequence" ("Name", "Period", "LastValue", "CreateDate", "UpdateDate")
            VALUES ({name}, {period}, 1, {now}, {now})
            ON CONFLICT ("Name", "Period") DO UPDATE SET "LastValue" = "NumberSequence"."LastValue" + 1, "UpdateDate" = {now}
            RETURNING "LastValue" AS "Value"
            """).ToListAsync(ct);
        return NumberPattern.Format(pattern, date, value[0]);
    }
}
