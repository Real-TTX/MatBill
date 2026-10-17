using MatBill.Configuration;
using MatBill.Core.Billing;
using MatBill.Data;
using MatBill.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace MatBill.Services;

/// <summary>Sample data for trying MatBill out: a small studio with customers, products, a few invoices and two series. Only into an empty installation.</summary>
public sealed class DemoData(MbDbContext db, ConfigStore config, NumberService numbers, InvoiceService invoices, RecurringService recurring, TimeProvider clock)
{
    public async Task<bool> IsEmptyAsync(CancellationToken ct = default)
        => !await db.Customer.AnyAsync(ct) && !await db.Invoice.AnyAsync(ct) && !await db.Product.AnyAsync(ct);

    public async Task<bool> CreateAsync(long userId, CancellationToken ct = default)
    {
        if (!await IsEmptyAsync(ct))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(config.Current.Company.Name))
        {
            config.Update(c =>
            {
                c.Company.Name = "nimbus studio";
                c.Company.Manager = "Inhaber: Jonas Dierksen";
                c.Company.Street = "Musterstraße 12";
                c.Company.PostalCode = "80331";
                c.Company.City = "München";
                c.Company.Email = "hallo@nimbus.studio";
                c.Company.Website = "www.nimbus.studio";
                c.Company.TaxNumber = "123/456/78901";
                c.Company.BankName = "Musterbank";
                c.Company.Iban = "DE12 3456 7890 1234 5678 90";
                c.Company.Bic = "MUSTDEM1XXX";
            });
        }

        var today = Fmt.Today(clock);
        var webdesign = new Product { Name = "Webdesign", Description = "Konzeption & Gestaltung", Unit = "Pauschal", UnitPrice = 1200m, Kind = ProductKind.Service };
        var consulting = new Product { Name = "Beratung", Description = "Beratungsleistung pro Stunde", Unit = "Stunde", UnitPrice = 150m, Kind = ProductKind.Service };
        var hosting = new Product { Name = "Hosting", Description = "Webhosting inkl. SSL und täglicher Sicherung", Unit = "Monat", UnitPrice = 29m, Kind = ProductKind.Service };
        var care = new Product { Name = "Wartung", Description = "Updates, Monitoring und kleine Änderungen", Unit = "Quartal", UnitPrice = 240m, Kind = ProductKind.Service };
        var book = new Product { Name = "Fachbuch „Gutes Design“", Unit = "Stück", UnitPrice = 39.90m, TaxRate = 7m, Kind = ProductKind.Product };
        db.Product.AddRange(webdesign, consulting, hosting, care, book);

        async Task<Customer> CustomerAsync(string name, string? contact, string street, string zip, string city, string email, CustomerKind kind = CustomerKind.Company)
        {
            var c = new Customer { Number = await numbers.NextCustomerNumberAsync(today, ct), Name = name, ContactName = contact, Street = street, PostalCode = zip, City = city, Email = email, Kind = kind };
            db.Customer.Add(c);
            return c;
        }

        var nord = await CustomerAsync("Studio Nord GmbH", "z. Hd. Frau Keller", "Hafenstraße 12", "68159", "Mannheim", "buchhaltung@studio-nord.example");
        var mueller = await CustomerAsync("Müller & Partner", null, "Berliner Straße 24", "69120", "Heidelberg", "info@mueller-partner.example");
        var alex = await CustomerAsync("Alex Schneider", null, "Waldweg 8", "68165", "Mannheim", "alex@schneider.example", CustomerKind.Person);
        await db.SaveChangesAsync(ct);

        static InvoiceLine Line(Product p, decimal quantity) => new() { ProductId = p.Id, Name = p.Name, Description = p.Description, Unit = p.Unit, UnitPrice = p.UnitPrice, TaxRate = p.TaxRate, Quantity = quantity };

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

        var paid = await InvoiceAsync(mueller, today.AddDays(-45), true, Line(webdesign, 1), Line(consulting, 4));
        await invoices.MarkPaidAsync(paid, today.AddDays(-30), ct);
        await InvoiceAsync(alex, today.AddDays(-25), true, Line(consulting, 2), Line(book, 1));
        var first = await InvoiceAsync(nord, today.AddDays(-3), true, Line(webdesign, 1), Line(consulting, 2));
        await InvoiceAsync(mueller, today, false, Line(consulting, 6));

        var hostingSeries = recurring.FromInvoice(new Invoice
        {
            CustomerId = nord.Id, RecipientName = nord.Name, Title = "Rechnung", PaymentTermDays = 14,
            IntroText = config.Current.Invoicing.IntroText, PaymentTerms = config.Current.Invoicing.PaymentTerms, ClosingText = config.Current.Invoicing.ClosingText,
            Lines = [Line(hosting, 1)],
        }, RecurrenceInterval.Monthly, today.AddDays(5), null, autoSend: false);
        hostingSeries.Name = "Studio Nord: Hosting";
        var careSeries = recurring.FromInvoice(first, RecurrenceInterval.Quarterly, today.AddDays(20), today.AddYears(2), autoSend: false);
        careSeries.Name = "Studio Nord: Wartung";
        careSeries.Lines = [InvoiceService.CopyLine(Line(care, 1), new RecurringInvoiceLine { Position = 1 })];
        db.RecurringInvoice.AddRange(hostingSeries, careSeries);
        await db.SaveChangesAsync(ct);
        return true;
    }
}
