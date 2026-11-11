using System.IO.Compression;
using System.Text;
using MatBill.Data;
using MatBill.Data.Entities;
using MatBill.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace MatBill.Tests.Integration;

/// <summary>Runs only when <see cref="Variable"/> names the folder of a real export (it is never in the repository) and the database is there.</summary>
public sealed class ImportTrialFactAttribute : FactAttribute
{
    public ImportTrialFactAttribute()
    {
        if (!AppFixture.Available || !Directory.Exists(Environment.GetEnvironmentVariable(ImportTrialTests.Variable)))
        {
            Skip = $"{ImportTrialTests.Variable} names no export folder (or {AppFixture.Variable} is not set).";
        }
    }
}

/// <summary>
/// A trial run of a real export against a fresh database, before it is imported into a live instance: everything is taken over, and the export of every year
/// holds every original PDF and every receipt unchanged. Prints the counts and the sums per year to compare with the old program.
/// </summary>
[Collection(IntegrationCollection.Name)]
public sealed class ImportTrialTests(AppFixture app, ITestOutputHelper output) : IClassFixture<AppFixture>
{
    public const string Variable = "MATBILL_IMPORT_TRIAL";

    [ImportTrialFact]
    public async Task A_real_export_is_taken_over_and_every_year_exports_all_its_documents()
    {
        var folder = Environment.GetEnvironmentVariable(Variable)!;
        using var buffer = new MemoryStream();
        ZipFile.CreateFromDirectory(folder, buffer, CompressionLevel.Fastest, includeBaseDirectory: true);
        var zip = buffer.ToArray();

        var check = await app.WithScopeAsync(sp => sp.GetRequiredService<LegacyImportService>().RunAsync(new MemoryStream(zip), apply: false, continueNumbers: true));
        Assert.Empty(check.Problems);
        var result = await app.WithScopeAsync(sp => sp.GetRequiredService<LegacyImportService>().RunAsync(new MemoryStream(zip), apply: true, continueNumbers: true));
        Assert.Empty(result.Problems);
        Assert.True(result.Applied);

        var report = new StringBuilder();
        report.AppendLine($"Kunden {result.CustomersNew} neu / {result.CustomersKnown} da, Artikel {result.ProductsNew} / {result.ProductsKnown}, Rechnungen {result.InvoicesNew} / {result.InvoicesKnown} (Entwürfe ausgelassen: {result.DraftsSkipped}), Ausgaben {result.ExpensesNew} / {result.ExpensesKnown} (zu prüfen: {result.ExpensesToReview})");
        report.AppendLine($"Neue Kategorien: {string.Join(", ", result.CategoriesAdded)}; Muster {result.NumberPattern}, nächste Nummer {result.NextInvoiceNumber}");
        foreach (var note in result.Notes)
        {
            report.AppendLine("Hinweis: " + note);
        }

        await app.WithScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<MbDbContext>();
            var export = sp.GetRequiredService<ExportService>();
            var invoices = await db.Invoice.AsNoTracking().Where(i => i.ImportSource != null).ToListAsync();
            var expenses = await db.Expense.AsNoTracking().Where(e => e.ImportSource != null).ToListAsync();
            report.AppendLine($"In der Datenbank: {await db.Customer.CountAsync()} Kunden, {await db.Product.CountAsync()} Artikel, {invoices.Count} Rechnungen, {expenses.Count} Ausgaben");
            foreach (var year in invoices.Select(i => i.InvoiceDate.Year).Concat(expenses.Select(e => e.ExpenseDate.Year)).Distinct().Order())
            {
                var paid = invoices.Where(i => i.InvoiceDate.Year == year && i.Status == InvoiceStatus.Paid).Sum(i => i.GrossTotal);
                var spent = expenses.Where(e => e.ExpenseDate.Year == year).Sum(e => e.GrossAmount);
                var zip = await export.ExportAsync(new DateOnly(year, 1, 1), new DateOnly(year, 12, 31), withBank: false);
                using var archive = new ZipArchive(new MemoryStream(zip.Zip));
                var pdfs = archive.Entries.Count(e => e.FullName.Contains("/Rechnungen/"));
                var receipts = archive.Entries.Count(e => e.FullName.Contains("/Ausgaben/"));
                var yearInvoices = invoices.Where(i => i.InvoiceDate.Year == year).ToList();
                var yearExpenses = expenses.Count(e => e.ExpenseDate.Year == year);
                report.AppendLine($"{year}: Rechnungen bezahlt brutto {paid:N2} € ({yearInvoices.Count} Rechnungen, {pdfs} PDFs im Export), Ausgaben brutto {spent:N2} € ({yearExpenses} Ausgaben, {receipts} Belege im Export, fehlend {zip.Missing})");
                Assert.Equal(yearInvoices.Count, pdfs);
                Assert.Equal(yearExpenses, receipts);
                Assert.Equal(0, zip.Missing);

                // every PDF in the export is the original file of the folder, byte for byte
                foreach (var invoice in yearInvoices)
                {
                    var entry = Assert.Single(archive.Entries, e => e.FullName.Contains("/Rechnungen/") && e.Name.Contains($"_{invoice.Number}_"));
                    using var read = new MemoryStream();
                    await entry.Open().CopyToAsync(read);
                    var original = Directory.GetFiles(Path.Combine(folder, "Rechnungen"), $"*_{invoice.Number}.pdf").Single();
                    Assert.Equal(await File.ReadAllBytesAsync(original), read.ToArray());
                }
            }

            return 0;
        });

        output.WriteLine(report.ToString());
        if (Environment.GetEnvironmentVariable("MATBILL_IMPORT_TRIAL_REPORT") is { Length: > 0 } path)
        {
            await File.WriteAllTextAsync(path, report.ToString());
        }
    }
}
