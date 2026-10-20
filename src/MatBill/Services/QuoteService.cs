using MatBill.Configuration;
using MatBill.Core.Billing;
using MatBill.Data;
using MatBill.Data.Entities;
using MatBill.Localization;
using Microsoft.EntityFrameworkCore;

namespace MatBill.Services;

/// <summary>
/// Quotes: written in the same editor as invoices, issued with a number of their own range and a stored PDF, answered (accepted, declined) and turned into an
/// invoice draft with the same positions. An issued quote is not changed any more; a new version is a copy.
/// </summary>
public sealed class QuoteService(MbDbContext db, ConfigStore config, NumberService numbers, BrandingService branding, Storage.FileStoreService files,
    InvoiceService invoices, ActivityLogger activity, TimeProvider clock)
{
    public const string QuoteRange = "quote";

    public DateOnly Today => Fmt.Today(clock);

    public Quote NewDraft(Customer? customer = null)
    {
        var i = config.Current.Invoicing;
        var quote = new Quote
        {
            QuoteDate = Today,
            ValidityDays = i.QuoteValidityDays,
            IntroText = i.QuoteIntroText,
            Terms = i.QuoteTerms,
            ClosingText = i.ClosingText,
            TaxExempt = config.Current.Company.SmallBusiness,
        };
        quote.ValidUntil = quote.QuoteDate.AddDays(quote.ValidityDays);
        if (customer is not null)
        {
            Fill(quote, customer);
        }

        return quote;
    }

    private static void Fill(Quote q, Customer c)
    {
        q.CustomerId = c.Id;
        q.Customer = c;
        q.RecipientName = c.Name;
        q.RecipientContact = c.ContactName;
        q.RecipientStreet = c.Street;
        q.RecipientPostalCode = c.PostalCode;
        q.RecipientCity = c.City;
        q.RecipientCountry = c.Country;
        q.RecipientEmail = c.Email;
    }

    /// <summary>Takes what the editor posted into a draft (the field PaymentTermDays of the editor is the validity in days here).</summary>
    public List<FieldProblem> Apply(Quote quote, DocumentInput input)
    {
        if (!quote.IsDraft)
        {
            throw new InvalidOperationException("An issued quote cannot be changed.");
        }

        var problems = new List<FieldProblem>();
        quote.CustomerId = input.CustomerId;
        quote.RecipientName = InvoiceService.Clean(input.RecipientName);
        quote.RecipientContact = InvoiceService.Clean(input.RecipientContact);
        quote.RecipientStreet = InvoiceService.Clean(input.RecipientStreet);
        quote.RecipientPostalCode = InvoiceService.Clean(input.RecipientPostalCode);
        quote.RecipientCity = InvoiceService.Clean(input.RecipientCity);
        quote.RecipientCountry = InvoiceService.Clean(input.RecipientCountry) ?? "Deutschland";
        quote.RecipientEmail = InvoiceService.Clean(input.RecipientEmail);
        quote.Title = InvoiceService.Clean(input.Title) ?? "Angebot";
        quote.QuoteDate = input.InvoiceDate ?? Today;
        quote.ValidityDays = Math.Clamp(input.PaymentTermDays ?? config.Current.Invoicing.QuoteValidityDays, 1, 365);
        quote.ValidUntil = quote.QuoteDate.AddDays(quote.ValidityDays);
        quote.IntroText = InvoiceService.CleanText(input.IntroText);
        quote.Terms = InvoiceService.CleanText(input.PaymentTerms);
        quote.ClosingText = InvoiceService.CleanText(input.ClosingText);
        quote.TaxExempt = config.Current.Company.SmallBusiness;
        InvoiceService.ApplyLines(quote.Lines, input.Lines, () => new QuoteLine(), problems);
        Recalculate(quote);
        return problems;
    }

    public static void Recalculate(Quote quote)
    {
        var totals = InvoiceMath.Totals(quote.Lines.Select(l => l.Amounts), quote.TaxExempt);
        quote.NetTotal = totals.Net;
        quote.TaxTotal = totals.Tax;
        quote.GrossTotal = totals.Gross;
    }

    /// <summary>Links a typed recipient to a customer (or makes one), like an invoice does.</summary>
    public async Task LinkCustomerAsync(Quote quote, CancellationToken ct = default)
    {
        var stand = new Invoice
        {
            CustomerId = quote.CustomerId, RecipientName = quote.RecipientName, RecipientContact = quote.RecipientContact, RecipientStreet = quote.RecipientStreet,
            RecipientPostalCode = quote.RecipientPostalCode, RecipientCity = quote.RecipientCity, RecipientCountry = quote.RecipientCountry, RecipientEmail = quote.RecipientEmail,
        };
        await invoices.LinkCustomerAsync(stand, ct);
        quote.Customer = stand.Customer;
        if (stand.Customer is null || stand.Customer.Id > 0)
        {
            quote.CustomerId = stand.Customer?.Id;
        }
    }

    public async Task IssueAsync(Quote quote, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(quote.RecipientName) || quote.Lines.Count == 0 || quote.Lines.Any(l => string.IsNullOrWhiteSpace(l.Name)))
        {
            throw new InvoiceException(Text.T("Billing.Quote.Incomplete"));
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        quote.Number = await numbers.NextAsync(QuoteRange, config.Current.Invoicing.QuoteNumberPattern, quote.QuoteDate, ct);
        quote.Status = QuoteStatus.Open;
        quote.IssuedDate = clock.GetUtcNow().UtcDateTime;
        quote.ValidUntil = quote.QuoteDate.AddDays(quote.ValidityDays);
        Recalculate(quote);
        quote.PdfFile = await files.SaveAsync(await RenderAsync(quote, ct), PdfName(quote), "application/pdf", ct);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        await activity.LogAsync(Text.T("Billing.Activity.QuoteIssued"), nameof(Quote), quote.Id, $"{quote.Number} · {quote.RecipientName} · {Fmt.Money(quote.GrossTotal)}");
    }

    public async Task<byte[]> RenderAsync(Quote quote, CancellationToken ct = default)
        => InvoicePdf.Render(InvoiceDocument.Of(quote, config.Current.Invoicing), config.Current.Company, await branding.GetLogoAsync(ct));

    /// <summary>The pages as they look now as PNG images: the preview inside the app.</summary>
    public async Task<List<byte[]>> RenderPagesAsync(Quote quote, CancellationToken ct = default)
        => [.. InvoicePdf.RenderImages(InvoiceDocument.Of(quote, config.Current.Invoicing), config.Current.Company, await branding.GetLogoAsync(ct), dpi: 110)];

    public async Task<byte[]> PdfAsync(Quote quote, CancellationToken ct = default)
    {
        if (!quote.IsDraft && quote.PdfFileId is { } fileId)
        {
            var file = quote.PdfFile ?? await db.StoredFile.AsNoTracking().FirstAsync(f => f.Id == fileId, ct);
            try
            {
                return await files.ReadAllAsync(file, ct);
            }
            catch (Exception ex) when (ex is Storage.StorageException or IOException)
            {
                // drawn again from the data, which is the same quote
            }
        }

        return await RenderAsync(quote, ct);
    }

    public static string PdfName(Quote quote) => Storage.FileStoreService.SafeFileName($"{quote.Number ?? "Angebotsentwurf"} {quote.RecipientName}.pdf".Trim());

    public async Task AnswerAsync(Quote quote, bool accepted, CancellationToken ct = default)
    {
        if (quote.Status is not (QuoteStatus.Open or QuoteStatus.Accepted or QuoteStatus.Declined))
        {
            throw new InvoiceException(Text.T("Billing.Quote.OnlyIssuedCanAnswer"));
        }

        quote.Status = accepted ? QuoteStatus.Accepted : QuoteStatus.Declined;
        quote.AnsweredDate = clock.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);
        await activity.LogAsync(accepted ? Text.T("Billing.Activity.QuoteAccepted") : Text.T("Billing.Activity.QuoteDeclined"), nameof(Quote), quote.Id, quote.Number);
    }

    /// <summary>An invoice draft from the quote: recipient, positions and closing text; the quote is marked as invoiced.</summary>
    public async Task<Invoice> ToInvoiceAsync(Quote quote, CancellationToken ct = default)
    {
        if (quote.IsDraft)
        {
            throw new InvoiceException(Text.T("Billing.Quote.IssueFirst"));
        }

        if (quote.InvoiceId is { } existing && await db.Invoice.AnyAsync(i => i.Id == existing, ct))
        {
            throw new InvoiceException(Text.T("Billing.Quote.AlreadyInvoiced"));
        }

        var invoice = invoices.NewDraft(quote.Customer);
        invoice.CustomerId = quote.CustomerId;
        invoice.RecipientName = quote.RecipientName;
        invoice.RecipientContact = quote.RecipientContact;
        invoice.RecipientStreet = quote.RecipientStreet;
        invoice.RecipientPostalCode = quote.RecipientPostalCode;
        invoice.RecipientCity = quote.RecipientCity;
        invoice.RecipientCountry = quote.RecipientCountry;
        invoice.RecipientEmail = quote.RecipientEmail;
        invoice.QuoteId = quote.Id;
        invoice.IntroText = $"{config.Current.Invoicing.IntroText}\n(zu unserem Angebot {quote.Number} vom {DocFmt.Date(quote.QuoteDate)})".Trim();
        invoice.Lines = [.. quote.Lines.OrderBy(l => l.Position).Select(l => InvoiceService.CopyLine(l, new InvoiceLine()))];
        InvoiceService.Recalculate(invoice);
        db.Invoice.Add(invoice);
        await db.SaveChangesAsync(ct);
        quote.InvoiceId = invoice.Id;
        quote.Status = QuoteStatus.Invoiced;
        await db.SaveChangesAsync(ct);
        await activity.LogAsync(Text.T("Billing.Activity.QuoteInvoiced"), nameof(Quote), quote.Id, quote.Number);
        return invoice;
    }

    public async Task<Quote> DuplicateAsync(Quote source, CancellationToken ct = default)
    {
        var copy = NewDraft(source.Customer);
        copy.CustomerId = source.CustomerId;
        copy.RecipientName = source.RecipientName;
        copy.RecipientContact = source.RecipientContact;
        copy.RecipientStreet = source.RecipientStreet;
        copy.RecipientPostalCode = source.RecipientPostalCode;
        copy.RecipientCity = source.RecipientCity;
        copy.RecipientCountry = source.RecipientCountry;
        copy.RecipientEmail = source.RecipientEmail;
        copy.Title = source.Title;
        copy.IntroText = source.IntroText;
        copy.Terms = source.Terms;
        copy.ClosingText = source.ClosingText;
        copy.ValidityDays = source.ValidityDays;
        copy.ValidUntil = copy.QuoteDate.AddDays(copy.ValidityDays);
        copy.Lines = [.. source.Lines.OrderBy(l => l.Position).Select(l => InvoiceService.CopyLine(l, new QuoteLine()))];
        Recalculate(copy);
        db.Quote.Add(copy);
        await db.SaveChangesAsync(ct);
        return copy;
    }

    public async Task DeleteDraftAsync(Quote quote, CancellationToken ct = default)
    {
        if (!quote.IsDraft)
        {
            throw new InvoiceException(Text.T("Billing.Quote.IssuedCannotDelete"));
        }

        db.Quote.Remove(quote);
        await db.SaveChangesAsync(ct);
    }

    public Task<Quote?> FindAsync(long id, CancellationToken ct = default)
        => db.Quote.Include(q => q.Lines.OrderBy(l => l.Position)).Include(q => q.Customer).Include(q => q.PdfFile).FirstOrDefaultAsync(q => q.Id == id, ct);
}
