using MatBill.Configuration;
using MatBill.Core.Billing;
using MatBill.Data;
using MatBill.Data.Entities;
using MatBill.Localization;
using Microsoft.EntityFrameworkCore;

namespace MatBill.Services;

/// <summary>A problem of a form: the key of the field (or empty for the whole form) and the message.</summary>
public sealed record FieldProblem(string Key, string Message);

/// <summary>
/// The life of an invoice: a draft is written and changed freely; issuing it gives it its number, fixes the date and stores the PDF, and from then on it is never
/// changed again (it is paid, or cancelled and written anew). The pages and the series go through this class, so the rules live in one place.
/// </summary>
public sealed class InvoiceService(MbDbContext db, ConfigStore config, NumberService numbers, BrandingService branding, Storage.FileStoreService files,
    ActivityLogger activity, TimeProvider clock, ILogger<InvoiceService> logger)
{
    public DateOnly Today => Fmt.Today(clock);

    /// <summary>A new draft with the texts of the configuration, for <paramref name="customer"/> if one is given. Not saved.</summary>
    public Invoice NewDraft(Customer? customer = null)
    {
        var defaults = config.Current.Invoicing;
        var invoice = new Invoice
        {
            Title = "Rechnung",
            InvoiceDate = Today,
            PaymentTermDays = customer?.PaymentTermDays ?? defaults.PaymentTermDays,
            IntroText = defaults.IntroText,
            PaymentTerms = defaults.PaymentTerms,
            ClosingText = defaults.ClosingText,
            TaxExempt = config.Current.Company.SmallBusiness,
        };
        if (customer is not null)
        {
            FillRecipient(invoice, customer);
        }

        invoice.DueDate = InvoiceMath.DueDate(invoice.InvoiceDate, invoice.PaymentTermDays);
        return invoice;
    }

    public static void FillRecipient(Invoice invoice, Customer customer)
    {
        invoice.CustomerId = customer.Id;
        invoice.Customer = customer;
        invoice.RecipientName = customer.Name;
        invoice.RecipientContact = customer.ContactName;
        invoice.RecipientStreet = customer.Street;
        invoice.RecipientPostalCode = customer.PostalCode;
        invoice.RecipientCity = customer.City;
        invoice.RecipientCountry = customer.Country;
        invoice.RecipientEmail = customer.Email;
    }

    /// <summary>Takes what the editor posted into a draft. Returns the problems of the input (a position with a price but no name, a number that is no number).</summary>
    public List<FieldProblem> Apply(Invoice invoice, DocumentInput input)
    {
        if (!invoice.IsDraft)
        {
            throw new InvalidOperationException("An issued invoice cannot be changed.");
        }

        var problems = new List<FieldProblem>();
        invoice.CustomerId = input.CustomerId;
        invoice.RecipientName = Clean(input.RecipientName);
        invoice.RecipientContact = Clean(input.RecipientContact);
        invoice.RecipientStreet = Clean(input.RecipientStreet);
        invoice.RecipientPostalCode = Clean(input.RecipientPostalCode);
        invoice.RecipientCity = Clean(input.RecipientCity);
        invoice.RecipientCountry = Clean(input.RecipientCountry) ?? "Deutschland";
        invoice.RecipientEmail = Clean(input.RecipientEmail);
        invoice.Title = Clean(input.Title) ?? "Rechnung";
        invoice.InvoiceDate = input.InvoiceDate ?? Today;
        invoice.ServiceFrom = input.ServiceFrom;
        invoice.ServiceTo = input.ServiceTo is { } to && input.ServiceFrom is { } from && to < from ? from : input.ServiceTo;
        invoice.PaymentTermDays = Math.Clamp(input.PaymentTermDays ?? config.Current.Invoicing.PaymentTermDays, 0, 365);
        invoice.DueDate = InvoiceMath.DueDate(invoice.InvoiceDate, invoice.PaymentTermDays);
        invoice.IntroText = CleanText(input.IntroText);
        invoice.PaymentTerms = CleanText(input.PaymentTerms);
        invoice.ClosingText = CleanText(input.ClosingText);
        if (invoice.Kind == InvoiceKind.Invoice)
        {
            invoice.TaxExempt = config.Current.Company.SmallBusiness;
        }

        ApplyLines(invoice.Lines, input.Lines, () => new InvoiceLine(), problems);
        Recalculate(invoice);
        return problems;
    }

    /// <summary>
    /// Every invoice belongs to a customer: a recipient that was typed instead of chosen becomes one (or is linked to the customer of that exact name), with the
    /// address of the invoice. A customer chosen in the editor gets the e-mail address of the invoice when it had none.
    /// </summary>
    public async Task LinkCustomerAsync(Invoice invoice, CancellationToken ct = default)
    {
        Customer? customer = invoice.CustomerId is { } id ? await db.Customer.FirstOrDefaultAsync(c => c.Id == id, ct) : null;
        if (customer is null && !string.IsNullOrWhiteSpace(invoice.RecipientName))
        {
            var name = invoice.RecipientName.Trim();
            customer = await db.Customer.FirstOrDefaultAsync(c => c.Name.ToLower() == name.ToLower(), ct);
            if (customer is null)
            {
                customer = new Customer
                {
                    Number = await numbers.NextCustomerNumberAsync(Today, ct),
                    Name = name,
                    ContactName = invoice.RecipientContact,
                    Street = invoice.RecipientStreet,
                    PostalCode = invoice.RecipientPostalCode,
                    City = invoice.RecipientCity,
                    Country = invoice.RecipientCountry ?? "Deutschland",
                    Email = invoice.RecipientEmail,
                };
                db.Customer.Add(customer);
            }
        }

        if (customer is not null && string.IsNullOrWhiteSpace(customer.Email) && !string.IsNullOrWhiteSpace(invoice.RecipientEmail))
        {
            customer.Email = invoice.RecipientEmail;
        }

        // A new customer has no id yet: the navigation gives the invoice its key when both are saved.
        invoice.Customer = customer;
        if (customer is null || customer.Id > 0)
        {
            invoice.CustomerId = customer?.Id;
        }
    }

    /// <summary>Takes the posted positions into <paramref name="target"/> (an invoice or a series): rows that are empty are dropped, the order is the posted order.</summary>
    public static void ApplyLines<T>(List<T> target, IReadOnlyList<LineInput> posted, Func<T> create, List<FieldProblem> problems) where T : LineBase
    {
        var kept = posted.Select((line, index) => (line, index)).Where(p => !p.line.IsEmpty).ToList();
        while (target.Count > kept.Count)
        {
            target.RemoveAt(target.Count - 1);
        }

        for (var i = 0; i < kept.Count; i++)
        {
            var (input, index) = kept[i];
            if (i >= target.Count)
            {
                target.Add(create());
            }

            var line = target[i];
            line.Position = i + 1;
            line.ProductId = input.ProductId;
            line.Name = Clean(input.Name) ?? "";
            line.Description = CleanText(input.Description);
            line.Unit = Clean(input.Unit);

            if (line.Name.Length == 0)
            {
                problems.Add(new($"Input.Lines[{index}].Name", Text.T("Billing.Line.NameMissing", i + 1)));
            }

            if (string.IsNullOrWhiteSpace(input.Quantity))
            {
                line.Quantity = 1m;
            }
            else if (Amount.TryParse(input.Quantity, out var quantity) && quantity is >= -999_999 and <= 999_999)
            {
                line.Quantity = Math.Round(quantity, 3, MidpointRounding.AwayFromZero);
            }
            else
            {
                problems.Add(new($"Input.Lines[{index}].Quantity", Text.T("Billing.Line.QuantityInvalid", i + 1)));
            }

            if (string.IsNullOrWhiteSpace(input.UnitPrice))
            {
                line.UnitPrice = 0m;
            }
            else if (Amount.TryParse(input.UnitPrice, out var price) && price is >= -99_999_999 and <= 99_999_999)
            {
                line.UnitPrice = Math.Round(price, 4, MidpointRounding.AwayFromZero);
            }
            else
            {
                problems.Add(new($"Input.Lines[{index}].UnitPrice", Text.T("Billing.Line.PriceInvalid", i + 1)));
            }

            line.TaxRate = Amount.TryParse(input.TaxRate, out var rate) && rate is >= 0 and <= 100 ? rate : 19m;
        }
    }

    public static void Recalculate(Invoice invoice)
    {
        var totals = InvoiceMath.Totals(invoice.Lines.Select(l => l.Amounts), invoice.TaxExempt);
        invoice.NetTotal = totals.Net;
        invoice.TaxTotal = totals.Tax;
        invoice.GrossTotal = totals.Gross;
    }

    /// <summary>What keeps a draft from being issued: a recipient, at least one position, a name for each.</summary>
    public static List<FieldProblem> CheckIssuable(Invoice invoice)
    {
        var problems = new List<FieldProblem>();
        if (string.IsNullOrWhiteSpace(invoice.RecipientName))
        {
            problems.Add(new("Input.RecipientName", Text.T("Billing.Invoice.RecipientMissing")));
        }

        if (invoice.Lines.Count == 0)
        {
            problems.Add(new("", Text.T("Billing.Invoice.NoLines")));
        }

        if (invoice.Lines.Any(l => string.IsNullOrWhiteSpace(l.Name)))
        {
            problems.Add(new("", Text.T("Billing.Invoice.LineNameMissing")));
        }

        return problems;
    }

    /// <summary>
    /// Issues a draft: the next number, the status Open, the PDF stored for good. Runs in one transaction (the number goes back to the range when anything fails).
    /// The caller has applied and saved the draft before; <paramref name="invoice"/> must be tracked by this context.
    /// </summary>
    public async Task IssueAsync(Invoice invoice, CancellationToken ct = default)
    {
        if (!invoice.IsDraft)
        {
            throw new InvalidOperationException("Only a draft can be issued.");
        }

        var problems = CheckIssuable(invoice);
        if (problems.Count > 0)
        {
            throw new InvoiceException(problems[0].Message);
        }

        var ownTransaction = db.Database.CurrentTransaction is null;
        await using var transaction = ownTransaction ? await db.Database.BeginTransactionAsync(ct) : null;
        invoice.Customer ??= invoice.CustomerId is { } customerId ? await db.Customer.FirstOrDefaultAsync(c => c.Id == customerId, ct) : null;
        invoice.Number = await numbers.NextInvoiceNumberAsync(invoice.InvoiceDate, ct);
        invoice.Status = InvoiceStatus.Open;
        invoice.IssuedDate = clock.GetUtcNow().UtcDateTime;
        if (invoice.Kind == InvoiceKind.Invoice)
        {
            invoice.TaxExempt = config.Current.Company.SmallBusiness;
        }
        invoice.ServiceFrom ??= invoice.InvoiceDate;
        invoice.BuyerReference ??= invoice.Customer?.BuyerReference;
        invoice.DueDate = InvoiceMath.DueDate(invoice.InvoiceDate, invoice.PaymentTermDays);
        Recalculate(invoice);

        var pdf = await IssuedPdfAsync(invoice, ct);
        invoice.PdfFile = await files.SaveAsync(pdf, PdfName(invoice), "application/pdf", ct);
        await db.SaveChangesAsync(ct);
        if (transaction is not null)
        {
            // Inside a caller's transaction (a series) the caller logs, once its transaction stands.
            await transaction.CommitAsync(ct);
            await activity.LogAsync(Text.T("Billing.Activity.InvoiceIssued"), nameof(Invoice), invoice.Id, $"{invoice.Number} · {invoice.RecipientName} · {Fmt.Money(invoice.GrossTotal)}");
        }
    }

    /// <summary>
    /// The PDF that is stored when an invoice is issued: a ZUGFeRD e-invoice (PDF/A-3 with the XML of EN 16931 inside) unless the settings say a plain PDF.
    /// Should the e-invoice fail (data the standard does not accept), the plain PDF is stored and the reason is logged: issuing never fails because of it.
    /// </summary>
    private async Task<byte[]> IssuedPdfAsync(Invoice invoice, CancellationToken ct)
    {
        if (!config.Current.Invoicing.ZugferdPdf)
        {
            return await RenderAsync(invoice, ct);
        }

        try
        {
            var company = config.Current.Company;
            var pdf = InvoicePdf.Render(InvoiceDocument.Of(invoice, config.Current.Invoicing), company, await branding.GetLogoAsync(ct), pdfA: true);
            return EInvoice.Embed(pdf, EInvoice.Xml(invoice, company));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Invoice {Number}: the ZUGFeRD PDF could not be made, a plain PDF is stored.", invoice.Number);
            return await RenderAsync(invoice, ct);
        }
    }

    /// <summary>The e-invoice of an issued invoice as XML: EN 16931 (CII), or the XRechnung profile for public authorities.</summary>
    public byte[] Xml(Invoice invoice, bool xrechnung) => EInvoice.Xml(invoice, config.Current.Company, xrechnung);

    /// <summary>The PDF as it looks now (a draft, or a preview); an issued invoice is read from the store with <see cref="PdfAsync"/>.</summary>
    public async Task<byte[]> RenderAsync(Invoice invoice, CancellationToken ct = default)
        => InvoicePdf.Render(InvoiceDocument.Of(invoice, config.Current.Invoicing), config.Current.Company, await branding.GetLogoAsync(ct));

    /// <summary>The PDF of an invoice: the stored one of an issued invoice (it never changes), a fresh one of a draft.</summary>
    public async Task<byte[]> PdfAsync(Invoice invoice, CancellationToken ct = default)
    {
        if (!invoice.IsDraft && invoice.PdfFileId is { } fileId)
        {
            var file = invoice.PdfFile ?? await db.StoredFile.AsNoTracking().FirstAsync(f => f.Id == fileId, ct);
            try
            {
                return await files.ReadAllAsync(file, ct);
            }
            catch (Exception ex) when (ex is Storage.StorageException or IOException)
            {
                // The stored file is gone (a restore without files): draw it again from the data, which is the same invoice.
            }
        }

        return await RenderAsync(invoice, ct);
    }

    public static string PdfName(Invoice invoice) => Storage.FileStoreService.SafeFileName($"{(invoice.Number ?? "Entwurf")} {invoice.RecipientName}.pdf".Trim());

    public async Task MarkPaidAsync(Invoice invoice, DateOnly paidOn, CancellationToken ct = default)
    {
        if (invoice.Status != InvoiceStatus.Open)
        {
            throw new InvoiceException(Text.T("Billing.Invoice.OnlyOpenCanBePaid"));
        }

        invoice.Status = InvoiceStatus.Paid;
        invoice.PaidDate = paidOn;
        await db.SaveChangesAsync(ct);
        await activity.LogAsync(Text.T("Billing.Activity.InvoicePaid"), nameof(Invoice), invoice.Id, Text.T("Billing.Activity.PaidOn", invoice.Number ?? "", Fmt.Date(paidOn)));
    }

    public async Task ReopenAsync(Invoice invoice, CancellationToken ct = default)
    {
        if (invoice.Status != InvoiceStatus.Paid)
        {
            throw new InvoiceException(Text.T("Billing.Invoice.OnlyPaidCanReopen"));
        }

        invoice.Status = InvoiceStatus.Open;
        invoice.PaidDate = null;
        await db.SaveChangesAsync(ct);
        await activity.LogAsync(Text.T("Billing.Activity.PaymentUndone"), nameof(Invoice), invoice.Id, invoice.Number);
    }

    /// <summary>
    /// Cancels an issued invoice the way the tax office wants it: the invoice itself stays as it was, a cancellation invoice ("Stornorechnung") is written with
    /// its own number from the same range, the same positions with negative quantities and a reference to the cancelled one. Both are issued in one transaction.
    /// Returns the cancellation invoice.
    /// </summary>
    public async Task<Invoice> CancelAsync(Invoice invoice, string? reason, CancellationToken ct = default)
    {
        if (invoice.Status is not (InvoiceStatus.Open or InvoiceStatus.Paid) || invoice.Kind != InvoiceKind.Invoice)
        {
            throw new InvoiceException(Text.T("Billing.Invoice.OnlyIssuedCanCancel"));
        }

        var wasPaid = invoice.Status == InvoiceStatus.Paid;
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var cancellation = NewDraft();
        cancellation.Kind = InvoiceKind.Cancellation;
        cancellation.Title = "Stornorechnung";
        cancellation.CancelsInvoiceId = invoice.Id;
        cancellation.CancelsNumber = invoice.Number;
        cancellation.CustomerId = invoice.CustomerId;
        cancellation.Customer = invoice.Customer;
        cancellation.RecipientName = invoice.RecipientName;
        cancellation.RecipientContact = invoice.RecipientContact;
        cancellation.RecipientStreet = invoice.RecipientStreet;
        cancellation.RecipientPostalCode = invoice.RecipientPostalCode;
        cancellation.RecipientCity = invoice.RecipientCity;
        cancellation.RecipientCountry = invoice.RecipientCountry;
        cancellation.RecipientEmail = invoice.RecipientEmail;
        cancellation.BuyerReference = invoice.BuyerReference;
        cancellation.ServiceFrom = invoice.ServiceFrom;
        cancellation.ServiceTo = invoice.ServiceTo;
        cancellation.PaymentTermDays = 0;
        cancellation.IntroText = $"Hiermit stornieren wir unsere Rechnung {invoice.Number} vom {DocFmt.Date(invoice.InvoiceDate)}."
            + (string.IsNullOrWhiteSpace(reason) ? "" : $"\nGrund: {reason.Trim()}");
        cancellation.PaymentTerms = wasPaid ? "Den bereits gezahlten Betrag erstatten wir Ihnen auf Ihr Konto." : $"Die Rechnung {invoice.Number} ist damit ausgeglichen. Es ist nichts mehr zu zahlen.";
        cancellation.ClosingText = invoice.ClosingText;
        cancellation.TaxExempt = invoice.TaxExempt;
        cancellation.Lines = [.. invoice.Lines.OrderBy(l => l.Position).Select(l => { var copy = CopyLine(l, new InvoiceLine()); copy.Quantity = -l.Quantity; return copy; })];
        Recalculate(cancellation);
        db.Invoice.Add(cancellation);
        await db.SaveChangesAsync(ct);
        await IssueAsync(cancellation, ct);

        // The pair settles itself: the cancellation is closed at once, the invoice is cancelled and points to it.
        cancellation.Status = InvoiceStatus.Paid;
        cancellation.PaidDate = cancellation.InvoiceDate;
        invoice.Status = InvoiceStatus.Cancelled;
        invoice.CancelledDate = clock.GetUtcNow().UtcDateTime;
        invoice.CancelReason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        invoice.CancellationInvoiceId = cancellation.Id;
        if (invoice.SendState is SendState.Pending or SendState.Failed)
        {
            invoice.SendState = SendState.None;
        }

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        await activity.LogAsync(Text.T("Billing.Activity.InvoiceCancelled"), nameof(Invoice), invoice.Id,
            Text.T("Billing.Activity.CancelledBy", invoice.Number ?? "", cancellation.Number ?? "") + (invoice.CancelReason is null ? "" : ": " + invoice.CancelReason));
        return cancellation;
    }

    /// <summary>A new draft with the recipient, the texts and the positions of <paramref name="source"/>, dated today.</summary>
    public async Task<Invoice> DuplicateAsync(Invoice source, CancellationToken ct = default)
    {
        var copy = NewDraft();
        copy.CustomerId = source.CustomerId;
        copy.RecipientName = source.RecipientName;
        copy.RecipientContact = source.RecipientContact;
        copy.RecipientStreet = source.RecipientStreet;
        copy.RecipientPostalCode = source.RecipientPostalCode;
        copy.RecipientCity = source.RecipientCity;
        copy.RecipientCountry = source.RecipientCountry;
        copy.RecipientEmail = source.RecipientEmail;
        copy.Title = source.Title;
        copy.PaymentTermDays = source.PaymentTermDays;
        copy.DueDate = InvoiceMath.DueDate(copy.InvoiceDate, copy.PaymentTermDays);
        copy.IntroText = source.IntroText;
        copy.PaymentTerms = source.PaymentTerms;
        copy.ClosingText = source.ClosingText;
        copy.Lines = [.. source.Lines.OrderBy(l => l.Position).Select(l => CopyLine(l, new InvoiceLine()))];
        Recalculate(copy);
        db.Invoice.Add(copy);
        await db.SaveChangesAsync(ct);
        return copy;
    }

    public static T CopyLine<T>(LineBase from, T to) where T : LineBase
    {
        to.Position = from.Position;
        to.ProductId = from.ProductId;
        to.Name = from.Name;
        to.Description = from.Description;
        to.Quantity = from.Quantity;
        to.Unit = from.Unit;
        to.UnitPrice = from.UnitPrice;
        to.TaxRate = from.TaxRate;
        return to;
    }

    public async Task DeleteDraftAsync(Invoice invoice, CancellationToken ct = default)
    {
        if (!invoice.IsDraft)
        {
            throw new InvoiceException(Text.T("Billing.Invoice.IssuedCannotDelete"));
        }

        db.Invoice.Remove(invoice);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>The invoice with its positions and customer, tracked; null when there is none.</summary>
    public Task<Invoice?> FindAsync(long id, CancellationToken ct = default)
        => db.Invoice.Include(i => i.Lines.OrderBy(l => l.Position)).Include(i => i.Customer).Include(i => i.PdfFile).FirstOrDefaultAsync(i => i.Id == id, ct);

    internal static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>A text of several lines: trimmed at the ends, the line breaks kept and made uniform.</summary>
    internal static string? CleanText(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
}

/// <summary>A rule of invoices was broken; the message is for the person.</summary>
public sealed class InvoiceException(string message) : Exception(message);
