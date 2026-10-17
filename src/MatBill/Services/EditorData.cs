using System.Text.Json;
using MatBill.Core.Billing;
using MatBill.Data.Entities;

namespace MatBill.Services;

/// <summary>A customer as the editor's script needs it to fill the address of the document.</summary>
public sealed record CustomerJson(long Id, string Number, string Name, string? Contact, string? Street, string? PostalCode, string? City, string? Country, string? Email, int? PaymentTermDays)
{
    public static CustomerJson Of(Customer c) => new(c.Id, c.Number, c.Name, c.ContactName, c.Street, c.PostalCode, c.City, c.Country, c.Email, c.PaymentTermDays);
}

/// <summary>A product as the editor's script needs it to fill a position.</summary>
public sealed record ProductJson(long Id, string Name, string? Description, string? Unit, string UnitPrice, string TaxRate)
{
    public static ProductJson Of(Product p) => new(p.Id, p.Name, p.Description, p.Unit, Amount.Edit(p.UnitPrice), Amount.Quantity(p.TaxRate));
}

/// <summary>The data the editor's script reads from the page (a JSON block that is never executed, so the content security policy allows it).</summary>
public static class EditorData
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>JSON that is safe inside a script element: no "&lt;/script" can close it early.</summary>
    public static string Serialize(object value) => JsonSerializer.Serialize(value, Json).Replace("<", "\\u003c", StringComparison.Ordinal);
}

/// <summary>The template of a series as the next invoice would look: for the preview, never saved.</summary>
public static class RecurringPreview
{
    public static Invoice Of(RecurringInvoice series, InvoiceService invoices, bool smallBusiness)
    {
        var date = series.NextRunDate ?? invoices.Today;
        var invoice = invoices.NewDraft(series.Customer);
        var (from, to) = Recurrence.Period(series.StartDate, series.Interval, date);
        invoice.Title = series.Title;
        invoice.InvoiceDate = date;
        invoice.ServiceFrom = series.StatePeriod ? from : date;
        invoice.ServiceTo = series.StatePeriod ? to : null;
        invoice.PaymentTermDays = series.PaymentTermDays;
        invoice.DueDate = InvoiceMath.DueDate(date, series.PaymentTermDays);
        invoice.IntroText = series.IntroText;
        invoice.PaymentTerms = series.PaymentTerms;
        invoice.ClosingText = series.ClosingText;
        invoice.TaxExempt = smallBusiness;
        invoice.Lines = [.. series.Lines.OrderBy(l => l.Position).Select(l => InvoiceService.CopyLine(l, new InvoiceLine()))];
        InvoiceService.Recalculate(invoice);
        return invoice;
    }
}
