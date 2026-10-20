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

    [Theory]
    [InlineData(DocumentLayouts.Classic)]
    [InlineData(DocumentLayouts.Centered)]
    [InlineData(DocumentLayouts.Right)]
    public void Every_layout_of_the_head_draws_the_sender(string layout)
    {
        var config = new AppConfig();
        config.Company.Name = "Matthix";
        config.Company.NameLine2 = "Matthias Schmoldt";
        config.Company.Street = "Weg 1";
        config.Company.PostalCode = "12345";
        config.Company.City = "Stadt";
        config.Company.Layout = layout;
        var sample = SampleDocument.Of(config, null);
        var pdf = InvoicePdf.Render(sample.Doc, sample.Company, null);
        using var document = PdfDocument.Open(pdf);
        var text = string.Join(" ", document.GetPages().Select(p => p.Text));
        Assert.Contains("Matthias", text);
        Assert.Contains("Schmoldt", text);
        Assert.Single(InvoicePdf.RenderImages(sample.Doc, sample.Company, null));
    }
}
