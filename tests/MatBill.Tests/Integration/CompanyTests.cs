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
    public async Task Branding_saves_without_a_tax_number_and_the_overview_stops_asking()
    {
        await _app.EnsureAdminAsync();
        await WithCompanyAsync(c => { c.Name = ""; c.TaxNumber = null; c.VatId = null; c.Iban = null; }, async () =>
        {
            using var client = _app.NewClient();
            await AppFixture.LoginAsync(client, "admin@example.com", "Start12345");
            Assert.Contains(De("Dashboard.Setup.Title"), await AppFixture.TextAsync(client, "/"));

            var saved = await AppFixture.PostFormAsync(client, "/Branding", new Dictionary<string, string>
            {
                ["Input.Name"] = "Matthix", ["Input.NameLine2"] = "Matthias Schmoldt", ["Input.Street"] = "Weg 1", ["Input.PostalCode"] = "12345", ["Input.City"] = "Stadt",
                ["Input.AccentColor"] = "#1d4ed8", ["Input.SmallBusiness"] = "false",
            });
            Assert.Equal(HttpStatusCode.Redirect, saved.StatusCode);
            Assert.Equal("Matthias Schmoldt", Config.Current.Company.NameLine2);
            Assert.Equal("Matthix · Matthias Schmoldt · Weg 1 · 12345 Stadt", Config.Current.Company.SenderLine);

            Assert.DoesNotContain(De("Dashboard.Setup.Title"), await AppFixture.TextAsync(client, "/"));
            Assert.Contains(De("Branding.TaxNumber.Missing"), await AppFixture.TextAsync(client, "/Branding"));
        });
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
