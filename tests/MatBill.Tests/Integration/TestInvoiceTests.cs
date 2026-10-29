using System.Net;
using MatBill.Data;
using MatBill.Data.Entities;
using MatBill.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MatBill.Tests.Integration;

/// <summary>
/// The one exception to "an issued invoice never changes": a test invoice that never left the house can be deleted for good by an administrator, so that the first
/// real invoice gets the first number. Everything that makes an invoice real (mail, payment, bank, link, series, API, quote) forbids it.
/// </summary>
[Collection(IntegrationCollection.Name)]
public sealed class TestInvoiceTests : IClassFixture<AppFixture>
{
    private readonly AppFixture _app;

    public TestInvoiceTests(AppFixture app) => _app = app;

    private async Task<HttpClient> AdminClientAsync()
    {
        await _app.EnsureAdminAsync();
        var client = _app.NewClient();
        await AppFixture.LoginAsync(client, "admin@example.com", "Start12345");
        return client;
    }

    private Task<T> WithServiceAsync<T>(Func<TestInvoiceService, InvoiceService, MbDbContext, Task<T>> action)
        => _app.WithScopeAsync(sp => action(sp.GetRequiredService<TestInvoiceService>(), sp.GetRequiredService<InvoiceService>(), sp.GetRequiredService<MbDbContext>()));

    private Task<List<string>> ProblemsAsync(long invoiceId)
        => WithServiceAsync(async (tests, invoices, _) => await tests.ProblemsAsync((await invoices.FindAsync(invoiceId))!));

    private Task<bool> ExistsAsync(long invoiceId)
        => _app.WithScopeAsync(sp => sp.GetRequiredService<MbDbContext>().Invoice.IgnoreQueryFilters().AnyAsync(i => i.Id == invoiceId));

    [DatabaseFact]
    public async Task A_test_invoice_is_deleted_for_good_and_its_number_is_given_again()
    {
        var (_, invoice, file) = await _app.CreateInvoiceWithPdfAsync("Test A");
        var number = invoice.Number!;
        using var client = await AdminClientAsync();
        var url = $"/Admin/TestInvoices?id={invoice.Id}";

        var page = await AppFixture.TextAsync(client, url);
        Assert.Contains(number, page);
        Assert.Contains("endgültig löschen", page);

        var response = await AppFixture.PostFormAsync(client, url, new Dictionary<string, string>
        {
            ["Input.ConfirmNumber"] = number, ["Input.Reason"] = "Probelauf", ["Input.ResetCounter"] = "true",
        });
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        // gone for real: not even a soft-deleted row keeps the number taken, the PDF is gone, a permanent record stays
        Assert.False(await ExistsAsync(invoice.Id));
        Assert.False(await _app.WithScopeAsync(sp => sp.GetRequiredService<MbDbContext>().StoredFile.AnyAsync(f => f.Id == file.Id)));
        var record = await _app.WithScopeAsync(sp => sp.GetRequiredService<MbDbContext>().DeletedDocument.AsNoTracking().SingleAsync(d => d.Number == number));
        Assert.Equal("Probelauf", record.Reason);
        Assert.Equal(InvoiceKind.Invoice, record.Kind);
        Assert.Equal(invoice.GrossTotal, record.GrossTotal);
        Assert.Contains(number, await AppFixture.TextAsync(client, "/Admin/TestInvoices"));

        // the range goes on at the freed number: the next invoice has it again
        var (_, next, _) = await _app.CreateInvoiceWithPdfAsync("Test A2");
        Assert.Equal(number, next.Number);
    }

    [DatabaseFact]
    public async Task Without_the_number_typed_nothing_is_deleted()
    {
        var (_, invoice, _) = await _app.CreateInvoiceWithPdfAsync("Test B");
        using var client = await AdminClientAsync();
        var url = $"/Admin/TestInvoices?id={invoice.Id}";

        var wrong = await AppFixture.PostFormAsync(client, url, new Dictionary<string, string> { ["Input.ConfirmNumber"] = "RE-0000", ["Input.ResetCounter"] = "true" });
        Assert.Equal(HttpStatusCode.OK, wrong.StatusCode);
        Assert.Contains("stimmt nicht überein", await AppFixture.TextAsync(wrong));
        Assert.True(await ExistsAsync(invoice.Id));
    }

    [DatabaseFact]
    public async Task The_range_does_not_go_back_over_a_later_number_and_says_so()
    {
        var (_, first, _) = await _app.CreateInvoiceWithPdfAsync("Test C1");
        var (_, second, _) = await _app.CreateInvoiceWithPdfAsync("Test C2");
        var before = await _app.WithScopeAsync(sp => sp.GetRequiredService<NumberService>().PeekInvoiceNumberAsync(sp.GetRequiredService<InvoiceService>().Today));

        var result = await _app.WithScopeAsync(async sp =>
        {
            using var admin = sp.GetRequiredService<ICurrentUser>().RunAs(null, "Test");
            var invoices = sp.GetRequiredService<InvoiceService>();
            return await sp.GetRequiredService<TestInvoiceService>().DeleteAsync((await invoices.FindAsync(first.Id))!, null, resetCounter: true);
        });

        Assert.Equal(CounterOutcome.Blocked, result.Counter);
        Assert.Equal(before, result.NextNumber);
        Assert.False(await ExistsAsync(first.Id));
        Assert.True(await ExistsAsync(second.Id));
    }

    [DatabaseFact]
    public async Task A_cancelled_test_invoice_goes_together_with_its_cancellation()
    {
        var (_, invoice, _) = await _app.CreateInvoiceWithPdfAsync("Test D");
        var cancellation = await _app.WithScopeAsync(async sp =>
        {
            var invoices = sp.GetRequiredService<InvoiceService>();
            return await invoices.CancelAsync((await invoices.FindAsync(invoice.Id))!, "Test");
        });

        // the cancellation alone is refused (with the way out), the original is deletable and takes the cancellation along
        Assert.Contains("Stornorechnung", string.Join(" ", await ProblemsAsync(cancellation.Id)));
        Assert.Empty(await ProblemsAsync(invoice.Id));
        var result = await _app.WithScopeAsync(async sp =>
        {
            using var admin = sp.GetRequiredService<ICurrentUser>().RunAs(null, "Tester");
            var invoices = sp.GetRequiredService<InvoiceService>();
            return await sp.GetRequiredService<TestInvoiceService>().DeleteAsync((await invoices.FindAsync(invoice.Id))!, null, resetCounter: true);
        });

        Assert.Equal(2, result.Numbers.Count);
        Assert.False(await ExistsAsync(invoice.Id));
        Assert.False(await ExistsAsync(cancellation.Id));
        var records = await _app.WithScopeAsync(sp => sp.GetRequiredService<MbDbContext>().DeletedDocument.AsNoTracking().Where(d => result.Numbers.Contains(d.Number)).ToListAsync());
        Assert.Equal(2, records.Count);
        Assert.Contains(records, r => r.Kind == InvoiceKind.Cancellation);
        Assert.All(records, r => Assert.Equal("Tester", r.DeletedByName));
        Assert.All(records, r => Assert.Equal("Testrechnung", r.Reason));
    }

    [DatabaseFact]
    public async Task An_invoice_that_left_the_house_is_never_deleted()
    {
        // sent
        var (_, sent, _) = await _app.CreateInvoiceWithPdfAsync("Test E1");
        await _app.WithScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<MbDbContext>();
            var tracked = await db.Invoice.FirstAsync(i => i.Id == sent.Id);
            tracked.SendState = SendState.Sent;
            tracked.SentDate = DateTime.UtcNow;
            await db.SaveChangesAsync();
        });
        Assert.Contains("versendet", string.Join(" ", await ProblemsAsync(sent.Id)));

        // paid
        var (_, paid, _) = await _app.CreateInvoiceWithPdfAsync("Test E2");
        await _app.WithScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<MbDbContext>();
            var tracked = await db.Invoice.FirstAsync(i => i.Id == paid.Id);
            tracked.Status = InvoiceStatus.Paid;
            tracked.PaidDate = DateOnly.FromDateTime(DateTime.Today);
            await db.SaveChangesAsync();
        });
        Assert.Contains("bezahlt", string.Join(" ", await ProblemsAsync(paid.Id)));

        // a bank transaction is assigned
        var (_, banked, _) = await _app.CreateInvoiceWithPdfAsync("Test E3");
        await _app.WithScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<MbDbContext>();
            var account = new BankAccount { Name = "Konto E3" };
            db.BankAccount.Add(account);
            await db.SaveChangesAsync();
            db.BankTransaction.Add(new BankTransaction { BankAccountId = account.Id, BookingDate = DateOnly.FromDateTime(DateTime.Today), Amount = 119, ImportKey = "ref:e3", State = MatchState.Matched, InvoiceId = banked.Id });
            await db.SaveChangesAsync();
        });
        Assert.Contains("Bankumsatz", string.Join(" ", await ProblemsAsync(banked.Id)));

        // a share link exists
        var (_, linked, _) = await _app.CreateInvoiceWithPdfAsync("Test E4");
        await _app.WithScopeAsync(async sp =>
        {
            var tracked = await sp.GetRequiredService<InvoiceService>().FindAsync(linked.Id);
            await sp.GetRequiredService<ShareService>().CreateAsync(tracked!, "Rechnung", "Hallo", 7, null, null);
        });
        Assert.Contains("Freigabelink", string.Join(" ", await ProblemsAsync(linked.Id)));

        // from a quote
        var (_, quoted, _) = await _app.CreateInvoiceWithPdfAsync("Test E5");
        await _app.WithScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<MbDbContext>();
            (await db.Invoice.FirstAsync(i => i.Id == quoted.Id)).QuoteId = 1;
            await db.SaveChangesAsync();
        });
        Assert.Contains("Angebot", string.Join(" ", await ProblemsAsync(quoted.Id)));

        // and the service refuses even when the page is asked for it
        var refused = await Assert.ThrowsAsync<InvoiceException>(() => _app.WithScopeAsync(async sp =>
        {
            var invoices = sp.GetRequiredService<InvoiceService>();
            return await sp.GetRequiredService<TestInvoiceService>().DeleteAsync((await invoices.FindAsync(sent.Id))!, null, resetCounter: false);
        }));
        Assert.Contains("versendet", refused.Message);
        foreach (var id in new[] { sent.Id, paid.Id, banked.Id, linked.Id, quoted.Id })
        {
            Assert.True(await ExistsAsync(id));
        }
    }

    [DatabaseFact]
    public async Task A_draft_has_its_own_way_and_only_an_administrator_may_delete()
    {
        await _app.EnsureAdminAsync();
        var draftId = await _app.WithScopeAsync(async sp =>
        {
            var invoices = sp.GetRequiredService<InvoiceService>();
            var db = sp.GetRequiredService<MbDbContext>();
            var draft = invoices.NewDraft();
            draft.Lines.Add(new InvoiceLine { Position = 1, Name = "Entwurf", Quantity = 1, UnitPrice = 10, TaxRate = 19 });
            InvoiceService.Recalculate(draft);
            db.Invoice.Add(draft);
            await db.SaveChangesAsync();
            return draft.Id;
        });
        Assert.Contains("Entwurf", string.Join(" ", await ProblemsAsync(draftId)));

        var (_, invoice, _) = await _app.CreateInvoiceWithPdfAsync("Test F");
        await _app.CreateUserAsync("buchhaltung@example.test", "Richtig-Richtig-9", UserRole.User);
        using var user = _app.NewClient();
        await AppFixture.LoginAsync(user, "buchhaltung@example.test", "Richtig-Richtig-9");
        var page = await user.GetAsync($"/Admin/TestInvoices?id={invoice.Id}");
        Assert.NotEqual(HttpStatusCode.OK, page.StatusCode);
        var post = await user.PostAsync($"/Admin/TestInvoices?id={invoice.Id}", new FormUrlEncodedContent(new Dictionary<string, string> { ["Input.ConfirmNumber"] = invoice.Number! }));
        Assert.NotEqual(HttpStatusCode.OK, post.StatusCode);
        Assert.True(await ExistsAsync(invoice.Id));
    }
}
