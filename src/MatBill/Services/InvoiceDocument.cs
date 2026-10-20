using MatBill.Configuration;
using MatBill.Core.Billing;
using MatBill.Data.Entities;

namespace MatBill.Services;

public sealed record DocumentLine(int Position, string Name, string? Description, decimal Quantity, string? Unit, decimal UnitPrice, decimal TaxRate, decimal Net);

/// <summary>
/// An invoice as it is printed: the one model that the PDF and the read-only document page draw. It is made from an invoice, from a draft or from the template of a
/// series (which has no number and no date yet).
/// </summary>
public sealed record InvoiceDocument
{
    public string Title { get; init; } = "Rechnung";

    public string? Number { get; init; }

    public string? CustomerNumber { get; init; }

    public DateOnly? InvoiceDate { get; init; }

    public DateOnly? DueDate { get; init; }

    public int PaymentTermDays { get; init; }

    public DateOnly? ServiceFrom { get; init; }

    public DateOnly? ServiceTo { get; init; }

    public IReadOnlyList<string> RecipientLines { get; init; } = [];

    public string? IntroText { get; init; }

    public string? PaymentTerms { get; init; }

    public string? ClosingText { get; init; }

    public IReadOnlyList<DocumentLine> Lines { get; init; } = [];

    public InvoiceTotals Totals { get; init; } = InvoiceTotals.Empty;

    public bool TaxExempt { get; init; }

    public string? TaxExemptNote { get; init; }

    public bool IsDraft { get; init; }

    public bool IsCancelled { get; init; }

    public DocumentKind Kind { get; init; } = DocumentKind.Invoice;

    /// <summary>A cancellation invoice: the number of the invoice it cancels.</summary>
    public string? CancelsNumber { get; init; }

    /// <summary>A quote: the last day it stands.</summary>
    public DateOnly? ValidUntil { get; init; }

    public string? BuyerReference { get; init; }

    public string NumberLabel => Kind == DocumentKind.Quote ? "Angebotsnummer" : "Rechnungsnummer";

    public string DateLabel => Kind == DocumentKind.Quote ? "Angebotsdatum" : "Rechnungsdatum";

    /// <summary>The box below the totals: payment terms of an invoice, terms of a quote.</summary>
    public string TermsLabel => Kind == DocumentKind.Quote ? "Bedingungen" : "Zahlungsbedingungen";

    /// <summary>The stamp across a page that is no valid document (yet or any more).</summary>
    public string? Stamp => IsDraft ? "ENTWURF" : IsCancelled ? "STORNIERT" : null;

    public static InvoiceDocument Of(Quote quote, InvoicingConfig invoicing) => new()
    {
        Kind = DocumentKind.Quote,
        Title = quote.Title,
        Number = quote.Number,
        CustomerNumber = quote.Customer?.Number,
        InvoiceDate = quote.QuoteDate,
        ValidUntil = quote.ValidUntil,
        RecipientLines = RecipientLinesOf(quote.RecipientName, quote.RecipientContact, quote.RecipientStreet, quote.RecipientPostalCode, quote.RecipientCity, quote.RecipientCountry),
        IntroText = quote.IntroText,
        PaymentTerms = Texts.ExpandQuote(quote.Terms, quote.ValidUntil),
        ClosingText = quote.ClosingText,
        Lines = [.. quote.Lines.OrderBy(l => l.Position).Select((l, i) => new DocumentLine(i + 1, l.Name, l.Description, l.Quantity, l.Unit, l.UnitPrice, quote.TaxExempt ? 0m : l.TaxRate, l.Net))],
        Totals = InvoiceMath.Totals(quote.Lines.Select(l => l.Amounts), quote.TaxExempt),
        TaxExempt = quote.TaxExempt,
        TaxExemptNote = quote.TaxExempt ? invoicing.SmallBusinessNote : null,
        IsDraft = quote.Status == QuoteStatus.Draft,
    };

    /// <summary>"Leistungsdatum 17.10.2026" or "Leistungszeitraum 01.10.2026 bis 31.10.2026"; null when there is none.</summary>
    public string? ServiceText => (ServiceFrom, ServiceTo) switch
    {
        ({ } a, { } b) when a != b => $"{DocFmt.Date(a)} bis {DocFmt.Date(b)}",
        ({ } a, _) => DocFmt.Date(a),
        (null, { } b) => DocFmt.Date(b),
        _ => null,
    };

    public string ServiceLabel => ServiceFrom is { } a && ServiceTo is { } b && a != b ? "Leistungszeitraum" : "Leistungsdatum";

    public static InvoiceDocument Of(Invoice invoice, InvoicingConfig invoicing) => new()
    {
        Kind = invoice.Kind == InvoiceKind.Cancellation ? DocumentKind.Cancellation : DocumentKind.Invoice,
        CancelsNumber = invoice.CancelsNumber,
        BuyerReference = invoice.BuyerReference,
        Title = invoice.Title,
        Number = invoice.Number,
        CustomerNumber = invoice.Customer?.Number,
        InvoiceDate = invoice.InvoiceDate,
        DueDate = invoice.DueDate,
        PaymentTermDays = invoice.PaymentTermDays,
        ServiceFrom = invoice.ServiceFrom,
        ServiceTo = invoice.ServiceTo,
        RecipientLines = RecipientLinesOf(invoice),
        IntroText = invoice.IntroText,
        PaymentTerms = Texts.Expand(invoice.PaymentTerms, invoice.PaymentTermDays, invoice.DueDate),
        ClosingText = invoice.ClosingText,
        Lines = [.. invoice.Lines.OrderBy(l => l.Position).Select((l, i) => new DocumentLine(i + 1, l.Name, l.Description, l.Quantity, l.Unit, l.UnitPrice, invoice.TaxExempt ? 0m : l.TaxRate, l.Net))],
        Totals = InvoiceMath.Totals(invoice.Lines.Select(l => l.Amounts), invoice.TaxExempt),
        TaxExempt = invoice.TaxExempt,
        TaxExemptNote = invoice.TaxExempt ? invoicing.SmallBusinessNote : null,
        IsDraft = invoice.Status == InvoiceStatus.Draft,
        IsCancelled = invoice.Status == InvoiceStatus.Cancelled,
    };

    public static IReadOnlyList<string> RecipientLinesOf(Invoice i)
        => RecipientLinesOf(i.RecipientName, i.RecipientContact, i.RecipientStreet, i.RecipientPostalCode, i.RecipientCity, i.RecipientCountry);

    public static IReadOnlyList<string> RecipientLinesOf(string? name, string? contact, string? street, string? postalCode, string? city, string? country)
        => [.. new[]
        {
            name,
            contact,
            street,
            string.Join(" ", new[] { postalCode, city }.Where(s => !string.IsNullOrWhiteSpace(s))),
            string.Equals(country, "Deutschland", StringComparison.OrdinalIgnoreCase) ? null : country,
        }.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!)];
}

public enum DocumentKind
{
    Invoice,
    Cancellation,
    Quote,
}

/// <summary>
/// The placeholders of the texts of an invoice, a quote and a mail: {Days}, {DueDate}, {ValidUntil}, {Number}, {Customer}, {Amount}, {Date}, {Company}.
/// The German names of earlier versions ({Tage}, {Faellig}, {Gueltig}, {Nummer}, {Kunde}, {Betrag}, {Datum}, {Firma}) still work.
/// </summary>
/// <summary>
/// The footer of every document: the company with its address, how to reach it, the bank, tax and register, each a column of lines (an empty column is left
/// out). This is the one place where the paper shows the full address: the head carries the logo, the window of the envelope the short sender line (DIN 5008).
/// InvoicePdf and the paper of the editor draw these columns.
/// </summary>
public static partial class DocumentFooter
{
    /// <summary>The columns the footer can have: Branding shows as many fields.</summary>
    public const int MaxColumns = 4;

    /// <summary>The placeholders of a footer written by hand and what they stand for.</summary>
    public static IReadOnlyList<(string Name, Func<CompanyConfig, string?> Value)> Placeholders { get; } =
    [
        ("Name", c => c.Name), ("NameLine2", c => c.NameLine2), ("Street", c => c.Street), ("PostalCode", c => c.PostalCode), ("City", c => c.City),
        ("Country", c => c.Country), ("Manager", c => c.Manager), ("Phone", c => c.Phone), ("Email", c => c.Email), ("Website", c => c.Website),
        ("BankName", c => c.BankName), ("Iban", c => c.Iban), ("Bic", c => c.Bic), ("AccountHolder", c => c.AccountHolder),
        ("Bank2Name", c => c.Bank2Name), ("Bank2Iban", c => c.Bank2Iban), ("Bank2Bic", c => c.Bank2Bic),
        ("TaxNumber", c => c.TaxNumber), ("VatId", c => c.VatId), ("Register", c => c.Register),
    ];

    /// <summary>How much of the width a column gets: as much as its longest line needs (within bounds), so an IBAN stays on one line where a short column has room to give.</summary>
    public static int Weight(List<string> column) => Math.Clamp(column.Count == 0 ? 10 : column.Max(l => l.Length), 10, 48);

    public static List<List<string>> Columns(CompanyConfig company)
        => company.FooterColumns.Any(c => !string.IsNullOrWhiteSpace(c)) ? Written(company) : Automatic(company);

    /// <summary>The footer from the company data: address, contact, bank (a second account under the first), tax and register.</summary>
    private static List<List<string>> Automatic(CompanyConfig company)
    {
        static List<string> Lines(params string?[] lines) => [.. lines.Where(l => !string.IsNullOrWhiteSpace(l)).Select(l => l!.Trim())];
        static string? Labelled(string label, string? value) => string.IsNullOrWhiteSpace(value) ? null : label + " " + value.Trim();
        var place = string.Join(" ", new[] { company.PostalCode, company.City }.Where(s => !string.IsNullOrWhiteSpace(s)));
        return new List<List<string>>
        {
            Lines(company.Name, company.NameLine2, company.Street, place, company.Manager),
            Lines(Labelled("Tel.", company.Phone), company.Email, company.Website),
            Lines(company.BankName, Labelled("IBAN", company.Iban), Labelled("BIC", company.Bic), Labelled("Kontoinhaber:", company.AccountHolder),
                company.Bank2Name, Labelled("IBAN", company.Bank2Iban), Labelled("BIC", company.Bank2Bic)),
            Lines(Labelled("Steuernummer", company.TaxNumber), Labelled("USt-IdNr.", company.VatId), company.Register),
        }.Where(c => c.Count > 0).ToList();
    }

    /// <summary>
    /// The footer as written in Branding: one column per field, one line per line, the placeholders filled in. A line whose placeholders are all empty is left
    /// out ("IBAN {Bank2Iban}" without a second account), so one text fits companies with and without every detail.
    /// </summary>
    private static List<List<string>> Written(CompanyConfig company)
    {
        var columns = new List<List<string>>();
        foreach (var text in company.FooterColumns.Take(MaxColumns))
        {
            var lines = new List<string>();
            foreach (var raw in (text ?? "").Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                var tokens = PlaceholderRegex().Matches(line).Select(m => m.Groups[1].Value).ToList();
                var values = tokens.Select(t => Placeholders.FirstOrDefault(p => p.Name.Equals(t, StringComparison.OrdinalIgnoreCase)).Value?.Invoke(company)).ToList();
                if (tokens.Count > 0 && values.All(string.IsNullOrWhiteSpace))
                {
                    continue;
                }

                var filled = PlaceholderRegex().Replace(line, m => Placeholders.FirstOrDefault(p => p.Name.Equals(m.Groups[1].Value, StringComparison.OrdinalIgnoreCase)).Value?.Invoke(company)?.Trim() ?? m.Value).Trim();
                if (filled.Length > 0)
                {
                    lines.Add(filled);
                }
            }

            if (lines.Count > 0)
            {
                columns.Add(lines);
            }
        }

        return columns;
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"\{([A-Za-z0-9]+)\}")]
    private static partial System.Text.RegularExpressions.Regex PlaceholderRegex();
}

public static partial class Texts
{
    /// <summary>The payment terms with the days and the due date filled in.</summary>
    public static string? Expand(string? text, int days, DateOnly? due)
        => Fill(days == 0 ? Immediately(text) : days == 1 ? OneDay(text) : text, ("Days", "Tage", days.ToString(DocFmt.Culture)), ("DueDate", "Faellig", DocFmt.Date(due)));

    /// <summary>Due at once: "innerhalb von {Days} Tagen" becomes "sofort" ("within {Days} days": "immediately"), not "innerhalb von 0 Tagen".</summary>
    private static string? Immediately(string? text)
        => text is null ? null : ImmediatelyRegex().Replace(text, m => m.Value.StartsWith("within", StringComparison.OrdinalIgnoreCase) ? "immediately" : "sofort");

    /// <summary>"innerhalb von 1 Tagen" is "innerhalb von 1 Tag".</summary>
    private static string? OneDay(string? text)
        => text is null ? null : OneDayRegex().Replace(text, m => m.Groups[1].Value + (m.Groups[2].Value.StartsWith('d') ? "day" : "Tag"));

    [System.Text.RegularExpressions.GeneratedRegex(@"(?:innerhalb von|binnen|within)\s+\{(?:Days|Tage)\}\s+(?:Tagen|Tage|days)", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex ImmediatelyRegex();

    [System.Text.RegularExpressions.GeneratedRegex(@"(\{(?:Days|Tage)\}\s+)(Tagen|Tage|days)\b")]
    private static partial System.Text.RegularExpressions.Regex OneDayRegex();

    /// <summary>The terms of a quote with the last day filled in.</summary>
    public static string? ExpandQuote(string? text, DateOnly validUntil) => Fill(text, ("ValidUntil", "Gueltig", DocFmt.Date(validUntil)));

    public static string Mail(string template, Quote quote, CompanyConfig company)
        => Fill(template,
            ("Number", "Nummer", quote.Number ?? ""), ("Customer", "Kunde", quote.RecipientName ?? ""), ("Amount", "Betrag", DocFmt.Money(quote.GrossTotal)),
            ("ValidUntil", "Gueltig", DocFmt.Date(quote.ValidUntil)), ("Date", "Datum", DocFmt.Date(quote.QuoteDate)), ("Company", "Firma", company.Name))!;

    public static string Mail(string template, Invoice invoice, CompanyConfig company)
        => Fill(template,
            ("Number", "Nummer", invoice.Number ?? ""), ("Customer", "Kunde", invoice.RecipientName ?? ""), ("Amount", "Betrag", DocFmt.Money(invoice.GrossTotal)),
            ("DueDate", "Faellig", DocFmt.Date(invoice.DueDate)), ("Date", "Datum", DocFmt.Date(invoice.InvoiceDate)), ("Company", "Firma", company.Name))!;

    private static string? Fill(string? text, params (string Name, string Legacy, string Value)[] values)
    {
        if (text is null)
        {
            return null;
        }

        foreach (var (name, legacy, value) in values)
        {
            text = text.Replace("{" + name + "}", value, StringComparison.Ordinal).Replace("{" + legacy + "}", value, StringComparison.Ordinal);
        }

        return text;
    }
}
