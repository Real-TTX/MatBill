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
    public void The_ready_made_texts_use_only_known_placeholders_and_due_at_once_is_a_text_of_its_own()
    {
        var invoicing = new InvoicingConfig();
        var all = DocumentTextPresets.Terms(invoicing).Concat(DocumentTextPresets.QuoteTerms(invoicing)).Concat(DocumentTextPresets.Intro(invoicing))
            .Concat(DocumentTextPresets.QuoteIntro(invoicing)).Concat(DocumentTextPresets.Closing(invoicing)).ToList();
        var known = new[] { "Days", "DueDate", "ValidUntil" };
        foreach (var preset in all)
        {
            Assert.False(string.IsNullOrWhiteSpace(preset.Text), preset.Label);
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(preset.Text, @"\{(\w+)\}"))
            {
                Assert.Contains(m.Groups[1].Value, known);
            }
        }

        // "due at once" says so in words and sets the term to 0; it never reads "within 0 days"
        var immediately = DocumentTextPresets.Terms(invoicing).Single(p => p.Label == "Editor.Preset.Terms.Immediately");
        Assert.Equal(0, immediately.Days);
        Assert.DoesNotContain("{Days}", immediately.Text);
        Assert.Contains("sofort", Texts.Expand(immediately.Text, 0, new DateOnly(2026, 10, 20)));

        // the first entry is the standard of the settings
        Assert.Equal(invoicing.PaymentTerms, DocumentTextPresets.Terms(invoicing)[0].Text);
        Assert.Equal(invoicing.QuoteIntroText, DocumentTextPresets.QuoteIntro(invoicing)[0].Text);
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
    [InlineData(DocumentLayouts.Classic, "left", DocumentLogoSizes.Medium, 16)]
    [InlineData(DocumentLayouts.Centered, "middle", DocumentLogoSizes.Medium, 16)]
    [InlineData(DocumentLayouts.Right, "right", DocumentLogoSizes.Medium, 16)]
    [InlineData(DocumentLayouts.Classic, "left", DocumentLogoSizes.Small, 12)]
    [InlineData(DocumentLayouts.Centered, "middle", DocumentLogoSizes.Large, 22)]
    [InlineData(DocumentLayouts.Right, "right", DocumentLogoSizes.Large, 22)]
    public void The_logo_stands_where_the_layout_says_and_is_as_large_as_its_size_says(string layout, string where, string size, double height)
    {
        var config = new AppConfig();
        config.Company.Name = "Nordlicht Design";
        config.Company.Layout = layout;
        config.Company.LogoSize = size;
        var logo = new LogoImage(File.ReadAllBytes(Path.Combine(RepoRoot(), "src", "MatBill", "wwwroot", "img", "icon-512.png")), IsSvg: false);
        var sample = SampleDocument.Of(config, null);
        using var document = PdfDocument.Open(InvoicePdf.Render(sample.Doc, sample.Company, logo));
        var page = document.GetPage(1);
        var image = Assert.Single(page.GetImages());
        var box = image.Bounds;
        var middle = (box.Left + box.Right) / 2;
        const double mm = 72 / 25.4;
        Assert.InRange(box.Height, height * mm - 1, height * mm + 1);
        switch (where)
        {
            case "middle":
                Assert.InRange(middle, page.Width / 2 - 1, page.Width / 2 + 1);
                break;
            case "left":
                Assert.InRange(box.Left, 20 * mm - 1, 20 * mm + 1);
                break;
            default:
                Assert.InRange(box.Right, page.Width - 20 * mm - 1, page.Width - 20 * mm + 1);
                break;
        }
    }

    [Theory]
    // the word mark of the demo: a list with a quoted name in a double-quoted attribute
    [InlineData("<svg><text font-family=\"Inter, 'Segoe UI', system-ui, -apple-system, sans-serif\">A</text></svg>", "<svg><text font-family=\"Lato\">A</text></svg>")]
    [InlineData("<svg><text font-family='Helvetica, Arial, sans-serif'>A</text></svg>", "<svg><text font-family='Lato'>A</text></svg>")]
    [InlineData("<svg><text style=\"font-family: 'Segoe UI', sans-serif; font-weight: 700\">A</text></svg>", "<svg><text style=\"font-family: Lato; font-weight: 700\">A</text></svg>")]
    [InlineData("<svg><style>text { font-family: Inter, system-ui }</style><text>A</text></svg>", "<svg><style>text { font-family: Lato}</style><text>A</text></svg>")]
    // a serif or a monospace list stays, an SVG without any font gets Lato as its default
    [InlineData("<svg><text font-family=\"Georgia, serif\">A</text></svg>", "<svg><text font-family=\"Georgia, serif\">A</text></svg>")]
    [InlineData("<svg viewBox=\"0 0 1 1\"><text>A</text></svg>", "<svg font-family=\"Lato\" viewBox=\"0 0 1 1\"><text>A</text></svg>")]
    public void A_logo_in_svg_that_asks_for_a_sans_serif_font_gets_lato_in_the_pdf(string svg, string expected)
        => Assert.Equal(expected, InvoicePdf.SvgForPdf(svg));

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
