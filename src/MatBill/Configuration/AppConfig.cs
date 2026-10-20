using System.Text.Json.Serialization;

namespace MatBill.Configuration;

/// <summary>
/// The configuration (JSON, <c>{data}/config/app.json</c>): how this installation is deployed and what the administrator set up.
/// The sections <see cref="Database"/> and <see cref="Server"/> can be overridden by environment variables
/// (<c>MATBILL__Database__Host</c>); the rest is edited on the administration pages.
/// </summary>
public sealed class AppConfig
{
    public DatabaseConfig Database { get; set; } = new();

    public ServerConfig Server { get; set; } = new();

    public CompanyConfig Company { get; set; } = new();

    public InvoicingConfig Invoicing { get; set; } = new();

    public MailConfig Mail { get; set; } = new();

    public StorageConfig Storage { get; set; } = new();

    public SecurityConfig Security { get; set; } = new();

    public BackupConfig Backup { get; set; } = new();

    public BankingConfig Banking { get; set; } = new();
}

/// <summary>
/// The company that writes the invoices, and how its invoices look (the branding): the sender block, the footer with bank and tax numbers, the logo and the
/// colour of the document. An issued invoice keeps the look it had (its PDF is stored), so a change here only shows on what comes next.
/// </summary>
public sealed class CompanyConfig
{
    public string Name { get; set; } = "";

    /// <summary>A second line of the name, under it in the sender block and in the sender line ("Matthias Schmoldt" under the trading name "Matthix").</summary>
    public string? NameLine2 { get; set; }

    /// <summary>The owner or managing director ("Inhaber: Jonas Dierksen", "Geschäftsführer: ..."), a line of the footer.</summary>
    public string? Manager { get; set; }

    public string? Street { get; set; }

    public string? PostalCode { get; set; }

    public string? City { get; set; }

    public string Country { get; set; } = "Deutschland";

    public string? Email { get; set; }

    public string? Phone { get; set; }

    public string? Website { get; set; }

    /// <summary>"Steuernummer": an invoice needs it or the VAT id.</summary>
    public string? TaxNumber { get; set; }

    public string? VatId { get; set; }

    /// <summary>Registry court and number ("Amtsgericht München, HRB 12345").</summary>
    public string? Register { get; set; }

    public string? BankName { get; set; }

    public string? Iban { get; set; }

    public string? Bic { get; set; }

    public string? AccountHolder { get; set; }

    /// <summary>The colour of headings and the total on the document ("#1d4ed8").</summary>
    public string AccentColor { get; set; } = "#1d4ed8";

    /// <summary>The token of the stored logo (a <c>StoredFile</c>); empty: the name is written instead.</summary>
    public string? LogoToken { get; set; }

    /// <summary>A line under the payment terms, in the handwriting of the document ("Danke!"); empty shows nothing.</summary>
    public string? Signature { get; set; } = "Danke!";

    /// <summary>The head of the document: <see cref="DocumentLayouts.Classic"/> (logo left, sender right), <see cref="DocumentLayouts.Centered"/> (logo centred above
    /// the sender in one line) or <see cref="DocumentLayouts.Right"/> (sender left, logo right). The same in the editor, the view and the PDF.</summary>
    public string Layout { get; set; } = DocumentLayouts.Classic;

    /// <summary>Small business (§ 19 UStG): no VAT is charged, the invoice says why.</summary>
    public bool SmallBusiness { get; set; }

    public string SenderLine => string.Join(" · ", new[] { Name, NameLine2, Street, string.Join(" ", new[] { PostalCode, City }.Where(s => !string.IsNullOrWhiteSpace(s))) }.Where(s => !string.IsNullOrWhiteSpace(s)));
}

/// <summary>The number ranges and the texts a new invoice starts with.</summary>
public sealed class InvoicingConfig
{
    /// <summary>The pattern of invoice numbers (see <see cref="Core.Billing.NumberPattern"/>).</summary>
    public string QuoteNumberPattern { get; set; } = "AN-{YYYY}-{NNNN}";

    /// <summary>The own receipt numbers of expenses ("Belegnummer").</summary>
    public string ExpenseNumberPattern { get; set; } = "BE-{YYYY}-{NNNN}";

    public int QuoteValidityDays { get; set; } = 30;

    public string QuoteIntroText { get; set; } = "Guten Tag,\ngerne bieten wir Ihnen folgende Leistungen an:";

    public string QuoteTerms { get; set; } = "Dieses Angebot ist gültig bis {ValidUntil}. Wir freuen uns auf Ihren Auftrag.";

    /// <summary>The categories of expenses (one list for the select and the reports).</summary>
    public List<string> ExpenseCategories { get; set; } =
    [
        "Büro und Material", "Software und Abos", "Telefon und Internet", "Fremdleistungen", "Wareneinkauf", "Reisen", "Bewirtung", "Fahrzeug",
        "Miete und Nebenkosten", "Werbung", "Versicherungen", "Fortbildung", "Bankgebühren", "Sonstiges",
    ];

    /// <summary>Every issued invoice is a ZUGFeRD e-invoice: a PDF/A-3 with the structured data (EN 16931) inside.</summary>
    public bool ZugferdPdf { get; set; } = true;

    public string InvoiceNumberPattern { get; set; } = "RE-{YYYY}-{NNNN}";

    public string CustomerNumberPattern { get; set; } = "K-{NNNN}";

    /// <summary>A customer number can be typed when a customer is created; left empty (and when this is off) it comes from the range.</summary>
    public bool CustomerNumberManual { get; set; }

    public int PaymentTermDays { get; set; } = 14;

    public string IntroText { get; set; } = "Hallo,\nvielen Dank für die gute Zusammenarbeit. Hiermit berechnen wir folgende Leistungen:";

    /// <summary>The payment terms; <c>{Days}</c> becomes the days of the term, <c>{DueDate}</c> the due date.</summary>
    public string PaymentTerms { get; set; } = "Bitte überweisen Sie den Rechnungsbetrag innerhalb von {Days} Tagen auf das unten angegebene Konto.";

    public string ClosingText { get; set; } = "Mit freundlichen Grüßen";

    /// <summary>The note of a small business, printed instead of the tax.</summary>
    public string SmallBusinessNote { get; set; } = "Gemäß § 19 UStG wird keine Umsatzsteuer berechnet.";

    /// <summary>How many days before a series runs the users get a note.</summary>
    public int NoticeDays { get; set; } = 3;

    public decimal DefaultTaxRate { get; set; } = 19m;
}

public enum MailSecurity
{
    /// <summary>STARTTLS when the server offers it (port 587).</summary>
    Auto,
    StartTls,
    /// <summary>TLS from the first byte (port 465).</summary>
    SslOnConnect,
    /// <summary>No encryption (only inside a trusted network).</summary>
    None,
}

/// <summary>The mail server that sends invoices, and the text of the mail. <c>{Number}</c>, <c>{Customer}</c>, <c>{Amount}</c>, <c>{DueDate}</c> and <c>{Company}</c> are filled in.</summary>
public sealed class MailConfig
{
    public string Host { get; set; } = "";

    public int Port { get; set; } = 587;

    public MailSecurity Security { get; set; } = MailSecurity.Auto;

    public string UserName { get; set; } = "";

    public string PasswordProtected { get; set; } = "";

    public string FromAddress { get; set; } = "";

    public string FromName { get; set; } = "";

    /// <summary>A copy of every invoice mail goes here (the own archive); empty sends none.</summary>
    public string Bcc { get; set; } = "";

    public string Subject { get; set; } = "Rechnung {Number} von {Company}";

    public string Body { get; set; } = "Guten Tag,\n\nanbei erhalten Sie unsere Rechnung {Number} über {Amount}, fällig am {DueDate}.\n\nMit freundlichen Grüßen\n{Company}";

    public string QuoteSubject { get; set; } = "Angebot {Number} von {Company}";

    public string QuoteBody { get; set; } = "Guten Tag,\n\nanbei erhalten Sie unser Angebot {Number} über {Amount}, gültig bis {ValidUntil}.\n\nMit freundlichen Grüßen\n{Company}";

    [JsonIgnore]
    public bool IsConfigured => !string.IsNullOrWhiteSpace(Host) && !string.IsNullOrWhiteSpace(FromAddress);
}

public sealed class DatabaseConfig
{
    public string Host { get; set; } = "db";

    public int Port { get; set; } = 5432;

    public string Database { get; set; } = "matbill";

    public string Username { get; set; } = "postgres";

    public string Password { get; set; } = "matbill";
}

public sealed class ServerConfig
{
    /// <summary>The address people use, for the links that are handed on ("https://rechnungen.example.de"). Empty: taken from the request.</summary>
    public string PublicUrl { get; set; } = "";

    /// <summary>"Europe/Berlin": the time zone of the installation, for dates and schedules.</summary>
    public string TimeZone { get; set; } = "Europe/Berlin";

    /// <summary>The cookie is marked secure when the site is reached over HTTPS (also behind a reverse proxy that says so).</summary>
    public bool ForceSecureCookie { get; set; }
}

public enum StorageMode
{
    /// <summary>The files are kept in the data volume.</summary>
    Local,
    /// <summary>The files are kept on a share of a NAS (SMB).</summary>
    Smb,
}

public sealed class StorageConfig
{
    public StorageMode Mode { get; set; } = StorageMode.Local;

    public string Host { get; set; } = "";

    public int Port { get; set; } = 445;

    public string Share { get; set; } = "";

    /// <summary>A folder inside the share.</summary>
    public string Path { get; set; } = "MatBill";

    public string Domain { get; set; } = "";

    public string UserName { get; set; } = "";

    public string PasswordProtected { get; set; } = "";
}

public sealed class SecurityConfig
{
    public int SessionDays { get; set; } = 14;

    /// <summary>Administrators have to set up an authenticator app.</summary>
    public bool RequireTotpForAdmins { get; set; }

    /// <summary>Default life of a share link of an invoice, in days.</summary>
    public int ShareLinkDays { get; set; } = 30;

    [JsonIgnore]
    public int MaxFailedLogins => 5;
}

public sealed class BackupConfig
{
    public bool Enabled { get; set; } = true;

    /// <summary>The scratch folder of a backup; empty: {data}/tmp.</summary>
    public string TempDirectory { get; set; } = "";
}

/// <summary>
/// The online connection to the banks through Enable Banking (PSD2, all European banks): the id of the application and its private key (protected with the key
/// ring), from enablebanking.com. Statements can always be imported as files as well.
/// </summary>
public sealed class BankingConfig
{
    public string EnableBankingAppId { get; set; } = "";

    public string EnableBankingKeyProtected { get; set; } = "";

    /// <summary>The country whose banks are offered first.</summary>
    public string Country { get; set; } = "DE";

    /// <summary>Assign payments to invoices and expenses by themselves when it is clear (else they are suggested).</summary>
    public bool AutoMatch { get; set; } = true;

    /// <summary>How often connected accounts are fetched, in hours (banks allow about four fetches a day without the person).</summary>
    public int SyncHours { get; set; } = 6;

    [JsonIgnore]
    public bool IsOnlineConfigured => !string.IsNullOrWhiteSpace(EnableBankingAppId) && !string.IsNullOrWhiteSpace(EnableBankingKeyProtected);
}

/// <summary>The layouts of the head of a document (<see cref="CompanyConfig.Layout"/>).</summary>
public static class DocumentLayouts
{
    public const string Classic = "classic";

    public const string Centered = "centered";

    public const string Right = "right";

    public static readonly string[] All = [Classic, Centered, Right];

    public static string Of(string? layout) => All.Contains(layout) ? layout! : Classic;
}
