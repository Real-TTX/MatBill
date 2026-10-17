namespace MatBill.Data.Entities;

/// <summary>Draft: changed freely. Open: issued (number, PDF), waiting for an answer. Accepted, Declined end it; Invoiced: an invoice was made from it.</summary>
public enum QuoteStatus
{
    Draft,
    Open,
    Accepted,
    Declined,
    Invoiced,
}

/// <summary>An offer to a customer. Written in the same editor as an invoice; an accepted quote becomes an invoice draft with its positions.</summary>
public class Quote : AuditedEntity, ISoftDeletable
{
    public string? Number { get; set; }

    public QuoteStatus Status { get; set; } = QuoteStatus.Draft;

    public long? CustomerId { get; set; }

    public Customer? Customer { get; set; }

    public string? RecipientName { get; set; }

    public string? RecipientContact { get; set; }

    public string? RecipientStreet { get; set; }

    public string? RecipientPostalCode { get; set; }

    public string? RecipientCity { get; set; }

    public string? RecipientCountry { get; set; }

    public string? RecipientEmail { get; set; }

    public string Title { get; set; } = "Angebot";

    public DateOnly QuoteDate { get; set; }

    /// <summary>How long the offer stands, in days; <see cref="ValidUntil"/> follows from it.</summary>
    public int ValidityDays { get; set; } = 30;

    public DateOnly ValidUntil { get; set; }

    public string? IntroText { get; set; }

    public string? Terms { get; set; }

    public string? ClosingText { get; set; }

    public bool TaxExempt { get; set; }

    public decimal NetTotal { get; set; }

    public decimal TaxTotal { get; set; }

    public decimal GrossTotal { get; set; }

    public DateTime? IssuedDate { get; set; }

    public DateTime? AnsweredDate { get; set; }

    public long? PdfFileId { get; set; }

    public StoredFile? PdfFile { get; set; }

    public DateTime? SentDate { get; set; }

    /// <summary>The invoice made from this quote.</summary>
    public long? InvoiceId { get; set; }

    public List<QuoteLine> Lines { get; set; } = [];

    public UpdateState UpdateState { get; set; } = UpdateState.Created;

    public bool IsDraft => Status == QuoteStatus.Draft;

    public bool IsExpired(DateOnly today) => Status == QuoteStatus.Open && ValidUntil < today;
}

public class QuoteLine : LineBase
{
    public long QuoteId { get; set; }

    public Quote? Quote { get; set; }
}

public enum ExpenseStatus
{
    Open,
    Paid,
}

/// <summary>
/// A receipt of money spent: who, when, what for, how much (net, tax, gross), the receipt itself as a file. <see cref="Number"/> is the own receipt number
/// (a range like the invoices), so that every receipt can be found in the export for the tax office.
/// </summary>
public class Expense : AuditedEntity, ISoftDeletable
{
    public string Number { get; set; } = "";

    public DateOnly ExpenseDate { get; set; }

    public string Supplier { get; set; } = "";

    public string? SupplierIban { get; set; }

    /// <summary>The invoice number of the supplier.</summary>
    public string? SupplierInvoiceNumber { get; set; }

    public string Category { get; set; } = "Sonstiges";

    public string? Description { get; set; }

    public decimal NetAmount { get; set; }

    public decimal TaxRate { get; set; } = 19m;

    public decimal TaxAmount { get; set; }

    public decimal GrossAmount { get; set; }

    public ExpenseStatus Status { get; set; } = ExpenseStatus.Open;

    public DateOnly? DueDate { get; set; }

    public DateOnly? PaidDate { get; set; }

    public long? ReceiptFileId { get; set; }

    public StoredFile? ReceiptFile { get; set; }

    /// <summary>The receipt was read from an e-invoice (ZUGFeRD, XRechnung): the amounts are the supplier's own.</summary>
    public bool FromEInvoice { get; set; }

    /// <summary>Uploaded but not yet looked at (several files dropped at once); the list shows it as "zu prüfen".</summary>
    public bool NeedsReview { get; set; }

    public string? Note { get; set; }

    public UpdateState UpdateState { get; set; } = UpdateState.Created;
}

public enum BankProvider
{
    /// <summary>Statements are imported as files (CSV, CAMT, MT940).</summary>
    File,
    /// <summary>Bookings are fetched online through Enable Banking (PSD2).</summary>
    EnableBanking,
}

public class BankAccount : AuditedEntity, ISoftDeletable
{
    public string Name { get; set; } = "";

    public string? Iban { get; set; }

    public string? Bic { get; set; }

    public string? BankName { get; set; }

    public string Currency { get; set; } = "EUR";

    public BankProvider Provider { get; set; } = BankProvider.File;

    public decimal? Balance { get; set; }

    public DateOnly? BalanceDate { get; set; }

    public long? BankConnectionId { get; set; }

    public BankConnection? BankConnection { get; set; }

    /// <summary>The id of the account at the provider (Enable Banking: the account uid).</summary>
    public string? ExternalId { get; set; }

    public DateTime? LastSyncDate { get; set; }

    public string? LastSyncError { get; set; }

    /// <summary>The column mapping of the last CSV import of this account (JSON of a CsvMapping and the headers it belongs to).</summary>
    public string? CsvMapping { get; set; }

    public bool IsActive { get; set; } = true;

    public UpdateState UpdateState { get; set; } = UpdateState.Created;
}

public enum BankConnectionStatus
{
    Pending,
    Active,
    Expired,
    Failed,
}

/// <summary>A consent at a bank through Enable Banking: started (the state of the redirect), then a session with its accounts until it runs out.</summary>
public class BankConnection : AuditedEntity
{
    /// <summary>The state of the authorization: the secret the bank sends back with the redirect.</summary>
    public Guid Token { get; set; } = Guid.NewGuid();

    public BankProvider Provider { get; set; } = BankProvider.EnableBanking;

    public string BankName { get; set; } = "";

    public string Country { get; set; } = "DE";

    public string? SessionId { get; set; }

    public DateTime? ValidUntil { get; set; }

    public BankConnectionStatus Status { get; set; } = BankConnectionStatus.Pending;

    public string? Error { get; set; }
}

public enum MatchState
{
    Open,
    Matched,
    Ignored,
}

/// <summary>A booking on a bank account. It settles an invoice (money in) or an expense (money out), or it is ignored (a transfer between own accounts).</summary>
public class BankTransaction : AuditedEntity
{
    public long BankAccountId { get; set; }

    public BankAccount? BankAccount { get; set; }

    public DateOnly BookingDate { get; set; }

    public DateOnly? ValueDate { get; set; }

    /// <summary>Signed: money that came in is positive.</summary>
    public decimal Amount { get; set; }

    public string Currency { get; set; } = "EUR";

    public string? CounterpartyName { get; set; }

    public string? CounterpartyIban { get; set; }

    public string? Purpose { get; set; }

    public string? EndToEndId { get; set; }

    /// <summary>What keeps it from being imported twice (the bank's id, or a fingerprint of date, amount, party and purpose).</summary>
    public string ImportKey { get; set; } = "";

    public long? BankImportId { get; set; }

    public MatchState State { get; set; } = MatchState.Open;

    public long? InvoiceId { get; set; }

    public Invoice? Invoice { get; set; }

    public long? ExpenseId { get; set; }

    public Expense? Expense { get; set; }

    /// <summary>Assigned by the matcher (true) or by a person.</summary>
    public bool MatchedAutomatically { get; set; }

    public int MatchScore { get; set; }

    public string? Note { get; set; }
}

/// <summary>One import of a statement file or one online fetch: what came, what was new, what was assigned.</summary>
public class BankImport : AuditedEntity
{
    public long BankAccountId { get; set; }

    public BankAccount? BankAccount { get; set; }

    public string Source { get; set; } = "";

    public string? FileName { get; set; }

    public DateTime ImportDate { get; set; }

    public int Total { get; set; }

    public int Added { get; set; }

    public int Duplicates { get; set; }

    public int Matched { get; set; }
}
