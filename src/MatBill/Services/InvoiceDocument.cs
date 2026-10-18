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
public static class Texts
{
    /// <summary>The payment terms with the days and the due date filled in.</summary>
    public static string? Expand(string? text, int days, DateOnly? due)
        => Fill(text, ("Days", "Tage", days.ToString(DocFmt.Culture)), ("DueDate", "Faellig", DocFmt.Date(due)));

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
