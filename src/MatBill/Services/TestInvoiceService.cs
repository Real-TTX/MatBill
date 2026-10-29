using MatBill.Core.Billing;
using MatBill.Data;
using MatBill.Data.Entities;
using MatBill.Localization;
using MatBill.Storage;
using Microsoft.EntityFrameworkCore;

namespace MatBill.Services;

/// <summary>What happened to the number range when test invoices were deleted.</summary>
public enum CounterOutcome
{
    /// <summary>The administrator did not ask for it.</summary>
    NotAsked,

    /// <summary>The range goes on at the number that was freed.</summary>
    Reset,

    /// <summary>A later number is on a document: the range cannot go back (a number is never given twice).</summary>
    Blocked,

    /// <summary>The number does not fit the pattern of today (it was changed since): nothing to reset.</summary>
    Unknown,
}

/// <summary>The numbers that are gone, what became of the range and the number the next invoice gets.</summary>
public sealed record TestDeleteResult(IReadOnlyList<string> Numbers, CounterOutcome Counter, string NextNumber);

/// <summary>
/// The one exception to "an issued invoice never changes": an invoice that was only a test (an administrator tried the program, never sent it, nobody paid it)
/// can be deleted for good, so that the first real invoice gets the first number. The rules are strict and every one of them is checked here, not on the page:
/// an invoice that left the house in any way (mail, a share link, a payment, a bank transaction, a series, the API, a quote) is never deleted; it is cancelled.
/// A cancelled test invoice goes together with its cancellation invoice. The number is freed for real (a soft delete would keep it taken), a permanent
/// <see cref="DeletedDocument"/> says which numbers were removed, when, by whom and why, and the stored PDF goes with the invoice.
/// </summary>
public sealed class TestInvoiceService(MbDbContext db, NumberService numbers, FileStoreService files, ActivityLogger activity, ICurrentUser user,
    TimeProvider clock, ILogger<TestInvoiceService> logger)
{
    /// <summary>The issued invoices that could be test invoices (not drafts, not cancellations), newest first: the page lists them with what stands in the way.</summary>
    public Task<List<Invoice>> CandidatesAsync(CancellationToken ct = default)
        => db.Invoice.AsNoTracking().Where(i => i.Status != InvoiceStatus.Draft && i.Kind == InvoiceKind.Invoice).OrderByDescending(i => i.IssuedDate).ThenByDescending(i => i.Id).ToListAsync(ct);

    /// <summary>Why the invoice cannot be deleted as a test invoice (in the language of the person); empty when it can.</summary>
    public async Task<List<string>> ProblemsAsync(Invoice invoice, CancellationToken ct = default)
    {
        var problems = new List<string>();
        if (invoice.Kind == InvoiceKind.Cancellation)
        {
            problems.Add(Text.T("Billing.TestDelete.Reason.Cancellation"));
            return problems;
        }

        if (invoice.IsDraft)
        {
            problems.Add(Text.T("Billing.TestDelete.Reason.Draft"));
            return problems;
        }

        var all = new List<Invoice> { invoice };
        if (invoice.Status == InvoiceStatus.Cancelled)
        {
            var cancellation = invoice.CancellationInvoiceId is { } id ? await db.Invoice.AsNoTracking().FirstOrDefaultAsync(i => i.Id == id, ct) : null;
            if (cancellation is null)
            {
                problems.Add(Text.T("Billing.TestDelete.Reason.State"));
                return problems;
            }

            all.Add(cancellation);
        }
        else if (invoice.Status == InvoiceStatus.Paid)
        {
            problems.Add(Text.T("Billing.TestDelete.Reason.Paid"));
        }
        else if (invoice.Status != InvoiceStatus.Open)
        {
            problems.Add(Text.T("Billing.TestDelete.Reason.State"));
        }

        var ids = all.Select(i => i.Id).ToList();
        var mailed = (await db.MailDelivery.Where(m => m.InvoiceId != null && ids.Contains(m.InvoiceId.Value)).Select(m => m.InvoiceId!.Value).Distinct().ToListAsync(ct)).ToHashSet();
        var banked = (await db.BankTransaction.Where(t => t.InvoiceId != null && ids.Contains(t.InvoiceId.Value)).Select(t => t.InvoiceId!.Value).Distinct().ToListAsync(ct)).ToHashSet();
        var linked = (await db.ShareLink.Where(l => ids.Contains(l.InvoiceId)).Select(l => l.InvoiceId).Distinct().ToListAsync(ct)).ToHashSet();
        foreach (var i in all)
        {
            // with a pair the reason says which of the two it is about
            string Of(string key) => all.Count > 1 ? Text.T("Billing.TestDelete.Reason.Of", i.Number ?? "", Text.T(key)) : Text.T(key);
            if (i.SendState != SendState.None || i.SentDate is not null || mailed.Contains(i.Id))
            {
                problems.Add(Of("Billing.TestDelete.Reason.Sent"));
            }

            if (banked.Contains(i.Id))
            {
                problems.Add(Of("Billing.TestDelete.Reason.Bank"));
            }

            if (linked.Contains(i.Id))
            {
                problems.Add(Of("Billing.TestDelete.Reason.Link"));
            }

            if (i.RecurringInvoiceId is not null)
            {
                problems.Add(Of("Billing.TestDelete.Reason.Series"));
            }

            if (i.ApiClientId is not null)
            {
                problems.Add(Of("Billing.TestDelete.Reason.Api"));
            }

            if (i.QuoteId is not null)
            {
                problems.Add(Of("Billing.TestDelete.Reason.Quote"));
            }
        }

        return problems;
    }

    /// <summary>
    /// Deletes the invoice (and its cancellation) for good. Throws <see cref="InvoiceException"/> with the first reason when it may not be deleted.
    /// With <paramref name="resetCounter"/> the number range goes on at the lowest number that was freed, as far as no later number is on a document.
    /// </summary>
    public async Task<TestDeleteResult> DeleteAsync(Invoice invoice, string? reason, bool resetCounter, CancellationToken ct = default)
    {
        if (await ProblemsAsync(invoice, ct) is { Count: > 0 } problems)
        {
            throw new InvoiceException(problems[0]);
        }

        var all = new List<Invoice> { invoice };
        if (invoice.CancellationInvoiceId is { } id && await db.Invoice.Include(i => i.PdfFile).FirstOrDefaultAsync(i => i.Id == id, ct) is { } cancellation)
        {
            all.Add(cancellation);
        }

        var who = user.Name ?? "";
        var why = string.IsNullOrWhiteSpace(reason) ? Text.T("Billing.TestDelete.DefaultReason") : reason.Trim();
        var now = clock.GetUtcNow().UtcDateTime;
        var ids = all.Select(i => i.Id).ToList();
        var fileIds = all.Where(i => i.PdfFileId is not null).Select(i => i.PdfFileId!.Value).ToList();
        var numbersGone = all.Select(i => i.Number ?? "").ToList();
        var counters = new Dictionary<string, (DateOnly Date, long Counter)>();
        var pattern = "";
        var range = NumberService.InvoiceRange;
        if (resetCounter)
        {
            (range, pattern) = await numbers.InvoiceRangeOfAsync(invoice.ApiClientId, ct);
            foreach (var i in all)
            {
                if (await CounterOfAsync(range, pattern, i, ct) is { } counter)
                {
                    var period = NumberPattern.PeriodKey(pattern, i.InvoiceDate);
                    counters[period] = counters.TryGetValue(period, out var known) && known.Counter < counter ? known : (i.InvoiceDate, counter);
                }
            }
        }

        List<StoredFile> storedFiles;
        await using (var transaction = await db.Database.BeginTransactionAsync(ct))
        {
            foreach (var i in all)
            {
                db.DeletedDocument.Add(new DeletedDocument
                {
                    Kind = i.Kind,
                    Number = i.Number ?? "",
                    DocumentDate = i.InvoiceDate,
                    GrossTotal = i.GrossTotal,
                    RecipientName = i.RecipientName,
                    DeletedByName = who,
                    DeletedDate = now,
                    Reason = why,
                });
            }

            await db.SaveChangesAsync(ct);
            // really gone: the positions follow by the foreign key; a delete through the context would only mark the invoice and keep its number taken
            await db.Invoice.IgnoreQueryFilters().Where(i => ids.Contains(i.Id)).ExecuteDeleteAsync(ct);
            db.ChangeTracker.Clear();
            storedFiles = await db.StoredFile.Where(f => fileIds.Contains(f.Id)).ToListAsync(ct);
            db.StoredFile.RemoveRange(storedFiles);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }

        // the files of the PDFs after the commit: a failure leaves a file without a row, never a row without a file
        foreach (var file in storedFiles)
        {
            try
            {
                await files.DeleteAsync(file, ct);
            }
            catch (Exception ex) when (ex is StorageException or IOException)
            {
                logger.LogWarning(ex, "The file of a deleted test invoice could not be removed: {Key}", file.StorageKey);
            }
        }

        var outcome = CounterOutcome.NotAsked;
        if (resetCounter)
        {
            outcome = counters.Count == 0 ? CounterOutcome.Unknown : CounterOutcome.Reset;
            foreach (var (date, counter) in counters.Values)
            {
                if (await numbers.CanContinueAtAsync(range, pattern, date, counter, ct))
                {
                    await numbers.SetNextCounterAsync(range, pattern, date, counter, ct);
                }
                else
                {
                    outcome = CounterOutcome.Blocked;
                }
            }
        }

        await activity.LogAsync(Text.T("Billing.Activity.TestDeleted"), nameof(Invoice), null, string.Join(", ", numbersGone) + ": " + why);
        var today = Fmt.Today(clock);
        return new TestDeleteResult(numbersGone, outcome, await numbers.PeekInvoiceNumberAsync(today, invoice.ApiClientId, ct));
    }

    /// <summary>The counter that gave the invoice its number (the number is looked for in the numbers of the range up to the counter now).</summary>
    private async Task<long?> CounterOfAsync(string range, string pattern, Invoice invoice, CancellationToken ct)
    {
        var next = await numbers.NextCounterAsync(range, pattern, invoice.InvoiceDate, ct);
        for (var counter = next - 1; counter >= 1 && next - counter <= 5000; counter--)
        {
            if (NumberPattern.Format(pattern, invoice.InvoiceDate, counter) == invoice.Number)
            {
                return counter;
            }
        }

        return null;
    }
}
