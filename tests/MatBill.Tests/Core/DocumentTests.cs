using MatBill.Configuration;
using MatBill.Pages;
using MatBill.Services;
using UglyToad.PdfPig;

namespace MatBill.Tests.Core;

/// <summary>The texts and the head of a document: payment terms that read well for 0 and 1 days, and every layout of the head in the PDF.</summary>
public sealed class DocumentTests
{
    static DocumentTests() => QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;

    private const string Terms = "Bitte überweisen Sie den Rechnungsbetrag innerhalb von {Days} Tagen auf das unten angegebene Konto.";

    [Fact]
    public void Payment_terms_say_at_once_for_zero_days_and_one_day_for_one()
    {
        Assert.Equal("Bitte überweisen Sie den Rechnungsbetrag sofort auf das unten angegebene Konto.", Texts.Expand(Terms, 0, new DateOnly(2026, 10, 20)));
        Assert.Equal("Bitte überweisen Sie den Rechnungsbetrag innerhalb von 1 Tag auf das unten angegebene Konto.", Texts.Expand(Terms, 1, new DateOnly(2026, 10, 21)));
        Assert.Equal("Bitte überweisen Sie den Rechnungsbetrag innerhalb von 14 Tagen auf das unten angegebene Konto.", Texts.Expand(Terms, 14, new DateOnly(2026, 11, 3)));
        Assert.Equal("Please pay immediately.", Texts.Expand("Please pay within {Days} days.", 0, null));
        Assert.Equal("Zahlbar bis 20.10.2026.", Texts.Expand("Zahlbar bis {DueDate}.", 0, new DateOnly(2026, 10, 20)));
    }

    [Fact]
    public void The_footer_carries_the_address_and_a_second_bank_and_can_be_written_with_placeholders()
    {
        var company = new CompanyConfig { Name = "Nordlicht Design", NameLine2 = "Erika Mustermann", Street = "Weg 1", PostalCode = "12345", City = "Stadt", Phone = "0123 456", BankName = "Bank A", Iban = "DE11", Bank2Name = "Bank B", Bank2Iban = "DE22", TaxNumber = "12/345" };

        // made from the data: the full address in lines, both accounts in the bank column
        var automatic = DocumentFooter.Columns(company);
        Assert.Equal(["Nordlicht Design", "Erika Mustermann", "Weg 1", "12345 Stadt"], automatic[0]);
        Assert.Equal(["Bank A", "IBAN DE11", "Bank B", "IBAN DE22"], automatic[2]);

        // written by hand: placeholders filled in, a line whose placeholders are all empty left out, an empty column left out
        company.FooterColumns = ["{Name} · {City}\nBIC {Bic}\nInhaber: {NameLine2}", "", "IBAN {Iban}\nIBAN {Bank2Iban}\n{Bank2Bic}"];
        var written = DocumentFooter.Columns(company);
        Assert.Equal(2, written.Count);
        Assert.Equal(["Nordlicht Design · Stadt", "Inhaber: Erika Mustermann"], written[0]);
        Assert.Equal(["IBAN DE11", "IBAN DE22"], written[1]);
    }

    [Theory]
    [InlineData(DocumentLayouts.Classic)]
    [InlineData(DocumentLayouts.Centered)]
    [InlineData(DocumentLayouts.Right)]
    public void Every_layout_of_the_head_draws_the_sender(string layout)
    {
        var config = new AppConfig();
        config.Company.Name = "Nordlicht Design";
        config.Company.NameLine2 = "Erika Mustermann";
        config.Company.Street = "Weg 1";
        config.Company.PostalCode = "12345";
        config.Company.City = "Stadt";
        config.Company.Layout = layout;
        var sample = SampleDocument.Of(config, null);
        var pdf = InvoicePdf.Render(sample.Doc, sample.Company, null);
        using var document = PdfDocument.Open(pdf);
        var text = string.Join(" ", document.GetPages().Select(p => p.Text));
        Assert.Contains("Erika", text);
        Assert.Contains("Mustermann", text);
        Assert.Single(InvoicePdf.RenderImages(sample.Doc, sample.Company, null));
    }
}
