using MatBill.Core.Billing;
using MatBill.Data;
using MatBill.Data.Entities;
using MatBill.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MatBill.Tests.Integration;

/// <summary>A series makes invoices of their own: number, date, period; a later change of the template leaves them alone; a failure is recorded and tried again.</summary>
[Collection(IntegrationCollection.Name)]
public sealed class RecurringTests : IClassFixture<AppFixture>
{
    private readonly AppFixture _app;

    public RecurringTests(AppFixture app) => _app = app;

    [DatabaseFact]
    public async Task A_due_series_makes_an_issued_invoice_and_moves_on_and_its_template_changes_only_what_comes_next()
    {
        await _app.EnsureAdminAsync();
        var (customer, source, _) = await _app.CreateInvoiceWithPdfAsync("Serie", 50m);
        var seriesId = await _app.WithScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<MbDbContext>();
            var recurring = sp.GetRequiredService<RecurringService>();
            var tracked = await sp.GetRequiredService<InvoiceService>().FindAsync(source.Id);
            var series = recurring.FromInvoice(tracked!, RecurrenceInterval.Monthly, recurring.Today.AddMonths(-2), null, autoSend: false);
            db.RecurringInvoice.Add(series);
            await db.SaveChangesAsync();
            // as if the server had been off: the series is two dates behind
            series.NextRunDate = recurring.Today.AddMonths(-2);
            await db.SaveChangesAsync();
            return series.Id;
        });

        var made = await _app.WithScopeAsync(sp => sp.GetRequiredService<RecurringService>().RunDueAsync());
        Assert.Equal(3, made);

        await _app.WithScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<MbDbContext>();
            var invoices = await db.Invoice.Include(i => i.Lines).Where(i => i.RecurringInvoiceId == seriesId).OrderBy(i => i.RecurringDate).ToListAsync();
            Assert.Equal(3, invoices.Count);
            Assert.All(invoices, i => Assert.Equal(InvoiceStatus.Open, i.Status));
            Assert.All(invoices, i => Assert.NotNull(i.PdfFileId));
            Assert.Equal(3, invoices.Select(i => i.Number).Distinct().Count());
            Assert.Equal(invoices[0].RecurringDate, invoices[0].InvoiceDate);
            Assert.Equal(invoices[0].InvoiceDate.AddMonths(1).AddDays(-1), invoices[0].ServiceTo);
            Assert.Equal(59.50m, invoices[0].GrossTotal);

            var series = await db.RecurringInvoice.Include(s => s.Lines).FirstAsync(s => s.Id == seriesId);
            Assert.Equal(3, series.CreatedCount);
            Assert.True(series.NextRunDate > sp.GetRequiredService<RecurringService>().Today);

            // the template changes: the invoices that were made keep their price
            series.Lines[0].UnitPrice = 80m;
            await db.SaveChangesAsync();
        });

        var prices = await _app.WithScopeAsync(sp => sp.GetRequiredService<MbDbContext>().InvoiceLine.Where(l => l.Invoice!.RecurringInvoiceId == seriesId).Select(l => l.UnitPrice).ToListAsync());
        Assert.All(prices, p => Assert.Equal(50m, p));
        _ = customer;
    }

    [DatabaseFact]
    public async Task A_series_that_cannot_make_its_invoice_records_the_problem_notifies_and_waits()
    {
        await _app.EnsureAdminAsync();
        var (customer, source, _) = await _app.CreateInvoiceWithPdfAsync("Kaputt");
        var seriesId = await _app.WithScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<MbDbContext>();
            var recurring = sp.GetRequiredService<RecurringService>();
            var tracked = await sp.GetRequiredService<InvoiceService>().FindAsync(source.Id);
            var series = recurring.FromInvoice(tracked!, RecurrenceInterval.Weekly, recurring.Today, null, autoSend: true);
            db.RecurringInvoice.Add(series);
            // automatic sending without an address cannot work
            (await db.Customer.FirstAsync(c => c.Id == customer.Id)).Email = null;
            await db.SaveChangesAsync();
            return series.Id;
        });

        Assert.Equal(0, await _app.WithScopeAsync(sp => sp.GetRequiredService<RecurringService>().RunDueAsync()));
        await _app.WithScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<MbDbContext>();
            var series = await db.RecurringInvoice.FirstAsync(s => s.Id == seriesId);
            Assert.Equal(1, series.FailedAttempts);
            Assert.Contains("keine E-Mail-Adresse", series.LastError);
            Assert.NotNull(series.RetryAfterDate);
            Assert.True(await db.RecurringRun.AnyAsync(r => r.RecurringInvoiceId == seriesId && r.Status == RecurringRunStatus.Failed));
            Assert.True(await db.Notification.AnyAsync(n => n.Tone == NotificationTone.Error && n.Url == $"/Recurring/Edit?id={seriesId}"));
        });

        // waiting: a second run right away does not try again
        Assert.Equal(0, await _app.WithScopeAsync(sp => sp.GetRequiredService<RecurringService>().RunDueAsync()));
        Assert.Equal(1, await _app.WithScopeAsync(sp => sp.GetRequiredService<MbDbContext>().RecurringRun.CountAsync(r => r.RecurringInvoiceId == seriesId)));
    }
}
