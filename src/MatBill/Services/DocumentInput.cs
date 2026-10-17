using MatBill.Core.Billing;
using MatBill.Data.Entities;

namespace MatBill.Services;

/// <summary>
/// What the document editor posts, for an invoice and for the template of a series alike. Amounts arrive as they were typed ("1.200,50") and are read with
/// <see cref="Amount"/>; the positions are numbered by the editor's script before it posts (<c>Lines[0].Name</c> ...).
/// </summary>
public sealed class DocumentInput
{
    public long? CustomerId { get; set; }

    public string? RecipientName { get; set; }

    public string? RecipientContact { get; set; }

    public string? RecipientStreet { get; set; }

    public string? RecipientPostalCode { get; set; }

    public string? RecipientCity { get; set; }

    public string? RecipientCountry { get; set; }

    public string? RecipientEmail { get; set; }

    public string? Title { get; set; }

    public DateOnly? InvoiceDate { get; set; }

    public DateOnly? ServiceFrom { get; set; }

    public DateOnly? ServiceTo { get; set; }

    public int? PaymentTermDays { get; set; }

    public string? IntroText { get; set; }

    public string? PaymentTerms { get; set; }

    public string? ClosingText { get; set; }

    public List<LineInput> Lines { get; set; } = [];

    // ---- "Wiederholung einrichten": only the invoice editor shows it; a series is made from the invoice when it is saved with Repeat on.

    public bool Repeat { get; set; }

    public RecurrenceInterval Interval { get; set; } = RecurrenceInterval.Monthly;

    public DateOnly? RepeatStart { get; set; }

    /// <summary>"open" (no end) or "until" (<see cref="RepeatEnd"/>).</summary>
    public string RepeatRuns { get; set; } = "open";

    public DateOnly? RepeatEnd { get; set; }

    public bool AutoSend { get; set; }
}

public sealed class LineInput
{
    public long? ProductId { get; set; }

    public string? Name { get; set; }

    public string? Description { get; set; }

    public string? Quantity { get; set; }

    public string? Unit { get; set; }

    public string? UnitPrice { get; set; }

    public string? TaxRate { get; set; }

    public bool IsEmpty => string.IsNullOrWhiteSpace(Name) && string.IsNullOrWhiteSpace(Description) && string.IsNullOrWhiteSpace(UnitPrice) && ProductId is null;
}

/// <summary>What the editor shows: the fields of an invoice or of a series, as the form holds them (amounts written the German way).</summary>
public static class DocumentInputs
{
    public static DocumentInput From(Invoice i) => new()
    {
        CustomerId = i.CustomerId,
        RecipientName = i.RecipientName,
        RecipientContact = i.RecipientContact,
        RecipientStreet = i.RecipientStreet,
        RecipientPostalCode = i.RecipientPostalCode,
        RecipientCity = i.RecipientCity,
        RecipientCountry = i.RecipientCountry,
        RecipientEmail = i.RecipientEmail,
        Title = i.Title,
        InvoiceDate = i.InvoiceDate,
        ServiceFrom = i.ServiceFrom,
        ServiceTo = i.ServiceTo,
        PaymentTermDays = i.PaymentTermDays,
        IntroText = i.IntroText,
        PaymentTerms = i.PaymentTerms,
        ClosingText = i.ClosingText,
        Lines = [.. i.Lines.OrderBy(l => l.Position).Select(LineOf)],
    };

    public static DocumentInput From(RecurringInvoice s) => new()
    {
        CustomerId = s.CustomerId,
        RecipientName = s.Customer?.Name,
        RecipientContact = s.Customer?.ContactName,
        RecipientStreet = s.Customer?.Street,
        RecipientPostalCode = s.Customer?.PostalCode,
        RecipientCity = s.Customer?.City,
        RecipientCountry = s.Customer?.Country,
        RecipientEmail = s.Customer?.Email,
        Title = s.Title,
        PaymentTermDays = s.PaymentTermDays,
        IntroText = s.IntroText,
        PaymentTerms = s.PaymentTerms,
        ClosingText = s.ClosingText,
        Lines = [.. s.Lines.OrderBy(l => l.Position).Select(LineOf)],
        Interval = s.Interval,
        RepeatStart = s.StartDate,
        RepeatRuns = s.EndDate is null ? "open" : "until",
        RepeatEnd = s.EndDate,
        AutoSend = s.AutoSend,
    };

    public static LineInput LineOf(LineBase l) => new()
    {
        ProductId = l.ProductId,
        Name = l.Name,
        Description = l.Description,
        Quantity = Amount.Quantity(l.Quantity),
        Unit = l.Unit,
        UnitPrice = Amount.Edit(l.UnitPrice, l.UnitPrice == Math.Round(l.UnitPrice, 2) ? 2 : 4),
        TaxRate = Amount.Quantity(l.TaxRate),
    };

    /// <summary>The sums of what the form holds now (for the page as it is drawn; the script keeps them up to date while typing).</summary>
    public static InvoiceTotals Totals(DocumentInput input, bool taxExempt)
        => InvoiceMath.Totals(input.Lines.Where(l => !l.IsEmpty).Select(l => new LineAmounts(
            Amount.ParseOr(l.Quantity, string.IsNullOrWhiteSpace(l.Quantity) ? 1m : 0m), Amount.ParseOr(l.UnitPrice, 0m), Amount.ParseOr(l.TaxRate, 19m))), taxExempt);

    public static decimal LineNet(LineInput l)
        => InvoiceMath.LineNet(Amount.ParseOr(l.Quantity, string.IsNullOrWhiteSpace(l.Quantity) ? 1m : 0m), Amount.ParseOr(l.UnitPrice, 0m));
}
