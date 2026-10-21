using System.Text;
using MatBill.Configuration;
using MatBill.Core.Billing;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace MatBill.Services;

/// <summary>The logo of the company as the PDF needs it: the bytes and whether they are an SVG.</summary>
public sealed record LogoImage(byte[] Bytes, bool IsSvg);

/// <summary>
/// Draws an invoice as an A4 PDF in the layout of the document editor: logo and sender at the top, the address field where a window envelope shows it
/// (DIN 5008), the facts of the invoice beside it, the positions, the totals, the payment terms and a footer with address, bank and tax numbers on every page.
/// </summary>
public static partial class InvoicePdf
{
    private const string Ink = "#1f2937";
    private const string Muted = "#6b7280";
    private const string Line = "#e5e7eb";
    private const string Soft = "#f5f6f8";


    /// <summary>
    /// The SVG of a logo as the PDF draws it. The text of an SVG is set in the fonts of the system only (not in those of QuestPDF), and the server has none of the
    /// fonts of a browser: the text of a word mark came out in a serif font in the PDF while the editor showed it without serifs. A list of families that ends in a
    /// generic sans-serif family becomes Lato, the font of the document, which the container installs as a font of the system (Dockerfile); an SVG that names no
    /// font gets Lato as its default. A serif or a monospace list stays as it is.
    /// </summary>
    public static string SvgForPdf(string svg)
    {
        var fixedLists = FontFamilyRegex().Replace(svg, m =>
        {
            var value = m.Groups["dq"].Success ? m.Groups["dq"] : m.Groups["sq"].Success ? m.Groups["sq"] : m.Groups["css"];
            return SansSerifRegex().IsMatch(value.Value) ? m.Value[..(value.Index - m.Index)] + "Lato" + m.Value[(value.Index - m.Index + value.Length)..] : m.Value;
        });
        return fixedLists.Contains("font-family", StringComparison.OrdinalIgnoreCase) ? fixedLists : SvgTagRegex().Replace(fixedLists, "<svg font-family=\"Lato\"", 1);
    }

    // font-family="…" and font-family='…' as an attribute (the value may hold the other quote), font-family: …; in a style
    [System.Text.RegularExpressions.GeneratedRegex("""font-family\s*(?:=\s*"(?<dq>[^"]*)"|=\s*'(?<sq>[^']*)'|:\s*(?<css>[^;}"<]*))""", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex FontFamilyRegex();

    [System.Text.RegularExpressions.GeneratedRegex(@"(^|,)\s*['""]?(sans-serif|system-ui|ui-sans-serif|-apple-system|BlinkMacSystemFont)['""]?\s*$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex SansSerifRegex();

    [System.Text.RegularExpressions.GeneratedRegex(@"<svg(?=[\s>])", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex SvgTagRegex();

    /// <summary>The PDF; with <paramref name="pdfA"/> it is PDF/A-3b, the archive format that a ZUGFeRD e-invoice is made of (<see cref="EInvoice"/> adds the XML).</summary>
    public static byte[] Render(InvoiceDocument doc, CompanyConfig company, LogoImage? logo, bool pdfA = false)
        => Build(doc, company, logo, pdfA).GeneratePdf();

    /// <summary>The pages as PNG images (<paramref name="dpi"/> 96: an A4 page is 794 pixels wide, the width of the paper in the editor).</summary>
    public static IEnumerable<byte[]> RenderImages(InvoiceDocument doc, CompanyConfig company, LogoImage? logo, int dpi = 96)
        => Build(doc, company, logo, false).GenerateImages(new ImageGenerationSettings { ImageFormat = ImageFormat.Png, RasterDpi = dpi });

    private static IDocument Build(InvoiceDocument doc, CompanyConfig company, LogoImage? logo, bool pdfA)
    {
        var accent = IsColour(company.AccentColor) ? company.AccentColor : "#1d4ed8";
        return Document.Create(container => container.Page(page =>
        {
            page.Size(PageSizes.A4);
            page.MarginHorizontal(20, Unit.Millimetre);
            page.MarginTop(15, Unit.Millimetre);
            page.MarginBottom(12, Unit.Millimetre);
            page.PageColor(Colors.White);
            page.DefaultTextStyle(x => x.FontSize(9.5f).FontColor(Ink).LineHeight(1.35f));

            if (doc.Stamp is { } stamp)
            {
                // behind the content, never over it: the text stays readable, and the stamp is as light as on the paper in the editor
                page.Background().AlignCenter().AlignMiddle().Rotate(-30).Text(stamp).FontSize(64).Bold().FontColor("#f1f2f5");
            }

            var design = DocumentDesigns.Of(company.Design);
            page.Content().Column(col =>
            {
                // the head has a fixed height, so the address stands in the window of the envelope whatever the size of the logo (DIN 5008 B: 45 to 90 mm from the top)
                col.Item().Height(HeadHeight, Unit.Millimetre).Element(c => Head(c, company, logo));
                col.Item().PaddingTop(AddressGap, Unit.Millimetre).Element(c => AddressAndFacts(c, doc, company));

                if (!string.IsNullOrWhiteSpace(doc.IntroText))
                {
                    col.Item().PaddingTop(10, Unit.Millimetre).Text(doc.IntroText.Trim());
                }

                col.Item().PaddingTop(6, Unit.Millimetre).Element(c => Positions(c, doc, design, accent));
                col.Item().PaddingTop(4, Unit.Millimetre).AlignRight().Width(80, Unit.Millimetre).Element(c => Totals(c, doc, design, accent));

                if (doc.TaxExempt && !string.IsNullOrWhiteSpace(doc.TaxExemptNote))
                {
                    col.Item().PaddingTop(3, Unit.Millimetre).Text(doc.TaxExemptNote).FontSize(8.5f).FontColor(Muted);
                }

                if (!string.IsNullOrWhiteSpace(doc.PaymentTerms))
                {
                    var terms = col.Item().PaddingTop(8, Unit.Millimetre);
                    terms = design switch
                    {
                        DocumentDesigns.Lines => terms.BorderTop(0.5f).BorderBottom(0.5f).BorderColor(Line).PaddingVertical(3, Unit.Millimetre),
                        DocumentDesigns.Accent => terms.BorderLeft(1.2f, Unit.Millimetre).BorderColor(accent).Background(Soft).Padding(4, Unit.Millimetre),
                        _ => terms.Background(Soft).Padding(4, Unit.Millimetre),
                    };
                    terms.Column(box =>
                    {
                        box.Item().Text(doc.TermsLabel).SemiBold();
                        box.Item().PaddingTop(2).Text(doc.PaymentTerms.Trim()).FontColor("#374151");
                    });
                }

                if (!string.IsNullOrWhiteSpace(doc.ClosingText) || !string.IsNullOrWhiteSpace(company.Signature))
                {
                    col.Item().PaddingTop(8, Unit.Millimetre).Row(row =>
                    {
                        row.RelativeItem().Text((doc.ClosingText ?? "").Trim());
                        if (!string.IsNullOrWhiteSpace(company.Signature))
                        {
                            row.AutoItem().AlignBottom().Text(company.Signature).Italic().FontSize(16).FontColor(accent);
                        }
                    });
                }
            });

            page.Footer().Element(c => Footer(c, company, accent));
        }))
        .WithMetadata(new DocumentMetadata { Title = $"{doc.Title} {doc.Number}".Trim(), Author = company.Name, Subject = doc.Title, Creator = "MatBill", Language = "de-DE" })
        .WithSettings(pdfA ? new DocumentSettings { PDFA_Conformance = PDFA_Conformance.PDFA_3B } : new DocumentSettings());
    }

    /// <summary>The head: the logo (or the name) left, in the middle or right (<see cref="DocumentLayouts"/>). The address is in the footer, the short sender line
    /// in the window of the envelope; _DocumentView and _DocumentEditor draw the same.</summary>
    private static void Head(IContainer container, CompanyConfig company, LogoImage? logo)
    {
        var layout = DocumentLayouts.Of(company.Layout);
        Logo(container, company, logo, center: layout == DocumentLayouts.Centered, right: layout == DocumentLayouts.Right);
    }

    /// <summary>The height of the head (mm): the largest logo has room in it, and with the top margin of 15 mm the address below begins at the window.</summary>
    public const float HeadHeight = 30;

    /// <summary>Between the head and the sender line (mm): the sender line stands at 50 mm from the top, 5 mm into the window (45 to 90 mm), the recipient under it.</summary>
    public const float AddressGap = 5;

    private static void Logo(IContainer c, CompanyConfig company, LogoImage? logo, bool center, bool right = false)
    {
        if (logo is null)
        {
            var text = c.Text(string.IsNullOrWhiteSpace(company.Name) ? " " : company.Name);
            text.FontSize(22).Bold();
            if (center)
            {
                text.AlignCenter();
            }
            else if (right)
            {
                text.AlignRight();
            }

            return;
        }

        // aligned first, then limited: a box limited first stands at the left and the logo would only be in the middle of that box, not of the page.
        // The size is a setting of its own (DocumentLogoSizes), the same in every position (app.css, .mb-doc__head--small/medium/large).
        var (height, maxWidth) = DocumentLogoSizes.Millimetres(company.LogoSize);
        var aligned = (center ? c.AlignCenter() : right ? c.AlignRight() : c.AlignLeft()).AlignTop();
        var box = aligned.Height(height, Unit.Millimetre).MaxWidth(maxWidth, Unit.Millimetre);
        if (logo.IsSvg)
        {
            box.Svg(SvgForPdf(Encoding.UTF8.GetString(logo.Bytes))).FitArea();
        }
        else
        {
            box.Image(logo.Bytes).FitArea();
        }
    }

    private static void AddressAndFacts(IContainer container, InvoiceDocument doc, CompanyConfig company)
        => container.Row(row =>
        {
            row.RelativeItem(1.1f).Column(col =>
            {
                col.Item().Text(company.SenderLine).FontSize(7).FontColor(Muted).Underline();
                col.Item().PaddingTop(3);
                foreach (var line in doc.RecipientLines)
                {
                    col.Item().Text(line);
                }
            });

            row.ConstantItem(10, Unit.Millimetre);
            row.RelativeItem().Column(col =>
            {
                col.Item().Text(doc.Title).FontSize(20).Bold();
                col.Item().PaddingTop(3, Unit.Millimetre);
                Fact(col, doc.NumberLabel, doc.Number ?? (doc.IsDraft ? "wird beim Ausstellen vergeben" : "–"));
                Fact(col, doc.DateLabel, doc.InvoiceDate is { } d ? DocFmt.Date(d) : "–");
                if (doc.Kind == DocumentKind.Cancellation && doc.CancelsNumber is { } cancels)
                {
                    Fact(col, "Storno zu Rechnung", cancels);
                }

                if (doc.ServiceText is { } service)
                {
                    Fact(col, doc.ServiceLabel, service);
                }

                if (!string.IsNullOrWhiteSpace(doc.CustomerNumber))
                {
                    Fact(col, "Kundennummer", doc.CustomerNumber);
                }

                if (!string.IsNullOrWhiteSpace(doc.BuyerReference))
                {
                    Fact(col, "Ihre Referenz", doc.BuyerReference);
                }

                if (doc.Kind == DocumentKind.Quote)
                {
                    if (doc.ValidUntil is { } valid)
                    {
                        Fact(col, "Gültig bis", DocFmt.Date(valid));
                    }
                }
                else if (doc.Kind == DocumentKind.Invoice)
                {
                    Fact(col, "Zahlungsziel", doc.PaymentTermDays == 0 ? "sofort" : $"{doc.PaymentTermDays} Tage");
                    if (doc.DueDate is { } due)
                    {
                        Fact(col, "Fällig am", DocFmt.Date(due));
                    }
                }
            });
        });

    private static void Fact(ColumnDescriptor col, string label, string value)
        => col.Item().PaddingVertical(1.5f).Row(r =>
        {
            r.RelativeItem().Text(label).FontColor(Muted);
            r.AutoItem().AlignRight().Text(value).SemiBold();
        });

    private static void Positions(IContainer container, InvoiceDocument doc, string design, string accent)
    {
        var showTax = doc.Totals.Taxes.Count > 1;
        // Akzent frames the positions; the heading of each design in HeadCell
        (design == DocumentDesigns.Accent ? container.Border(0.5f).BorderColor(Line) : container).Table(table =>
        {
            table.ColumnsDefinition(c =>
            {
                c.RelativeColumn(5);
                c.RelativeColumn(1.6f);
                c.RelativeColumn(1.9f);
                if (showTax)
                {
                    c.RelativeColumn(1f);
                }

                c.RelativeColumn(1.9f);
            });

            // the heading is one cell over all columns with a row of the same widths: one area (or one line) without seams between the columns
            table.Header(h => h.Cell().ColumnSpan(showTax ? 5u : 4u).Element(c => HeadCell(c, design, accent)).Row(r =>
            {
                static IContainer Pad(IContainer c) => c.PaddingHorizontal(2, Unit.Millimetre);
                r.RelativeItem(5).Element(Pad).Text("Leistung");
                r.RelativeItem(1.6f).Element(Pad).AlignRight().Text("Menge");
                r.RelativeItem(1.9f).Element(Pad).AlignRight().Text("Einzelpreis");
                if (showTax)
                {
                    r.RelativeItem(1f).Element(Pad).AlignRight().Text("USt.");
                }

                r.RelativeItem(1.9f).Element(Pad).AlignRight().Text("Gesamtpreis");
            }));

            foreach (var line in doc.Lines)
            {
                table.Cell().Element(BodyCell).Column(c =>
                {
                    c.Item().Text(line.Name).SemiBold();
                    if (!string.IsNullOrWhiteSpace(line.Description))
                    {
                        c.Item().Text(line.Description).FontSize(8.5f).FontColor(Muted);
                    }
                });
                table.Cell().Element(BodyCell).AlignRight().Text(Join(" ", Amount.Quantity(line.Quantity), line.Unit));
                table.Cell().Element(BodyCell).AlignRight().Text(DocFmt.Money(line.UnitPrice));
                if (showTax)
                {
                    table.Cell().Element(BodyCell).AlignRight().Text($"{Amount.Quantity(line.TaxRate)} %");
                }

                table.Cell().Element(BodyCell).AlignRight().Text(DocFmt.Money(line.Net));
            }
        });
    }

    /// <summary>The heading of the positions: grey (Klassisch), a line under it (Linien), the colour of the document with white letters (Akzent).</summary>
    private static IContainer HeadCell(IContainer c, string design, string accent)
        => (design switch
        {
            DocumentDesigns.Lines => c.BorderBottom(1).BorderColor(Ink),
            DocumentDesigns.Accent => c.Background(accent),
            _ => c.Background(Soft),
        })
        .PaddingVertical(2.5f, Unit.Millimetre)
        .DefaultTextStyle(x => x.SemiBold().FontSize(8.5f).FontColor(design == DocumentDesigns.Accent ? Colors.White : Ink));

    private static IContainer BodyCell(IContainer c)
        => c.BorderBottom(0.5f).BorderColor(Line).PaddingVertical(2.5f, Unit.Millimetre).PaddingHorizontal(2, Unit.Millimetre);

    private static void Totals(IContainer container, InvoiceDocument doc, string design, string accent)
        => container.Column(col =>
        {
            if (!doc.TaxExempt)
            {
                TotalRow(col, "Zwischensumme", DocFmt.Money(doc.Totals.Net));
                foreach (var g in doc.Totals.Taxes.Where(g => g.Rate > 0))
                {
                    TotalRow(col, doc.Totals.Taxes.Count(t => t.Rate > 0) > 1 ? $"Umsatzsteuer {Amount.Quantity(g.Rate)} % auf {DocFmt.Money(g.Net)}" : $"Umsatzsteuer ({Amount.Quantity(g.Rate)} %)", DocFmt.Money(g.Tax));
                }
            }

            // the total: under a grey line (Klassisch), between two lines of ink (Linien), white on the colour of the document (Akzent)
            var grand = col.Item().PaddingTop(2, Unit.Millimetre);
            grand = design switch
            {
                DocumentDesigns.Lines => grand.BorderTop(1).BorderBottom(1).BorderColor(Ink).PaddingVertical(2, Unit.Millimetre),
                DocumentDesigns.Accent => grand.Background(accent).PaddingVertical(2, Unit.Millimetre).PaddingHorizontal(2, Unit.Millimetre),
                _ => grand.BorderTop(1).BorderColor("#cbd5e1").PaddingTop(2, Unit.Millimetre),
            };
            var onAccent = design == DocumentDesigns.Accent;
            grand.Row(r =>
            {
                r.RelativeItem().AlignBottom().Text("Gesamtbetrag").Bold().FontSize(11).FontColor(onAccent ? Colors.White : Ink);
                r.AutoItem().Text(DocFmt.Money(doc.Totals.Gross)).Bold().FontSize(15).FontColor(onAccent ? Colors.White : accent);
            });
        });

    private static void TotalRow(ColumnDescriptor col, string label, string value)
        => col.Item().PaddingVertical(1.2f).Row(r =>
        {
            r.RelativeItem().Text(label).FontColor("#374151");
            r.AutoItem().Text(value);
        });

    /// <summary>The footer on every page: the columns of <see cref="DocumentFooter"/> side by side, the page number under the last one.</summary>
    private static void Footer(IContainer container, CompanyConfig company, string accent)
        => container.Column(col =>
        {
            col.Item().Width(12, Unit.Millimetre).Height(0.8f, Unit.Millimetre).Background(accent);
            var columns = DocumentFooter.Columns(company);
            col.Item().PaddingTop(2, Unit.Millimetre).DefaultTextStyle(x => x.FontSize(7f).FontColor(Muted).LineHeight(1.3f)).Row(row =>
            {
                row.Spacing(3, Unit.Millimetre);
                for (var i = 0; i < columns.Count; i++)
                {
                    var lines = columns[i];
                    var last = i == columns.Count - 1;
                    row.RelativeItem(DocumentFooter.Weight(lines)).Column(c =>
                    {
                        foreach (var line in lines)
                        {
                            c.Item().Text(line);
                        }

                        if (last)
                        {
                            c.Item().Text(t =>
                            {
                                t.Span("Seite ");
                                t.CurrentPageNumber();
                                t.Span(" von ");
                                t.TotalPages();
                            });
                        }
                    });
                }
            });
        });

    private static string Join(string separator, params string?[] parts) => string.Join(separator, parts.Where(p => !string.IsNullOrWhiteSpace(p)));

    public static bool IsColour(string? value) => value is { Length: 7 } && value[0] == '#' && value.Skip(1).All(Uri.IsHexDigit);
}
