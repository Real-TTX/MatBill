using System.Net;
using MatBill.Configuration;
using MatBill.Data;
using MatBill.Data.Entities;
using MatBill.Localization;
using MatBill.Services;
using Microsoft.Extensions.DependencyInjection;
using UglyToad.PdfPig;

namespace MatBill.Tests.Integration;

/// <summary>The company as sender: a second name line, no tax number yet, and the small business (§ 19 UStG) on every page that shows tax.</summary>
[Collection(IntegrationCollection.Name)]
public sealed class CompanyTests : IClassFixture<AppFixture>
{
    private readonly AppFixture _app;

    public CompanyTests(AppFixture app) => _app = app;

    private static string De(string key) => TextCatalog.Find(key, "de")!;

    private ConfigStore Config => _app.Services.GetRequiredService<ConfigStore>();

    /// <summary>Runs a test with a changed company and puts the company back afterwards (the configuration is shared by all tests).</summary>
    private async Task WithCompanyAsync(Action<CompanyConfig> change, Func<Task> test)
    {
        var saved = System.Text.Json.JsonSerializer.Serialize(Config.Current.Company);
        Config.Update(c => change(c.Company));
        try
        {
            await test();
        }
        finally
        {
            Config.Update(c => c.Company = System.Text.Json.JsonSerializer.Deserialize<CompanyConfig>(saved)!);
        }
    }

    [DatabaseFact]
    public async Task The_company_saves_without_a_tax_number_and_the_overview_stops_asking()
    {
        await _app.EnsureAdminAsync();
        await WithCompanyAsync(c => { c.Name = ""; c.TaxNumber = null; c.VatId = null; c.Iban = null; }, async () =>
        {
            using var client = _app.NewClient();
            await AppFixture.LoginAsync(client, "admin@example.com", "Start12345");
            Assert.Contains(De("Dashboard.Setup.Title"), await AppFixture.TextAsync(client, "/"));

            var saved = await AppFixture.PostFormAsync(client, "/Admin/Company", new Dictionary<string, string>
            {
                ["Input.Name"] = "Matthix", ["Input.NameLine2"] = "Matthias Schmoldt", ["Input.Street"] = "Weg 1", ["Input.PostalCode"] = "12345", ["Input.City"] = "Stadt",
                ["Input.SmallBusiness"] = "false",
            });
            Assert.Equal(HttpStatusCode.Redirect, saved.StatusCode);
            Assert.Equal("Matthias Schmoldt", Config.Current.Company.NameLine2);
            Assert.Equal("Matthix · Matthias Schmoldt · Weg 1 · 12345 Stadt", Config.Current.Company.SenderLine);

            Assert.DoesNotContain(De("Dashboard.Setup.Title"), await AppFixture.TextAsync(client, "/"));

            // the preview is the first page of the PDF as an image, also for a layout that is only being tried
            var preview = await client.GetAsync("/Admin/Branding?handler=Preview&layout=centered&accent=%23b4235f&signature=Ciao");
            Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
            Assert.Equal("image/png", preview.Content.Headers.ContentType?.MediaType);
            Assert.True((await preview.Content.ReadAsByteArrayAsync()).Length > 10_000);
            Assert.Equal(DocumentLayouts.Classic, DocumentLayouts.Of(Config.Current.Company.Layout));
            Assert.Contains(De("Company.TaxNumber.Missing"), await AppFixture.TextAsync(client, "/Admin/Company"));
        });
    }

    [DatabaseFact]
    public async Task A_customer_gets_a_chosen_number_and_the_range_skips_it()
    {
        await _app.EnsureAdminAsync();
        var saved = System.Text.Json.JsonSerializer.Serialize(Config.Current.Invoicing);
        Config.Update(c => { c.Invoicing.CustomerNumberManual = true; c.Invoicing.CustomerNumberPattern = "KN-{NNNN}"; });
        try
        {
            using var client = _app.NewClient();
            await AppFixture.LoginAsync(client, "admin@example.com", "Start12345");
            Assert.Contains("Input.Number", await AppFixture.TextAsync(client, "/Customers/Edit"));

            var created = await AppFixture.PostFormAsync(client, "/Customers/Edit", new Dictionary<string, string> { ["Input.Kind"] = "Company", ["Input.Name"] = "Handnummer GmbH", ["Input.Number"] = "KN-0500" });
            Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);

            // the same number again: refused, and the page opens the tab with the field
            var again = await AppFixture.PostFormAsync(client, "/Customers/Edit", new Dictionary<string, string> { ["Input.Kind"] = "Company", ["Input.Name"] = "Doppelt GmbH", ["Input.Number"] = "KN-0500" });
            Assert.Equal(HttpStatusCode.OK, again.StatusCode);
            Assert.Contains(TextCatalog.Find("Customers.Number.Taken", "de")!.Replace("{0}", "KN-0500"), await AppFixture.TextAsync(again));

            // the range arrives at 500: it takes 501
            var next = await _app.WithScopeAsync(async sp =>
            {
                var numbers = sp.GetRequiredService<NumberService>();
                var today = sp.GetRequiredService<InvoiceService>().Today;
                await numbers.SetNextCounterAsync(NumberService.CustomerRange, "KN-{NNNN}", today, 500);
                return await numbers.NextCustomerNumberAsync(today);
            });
            Assert.Equal("KN-0501", next);

            // without the setting there is no field
            Config.Update(c => c.Invoicing.CustomerNumberManual = false);
            Assert.DoesNotContain("Input.Number", await AppFixture.TextAsync(client, "/Customers/Edit"));

            // the old addresses of company and branding lead to the settings
            var old = await client.GetAsync("/Company");
            Assert.Equal("/Admin/Company", AppFixture.LocationPath(old));
        }
        finally
        {
            Config.Update(c => c.Invoicing = System.Text.Json.JsonSerializer.Deserialize<InvoicingConfig>(saved)!);
        }
    }

    [DatabaseFact]
    public async Task A_small_business_shows_no_tax_and_counts_expenses_gross()
    {
        await _app.EnsureAdminAsync();
        await WithCompanyAsync(c => { c.SmallBusiness = true; c.Name = "Matthix"; c.NameLine2 = "Matthias Schmoldt"; }, async () =>
        {
            var (_, invoice, _) = await _app.CreateInvoiceWithPdfAsync("Klein", 100m);
            Assert.True(invoice.TaxExempt);
            Assert.Equal(0m, invoice.TaxTotal);
            Assert.Equal(100m, invoice.GrossTotal);

            var pdf = await _app.WithScopeAsync(async sp => await sp.GetRequiredService<InvoiceService>().PdfAsync((await sp.GetRequiredService<InvoiceService>().FindAsync(invoice.Id))!));
            using (var document = PdfDocument.Open(pdf))
            {
                var text = string.Join(" ", document.GetPages().Select(p => p.Text));
                Assert.Contains("Matthias Schmoldt", text);
                Assert.Contains("UStG", text);
                Assert.DoesNotContain("Zwischensumme", text);
            }

            // a receipt of 119 euros costs a small business 119 euros: no input tax
            var months = await _app.WithScopeAsync(async sp =>
            {
                var db = sp.GetRequiredService<MbDbContext>();
                db.Expense.Add(new Expense { Number = "BE-KLEIN-1", ExpenseDate = new DateOnly(2001, 3, 5), Supplier = "Laden", NetAmount = 100m, TaxRate = 19m, TaxAmount = 19m, GrossAmount = 119m });
                await db.SaveChangesAsync();
                return await sp.GetRequiredService<ExportService>().MonthsAsync(new DateOnly(2001, 3, 1), new DateOnly(2001, 3, 31), cash: false);
            });
            Assert.Equal(119m, months.Single().ExpenseNet);
            Assert.Equal(0m, months.Single().InputTax);

            using var client = _app.NewClient();
            await AppFixture.LoginAsync(client, "admin@example.com", "Start12345");
            var view = await AppFixture.TextAsync(client, $"/Invoices/Edit?id={invoice.Id}");
            Assert.Contains("Matthias Schmoldt", view);
            Assert.DoesNotContain("Zwischensumme", view);
            Assert.DoesNotContain(De("Export.Kpi.TaxDue"), await AppFixture.TextAsync(client, "/Export?year=2001"));
            Assert.DoesNotContain(De("Products.Column.Vat"), await AppFixture.TextAsync(client, "/Products"));
            Assert.Contains("is-exempt", await AppFixture.TextAsync(client, "/Invoices/Edit"));
        });
    }
}
