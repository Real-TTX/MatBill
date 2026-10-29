using MatBill.Core.Billing;

namespace MatBill.Data.Entities;

public enum CustomerKind
{
    Company,
    Person,
}

/// <summary>Who receives invoices. An invoice copies the address when it is written, so a later change of the customer never changes an issued invoice.</summary>
public class Customer : AuditedEntity, ISoftDeletable
{
    /// <summary>The customer number ("K-0001"), given from the number range of the customers.</summary>
    public string Number { get; set; } = "";

    public CustomerKind Kind { get; set; } = CustomerKind.Company;

    /// <summary>The company, or the full name of a person.</summary>
    public string Name { get; set; } = "";

    /// <summary>The person to address at a company ("z. Hd. Frau Keller").</summary>
    public string? ContactName { get; set; }

    /// <summary>Where invoices are sent by e-mail.</summary>
    public string? Email { get; set; }

    public string? Phone { get; set; }

    public string? Street { get; set; }

    public string? PostalCode { get; set; }

    public string? City { get; set; }

    public string Country { get; set; } = "Deutschland";

    /// <summary>The VAT identification number of the customer (needed for reverse charge within the EU).</summary>
    public string? VatId { get; set; }

    /// <summary>The Leitweg-ID of a public authority (or another buyer reference): every e-invoice to this customer carries it.</summary>
    public string? BuyerReference { get; set; }

    /// <summary>The payment term of this customer in days; null takes the default of the company.</summary>
    public int? PaymentTermDays { get; set; }

    public string? Note { get; set; }

    /// <summary>The API client that created this customer, and the key it knows the customer by ("matmon:org:…"): unique per client.</summary>
    public long? ApiClientId { get; set; }

    public ApiClient? ApiClient { get; set; }

    public string? ExternalRef { get; set; }

    public UpdateState UpdateState { get; set; } = UpdateState.Created;

    /// <summary>The second line of a choice: street and town.</summary>
    public string AddressLine => string.Join(", ", new[] { Street, string.Join(" ", new[] { PostalCode, City }.Where(s => !string.IsNullOrWhiteSpace(s))) }.Where(s => !string.IsNullOrWhiteSpace(s)));
}

public enum ProductKind
{
    Service,
    Product,
}

/// <summary>A product or a service that a position of an invoice can be taken from. The position copies it: a later price change never changes an invoice.</summary>
public class Product : AuditedEntity, ISoftDeletable
{
    public string Name { get; set; } = "";

    public string? Description { get; set; }

    public ProductKind Kind { get; set; } = ProductKind.Service;

    /// <summary>"Stunde", "Stück", "Pauschal", "Monat" ...</summary>
    public string? Unit { get; set; }

    /// <summary>The net price of one unit.</summary>
    public decimal UnitPrice { get; set; }

    /// <summary>In percent: 19, 7 or 0.</summary>
    public decimal TaxRate { get; set; } = 19m;

    /// <summary>An item number of your own, optional.</summary>
    public string? Sku { get; set; }

    public bool IsActive { get; set; } = true;

    public UpdateState UpdateState { get; set; } = UpdateState.Created;
}

/// <summary>What every position has, on an invoice and on the template of a series.</summary>
public abstract class LineBase : AuditedEntity
{
    public int Position { get; set; }

    public long? ProductId { get; set; }

    public string Name { get; set; } = "";

    public string? Description { get; set; }

    public decimal Quantity { get; set; } = 1m;

    public string? Unit { get; set; }

    public decimal UnitPrice { get; set; }

    public decimal TaxRate { get; set; } = 19m;

    public decimal Net => InvoiceMath.LineNet(Quantity, UnitPrice);

    public LineAmounts Amounts => new(Quantity, UnitPrice, TaxRate);
}

/// <summary>
/// Draft: it can be changed, has no number yet and can be deleted. Open: issued (number, date and the PDF are fixed for good), waiting for the money.
/// Paid and Cancelled end it. An issued invoice is never changed again: a mistake is cancelled and written anew.
/// </summary>
public enum InvoiceStatus
{
    Draft,
    Open,
    Paid,
    Cancelled,
}

/// <summary>Whether the invoice still has to be sent by mail: by the series (automatic sending) or after a failed attempt.</summary>
public enum SendState
{
    None,
    Pending,
    Sent,
    Failed,
}

public class Invoice : AuditedEntity, ISoftDeletable
{
    /// <summary>The invoice number; null while it is a draft (the number is given when it is issued, so the range has no gaps).</summary>
    /// <summary>Invoice, or the cancellation invoice of another one (negative amounts, own number, refers to the cancelled one).</summary>
    public InvoiceKind Kind { get; set; } = InvoiceKind.Invoice;

    /// <summary>A cancellation invoice: the invoice it cancels.</summary>
    public long? CancelsInvoiceId { get; set; }

    /// <summary>A cancelled invoice: its cancellation invoice.</summary>
    public long? CancellationInvoiceId { get; set; }

    /// <summary>The number of the cancelled invoice, as the cancellation invoice prints it.</summary>
    public string? CancelsNumber { get; set; }

    /// <summary>The quote this invoice was made from.</summary>
    public long? QuoteId { get; set; }

    /// <summary>The reference of the buyer (Leitweg-ID of a public authority, or an order number): in the e-invoice (BT-10).</summary>
    public string? BuyerReference { get; set; }

    public string? Number { get; set; }

    public InvoiceStatus Status { get; set; } = InvoiceStatus.Draft;

    public long? CustomerId { get; set; }

    public Customer? Customer { get; set; }

    // The address as it stands on the invoice: copied from the customer, then the invoice's own.
    public string? RecipientName { get; set; }

    public string? RecipientContact { get; set; }

    public string? RecipientStreet { get; set; }

    public string? RecipientPostalCode { get; set; }

    public string? RecipientCity { get; set; }

    public string? RecipientCountry { get; set; }

    public string? RecipientEmail { get; set; }

    public string Title { get; set; } = "Rechnung";

    public DateOnly InvoiceDate { get; set; }

    /// <summary>The date (or period) of the service: a German invoice has to name it.</summary>
    public DateOnly? ServiceFrom { get; set; }

    public DateOnly? ServiceTo { get; set; }

    public int PaymentTermDays { get; set; } = 14;

    public DateOnly DueDate { get; set; }

    public string? IntroText { get; set; }

    public string? PaymentTerms { get; set; }

    public string? ClosingText { get; set; }

    /// <summary>No tax is charged (small business, § 19 UStG); fixed when the invoice is issued.</summary>
    public bool TaxExempt { get; set; }

    public decimal NetTotal { get; set; }

    public decimal TaxTotal { get; set; }

    public decimal GrossTotal { get; set; }

    public DateTime? IssuedDate { get; set; }

    public DateOnly? PaidDate { get; set; }

    public DateTime? CancelledDate { get; set; }

    public string? CancelReason { get; set; }

    /// <summary>The PDF as it was issued: kept, so that the invoice looks the same in ten years whatever the branding says then.</summary>
    public long? PdfFileId { get; set; }

    public StoredFile? PdfFile { get; set; }

    public SendState SendState { get; set; } = SendState.None;

    public DateTime? SentDate { get; set; }

    public int SendAttempts { get; set; }

    public DateTime? NextSendDate { get; set; }

    public string? SendError { get; set; }

    /// <summary>The series that made this invoice, and the date of the series it stands for (unique per series, so a date is never invoiced twice).</summary>
    public long? RecurringInvoiceId { get; set; }

    public RecurringInvoice? RecurringInvoice { get; set; }

    public DateOnly? RecurringDate { get; set; }

    /// <summary>
    /// The API client that requested this invoice (a cancellation inherits it, so it takes its number from the same range), its reference (the idempotency key,
    /// unique per client among the invoices that are not deleted; a cancellation has none) and its note for whoever checks the draft.
    /// </summary>
    public long? ApiClientId { get; set; }

    public ApiClient? ApiClient { get; set; }

    public string? ApiReference { get; set; }

    public string? ApiNote { get; set; }

    public List<InvoiceLine> Lines { get; set; } = [];

    public UpdateState UpdateState { get; set; } = UpdateState.Created;

    public bool IsDraft => Status == InvoiceStatus.Draft;

    public bool IsOverdue(DateOnly today) => Status == InvoiceStatus.Open && DueDate < today;
}

public class InvoiceLine : LineBase
{
    public long InvoiceId { get; set; }

    public Invoice? Invoice { get; set; }
}

public enum RecurringStatus
{
    Active,
    Paused,
    Ended,
}

/// <summary>
/// A series: the template of future invoices. Each run makes an invoice of its own (number, date, status) from the template; changing the template changes
/// only what comes next, never an invoice that was already made.
/// </summary>
public class RecurringInvoice : AuditedEntity, ISoftDeletable
{
    public string Name { get; set; } = "";

    public long CustomerId { get; set; }

    public Customer? Customer { get; set; }

    public RecurrenceInterval Interval { get; set; } = RecurrenceInterval.Monthly;

    /// <summary>The first date of the series and the anchor of every later one.</summary>
    public DateOnly StartDate { get; set; }

    /// <summary>The last day on which an invoice may be made; null runs without end.</summary>
    public DateOnly? EndDate { get; set; }

    /// <summary>The date of the next invoice; null when the series has ended.</summary>
    public DateOnly? NextRunDate { get; set; }

    public RecurringStatus Status { get; set; } = RecurringStatus.Active;

    /// <summary>Send each new invoice as a PDF to the customer by e-mail.</summary>
    public bool AutoSend { get; set; }

    public string Title { get; set; } = "Rechnung";

    public string? IntroText { get; set; }

    public string? PaymentTerms { get; set; }

    public string? ClosingText { get; set; }

    public int PaymentTermDays { get; set; } = 14;

    /// <summary>The invoice states the period it covers ("Leistungszeitraum 01.11. bis 30.11.2026").</summary>
    public bool StatePeriod { get; set; } = true;

    public long? SourceInvoiceId { get; set; }

    public int CreatedCount { get; set; }

    public DateTime? LastRunDate { get; set; }

    /// <summary>The problem of the last attempt; null after a run that worked.</summary>
    public string? LastError { get; set; }

    public int FailedAttempts { get; set; }

    /// <summary>After a failed attempt the scheduler waits until then before it tries again by itself.</summary>
    public DateTime? RetryAfterDate { get; set; }

    public List<RecurringInvoiceLine> Lines { get; set; } = [];

    public UpdateState UpdateState { get; set; } = UpdateState.Created;
}

public class RecurringInvoiceLine : LineBase
{
    public long RecurringInvoiceId { get; set; }

    public RecurringInvoice? RecurringInvoice { get; set; }
}

public enum RecurringRunStatus
{
    Created,
    Failed,
}

/// <summary>The log of a series: each invoice it made and each attempt that failed.</summary>
public class RecurringRun : AuditedEntity
{
    public long RecurringInvoiceId { get; set; }

    public RecurringInvoice? RecurringInvoice { get; set; }

    public DateOnly ScheduledDate { get; set; }

    public DateTime RunDate { get; set; }

    public RecurringRunStatus Status { get; set; }

    public long? InvoiceId { get; set; }

    public Invoice? Invoice { get; set; }

    public string? Message { get; set; }

    public bool IsManual { get; set; }
}

public enum MailStatus
{
    Sent,
    Failed,
}

/// <summary>Every attempt to send an invoice by mail, the ones that worked and the ones that did not.</summary>
public class MailDelivery : AuditedEntity
{
    public long? InvoiceId { get; set; }

    public Invoice? Invoice { get; set; }

    public string To { get; set; } = "";

    public string? Cc { get; set; }

    public string Subject { get; set; } = "";

    public MailStatus Status { get; set; }

    public string? Error { get; set; }

    public DateTime AttemptDate { get; set; }

    public bool IsAutomatic { get; set; }
}

public enum NotificationTone
{
    Info,
    Success,
    Warning,
    Error,
}

/// <summary>A note for one person: an invoice that is due soon, one that was made, a problem. <see cref="Key"/> keeps the same note from being written twice.</summary>
public class Notification : AuditedEntity
{
    public long UserId { get; set; }

    public User? User { get; set; }

    public NotificationTone Tone { get; set; } = NotificationTone.Info;

    public string Title { get; set; } = "";

    public string? Text { get; set; }

    public string? Url { get; set; }

    public string? Key { get; set; }

    public DateTime OccurredDate { get; set; }

    public DateTime? ReadDate { get; set; }
}

/// <summary>The counter of a number range in one period ("invoice", "2026"); advanced in the same transaction that uses the number.</summary>
public class NumberSequence : AuditedEntity
{
    public string Name { get; set; } = "";

    public string Period { get; set; } = "";

    public long LastValue { get; set; }
}

public enum InvoiceKind
{
    Invoice,
    Cancellation,
}
