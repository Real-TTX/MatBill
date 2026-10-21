using MatBill.Configuration;
using MatBill.Pages;
using MatBill.Services;
using UglyToad.PdfPig;

namespace MatBill.Tests.Core;

/// <summary>The address of a document stands in the window of the envelope (DIN 5008 B: 45 to 90 mm from the top, from 20 mm on the left), whatever the logo.</summary>
public sealed class WindowTests
{
    static WindowTests() => QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;

    public static TheoryData<string, string, bool, string> Cases()
    {
        var data = new TheoryData<string, string, bool, string>();
        foreach (var layout in DocumentLayouts.All)
        {
            foreach (var size in DocumentLogoSizes.All)
            {
                foreach (var design in DocumentDesigns.All)
                {
                    data.Add(layout, size, true, design);
                }
            }

            data.Add(layout, DocumentLogoSizes.Medium, false, DocumentDesigns.Classic);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void The_sender_line_and_the_recipient_stand_in_the_window_of_the_envelope(string layout, string size, bool withLogo, string design)
    {
        var config = new AppConfig();
        config.Company.Name = "Nordlicht Design";
        config.Company.Street = "Weg 1";
        config.Company.PostalCode = "12345";
        config.Company.City = "Stadt";
        config.Company.Layout = layout;
        config.Company.LogoSize = size;
        config.Company.Design = design;
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(dir!.FullName, "MatBill.slnx")))
        {
            dir = dir.Parent;
        }

        var logo = withLogo ? new LogoImage(File.ReadAllBytes(Path.Combine(dir.FullName, "src", "MatBill", "wwwroot", "img", "icon-512.png")), IsSvg: false) : null;
        var sample = SampleDocument.Of(config, null);
        using var document = PdfDocument.Open(InvoicePdf.Render(sample.Doc, sample.Company, logo));
        var page = document.GetPage(1);
        const double mm = 72 / 25.4;
        var words = page.GetWords().ToList();
        double FromTop(double y) => (page.Height - y) / mm;
        // the sender line is the first line with the street (the footer has it too, far below)
        var sender = words.Where(w => w.Text == "Weg").MinBy(w => FromTop(w.BoundingBox.Top))!;
        var city = words.First(w => w.Text == "Mannheim");
        Assert.InRange(FromTop(sender.BoundingBox.Top), 45, 62.7);
        Assert.InRange(FromTop(city.BoundingBox.Bottom), 50, 90);
        Assert.True(words.First(w => w.Text == "Studio").BoundingBox.Left / mm >= 20 - 0.5);
    }
}
