namespace MatBill.Data.Entities;

/// <summary>
/// The anonymous role: a link that shows one invoice read-only (and its PDF), for a limited time, optionally behind a password. The visitor has no account
/// and sees nothing but this invoice.
/// </summary>
public class ShareLink : AuditedEntity
{
    public Guid Token { get; set; } = Guid.NewGuid();

    public long InvoiceId { get; set; }

    public Invoice? Invoice { get; set; }

    public string Title { get; set; } = "";

    /// <summary>A message to the visitor, shown on the page.</summary>
    public string? Message { get; set; }

    public DateTime ExpiresDate { get; set; }

    public string? PasswordHash { get; set; }

    public string? RecipientName { get; set; }

    public DateTime? RevokedDate { get; set; }

    public int ViewCount { get; set; }

    public DateTime? LastViewDate { get; set; }

    public bool IsActive(DateTime nowUtc) => RevokedDate is null && ExpiresDate > nowUtc;
}
