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

    [Theory]
    [InlineData(DocumentLayouts.Classic, "left")]
    [InlineData(DocumentLayouts.Centered, "middle")]
    [InlineData(DocumentLayouts.Right, "right")]
    public void The_logo_stands_where_the_layout_says_and_in_the_middle_it_is_larger(string layout, string where)
    {
        var config = new AppConfig();
        config.Company.Name = "Nordlicht Design";
        config.Company.Layout = layout;
        var logo = new LogoImage(File.ReadAllBytes(Path.Combine(RepoRoot(), "src", "MatBill", "wwwroot", "img", "icon-512.png")), IsSvg: false);
        var sample = SampleDocument.Of(config, null);
        using var document = PdfDocument.Open(InvoicePdf.Render(sample.Doc, sample.Company, logo));
        var page = document.GetPage(1);
        var image = Assert.Single(page.GetImages());
        var box = image.Bounds;
        var middle = (box.Left + box.Right) / 2;
        const double mm = 72 / 25.4;
        switch (where)
        {
            case "middle":
                Assert.InRange(middle, page.Width / 2 - 1, page.Width / 2 + 1);
                Assert.InRange(box.Height, 22 * mm - 1, 22 * mm + 1);
                break;
            case "left":
                Assert.InRange(box.Left, 20 * mm - 1, 20 * mm + 1);
                Assert.InRange(box.Height, 16 * mm - 1, 16 * mm + 1);
                break;
            default:
                Assert.InRange(box.Right, page.Width - 20 * mm - 1, page.Width - 20 * mm + 1);
                break;
        }
    }

    [Fact]
    public void The_standard_footer_is_its_template_filled_in_and_an_unchanged_template_is_the_standard()
    {
        var company = new CompanyConfig { Name = "Nordlicht Design", Street = "Weg 1", PostalCode = "12345", City = "Stadt", Phone = "0123 456", BankName = "Bank A", Iban = "DE11", AccountHolder = "Erika Mustermann", TaxNumber = "12/345" };
        Assert.Equal(
            [["Nordlicht Design", "Weg 1", "12345 Stadt"], ["Tel. 0123 456"], ["Bank A", "IBAN DE11", "Kontoinhaber: Erika Mustermann"], ["Steuernummer 12/345"]],
            DocumentFooter.Columns(company));
        // the fields of Branding post the template with the line ends of the browser
        Assert.True(DocumentFooter.IsDefault([.. DocumentFooter.Default.Select(c => c.Replace("\n", "\r\n") + " ")]));
        Assert.False(DocumentFooter.IsDefault([.. DocumentFooter.Default.Take(3), "Steuernummer {TaxNumber}"]));
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MatBill.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("MatBill.slnx not found above " + AppContext.BaseDirectory);
    }
}
