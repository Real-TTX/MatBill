namespace MatBill.Data.Entities;

/// <summary>
/// The permanent record of a document number that was given and taken back: a test invoice (and its cancellation) that an administrator deleted for good
/// (<see cref="Services.TestInvoiceService"/>). It stays when the activity log is cleaned after a year, so that a gap or a repeated number in the range
/// can always be explained. It holds no amounts of positions and no address, only what identifies the document.
/// </summary>
public class DeletedDocument : AuditedEntity
{
    public InvoiceKind Kind { get; set; } = InvoiceKind.Invoice;

    public string Number { get; set; } = "";

    public DateOnly DocumentDate { get; set; }

    public decimal GrossTotal { get; set; }

    public string? RecipientName { get; set; }

    /// <summary>Who deleted it (the name as it was then).</summary>
    public string DeletedByName { get; set; } = "";

    public DateTime DeletedDate { get; set; }

    /// <summary>Why (a test invoice by default), as the administrator wrote it.</summary>
    public string? Reason { get; set; }
}
