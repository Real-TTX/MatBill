using System.IO.Compression;
using System.Text;
using MatBill.Data;
using MatBill.Data.Entities;
using MatBill.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using UglyToad.PdfPig;

namespace MatBill.Tests.Integration;

/// <summary>Cancellation invoices, quotes, e-invoices, expenses from e-invoices, bank imports with automatic assignment, the export for the tax office.</summary>
[Collection(IntegrationCollection.Name)]
public sealed class FinanceTests : IClassFixture<AppFixture>
{
    private readonly AppFixture _app;

    public FinanceTests(AppFixture app) => _app = app;

    [DatabaseFact]
    public async Task Cancelling_writes_a_cancellation_invoice_with_its_own_number_and_negative_amounts()
    {
        await _app.EnsureAdminAsync();
        var (_, invoice, _) = await _app.CreateInvoiceWithPdfAsync("Storno", 200m);
        var storno = await _app.WithScopeAsync(async sp =>
        {
            var invoices = sp.GetRequiredService<InvoiceService>();
            return await invoices.CancelAsync((await invoices.FindAsync(invoice.Id))!, "falsch");
        });

        await _app.WithScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<MbDbContext>();
            var original = await db.Invoice.AsNoTracking().FirstAsync(i => i.Id == invoice.Id);
            var cancellation = await db.Invoice.AsNoTracking().Include(i => i.Lines).FirstAsync(i => i.Id == storno.Id);
            Assert.Equal(InvoiceStatus.Cancelled, original.Status);
            Assert.Equal(cancellation.Id, original.CancellationInvoiceId);
            Assert.Equal(InvoiceKind.Cancellation, cancellation.Kind);
            Assert.NotEqual(original.Number, cancellation.Number);
            Assert.Equal(original.Number, cancellation.CancelsNumber);
            Assert.Equal(-original.GrossTotal, cancellation.GrossTotal);
            Assert.All(cancellation.Lines, l => Assert.True(l.Quantity < 0));
            Assert.NotNull(cancellation.PdfFileId);
        });

        // a cancellation cannot be cancelled, and the cancelled invoice not again
        await _app.WithScopeAsync(async sp =>
        {
            var invoices = sp.GetRequiredService<InvoiceService>();
            await Assert.ThrowsAsync<InvoiceException>(async () => await invoices.CancelAsync((await invoices.FindAsync(storno.Id))!, null));
            await Assert.ThrowsAsync<InvoiceException>(async () => await invoices.CancelAsync((await invoices.FindAsync(invoice.Id))!, null));
        });
    }

    [DatabaseFact]
    public async Task An_issued_invoice_is_a_zugferd_e_invoice_and_its_xml_reads_back()
    {
        await _app.EnsureAdminAsync();
        await _app.WithScopeAsync(sp =>
        {
            sp.GetRequiredService<Configuration.ConfigStore>().Update(c =>
            {
                c.Company.Name = "E-Rechnung Test GmbH";
                c.Company.Street = "Weg 1";
                c.Company.PostalCode = "10115";
                c.Company.City = "Berlin";
                c.Company.VatId = "DE123456789";
                c.Company.Email = "rechnung@example.test";
                c.Company.Iban = "DE89370400440532013000";
            });
            return Task.CompletedTask;
        });

        var (_, invoice, _) = await _app.CreateInvoiceWithPdfAsync("ZUGFeRD", 100m);
        var (pdf, xml, xrechnung) = await _app.WithScopeAsync(async sp =>
        {
            var invoices = sp.GetRequiredService<InvoiceService>();
            var tracked = (await invoices.FindAsync(invoice.Id))!;
            return (await invoices.PdfAsync(tracked), invoices.Xml(tracked, false), invoices.Xml(tracked, true));
        });

        using (var document = PdfDocument.Open(pdf))
        {
            Assert.True(document.Advanced.TryGetEmbeddedFiles(out var files));
            Assert.Contains(files, f => f.Name == "factur-x.xml" && Encoding.UTF8.GetString([.. f.Bytes]).Contains("CrossIndustryInvoice", StringComparison.Ordinal));
        }

        var read = EInvoice.Read(pdf, "rechnung.pdf");
        Assert.NotNull(read);
        Assert.True(read.FromEInvoice);
        Assert.Equal(invoice.Number, read.Number);
        Assert.Equal(119m, read.Gross);
        Assert.Equal("E-Rechnung Test GmbH", read.Supplier);

        Assert.Contains("urn:cen.eu:en16931:2017", Encoding.UTF8.GetString(xml), StringComparison.Ordinal);
        Assert.Contains("xrechnung", Encoding.UTF8.GetString(xrechnung), StringComparison.OrdinalIgnoreCase);
    }

    [DatabaseFact]
    public async Task An_invoice_can_be_issued_without_a_tax_number_or_vat_id()
    {
        await _app.EnsureAdminAsync();
        var config = _app.Services.GetRequiredService<Configuration.ConfigStore>();
        var (tax, vat, small) = (config.Current.Company.TaxNumber, config.Current.Company.VatId, config.Current.Company.SmallBusiness);
        try
        {
            foreach (var smallBusiness in new[] { false, true })
            {
                config.Update(c =>
                {
                    c.Company.Name = "Ohne Nummer";
                    c.Company.Street = "Weg 1";
                    c.Company.PostalCode = "10115";
                    c.Company.City = "Berlin";
                    c.Company.TaxNumber = null;
                    c.Company.VatId = null;
                    c.Company.SmallBusiness = smallBusiness;
                });

                var (_, invoice, _) = await _app.CreateInvoiceWithPdfAsync("Ohne Steuernummer", 100m);
                var (pdf, xml) = await _app.WithScopeAsync(async sp =>
                {
                    var invoices = sp.GetRequiredService<InvoiceService>();
                    var tracked = (await invoices.FindAsync(invoice.Id))!;
                    return (await invoices.PdfAsync(tracked), invoices.Xml(tracked, false));
                });

                Assert.NotNull(invoice.Number);
                using var document = PdfDocument.Open(pdf);
                Assert.True(document.Advanced.TryGetEmbeddedFiles(out var files), $"small business {smallBusiness}: the plain PDF has no e-invoice inside");
                Assert.Contains(files, f => f.Name == "factur-x.xml");
                Assert.DoesNotContain("SpecifiedTaxRegistration", Encoding.UTF8.GetString(xml), StringComparison.Ordinal);
            }
        }
        finally
        {
            config.Update(c => { c.Company.TaxNumber = tax; c.Company.VatId = vat; c.Company.SmallBusiness = small; });
        }
    }

    [DatabaseFact]
    public async Task A_quote_is_issued_accepted_and_becomes_an_invoice_draft()
    {
        await _app.EnsureAdminAsync();
        var invoiceId = await _app.WithScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<MbDbContext>();
            var quotes = sp.GetRequiredService<QuoteService>();
            var customer = new Customer { Number = "K-Q1", Name = "Angebotskunde" };
            db.Customer.Add(customer);
            await db.SaveChangesAsync();
            var quote = quotes.NewDraft(customer);
            quote.Lines.Add(new QuoteLine { Position = 1, Name = "Konzept", Quantity = 2, UnitPrice = 500, TaxRate = 19 });
            QuoteService.Recalculate(quote);
            db.Quote.Add(quote);
            await db.SaveChangesAsync();
            await quotes.IssueAsync(quote);
            Assert.StartsWith("AN-", quote.Number);
            Assert.Equal(1190m, quote.GrossTotal);
            await quotes.AnswerAsync(quote, accepted: true);
            var invoice = await quotes.ToInvoiceAsync(quote);
            Assert.Equal(QuoteStatus.Invoiced, quote.Status);
            await Assert.ThrowsAsync<InvoiceException>(() => quotes.ToInvoiceAsync(quote));
            return invoice.Id;
        });

        var invoice = await _app.WithScopeAsync(sp => sp.GetRequiredService<MbDbContext>().Invoice.AsNoTracking().Include(i => i.Lines).FirstAsync(i => i.Id == invoiceId));
        Assert.Equal(InvoiceStatus.Draft, invoice.Status);
        Assert.Equal(1190m, invoice.GrossTotal);
        Assert.Equal("Konzept", invoice.Lines.Single().Name);
    }

    [DatabaseFact]
    public async Task A_bank_statement_is_imported_once_and_pays_the_invoice_and_the_expense_it_names()
    {
        await _app.EnsureAdminAsync();
        var (_, invoice, _) = await _app.CreateInvoiceWithPdfAsync("Bank", 300m);
        var (accountId, expenseId) = await _app.WithScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<MbDbContext>();
            var account = new BankAccount { Name = "Test " + Guid.NewGuid().ToString("N")[..6] };
            var expense = new Expense { Number = "BE-T-" + Guid.NewGuid().ToString("N")[..6], ExpenseDate = DateOnly.FromDateTime(DateTime.Today), Supplier = "Papierladen", SupplierInvoiceNumber = "PL-777", GrossAmount = 47.11m, NetAmount = 39.59m, TaxAmount = 7.52m };
            db.BankAccount.Add(account);
            db.Expense.Add(expense);
            await db.SaveChangesAsync();
            return (account.Id, expense.Id);
        });

        var csv = $"Buchungstag;Betrag;Name;Verwendungszweck\n{DateTime.Today:dd.MM.yyyy};357,00;Kunde Bank;Rechnung {invoice.Number}\n{DateTime.Today:dd.MM.yyyy};-47,11;Papierladen GmbH;Rechnung PL-777\n{DateTime.Today:dd.MM.yyyy};-9,99;Streaming AG;Abo\n";
        var first = await ImportAsync(accountId, csv);
        Assert.Equal(3, first.Added);
        Assert.Equal(2, first.Matched);

        var again = await ImportAsync(accountId, csv);
        Assert.Equal(0, again.Added);
        Assert.Equal(3, again.Duplicates);

        await _app.WithScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<MbDbContext>();
            Assert.Equal(InvoiceStatus.Paid, (await db.Invoice.AsNoTracking().FirstAsync(i => i.Id == invoice.Id)).Status);
            Assert.Equal(ExpenseStatus.Paid, (await db.Expense.AsNoTracking().FirstAsync(e => e.Id == expenseId)).Status);

            // taking an assignment back opens the invoice again
            var t = await db.BankTransaction.FirstAsync(x => x.InvoiceId == invoice.Id);
            await sp.GetRequiredService<MatchingService>().UnassignAsync(t);
            Assert.Equal(InvoiceStatus.Open, (await db.Invoice.AsNoTracking().FirstAsync(i => i.Id == invoice.Id)).Status);

            // the unmatched debit becomes an expense with one click
            var abo = await db.BankTransaction.FirstAsync(x => x.BankAccountId == accountId && x.Amount == -9.99m);
            var created = await sp.GetRequiredService<MatchingService>().CreateExpenseAsync(abo, "Software und Abos");
            Assert.Equal(9.99m, created.GrossAmount);
            Assert.Equal(ExpenseStatus.Paid, created.Status);
        });
    }

    private Task<BankImport> ImportAsync(long accountId, string csv) => _app.WithScopeAsync(async sp =>
    {
        var db = sp.GetRequiredService<MbDbContext>();
        var banking = sp.GetRequiredService<BankingService>();
        var account = await db.BankAccount.FirstAsync(a => a.Id == accountId);
        var preview = banking.Read(Encoding.UTF8.GetBytes(csv), account);
        return await banking.ImportAsync(account, preview.Lines, "CSV", "test.csv", preview.Info, preview.Table, preview.Mapping);
    });

    [DatabaseFact]
    public async Task The_export_holds_the_invoices_the_receipts_and_the_lists()
    {
        await _app.EnsureAdminAsync();
        var (_, invoice, _) = await _app.CreateInvoiceWithPdfAsync("Export", 80m);
        var zip = await _app.WithScopeAsync(async sp =>
        {
            var today = DateOnly.FromDateTime(DateTime.Today);
            return await sp.GetRequiredService<ExportService>().ExportAsync(today.AddDays(-1), today.AddDays(1), withBank: true);
        });

        using var archive = new ZipArchive(new MemoryStream(zip.Zip));
        Assert.Contains(archive.Entries, e => e.FullName.Contains("/Rechnungen/", StringComparison.Ordinal) && e.FullName.Contains(invoice.Number!, StringComparison.Ordinal) && e.Name.EndsWith(".pdf", StringComparison.Ordinal));
        Assert.Contains(archive.Entries, e => e.Name == "Rechnungen.csv");
        Assert.Contains(archive.Entries, e => e.Name == "Ausgaben.csv");
        Assert.Contains(archive.Entries, e => e.Name == "Uebersicht.csv");
        Assert.Contains(archive.Entries, e => e.Name == "Kontoumsaetze.csv");
        using var reader = new StreamReader(archive.Entries.First(e => e.Name == "Rechnungen.csv").Open());
        Assert.Contains(invoice.Number!, await reader.ReadToEndAsync());
    }
}
