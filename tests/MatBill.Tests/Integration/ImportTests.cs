using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using MatBill.Configuration;
using MatBill.Data;
using MatBill.Data.Entities;
using MatBill.Services;
using MatBill.Tests.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MatBill.Tests.Integration;

/// <summary>
/// Taking over the data of Invoiz: a check saves nothing, the import takes every issued invoice over with its own number and its original PDF byte for byte,
/// paid as the payments say, marks what looks wrong, adds the missing category, lets the range go on after the last number, and a second run adds nothing.
/// One problem stops all of it.
/// </summary>
[Collection(IntegrationCollection.Name)]
public sealed class ImportTests : IClassFixture<AppFixture>
{
    private readonly AppFixture _app;

    public ImportTests(AppFixture app) => _app = app;

    private Task<LegacyImportResult> RunAsync(byte[] zip, bool apply, bool continueNumbers = true)
        => _app.WithScopeAsync(sp => sp.GetRequiredService<LegacyImportService>().RunAsync(new MemoryStream(zip), apply, continueNumbers));

    private static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    [DatabaseFact]
    public async Task A_check_saves_nothing_and_the_import_takes_everything_over_once()
    {
        var zip = SampleExport.Zip(SampleExport.Files("A", "00"));

        var check = await RunAsync(zip, apply: false);
        Assert.Empty(check.Problems);
        Assert.False(check.Applied);
        Assert.Equal((2, 2, 3, 1, 2), (check.CustomersNew, check.ProductsNew, check.InvoicesNew, check.DraftsSkipped, check.ExpensesNew));
        Assert.Equal((383.05m, 10m), check.Years[2023]);
        Assert.Equal((200m, 0m), check.Years[2024]);
        Assert.Equal((0m, 438m), check.Years[2025]);
        Assert.Equal("004", check.NextInvoiceNumber);
        Assert.False(await _app.WithScopeAsync(sp => sp.GetRequiredService<MbDbContext>().Invoice.AnyAsync(i => i.ImportRef == "A900")));

        var result = await RunAsync(zip, apply: true);
        Assert.Empty(result.Problems);
        Assert.True(result.Applied);
        Assert.Equal((2, 2, 3, 2), (result.CustomersNew, result.ProductsNew, result.InvoicesNew, result.ExpensesNew));
        Assert.Equal(1, result.ExpensesToReview);
        Assert.Equal(["IT-Hardware A"], result.CategoriesAdded);
        Assert.Equal("{NNN}", result.NumberPattern);
        Assert.Equal("004", result.NextInvoiceNumber);

        await _app.WithScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<MbDbContext>();
            var invoices = sp.GetRequiredService<InvoiceService>();
            var imported = await db.Invoice.Include(i => i.Lines).Include(i => i.PdfFile).Include(i => i.Customer)
                .Where(i => i.ImportSource == "Invoiz" && (i.ImportRef == "A900" || i.ImportRef == "A901" || i.ImportRef == "A902" || i.ImportRef == "A903")).ToListAsync();
            Assert.Equal(["001", "002", "003"], imported.Select(i => i.Number).Order());

            foreach (var invoice in imported)
            {
                // issued, paid, no tax, and the stored PDF is the original, byte for byte
                Assert.Equal(InvoiceStatus.Paid, invoice.Status);
                Assert.True(invoice.TaxExempt);
                Assert.Equal(SampleExport.Pdf(invoice.Number!), await invoices.PdfAsync(invoice));
                Assert.Equal(Sha(SampleExport.Pdf(invoice.Number!)), invoice.PdfFile!.Sha256);
                Assert.NotNull(invoice.Customer);
            }

            var first = imported.Single(i => i.Number == "001");
            Assert.Equal(new DateOnly(2024, 1, 5), first.PaidDate);
            Assert.Equal("A010", first.Customer!.Number);
            Assert.Equal("Hamburg", first.RecipientCity);
            Assert.Null(first.ImportNote);
            Assert.NotNull(first.Lines.Single().ProductId);

            // the year that does not fit is taken over as it is, and marked
            var third = imported.Single(i => i.Number == "003");
            Assert.Equal(new DateOnly(2023, 1, 9), third.InvoiceDate);
            Assert.Contains("002", third.ImportNote);
            Assert.Equal(383.05m, third.GrossTotal);
            Assert.Equal(164.5m, third.Lines.Single(l => l.Position == 1).UnitPrice);

            var expenses = await db.Expense.Include(e => e.ReceiptFile).Where(e => e.ImportRef == "A31" || e.ImportRef == "A32").ToListAsync();
            Assert.Equal(2, expenses.Count);
            var review = expenses.Single(e => e.ImportRef == "A32");
            Assert.True(review.NeedsReview);
            Assert.Equal("IT-Hardware A", review.Category);
            Assert.Equal(ExpenseStatus.Paid, review.Status);
            Assert.Equal(438m, review.GrossAmount);
            Assert.StartsWith("ZU PRÜFEN", review.Note);
            Assert.False(expenses.Single(e => e.ImportRef == "A31").NeedsReview);
            Assert.Contains("IT-Hardware A", sp.GetRequiredService<ConfigStore>().Current.Invoicing.ExpenseCategories);

            // the origin is in the activity log, with the hash of the file
            var ids = imported.Select(i => (long?)i.Id).ToList();
            var log = await db.ActivityLog.Where(a => a.EntityType == nameof(Invoice) && ids.Contains(a.EntityId)).ToListAsync();
            Assert.Equal(3, log.Count);
            Assert.Contains(log, a => a.Summary!.Contains(first.PdfFile!.Sha256) && a.Summary.Contains("A900"));
            return 0;
        });

        // a second run adds nothing
        var again = await RunAsync(zip, apply: true);
        Assert.Empty(again.Problems);
        Assert.Equal((0, 0, 0, 0), (again.CustomersNew, again.ProductsNew, again.InvoicesNew, again.ExpensesNew));
        Assert.Equal((2, 2, 3, 2), (again.CustomersKnown, again.ProductsKnown, again.InvoicesKnown, again.ExpensesKnown));
        Assert.Equal(2, await _app.WithScopeAsync(sp => sp.GetRequiredService<MbDbContext>().Customer.CountAsync(c => c.Number == "A010" || c.Number == "A011")));

        // the export of the years holds every original PDF and every receipt unchanged
        var export = await _app.WithScopeAsync(sp => sp.GetRequiredService<ExportService>().ExportAsync(new DateOnly(2023, 1, 1), new DateOnly(2025, 12, 31), withBank: false));
        using var archive = new ZipArchive(new MemoryStream(export.Zip));
        foreach (var number in new[] { "001", "002", "003" })
        {
            var entry = Assert.Single(archive.Entries, e => e.FullName.Contains("/Rechnungen/") && e.Name.Contains($"_{number}_"));
            using var read = new MemoryStream();
            await entry.Open().CopyToAsync(read);
            Assert.Equal(SampleExport.Pdf(number), read.ToArray());
        }

        foreach (var (supplier, bytes) in new[] { ("Gemeinde A", "JPEG A"u8.ToArray()), ("Händler A", "%PDF-1.4 receipt A"u8.ToArray()) })
        {
            var entry = Assert.Single(archive.Entries, e => e.FullName.Contains("/Ausgaben/") && e.Name.Contains(supplier));
            using var read = new MemoryStream();
            await entry.Open().CopyToAsync(read);
            Assert.Equal(bytes, read.ToArray());
        }
    }

    [DatabaseFact]
    public async Task One_missing_pdf_stops_the_whole_import()
    {
        var files = SampleExport.Files("B", "50");
        files.Remove("Invoiz-Export/Rechnungen/2024-02-01_502.pdf");

        var result = await RunAsync(SampleExport.Zip(files), apply: true);
        Assert.False(result.Applied);
        Assert.Contains(result.Problems, p => p.Contains("502"));
        await _app.WithScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<MbDbContext>();
            Assert.False(await db.Invoice.AnyAsync(i => i.ImportRef == "B900"));
            Assert.False(await db.Customer.AnyAsync(c => c.Number == "B010"));
            Assert.False(await db.Expense.AnyAsync(e => e.ImportRef == "B31"));
            Assert.DoesNotContain("IT-Hardware B", sp.GetRequiredService<ConfigStore>().Current.Invoicing.ExpenseCategories);
        });
    }

    [DatabaseFact]
    public async Task The_page_checks_an_upload_and_the_invoice_shows_where_it_comes_from()
    {
        await _app.EnsureAdminAsync();
        using var client = _app.NewClient();
        await AppFixture.LoginAsync(client, "admin@example.com", "Start12345");
        var zip = SampleExport.Zip(SampleExport.Files("C", "70"));

        async Task<HttpResponseMessage> PostAsync(string handler)
        {
            var token = await AppFixture.TokenAsync(client, "/Admin/Import");
            var form = new MultipartFormDataContent
            {
                { new StringContent(token), "__RequestVerificationToken" },
                { new StringContent("false"), "Input.ContinueNumbers" },
            };
            var file = new ByteArrayContent(zip);
            file.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
            form.Add(file, "file", "Invoiz-Export.zip");
            return await client.PostAsync($"/Admin/Import?handler={handler}", form);
        }

        var check = await PostAsync("Check");
        Assert.Equal(HttpStatusCode.OK, check.StatusCode);
        var text = await AppFixture.TextAsync(check);
        Assert.Contains("Prüfung bestanden", text);
        Assert.Contains("383,05", text);

        var run = await PostAsync("Import");
        Assert.Contains("Die Daten wurden übernommen", await AppFixture.TextAsync(run));

        var id = await _app.WithScopeAsync(sp => sp.GetRequiredService<MbDbContext>().Invoice.Where(i => i.ImportRef == "C902").Select(i => i.Id).SingleAsync());
        var page = await AppFixture.TextAsync(client, $"/Invoices/Edit?id={id}");
        Assert.Contains("aus Invoiz", page);
        Assert.Contains("passt nicht zur Nummernfolge", page);
        Assert.DoesNotContain("ZUGFeRD-XML", page);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/invoices/{id}/xml")).StatusCode);
        Assert.Equal(SampleExport.Pdf("703"), await client.GetByteArrayAsync($"/invoices/{id}/pdf"));
    }
}
