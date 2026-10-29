namespace MatBill.Data.Entities;

/// <summary>
/// Another program that may ask for invoices through the API (<c>/api/v1</c>), for example Matmon.Cloud that bills its partners. It authenticates with a key
/// ("mbk_" and 43 random characters) that is shown once when it is made; the database keeps its SHA-256 only. A client is never deleted, only revoked: its
/// invoices keep pointing to it, so their origin stays visible.
/// </summary>
public class ApiClient : AuditedEntity
{
    /// <summary>Who asks ("Matmon"): shown on the invoices it requested.</summary>
    public string Name { get; set; } = "";

    /// <summary>The first characters of the key, to recognise it in the list ("mbk_Ab3dE…"); never enough to use it.</summary>
    public string KeyPrefix { get; set; } = "";

    /// <summary>The SHA-256 of the key, lower-case hex.</summary>
    public string KeyHash { get; set; } = "";

    /// <summary>May create invoice requests (drafts, or issued invoices with <see cref="CanIssue"/>).</summary>
    public bool CanCreate { get; set; } = true;

    /// <summary>May read the state of its requests and the documents of its issued invoices.</summary>
    public bool CanRead { get; set; } = true;

    /// <summary>
    /// May issue directly ("issue": true in a request). Off: every request lands as a draft that somebody checks and issues in MatBill.
    /// </summary>
    public bool CanIssue { get; set; }

    /// <summary>
    /// The pattern of the client's own invoice number range ("RE-MAT-{YYYY}-{NNNN}"), counted in the range <c>invoice:client:&lt;id&gt;</c>;
    /// null or empty: its invoices take the normal invoice range.
    /// </summary>
    public string? NumberPattern { get; set; }

    public DateTime? RevokedDate { get; set; }

    public DateTime? LastUsedDate { get; set; }

    public string? LastUsedIp { get; set; }

    public bool IsActive => RevokedDate is null;
}
