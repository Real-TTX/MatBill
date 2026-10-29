using System.Globalization;
using MatBill.Configuration;
using MatBill.Data;
using MatBill.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace MatBill.Services;

// ------------------------------------------------------------------------------------------------ what a client sends (JSON, camelCase)

/// <summary>An invoice that another program asks for (POST /api/v1/invoice-requests). Described in docs/API.md.</summary>
public sealed class InvoiceRequestInput
{
    /// <summary>The client's own key of this invoice ("matmon:2026-10:org:…"): the same reference twice is the same invoice.</summary>
    public string? Reference { get; set; }

    public InvoiceRequestCustomer? Customer { get; set; }

    public List<InvoiceRequestLine>? Lines { get; set; }

    public InvoiceRequestPeriod? ServicePeriod { get; set; }

    /// <summary>"Rechnung" when empty.</summary>
    public string? Title { get; set; }

    /// <summary>The buyer's reference (order number, Leitweg-ID): BT-10 of the e-invoice. Else the one of the customer.</summary>
    public string? BuyerReference { get; set; }

    public int? PaymentTermDays { get; set; }

    /// <summary>For whoever checks the draft in MatBill; not printed.</summary>
    public string? Note { get; set; }

    /// <summary>Issue at once (only a client that may issue directly); otherwise the request becomes a draft.</summary>
    public bool Issue { get; set; }
}

public sealed class InvoiceRequestCustomer
{
    /// <summary>The client's key of the customer ("matmon:org:&lt;guid&gt;"): per client, one customer per key.</summary>
    public string? ExternalRef { get; set; }

    public string? Name { get; set; }

    /// <summary>The person to address ("z. Hd. Frau Keller").</summary>
    public string? Contact { get; set; }

    /// <summary>Street and house number, more lines are joined with ", ".</summary>
    public List<string>? AddressLines { get; set; }

    public string? Postcode { get; set; }

    public string? City { get; set; }

    /// <summary>A country name or an ISO code ("DE"); "Deutschland" when empty.</summary>
    public string? Country { get; set; }

    public string? VatId { get; set; }

    public string? Email { get; set; }
}

public sealed class InvoiceRequestLine
{
    public string? Text { get; set; }

    public string? Description { get; set; }

    public decimal? Quantity { get; set; }

    public string? Unit { get; set; }

    /// <summary>Net, per unit.</summary>
    public decimal? UnitPrice { get; set; }

    /// <summary>In percent; the default rate of the settings when empty.</summary>
    public decimal? TaxRate { get; set; }

    public DateOnly? ServicePeriodFrom { get; set; }

    public DateOnly? ServicePeriodTo { get; set; }
}

public sealed class InvoiceRequestPeriod
{
    public DateOnly? From { get; set; }

    public DateOnly? To { get; set; }
}

/// <summary>A request that cannot be served: the HTTP status, a stable code for programs, a sentence for people (English) and the problems of the fields.</summary>
public sealed class ApiRequestException(int status, string code, string message, IReadOnlyList<string>? problems = null) : Exception(message)
{
    public int Status { get; } = status;

    public string Code { get; } = code;

    public IReadOnlyList<string> Problems { get; } = problems ?? [];
}

/// <summary>
/// Invoices that other programs ask for. A request becomes a draft in MatBill (somebody checks and issues it there), or, for a client that may issue directly
/// and asks for it, an issued invoice with its number from the client's own range. The reference makes a request idempotent: asking twice gives the same
/// invoice. The customer is found by the client's key of it, else made; what the request says about the customer updates it (issued invoices keep their own
/// copy of the address, so that never changes an invoice).
/// </summary>
public sealed class InvoiceRequestService(MbDbContext db, InvoiceService invoices, NumberService numbers, ConfigStore config, ActivityLogger activity)
{
    public const int MaxLines = 500;

    /// <summary>The invoice of the request: created (true) or the one that the reference already stands for (false).</summary>
    public async Task<(Invoice Invoice, bool Created)> CreateAsync(ApiClient client, InvoiceRequestInput input, CancellationToken ct = default)
    {
        if (!client.CanCreate)
        {
            throw new ApiRequestException(403, "create_not_allowed", "This API client may not create invoice requests.");
        }

        var problems = Check(input);
        if (problems.Count > 0)
        {
            throw new ApiRequestException(400, "invalid_request", "The request is not complete or not valid.", problems);
        }

        if (input.Issue && !client.CanIssue)
        {
            // refused rather than turned into a draft: the client asked for an issued invoice and must not believe it has one
            throw new ApiRequestException(403, "issue_not_allowed",
                "This API client may not issue invoices directly. Send the request without \"issue\" (it becomes a draft that is issued in MatBill), or allow direct issuing for the client in MatBill.");
        }

        // a repeated request answers the invoice as it is now (a draft stays a draft; the body is not compared)
        var reference = input.Reference!.Trim();
        if (await FindLiveByReferenceAsync(client.Id, reference, ct) is { } existing)
        {
            return (existing, false);
        }

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return (await CreateNewAsync(client, input, reference, ct), true);
            }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } && attempt == 0)
            {
                // the same request at the same moment (or the same new customer): the other one won; its invoice is the answer, else try once more
                db.ChangeTracker.Clear();
                if (await FindLiveByReferenceAsync(client.Id, reference, ct) is { } raced)
                {
                    return (raced, false);
                }
            }
        }
    }

    private async Task<Invoice> CreateNewAsync(ApiClient client, InvoiceRequestInput input, string reference, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var customer = await CustomerAsync(client, input.Customer!, ct);
        var invoice = invoices.NewDraft(customer);
        invoice.ApiClientId = client.Id;
        invoice.ApiReference = reference;
        invoice.ApiNote = InvoiceService.CleanText(input.Note);
        invoice.Title = InvoiceService.Clean(input.Title) ?? "Rechnung";
        invoice.BuyerReference = InvoiceService.Clean(input.BuyerReference) ?? customer.BuyerReference;
        if (input.PaymentTermDays is { } days)
        {
            invoice.PaymentTermDays = days;
            invoice.DueDate = Core.Billing.InvoiceMath.DueDate(invoice.InvoiceDate, days);
        }

        var lines = input.Lines!;
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var description = InvoiceService.CleanText(line.Description);
            if (line.ServicePeriodFrom is { } from)
            {
                // the period of a position stands under it (German, it is part of the document)
                var period = "Leistungszeitraum: " + DocFmt.Date(from) + (line.ServicePeriodTo is { } to && to != from ? " – " + DocFmt.Date(to) : "");
                description = description is null ? period : description + "\n" + period;
            }

            invoice.Lines.Add(new InvoiceLine
            {
                Position = i + 1,
                Name = line.Text!.Trim(),
                Description = description,
                Quantity = Math.Round(line.Quantity ?? 1m, 3, MidpointRounding.AwayFromZero),
                Unit = InvoiceService.Clean(line.Unit),
                UnitPrice = Math.Round(line.UnitPrice!.Value, 4, MidpointRounding.AwayFromZero),
                TaxRate = line.TaxRate ?? config.Current.Invoicing.DefaultTaxRate,
            });
        }

        // the period of the invoice: the one of the request, else the span of the periods of its positions
        var starts = lines.Where(l => l.ServicePeriodFrom is not null).Select(l => l.ServicePeriodFrom!.Value).ToList();
        var ends = lines.Select(l => l.ServicePeriodTo ?? l.ServicePeriodFrom).Where(d => d is not null).Select(d => d!.Value).ToList();
        invoice.ServiceFrom = input.ServicePeriod?.From ?? (starts.Count > 0 ? starts.Min() : null);
        invoice.ServiceTo = input.ServicePeriod?.To ?? (ends.Count > 0 ? ends.Max() : null);
        if (invoice.ServiceTo < invoice.ServiceFrom)
        {
            invoice.ServiceTo = invoice.ServiceFrom;
        }

        InvoiceService.Recalculate(invoice);
        db.Invoice.Add(invoice);
        await db.SaveChangesAsync(ct);
        if (input.Issue)
        {
            await invoices.IssueAsync(invoice, ct);
        }

        await transaction.CommitAsync(ct);
        await activity.LogAsync(Localization.Text.T(input.Issue ? "Api.Activity.Issued" : "Api.Activity.Requested", client.Name), nameof(Invoice), invoice.Id,
            $"{invoice.Number ?? reference} · {invoice.RecipientName} · {Fmt.Money(invoice.GrossTotal)}");
        return invoice;
    }

    /// <summary>
    /// The customer the client knows by <see cref="InvoiceRequestCustomer.ExternalRef"/>, made when there is none. What the request says about it wins:
    /// the client keeps the master data of its customers, and an invoice copies the address when it is written, so an issued invoice never changes by this.
    /// </summary>
    private async Task<Customer> CustomerAsync(ApiClient client, InvoiceRequestCustomer input, CancellationToken ct)
    {
        var externalRef = input.ExternalRef!.Trim();
        var customer = await db.Customer.FirstOrDefaultAsync(c => c.ApiClientId == client.Id && c.ExternalRef == externalRef, ct);
        if (customer is null)
        {
            customer = new Customer { ApiClientId = client.Id, ExternalRef = externalRef, Number = await numbers.NextCustomerNumberAsync(invoices.Today, ct) };
            db.Customer.Add(customer);
        }

        customer.Name = input.Name!.Trim();
        customer.ContactName = InvoiceService.Clean(input.Contact) ?? customer.ContactName;
        if (input.AddressLines is { Count: > 0 } lines)
        {
            customer.Street = InvoiceService.Clean(string.Join(", ", lines.Select(l => l?.Trim()).Where(l => !string.IsNullOrEmpty(l))));
        }

        customer.PostalCode = InvoiceService.Clean(input.Postcode) ?? customer.PostalCode;
        customer.City = InvoiceService.Clean(input.City) ?? customer.City;
        customer.Country = Country(input.Country) ?? customer.Country;
        customer.VatId = InvoiceService.Clean(input.VatId) ?? customer.VatId;
        customer.Email = InvoiceService.Clean(input.Email) ?? customer.Email;
        await db.SaveChangesAsync(ct);
        return customer;
    }

    /// <summary>"DE" and "Germany" are written the way the documents write the country; anything else stays as sent.</summary>
    private static string? Country(string? value)
    {
        var country = InvoiceService.Clean(value);
        return country?.ToUpperInvariant() is "DE" or "DEU" or "GERMANY" ? "Deutschland" : country;
    }

    /// <summary>What is missing or wrong in a request, one sentence per problem (English; the API is for programs and their authors).</summary>
    public static List<string> Check(InvoiceRequestInput input)
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(input.Reference))
        {
            problems.Add("reference is required (the client's own key of this invoice; the same reference twice is the same invoice).");
        }
        else if (input.Reference.Trim().Length > 200)
        {
            problems.Add("reference is longer than 200 characters.");
        }

        if (input.Customer is not { } c)
        {
            problems.Add("customer is required.");
        }
        else
        {
            if (string.IsNullOrWhiteSpace(c.ExternalRef))
            {
                problems.Add("customer.externalRef is required (the client's own key of the customer).");
            }
            else if (c.ExternalRef.Trim().Length > 200)
            {
                problems.Add("customer.externalRef is longer than 200 characters.");
            }

            if (string.IsNullOrWhiteSpace(c.Name))
            {
                problems.Add("customer.name is required.");
            }
            else if (c.Name.Trim().Length > 200)
            {
                problems.Add("customer.name is longer than 200 characters.");
            }

            if (c.Email is { Length: > 0 } email && (!email.Contains('@') || email.Trim().Length > 200))
            {
                problems.Add("customer.email is not an e-mail address.");
            }

            if (c.AddressLines is { Count: > 3 })
            {
                problems.Add("customer.addressLines has more than 3 lines.");
            }
        }

        if (input.Lines is not { Count: > 0 } lines)
        {
            problems.Add("lines must hold at least one position.");
        }
        else if (lines.Count > MaxLines)
        {
            problems.Add($"lines holds more than {MaxLines} positions.");
        }
        else
        {
            for (var i = 0; i < lines.Count; i++)
            {
                var l = lines[i];
                var at = $"lines[{i.ToString(CultureInfo.InvariantCulture)}]";
                if (l is null)
                {
                    problems.Add($"{at} is empty.");
                    continue;
                }

                if (string.IsNullOrWhiteSpace(l.Text))
                {
                    problems.Add($"{at}.text is required.");
                }
                else if (l.Text.Trim().Length > 200)
                {
                    problems.Add($"{at}.text is longer than 200 characters.");
                }

                if (l.UnitPrice is not { } price)
                {
                    problems.Add($"{at}.unitPrice is required (net, per unit).");
                }
                else if (price is < -99_999_999 or > 99_999_999)
                {
                    problems.Add($"{at}.unitPrice is out of range.");
                }

                if (l.Quantity is { } q && q is < -999_999 or > 999_999)
                {
                    problems.Add($"{at}.quantity is out of range.");
                }

                if (l.TaxRate is { } rate && rate is < 0 or > 100)
                {
                    problems.Add($"{at}.taxRate must be between 0 and 100.");
                }

                if (l.ServicePeriodTo is not null && l.ServicePeriodFrom is null)
                {
                    problems.Add($"{at}.servicePeriodTo needs servicePeriodFrom.");
                }
                else if (l.ServicePeriodTo < l.ServicePeriodFrom)
                {
                    problems.Add($"{at}.servicePeriodTo is before servicePeriodFrom.");
                }
            }
        }

        if (input.ServicePeriod is { From: { } pf, To: { } pt } && pt < pf)
        {
            problems.Add("servicePeriod.to is before servicePeriod.from.");
        }

        if (input.PaymentTermDays is < 0 or > 365)
        {
            problems.Add("paymentTermDays must be between 0 and 365.");
        }

        if (input.Note is { Length: > 2000 })
        {
            problems.Add("note is longer than 2000 characters.");
        }

        if (input.Title is { Length: > 100 })
        {
            problems.Add("title is longer than 100 characters.");
        }

        return problems;
    }

    private Task<Invoice?> FindLiveByReferenceAsync(long clientId, string reference, CancellationToken ct)
        => db.Invoice.FirstOrDefaultAsync(i => i.ApiClientId == clientId && i.ApiReference == reference, ct);

    /// <summary>An invoice of the client (also a draft that was deleted in MatBill, which answers "deleted"); null for any other.</summary>
    public Task<Invoice?> FindAsync(ApiClient client, long id, CancellationToken ct = default)
        => db.Invoice.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(i => i.Id == id && i.ApiClientId == client.Id, ct);

    /// <summary>The invoice of a reference: the live one, else the newest deleted one.</summary>
    public Task<Invoice?> FindByReferenceAsync(ApiClient client, string reference, CancellationToken ct = default)
        => db.Invoice.IgnoreQueryFilters().AsNoTracking()
            .Where(i => i.ApiClientId == client.Id && i.ApiReference == reference)
            .OrderBy(i => i.UpdateState == UpdateState.Deleted).ThenByDescending(i => i.Id)
            .FirstOrDefaultAsync(ct);

    /// <summary>What the API tells about an invoice: its state, number, dates, sums, customer and where its documents are.</summary>
    public async Task<object> DescribeAsync(Invoice invoice, Func<string, string> absolute, CancellationToken ct = default)
    {
        var deleted = invoice.UpdateState == UpdateState.Deleted;
        var customer = invoice.CustomerId is { } cid
            ? await db.Customer.IgnoreQueryFilters().AsNoTracking().Where(c => c.Id == cid).Select(c => new { c.Id, c.Number, c.ExternalRef }).FirstOrDefaultAsync(ct)
            : null;
        var cancellation = invoice.CancellationInvoiceId is { } sid
            ? await db.Invoice.AsNoTracking().Where(i => i.Id == sid).Select(i => new { i.Id, i.Number }).FirstOrDefaultAsync(ct)
            : null;
        var issued = invoice.Number is not null && !deleted;
        var self = $"/api/v1/invoice-requests/{invoice.Id}";
        return new
        {
            id = invoice.Id,
            reference = invoice.ApiReference,
            kind = invoice.Kind == InvoiceKind.Cancellation ? "cancellation" : "invoice",
            status = deleted ? "deleted" : invoice.Status.ToString().ToLowerInvariant(),
            number = invoice.Number,
            invoiceDate = issued ? invoice.InvoiceDate : (DateOnly?)null,
            dueDate = issued ? invoice.DueDate : (DateOnly?)null,
            servicePeriod = invoice.ServiceFrom is null ? null : new { from = invoice.ServiceFrom, to = invoice.ServiceTo ?? invoice.ServiceFrom },
            issuedAt = invoice.IssuedDate is { } at ? DateTime.SpecifyKind(at, DateTimeKind.Utc) : (DateTime?)null,
            paidDate = invoice.Kind == InvoiceKind.Invoice ? invoice.PaidDate : null,
            cancelledAt = invoice.CancelledDate is { } c ? DateTime.SpecifyKind(c, DateTimeKind.Utc) : (DateTime?)null,
            currency = "EUR",
            netTotal = invoice.NetTotal,
            taxTotal = invoice.TaxTotal,
            grossTotal = invoice.GrossTotal,
            customer = customer is null ? null : new { id = customer.Id, number = customer.Number, externalRef = customer.ExternalRef },
            cancellation = cancellation is null ? null : new { id = cancellation.Id, number = cancellation.Number, url = absolute($"/api/v1/invoice-requests/{cancellation.Id}") },
            cancels = invoice.CancelsInvoiceId is { } cancels ? new { id = cancels, number = invoice.CancelsNumber } : null,
            links = new
            {
                self = absolute(self),
                pdf = issued ? absolute($"/api/v1/invoices/{invoice.Id}/pdf") : null,
                xml = issued ? absolute($"/api/v1/invoices/{invoice.Id}/xml") : null,
                xrechnung = issued ? absolute($"/api/v1/invoices/{invoice.Id}/xml?xrechnung=1") : null,
            },
        };
    }
}
