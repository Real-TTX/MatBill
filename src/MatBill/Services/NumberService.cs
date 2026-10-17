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

    public Task<string> NextCustomerNumberAsync(DateOnly date, CancellationToken ct = default)
        => NextAsync(CustomerRange, config.Current.Invoicing.CustomerNumberPattern, date, ct);

    /// <summary>The number the next invoice of <paramref name="date"/> would get, without taking it (the preview in a draft).</summary>
    public Task<string> PeekInvoiceNumberAsync(DateOnly date, CancellationToken ct = default)
        => PeekAsync(InvoiceRange, config.Current.Invoicing.InvoiceNumberPattern, date, ct);

    public async Task<string> PeekAsync(string name, string pattern, DateOnly date, CancellationToken ct = default)
    {
        var period = NumberPattern.PeriodKey(pattern, date);
        var last = await db.NumberSequence.AsNoTracking().Where(s => s.Name == name && s.Period == period).Select(s => (long?)s.LastValue).FirstOrDefaultAsync(ct) ?? 0;
        return NumberPattern.Format(pattern, date, last + 1);
    }

    /// <summary>The next number of any range (quotes, receipts of expenses ...).</summary>
    public async Task<string> NextAsync(string name, string pattern, DateOnly date, CancellationToken ct = default)
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
