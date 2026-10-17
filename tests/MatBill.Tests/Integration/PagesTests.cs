using System.Net;
using MatBill.Data;
using MatBill.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MatBill.Tests.Integration;

/// <summary>Every page of the application opens, with the sample data and as the people who may see it, and the roles keep to their side.</summary>
[Collection(IntegrationCollection.Name)]
public sealed class PagesTests : IClassFixture<AppFixture>
{
    private readonly AppFixture _app;

    public PagesTests(AppFixture app) => _app = app;

    private async Task<long> DemoAsync()
    {
        await _app.EnsureAdminAsync();
        var adminId = await _app.WithScopeAsync(sp => sp.GetRequiredService<MbDbContext>().User.Select(u => u.Id).FirstAsync());
        await _app.WithScopeAsync(sp => sp.GetRequiredService<DemoData>().CreateAsync(adminId));
        return adminId;
    }

    [DatabaseFact]
    public async Task The_sample_data_is_written_once_and_never_over_real_data()
    {
        var adminId = await DemoAsync();
        Assert.False(await _app.WithScopeAsync(sp => sp.GetRequiredService<DemoData>().CreateAsync(adminId)));
        Assert.True(await _app.WithScopeAsync(sp => sp.GetRequiredService<MbDbContext>().Invoice.CountAsync()) >= 4);
        Assert.True(await _app.WithScopeAsync(sp => sp.GetRequiredService<MbDbContext>().RecurringInvoice.CountAsync()) >= 2);
    }

    [DatabaseFact]
    public async Task Every_page_opens_for_an_administrator()
    {
        await DemoAsync();
        using var client = _app.NewClient();
        await AppFixture.LoginAsync(client, "admin@example.test", "Admin-Passwort-2026");

        var (issued, draft, series, customer, product, share) = await _app.WithScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<MbDbContext>();
            var issuedId = await db.Invoice.Where(i => i.Number != null).Select(i => i.Id).FirstAsync();
            var tracked = await sp.GetRequiredService<InvoiceService>().FindAsync(issuedId);
            var link = await sp.GetRequiredService<ShareService>().CreateAsync(tracked!, "Probe", null, 7, null, null);
            return (issuedId, await db.Invoice.Where(i => i.Number == null).Select(i => i.Id).FirstAsync(), await db.RecurringInvoice.Select(s => s.Id).FirstAsync(),
                await db.Customer.Select(c => c.Id).FirstAsync(), await db.Product.Select(p => p.Id).FirstAsync(), link.Id);
        });

        string[] urls =
        [
            "/", "/Invoices", "/Invoices?status=offen&sort=betrag&dir=desc", "/Invoices?status=ueberfaellig", "/Invoices/Edit", $"/Invoices/Edit?customer={customer}",
            $"/Invoices/Edit?id={issued}", $"/Invoices/Edit?id={draft}", $"/Invoices/Send?id={issued}", $"/Invoices/Share?id={issued}",
            "/Recurring", "/Recurring?status=aktiv", "/Recurring/Edit", $"/Recurring/Edit?id={series}", "/Customers", "/Customers?offen=ja", "/Customers/Edit", $"/Customers/Edit?id={customer}",
            "/Products", "/Products/Edit", $"/Products/Edit?id={product}", "/Branding", "/Notifications", "/Notifications?unread=true",
            "/Admin/Users", "/Admin/Users/Edit", "/Admin/Shares", $"/Admin/Shares/Edit?id={share}", "/Admin/Invoicing", "/Admin/Mail",
            "/Admin/Storage", "/Admin/Backup", "/Admin/Backup/Plans", "/Admin/Backup/Plans/Edit", "/Admin/Backup/Targets", "/Admin/Backup/Targets/Edit", "/Admin/Backup/Restore", "/Admin/Security", "/Admin/Activity",
            "/Admin/System", "/Account/Profile", "/Account/Security",
        ];

        var failures = new List<string>();
        foreach (var url in urls)
        {
            var response = await client.GetAsync(url);
            var text = response.StatusCode == HttpStatusCode.OK ? await response.Content.ReadAsStringAsync() : "";
            if (response.StatusCode != HttpStatusCode.OK || text.Contains("Exception", StringComparison.Ordinal) || !text.Contains("<h1", StringComparison.Ordinal))
            {
                failures.Add($"{(int)response.StatusCode} {url}");
            }
        }

        Assert.True(failures.Count == 0, "Pages that failed: " + string.Join(", ", failures));

        // the PDFs: the stored one of an issued invoice, a fresh one of a draft, the preview of a series
        foreach (var url in new[] { $"/invoices/{issued}/pdf", $"/invoices/{draft}/pdf", $"/recurring/{series}/preview" })
        {
            var pdf = await client.GetAsync(url);
            Assert.Equal(HttpStatusCode.OK, pdf.StatusCode);
            Assert.Equal("application/pdf", pdf.Content.Headers.ContentType?.MediaType);
        }
    }

    [DatabaseFact]
    public async Task A_user_stays_out_of_the_settings_and_the_anonymous_visitor_is_sent_to_the_sign_in()
    {
        await _app.EnsureAdminAsync();
        await _app.CreateUserAsync("normal@example.test", "Normal-Passwort-2026", Data.Entities.UserRole.User);

        using var anonymous = _app.NewClient();
        foreach (var url in new[] { "/", "/Invoices", "/Recurring", "/Customers", "/Products", "/Branding", "/Admin/Users", "/invoices/1/pdf", "/api/customers/1" })
        {
            var status = (await anonymous.GetAsync(url)).StatusCode;
            Assert.True(status is HttpStatusCode.Redirect or HttpStatusCode.Unauthorized, $"{url} answered {(int)status} to somebody who is not signed in");
        }

        using var user = _app.NewClient();
        await AppFixture.LoginAsync(user, "normal@example.test", "Normal-Passwort-2026");
        Assert.Equal(HttpStatusCode.OK, (await user.GetAsync("/Invoices")).StatusCode);
        foreach (var url in new[] { "/Admin/Users", "/Admin/Backup", "/Admin/Shares", "/Admin/System", "/Admin/Mail", "/Admin/Invoicing", "/Admin/Security", "/Branding" })
        {
            var status = (await user.GetAsync(url)).StatusCode;
            Assert.NotEqual(HttpStatusCode.OK, status);
        }

        // the menu of a user has no settings
        Assert.DoesNotContain("/Admin/Users", await AppFixture.TextAsync(user, "/Invoices"));
    }
}
