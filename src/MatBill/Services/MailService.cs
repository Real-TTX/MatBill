using MailKit.Net.Smtp;
using MailKit.Security;
using MatBill.Configuration;
using MatBill.Data;
using MatBill.Data.Entities;
using MatBill.Localization;
using Microsoft.EntityFrameworkCore;
using MimeKit;

namespace MatBill.Services;

/// <summary>A mail as the send page or a series wants it to go out.</summary>
public sealed record InvoiceMail(string To, string? Cc, string Subject, string Body);

/// <summary>
/// Sends invoices as PDF attachments over the configured SMTP server. Every attempt is written down (<see cref="MailDelivery"/>), the invoice says whether it went
/// out (<see cref="SendState"/>); a mail of a series that fails is tried again by the scheduler (three times, after 10 minutes, an hour and six hours) and can be
/// sent again by hand at any time.
/// </summary>
public sealed class MailService(MbDbContext db, ConfigStore config, SecretProtector secrets, InvoiceService invoices, NotificationService notifications,
    ActivityLogger activity, TimeProvider clock, ILogger<MailService> logger)
{
    public const int MaxAutomaticAttempts = 3;

    private static readonly TimeSpan[] Backoff = [TimeSpan.FromMinutes(10), TimeSpan.FromHours(1), TimeSpan.FromHours(6)];

    public bool IsConfigured => config.Current.Mail.IsConfigured;

    /// <summary>The mail an invoice gets by default: to the address on the invoice, the subject and text of the configuration.</summary>
    public InvoiceMail DefaultMail(Invoice invoice)
    {
        var mail = config.Current.Mail;
        var company = config.Current.Company;
        return new InvoiceMail(invoice.RecipientEmail ?? invoice.Customer?.Email ?? "", null, Texts.Mail(mail.Subject, invoice, company), Texts.Mail(mail.Body, invoice, company));
    }

    /// <summary>Sends <paramref name="invoice"/> (issued, tracked) and records the attempt. Returns null when it went out, else the reason in words.</summary>
    public async Task<string?> SendAsync(Invoice invoice, InvoiceMail mail, bool automatic, CancellationToken ct = default)
    {
        if (invoice.IsDraft)
        {
            return Text.T("Billing.Mail.DraftCannotSend");
        }

        var now = clock.GetUtcNow().UtcDateTime;
        string? error = null;
        try
        {
            var message = await BuildAsync(invoice, mail, ct);
            await DeliverAsync(message, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            error = Describe(ex);
            logger.LogWarning(ex, "Invoice {Number} could not be sent.", invoice.Number);
        }

        db.MailDelivery.Add(new MailDelivery
        {
            InvoiceId = invoice.Id,
            To = mail.To,
            Cc = mail.Cc,
            Subject = mail.Subject,
            Status = error is null ? MailStatus.Sent : MailStatus.Failed,
            Error = error,
            AttemptDate = now,
            IsAutomatic = automatic,
        });

        if (error is null)
        {
            invoice.SendState = SendState.Sent;
            invoice.SentDate = now;
            invoice.SendError = null;
            invoice.NextSendDate = null;
        }
        else
        {
            invoice.SendState = SendState.Failed;
            invoice.SendError = error;
            invoice.SendAttempts++;
            invoice.NextSendDate = automatic && invoice.SendAttempts < MaxAutomaticAttempts ? now + Backoff[Math.Min(invoice.SendAttempts - 1, Backoff.Length - 1)] : null;
        }

        await db.SaveChangesAsync(ct);
        await activity.LogAsync(error is null ? Text.T("Billing.Activity.InvoiceSent") : Text.T("Billing.Activity.InvoiceSendFailed"), nameof(Invoice), invoice.Id,
            Text.T("Billing.Activity.SentTo", invoice.Number ?? "", mail.To) + (error is null ? "" : ": " + error));
        return error;
    }

    /// <summary>The invoices of series that wait for their mail, and failed ones whose next attempt is due: sends them. Called by the scheduler.</summary>
    public async Task<int> SendPendingAsync(CancellationToken ct = default)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        if (!IsConfigured)
        {
            return 0;
        }

        var due = await db.Invoice
            .Where(i => i.Status == InvoiceStatus.Open && (i.SendState == SendState.Pending || (i.SendState == SendState.Failed && i.NextSendDate != null && i.NextSendDate <= now)))
            .OrderBy(i => i.Id).Select(i => i.Id).Take(20).ToListAsync(ct);
        foreach (var id in due)
        {
            var invoice = await invoices.FindAsync(id, ct);
            if (invoice is null)
            {
                continue;
            }

            var mail = DefaultMail(invoice);
            var number = invoice.Number ?? "";
            if (string.IsNullOrWhiteSpace(mail.To))
            {
                invoice.SendState = SendState.Failed;
                invoice.SendError = Text.T("Billing.Mail.NoAddress");
                invoice.NextSendDate = null;
                await db.SaveChangesAsync(ct);
                await notifications.NotifyAllAsync(NotificationTone.Error, () => (Text.T("Notify.NotSent.Title", number), Text.T("Billing.Mail.NoAddress")),
                    $"/Invoices/Edit?id={invoice.Id}", $"send-{invoice.Id}-noaddress", ct);
                continue;
            }

            var error = await SendAsync(invoice, mail, automatic: true, ct);
            if (error is null)
            {
                await notifications.NotifyAllAsync(NotificationTone.Success, () => (Text.T("Notify.Sent.Title", number), Text.T("Notify.Sent.Text", mail.To, Fmt.Money(invoice.GrossTotal))),
                    $"/Invoices/Edit?id={invoice.Id}", $"sent-{invoice.Id}", ct);
            }
            else if (invoice.NextSendDate is null)
            {
                await notifications.NotifyAllAsync(NotificationTone.Error, () => (Text.T("Notify.NotSent.Title", number), Text.T("Notify.NotSent.GiveUp", error)),
                    $"/Invoices/Edit?id={invoice.Id}", $"send-{invoice.Id}-{invoice.SendAttempts}", ct);
            }
        }

        return due.Count;
    }

    public InvoiceMail DefaultMail(Quote quote)
    {
        var mail = config.Current.Mail;
        var company = config.Current.Company;
        return new InvoiceMail(quote.RecipientEmail ?? quote.Customer?.Email ?? "", null, Texts.Mail(mail.QuoteSubject, quote, company), Texts.Mail(mail.QuoteBody, quote, company));
    }

    /// <summary>Sends an issued quote with its PDF; null when it went out, else the reason.</summary>
    public async Task<string?> SendAsync(Quote quote, InvoiceMail mail, byte[] pdf, CancellationToken ct = default)
    {
        string? error = null;
        try
        {
            var message = NewMessage(mail.To, mail.Cc, mail.Subject, mail.Body);
            var multipart = new Multipart("mixed") { message.Body ?? new TextPart("plain") { Text = mail.Body } };
            multipart.Add(new MimePart("application", "pdf")
            {
                Content = new MimeContent(new MemoryStream(pdf)),
                ContentDisposition = new ContentDisposition(ContentDisposition.Attachment),
                ContentTransferEncoding = ContentEncoding.Base64,
                FileName = QuoteService.PdfName(quote),
            });
            message.Body = multipart;
            await DeliverAsync(message, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            error = Describe(ex);
        }

        db.MailDelivery.Add(new MailDelivery { To = mail.To, Cc = mail.Cc, Subject = mail.Subject, Status = error is null ? MailStatus.Sent : MailStatus.Failed, Error = error, AttemptDate = clock.GetUtcNow().UtcDateTime });
        if (error is null)
        {
            quote.SentDate = clock.GetUtcNow().UtcDateTime;
        }

        await db.SaveChangesAsync(ct);
        await activity.LogAsync(error is null ? Text.T("Billing.Activity.QuoteSent") : Text.T("Billing.Activity.QuoteSendFailed"), nameof(Quote), quote.Id,
            Text.T("Billing.Activity.SentTo", quote.Number ?? "", mail.To) + (error is null ? "" : ": " + error));
        return error;
    }

    /// <summary>A test mail to <paramref name="to"/> with the current settings; null when it went out.</summary>
    public async Task<string?> SendTestAsync(string to, CancellationToken ct = default)
    {
        try
        {
            var message = NewMessage(to, null, Text.T("Billing.Mail.TestSubject"), Text.T("Billing.Mail.TestBody"));
            await DeliverAsync(message, ct);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Describe(ex);
        }
    }

    private async Task<MimeMessage> BuildAsync(Invoice invoice, InvoiceMail mail, CancellationToken ct)
    {
        var message = NewMessage(mail.To, mail.Cc, mail.Subject, mail.Body);
        var body = message.Body ?? new TextPart("plain") { Text = mail.Body };
        var multipart = new Multipart("mixed") { body };
        var pdf = await invoices.PdfAsync(invoice, ct);
        multipart.Add(new MimePart("application", "pdf")
        {
            Content = new MimeContent(new MemoryStream(pdf)),
            ContentDisposition = new ContentDisposition(ContentDisposition.Attachment),
            ContentTransferEncoding = ContentEncoding.Base64,
            FileName = InvoiceService.PdfName(invoice),
        });
        message.Body = multipart;
        return message;
    }

    private MimeMessage NewMessage(string to, string? cc, string subject, string text)
    {
        var settings = config.Current.Mail;
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(string.IsNullOrWhiteSpace(settings.FromName) ? config.Current.Company.Name : settings.FromName, settings.FromAddress));
        foreach (var address in Split(to))
        {
            message.To.Add(MailboxAddress.Parse(address));
        }

        foreach (var address in Split(cc))
        {
            message.Cc.Add(MailboxAddress.Parse(address));
        }

        foreach (var address in Split(settings.Bcc))
        {
            message.Bcc.Add(MailboxAddress.Parse(address));
        }

        if (!string.IsNullOrWhiteSpace(config.Current.Company.Email))
        {
            message.ReplyTo.Add(MailboxAddress.Parse(config.Current.Company.Email));
        }

        message.Subject = subject;
        message.Body = new TextPart("plain") { Text = text };
        return message;
    }

    private async Task DeliverAsync(MimeMessage message, CancellationToken ct)
    {
        var settings = config.Current.Mail;
        if (!settings.IsConfigured)
        {
            throw new InvalidOperationException(Text.T("Billing.Mail.NotConfigured"));
        }

        using var client = new SmtpClient { Timeout = 30_000 };
        var security = settings.Security switch
        {
            MailSecurity.StartTls => SecureSocketOptions.StartTls,
            MailSecurity.SslOnConnect => SecureSocketOptions.SslOnConnect,
            MailSecurity.None => SecureSocketOptions.None,
            _ => SecureSocketOptions.Auto,
        };
        await client.ConnectAsync(settings.Host, settings.Port, security, ct);
        if (!string.IsNullOrWhiteSpace(settings.UserName))
        {
            await client.AuthenticateAsync(settings.UserName, secrets.Unprotect(settings.PasswordProtected), ct);
        }

        await client.SendAsync(message, ct);
        await client.DisconnectAsync(true, ct);
    }

    private static IEnumerable<string> Split(string? addresses)
        => (addresses ?? "").Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>The reason in words a person can act on, not a stack trace.</summary>
    public static string Describe(Exception ex) => ex switch
    {
        AuthenticationException => Text.T("Billing.Mail.AuthFailed"),
        SmtpCommandException smtp => Text.T("Billing.Mail.Refused", smtp.Message),
        SmtpProtocolException => Text.T("Billing.Mail.Unexpected"),
        System.Net.Sockets.SocketException => Text.T("Billing.Mail.Unreachable"),
        SslHandshakeException => Text.T("Billing.Mail.TlsFailed"),
        ParseException => Text.T("Billing.Mail.InvalidAddress"),
        TimeoutException or OperationCanceledException => Text.T("Billing.Mail.Timeout"),
        InvalidOperationException => ex.Message,
        _ => ex.Message,
    };
}
