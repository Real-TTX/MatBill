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

    /// <summary>"Leistungsdatum 17.10.2026" or "Leistungszeitraum 01.10.2026 bis 31.10.2026"; null when there is none.</summary>
    public string? ServiceText => (ServiceFrom, ServiceTo) switch
    {
        ({ } a, { } b) when a != b => $"{Fmt.Date(a)} bis {Fmt.Date(b)}",
        ({ } a, _) => Fmt.Date(a),
        (null, { } b) => Fmt.Date(b),
        _ => null,
    };

    public string ServiceLabel => ServiceFrom is { } a && ServiceTo is { } b && a != b ? "Leistungszeitraum" : "Leistungsdatum";

    public static InvoiceDocument Of(Invoice invoice, InvoicingConfig invoicing) => new()
    {
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
        => [.. new[]
        {
            i.RecipientName,
            i.RecipientContact,
            i.RecipientStreet,
            string.Join(" ", new[] { i.RecipientPostalCode, i.RecipientCity }.Where(s => !string.IsNullOrWhiteSpace(s))),
            string.Equals(i.RecipientCountry, "Deutschland", StringComparison.OrdinalIgnoreCase) ? null : i.RecipientCountry,
        }.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!)];
}

/// <summary>The placeholders of the texts of an invoice and a mail.</summary>
public static class Texts
{
    /// <summary>The payment terms with the days and the due date filled in.</summary>
    public static string? Expand(string? text, int days, DateOnly? due)
        => text?.Replace("{Tage}", days.ToString(Fmt.Culture), StringComparison.Ordinal).Replace("{Faellig}", Fmt.Date(due), StringComparison.Ordinal);

    public static string Mail(string template, Invoice invoice, CompanyConfig company)
        => template
            .Replace("{Nummer}", invoice.Number ?? "", StringComparison.Ordinal)
            .Replace("{Kunde}", invoice.RecipientName ?? "", StringComparison.Ordinal)
            .Replace("{Betrag}", Fmt.Money(invoice.GrossTotal), StringComparison.Ordinal)
            .Replace("{Faellig}", Fmt.Date(invoice.DueDate), StringComparison.Ordinal)
            .Replace("{Datum}", Fmt.Date(invoice.InvoiceDate), StringComparison.Ordinal)
            .Replace("{Firma}", company.Name, StringComparison.Ordinal);
}
