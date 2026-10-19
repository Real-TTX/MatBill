using MatBill.Configuration;
using MatBill.Core.Billing;
using MatBill.Services;

namespace MatBill.Pages;

/// <summary>A sample invoice in the saved look of the company: the preview beside the company data and the branding.</summary>
public static class SampleDocument
{
    public static DocumentViewModel Of(AppConfig config, string? logoUrl)
    {
        var company = config.Company;
        var today = DateOnly.FromDateTime(DateTime.Today);
        var lines = new List<DocumentLine>
        {
            new(1, "Webdesign", "Konzeption & Gestaltung", 1, "Pauschal", 1200m, 19m, 1200m),
            new(2, "Beratung", "Beratungsleistung pro Stunde", 2, "Stunde", 150m, 19m, 300m),
        };
        return new DocumentViewModel(new InvoiceDocument
        {
            Number = "RE-" + today.Year + "-0042",
            CustomerNumber = "K-0001",
            InvoiceDate = today,
            DueDate = today.AddDays(14),
            PaymentTermDays = 14,
            ServiceFrom = today,
            // The sample is a (German) document, not interface: the file is one of the German files of TextsTests.
            RecipientLines = ["Studio Nord GmbH", "z. Hd. Frau Keller", "Hafenstraße 12", "68159 Mannheim"],
            IntroText = config.Invoicing.IntroText,
            PaymentTerms = Texts.Expand(config.Invoicing.PaymentTerms, 14, today.AddDays(14)),
            ClosingText = config.Invoicing.ClosingText,
            Lines = lines,
            Totals = InvoiceMath.Totals(lines.Select(l => new LineAmounts(l.Quantity, l.UnitPrice, l.TaxRate)), company.SmallBusiness),
            TaxExempt = company.SmallBusiness,
            TaxExemptNote = config.Invoicing.SmallBusinessNote,
        }, company, logoUrl);
    }
}
