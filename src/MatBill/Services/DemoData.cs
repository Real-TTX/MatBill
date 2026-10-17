using MatBill.Configuration;
using MatBill.Core.Banking;
using MatBill.Core.Billing;
using MatBill.Data;
using MatBill.Data.Entities;
using MatBill.Storage;
using Microsoft.EntityFrameworkCore;

namespace MatBill.Services;

/// <summary>
/// Sample data for trying MatBill out, into an empty installation only: the company "MatBill Demo" with the MatBill logo, customers, products, quotes, invoices
/// (one of them cancelled), two series, expenses and a bank account whose bookings pay some of them (assigned by the matcher).
/// </summary>
public sealed class DemoData(MbDbContext db, ConfigStore config, NumberService numbers, InvoiceService invoices, QuoteService quotes, RecurringService recurring,
    BankingService banking, FileStoreService files, IWebHostEnvironment env, TimeProvider clock)
{
    public const string CompanyName = "MatBill Demo";

    public async Task<bool> IsEmptyAsync(CancellationToken ct = default)
        => !await db.Customer.AnyAsync(ct) && !await db.Invoice.AnyAsync(ct) && !await db.Product.AnyAsync(ct);

    public async Task<bool> CreateAsync(long userId, CancellationToken ct = default)
    {
        if (!await IsEmptyAsync(ct))
        {
            return false;
        }

        await CompanyAsync(ct);
        var today = Fmt.Today(clock);

        var webdesign = new Product { Name = "Webdesign", Description = "Konzeption & Gestaltung", Unit = "Pauschal", UnitPrice = 1200m, Kind = ProductKind.Service };
        var consulting = new Product { Name = "Beratung", Description = "Beratungsleistung pro Stunde", Unit = "Stunde", UnitPrice = 150m, Kind = ProductKind.Service };
        var hosting = new Product { Name = "Hosting", Description = "Webhosting inkl. SSL und täglicher Sicherung", Unit = "Monat", UnitPrice = 29m, Kind = ProductKind.Service };
        var care = new Product { Name = "Wartung", Description = "Updates, Monitoring und kleine Änderungen", Unit = "Quartal", UnitPrice = 240m, Kind = ProductKind.Service };
        var book = new Product { Name = "Fachbuch „Gutes Design“", Unit = "Stück", UnitPrice = 39.90m, TaxRate = 7m, Kind = ProductKind.Product };
        db.Product.AddRange(webdesign, consulting, hosting, care, book);

        async Task<Customer> CustomerAsync(string name, string? contact, string street, string zip, string city, string email, CustomerKind kind = CustomerKind.Company, string? leitwegId = null)
        {
            var c = new Customer { Number = await numbers.NextCustomerNumberAsync(today, ct), Name = name, ContactName = contact, Street = street, PostalCode = zip, City = city, Email = email, Kind = kind, BuyerReference = leitwegId };
            db.Customer.Add(c);
            return c;
        }

        var nord = await CustomerAsync("Studio Nord GmbH", "z. Hd. Frau Keller", "Hafenstraße 12", "68159", "Mannheim", "buchhaltung@studio-nord.example");
        var mueller = await CustomerAsync("Müller & Partner", null, "Berliner Straße 24", "69120", "Heidelberg", "info@mueller-partner.example");
        var alex = await CustomerAsync("Alex Schneider", null, "Waldweg 8", "68165", "Mannheim", "alex@schneider.example", CustomerKind.Person);
        var city = await CustomerAsync("Stadtverwaltung Musterstadt", "Amt für Digitales", "Rathausplatz 1", "12345", "Musterstadt", "rechnung@musterstadt.example", leitwegId: "991-12345-67");
        await db.SaveChangesAsync(ct);

        static T Line<T>(Product p, decimal quantity) where T : LineBase, new()
            => new() { ProductId = p.Id, Name = p.Name, Description = p.Description, Unit = p.Unit, UnitPrice = p.UnitPrice, TaxRate = p.TaxRate, Quantity = quantity };

        async Task<Invoice> InvoiceAsync(Customer customer, DateOnly date, bool issue, params InvoiceLine[] lines)
        {
            var invoice = invoices.NewDraft(customer);
            invoice.InvoiceDate = date;
            invoice.ServiceFrom = date;
            invoice.DueDate = InvoiceMath.DueDate(date, invoice.PaymentTermDays);
            invoice.Lines = [.. lines.Select((l, i) => { l.Position = i + 1; return l; })];
            InvoiceService.Recalculate(invoice);
            db.Invoice.Add(invoice);
            await db.SaveChangesAsync(ct);
            if (issue)
            {
                await invoices.IssueAsync(invoice, ct);
            }

            return invoice;
        }

        // Quotes: one accepted and turned into an invoice, one open, one draft.
        async Task<Quote> QuoteAsync(Customer customer, DateOnly date, bool issue, params QuoteLine[] lines)
        {
            var quote = quotes.NewDraft(customer);
            quote.QuoteDate = date;
            quote.ValidUntil = date.AddDays(quote.ValidityDays);
            quote.Lines = [.. lines.Select((l, i) => { l.Position = i + 1; return l; })];
            QuoteService.Recalculate(quote);
            db.Quote.Add(quote);
            await db.SaveChangesAsync(ct);
            if (issue)
            {
                await quotes.IssueAsync(quote, ct);
            }

            return quote;
        }

        var won = await QuoteAsync(mueller, today.AddDays(-60), true, Line<QuoteLine>(webdesign, 1), Line<QuoteLine>(consulting, 4));
        await quotes.AnswerAsync(won, accepted: true, ct);
        await QuoteAsync(city, today.AddDays(-6), true, Line<QuoteLine>(webdesign, 2), Line<QuoteLine>(care, 4));
        await QuoteAsync(alex, today, false, Line<QuoteLine>(consulting, 3));

        var paid = await InvoiceAsync(mueller, today.AddDays(-45), true, Line<InvoiceLine>(webdesign, 1), Line<InvoiceLine>(consulting, 4));
        paid.QuoteId = won.Id;
        won.InvoiceId = paid.Id;
        won.Status = QuoteStatus.Invoiced;
        var overdue = await InvoiceAsync(alex, today.AddDays(-25), true, Line<InvoiceLine>(consulting, 2), Line<InvoiceLine>(book, 1));
        var first = await InvoiceAsync(nord, today.AddDays(-3), true, Line<InvoiceLine>(webdesign, 1), Line<InvoiceLine>(consulting, 2));
        var wrong = await InvoiceAsync(nord, today.AddDays(-20), true, Line<InvoiceLine>(consulting, 10));
        await invoices.CancelAsync(wrong, "Stundenzahl falsch berechnet", ct);
        await InvoiceAsync(mueller, today, false, Line<InvoiceLine>(consulting, 6));
        await db.SaveChangesAsync(ct);

        var hostingSeries = recurring.FromInvoice(new Invoice
        {
            CustomerId = nord.Id, RecipientName = nord.Name, Title = "Rechnung", PaymentTermDays = 14,
            IntroText = config.Current.Invoicing.IntroText, PaymentTerms = config.Current.Invoicing.PaymentTerms, ClosingText = config.Current.Invoicing.ClosingText,
            Lines = [Line<InvoiceLine>(hosting, 1)],
        }, RecurrenceInterval.Monthly, today.AddDays(5), null, autoSend: false);
        hostingSeries.Name = "Studio Nord: Hosting";
        var careSeries = recurring.FromInvoice(first, RecurrenceInterval.Quarterly, today.AddDays(20), today.AddYears(2), autoSend: false);
        careSeries.Name = "Studio Nord: Wartung";
        careSeries.Lines = [Line<RecurringInvoiceLine>(care, 1)];
        db.RecurringInvoice.AddRange(hostingSeries, careSeries);

        // Expenses: some paid by the bank account below, one waiting for its receipt to be checked.
        async Task<Expense> ExpenseAsync(string supplier, string category, DateOnly date, decimal gross, decimal rate, string? number, string? description)
        {
            var (net, tax, g) = ExpenseService.Amounts(null, gross, rate);
            var e = new Expense
            {
                Number = await numbers.NextExpenseNumberAsync(date, ct), ExpenseDate = date, Supplier = supplier, Category = category, GrossAmount = g, NetAmount = net, TaxAmount = tax,
                TaxRate = rate, SupplierInvoiceNumber = number, Description = description,
            };
            db.Expense.Add(e);
            return e;
        }

        await ExpenseAsync("Hostingwerk GmbH", "Software und Abos", today.AddDays(-28), 23.80m, 19m, "HW-88213", "Server für Kundenprojekte");
        await ExpenseAsync("Bürowelt Mannheim", "Büro und Material", today.AddDays(-15), 89.95m, 19m, "BW-2026-1177", "Papier, Toner");
        await ExpenseAsync("Telekom Deutschland GmbH", "Telefon und Internet", today.AddDays(-10), 59.95m, 19m, "TK-554210", "Internet Büro");
        var review = await ExpenseAsync("Deutsche Bahn", "Reisen", today.AddDays(-4), 74.60m, 7m, null, "Fahrt zum Kunden");
        review.NeedsReview = true;
        await db.SaveChangesAsync(ct);

        // A bank account with the bookings of the last weeks: the payments of invoices and expenses are found by the matcher.
        var account = new BankAccount { Name = "Geschäftskonto", BankName = "Musterbank", Iban = "DE12345678901234567890", Provider = BankProvider.File };
        db.BankAccount.Add(account);
        await db.SaveChangesAsync(ct);
        var lines = new List<StatementLine>
        {
            new(paid.InvoiceDate.AddDays(12), null, paid.GrossTotal, "EUR", "Müller und Partner", "DE44500105175407324931", $"Rechnung {paid.Number} Danke", "DEMO-1"),
            new(today.AddDays(-27), null, -23.80m, "EUR", "Hostingwerk GmbH", "DE02120300000000202051", "HW-88213 Server", "DEMO-2"),
            new(today.AddDays(-14), null, -89.95m, "EUR", "Bürowelt Mannheim", "DE02100100100006820101", "Rechnung BW-2026-1177", "DEMO-3"),
            new(today.AddDays(-9), null, -59.95m, "EUR", "Telekom Deutschland GmbH", "DE89370400440532013000", "Kundennr 1234 Rechnung TK-554210", "DEMO-4"),
            new(today.AddDays(-8), null, -1500m, "EUR", "Finanzamt Mannheim", "DE88600000000060001500", "USt-Voranmeldung", "DEMO-5"),
            new(today.AddDays(-2), null, -12.49m, "EUR", "Café am Markt", null, "Kartenzahlung", "DEMO-6"),
            new(today.AddDays(-1), null, 500m, "EUR", "Alex Schneider", "DE91100000000123456789", "Anzahlung", "DEMO-7"),
        };
        await banking.ImportAsync(account, lines, "Beispieldaten", "beispiel.csv", new StatementInfo(account.Iban, 12873.40m, today), ct: ct);
        _ = overdue;
        _ = userId;
        return true;
    }

    /// <summary>The demo company with the MatBill word mark as its logo (only where no company was entered yet).</summary>
    private async Task CompanyAsync(CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(config.Current.Company.Name) && config.Current.Company.Name != CompanyName)
        {
            return;
        }

        string? logoToken = null;
        var logo = env.WebRootFileProvider.GetFileInfo("logo-wordmark.svg");
        if (logo.Exists)
        {
            await using var stream = logo.CreateReadStream();
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, ct);
            var file = await files.SaveAsync(buffer.ToArray(), "logo.svg", "image/svg+xml", ct);
            await db.SaveChangesAsync(ct);
            logoToken = file.Token.ToString();
        }

        config.Update(c =>
        {
            c.Company.Name = CompanyName;
            c.Company.Manager = "Geschäftsführer: Max Mustermann";
            c.Company.Street = "Musterstraße 12";
            c.Company.PostalCode = "80331";
            c.Company.City = "München";
            c.Company.Email = "hallo@matbill.example";
            c.Company.Phone = "089 1234567";
            c.Company.Website = "www.matbill.example";
            c.Company.TaxNumber = "143/123/45678";
            c.Company.VatId = "DE123456789";
            c.Company.Register = "Amtsgericht München, HRB 123456";
            c.Company.BankName = "Musterbank";
            c.Company.Iban = "DE12 3456 7890 1234 5678 90";
            c.Company.Bic = "MUSTDEM1XXX";
            c.Company.AccentColor = "#1d4ed8";
            c.Company.LogoToken = logoToken ?? c.Company.LogoToken;
        });
    }
}
