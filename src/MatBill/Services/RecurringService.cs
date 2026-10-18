using MatBill.Configuration;
using MatBill.Core.Billing;
using MatBill.Data;
using MatBill.Data.Entities;
using MatBill.Localization;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace MatBill.Services;

/// <summary>
/// Series of invoices. A series is a template; on each of its dates it makes an invoice of its own: the customer's current address, the template's positions and
/// texts, the date of the series as the invoice date, the next number. That invoice is issued at once (number, PDF), sent by mail when the series says so, and
/// never changes when the template changes later.
/// </summary>
public sealed class RecurringService(MbDbContext db, ConfigStore config, InvoiceService invoices, NotificationService notifications, ActivityLogger activity,
    TimeProvider clock, ILogger<RecurringService> logger)
{
    /// <summary>How many failed attempts the scheduler makes by itself before it waits for a person.</summary>
    public const int MaxAutomaticAttempts = 5;

    public DateOnly Today => Fmt.Today(clock);

    /// <summary>A series from an invoice (a draft or an issued one): the same customer, positions and texts.</summary>
    public RecurringInvoice FromInvoice(Invoice source, RecurrenceInterval interval, DateOnly start, DateOnly? end, bool autoSend)
    {
        var series = new RecurringInvoice
        {
            CustomerId = source.CustomerId ?? throw new InvoiceException(Text.T("Billing.Series.NeedsCustomer")),
            Name = NameFor(source),
            Interval = interval,
            StartDate = start,
            EndDate = end,
            AutoSend = autoSend,
            Title = source.Title,
            IntroText = source.IntroText,
            PaymentTerms = source.PaymentTerms,
            ClosingText = source.ClosingText,
            PaymentTermDays = source.PaymentTermDays,
            SourceInvoiceId = source.Id == 0 ? null : source.Id,
            Lines = [.. source.Lines.OrderBy(l => l.Position).Select(l => InvoiceService.CopyLine(l, new RecurringInvoiceLine()))],
        };
        series.NextRunDate = NextFrom(series, Today);
        return series;
    }

    /// <summary>Takes what the editor posted into the template and the schedule of a series. Returns the problems of the input.</summary>
    public async Task<List<FieldProblem>> ApplyAsync(RecurringInvoice series, DocumentInput input, string? name, bool statePeriod, CancellationToken ct = default)
    {
        var problems = new List<FieldProblem>();
        var customer = input.CustomerId is { } id ? await db.Customer.FirstOrDefaultAsync(c => c.Id == id, ct) : null;
        if (customer is null && !string.IsNullOrWhiteSpace(input.RecipientName))
        {
            var typed = input.RecipientName.Trim();
            customer = await db.Customer.FirstOrDefaultAsync(c => c.Name.ToLower() == typed.ToLower(), ct);
        }

        if (customer is null)
        {
            problems.Add(new("Input.RecipientName", Text.T("Billing.Series.ChooseCustomer")));
        }
        else
        {
            series.CustomerId = customer.Id;
            series.Customer = customer;
        }

        series.Title = InvoiceService.Clean(input.Title) ?? "Rechnung";
        series.IntroText = InvoiceService.CleanText(input.IntroText);
        series.PaymentTerms = InvoiceService.CleanText(input.PaymentTerms);
        series.ClosingText = InvoiceService.CleanText(input.ClosingText);
        series.PaymentTermDays = Math.Clamp(input.PaymentTermDays ?? config.Current.Invoicing.PaymentTermDays, 0, 365);
        series.StatePeriod = statePeriod;
        series.Interval = input.Interval;
        series.StartDate = input.RepeatStart ?? (series.Id == 0 ? Today : series.StartDate);
        series.EndDate = input.RepeatRuns == "until" ? input.RepeatEnd : null;
        series.AutoSend = input.AutoSend;
        if (series.EndDate is { } end && end < series.StartDate)
        {
            problems.Add(new("Input.RepeatEnd", Text.T("Billing.Series.EndBeforeStart")));
        }

        InvoiceService.ApplyLines(series.Lines, input.Lines, () => new RecurringInvoiceLine(), problems);
        if (series.Lines.Count == 0)
        {
            problems.Add(new("", Text.T("Billing.Series.NoLines")));
        }

        var fallback = series.Lines.FirstOrDefault()?.Name is { Length: > 0 } first && customer is not null ? $"{customer.Name}: {first}" : customer?.Name ?? Text.T("Billing.Series.DefaultName");
        series.Name = InvoiceService.Clean(name) ?? fallback;
        if (series.Name.Length > 120)
        {
            series.Name = series.Name[..120];
        }

        return problems;
    }

    private static string NameFor(Invoice source)
    {
        var first = source.Lines.OrderBy(l => l.Position).FirstOrDefault()?.Name;
        var name = string.IsNullOrWhiteSpace(first) ? source.RecipientName ?? Text.T("Billing.Series.DefaultName") : $"{source.RecipientName}: {first}";
        return name.Length > 120 ? name[..120] : name;
    }

    /// <summary>The first date of the series on or after <paramref name="from"/> that is not after the end; null when there is none left.</summary>
    public static DateOnly? NextFrom(RecurringInvoice series, DateOnly from)
    {
        var next = Recurrence.OnOrAfter(series.StartDate, series.Interval, from);
        return series.EndDate is { } end && next > end ? null : next;
    }

    /// <summary>
    /// Recomputes the next date after a change of the schedule: never a date that was invoiced already, never the past for a new series (or with
    /// <paramref name="fromToday"/>, after a pause). A date that is due and failed stays due, so that fixing the template and saving it does not skip it.
    /// </summary>
    public async Task RescheduleAsync(RecurringInvoice series, CancellationToken ct = default, bool fromToday = false)
    {
        var lastInvoiced = series.Id == 0 ? null : await db.Invoice.Where(i => i.RecurringInvoiceId == series.Id).MaxAsync(i => i.RecurringDate, ct);
        DateOnly from;
        if (series.Id == 0 || fromToday)
        {
            from = lastInvoiced is { } l && l >= Today ? l.AddDays(1) : Today;
        }
        else if (lastInvoiced is { } last)
        {
            from = last.AddDays(1);
        }
        else
        {
            from = series.NextRunDate is { } due && due < Today ? due : Today;
        }

        series.NextRunDate = series.Status == RecurringStatus.Ended ? null : NextFrom(series, from);
        if (series.NextRunDate is null && series.Status == RecurringStatus.Active)
        {
            series.Status = RecurringStatus.Ended;
        }
    }

    public async Task PauseAsync(RecurringInvoice series, CancellationToken ct = default)
    {
        series.Status = RecurringStatus.Paused;
        await db.SaveChangesAsync(ct);
        await activity.LogAsync(Text.T("Billing.Activity.SeriesPaused"), nameof(RecurringInvoice), series.Id, series.Name);
    }

    /// <summary>Goes on with the next date from today: the dates that passed during the pause are not invoiced afterwards.</summary>
    public async Task ResumeAsync(RecurringInvoice series, CancellationToken ct = default)
    {
        series.Status = RecurringStatus.Active;
        series.FailedAttempts = 0;
        series.RetryAfterDate = null;
        await RescheduleAsync(series, ct, fromToday: true);
        await db.SaveChangesAsync(ct);
        await activity.LogAsync(Text.T("Billing.Activity.SeriesResumed"), nameof(RecurringInvoice), series.Id, series.Name);
    }

    public async Task EndAsync(RecurringInvoice series, CancellationToken ct = default)
    {
        series.Status = RecurringStatus.Ended;
        series.NextRunDate = null;
        await db.SaveChangesAsync(ct);
        await activity.LogAsync(Text.T("Billing.Activity.SeriesEnded"), nameof(RecurringInvoice), series.Id, series.Name);
    }

    /// <summary>Every active series whose date has come (and whose waiting time after a failure is over) makes its invoices, one per date that is due.</summary>
    public async Task<int> RunDueAsync(CancellationToken ct = default)
    {
        var today = Today;
        var now = clock.GetUtcNow().UtcDateTime;
        var due = await db.RecurringInvoice
            .Where(s => s.Status == RecurringStatus.Active && s.NextRunDate != null && s.NextRunDate <= today
                && s.FailedAttempts < MaxAutomaticAttempts && (s.RetryAfterDate == null || s.RetryAfterDate <= now))
            .OrderBy(s => s.NextRunDate).Select(s => s.Id).ToListAsync(ct);

        var made = 0;
        foreach (var id in due)
        {
            // A series that missed several dates (the server was off) catches up, one invoice per date, each with its own date.
            for (var guard = 0; guard < 60; guard++)
            {
                db.ChangeTracker.Clear();
                var series = await LoadAsync(id, ct);
                if (series is null || series.Status != RecurringStatus.Active || series.NextRunDate is not { } date || date > today)
                {
                    break;
                }

                var result = await RunAsync(series, manual: false, ct);
                if (result.Invoice is null)
                {
                    break;
                }

                made++;
            }
        }

        return made;
    }

    public sealed record RunResult(Invoice? Invoice, string? Error);

    /// <summary>
    /// Makes the invoice of the next date of <paramref name="series"/> now (the scheduler, or "Jetzt ausführen" / "Erneut ausführen" by hand).
    /// On failure nothing is half done: the invoice, its number and the step of the series are one transaction.
    /// </summary>
    public async Task<RunResult> RunAsync(RecurringInvoice series, bool manual, CancellationToken ct = default)
    {
        var date = series.NextRunDate ?? (manual ? Today : throw new InvalidOperationException("The series has no next date."));
        var now = clock.GetUtcNow().UtcDateTime;
        Invoice? invoice = null;
        string? error = null;

        await using (var transaction = await db.Database.BeginTransactionAsync(ct))
        {
            try
            {
                invoice = await BuildInvoiceAsync(series, date, ct);
                db.Invoice.Add(invoice);
                await db.SaveChangesAsync(ct);
                await invoices.IssueAsync(invoice, ct);
                if (series.AutoSend)
                {
                    invoice.SendState = SendState.Pending;
                    invoice.NextSendDate = now;
                }

                series.CreatedCount++;
                series.LastRunDate = now;
                series.LastError = null;
                series.FailedAttempts = 0;
                series.RetryAfterDate = null;
                series.NextRunDate = Recurrence.After(series.StartDate, series.Interval, date);
                if (series.EndDate is { } end && series.NextRunDate > end)
                {
                    series.NextRunDate = null;
                    series.Status = RecurringStatus.Ended;
                }

                db.RecurringRun.Add(new RecurringRun { RecurringInvoiceId = series.Id, ScheduledDate = date, RunDate = now, Status = RecurringRunStatus.Created, InvoiceId = invoice.Id, IsManual = manual });
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
            }
            catch (Exception ex) when (ex is InvoiceException or DbUpdateException or PostgresException or InvalidOperationException or IOException or Storage.StorageException)
            {
                await transaction.RollbackAsync(ct);
                error = ex is DbUpdateException { InnerException: PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } }
                    ? Text.T("Billing.Series.AlreadyInvoiced")
                    : ex is InvoiceException ? ex.Message : Text.T("Billing.Series.NotCreated", ex.Message);
                logger.LogWarning(ex, "Series {Id} failed for {Date}.", series.Id, date);
                invoice = null;
            }
        }

        if (error is null)
        {
            var number = invoice!.Number ?? "";
            var gross = invoice.GrossTotal;
            var name = series.Name;
            var mailed = series.AutoSend;
            await notifications.NotifyAllAsync(NotificationTone.Success,
                () => (Text.T("Notify.SeriesCreated.Title", number),
                    mailed ? Text.T("Notify.SeriesCreated.TextMailed", name, Fmt.Money(gross), Fmt.Date(date)) : Text.T("Notify.SeriesCreated.Text", name, Fmt.Money(gross), Fmt.Date(date))),
                $"/Invoices/Edit?id={invoice.Id}", $"created-{invoice.Id}", ct);
            await activity.LogAsync(Text.T("Billing.Activity.SeriesInvoiceCreated"), nameof(RecurringInvoice), series.Id, $"{series.Name} · {invoice.Number}");
            return new RunResult(invoice, null);
        }

        await RecordFailureAsync(series.Id, date, error, manual, ct);
        return new RunResult(null, error);
    }

    private async Task RecordFailureAsync(long seriesId, DateOnly date, string error, bool manual, CancellationToken ct)
    {
        db.ChangeTracker.Clear();
        var series = await db.RecurringInvoice.FirstAsync(s => s.Id == seriesId, ct);
        var now = clock.GetUtcNow().UtcDateTime;
        series.FailedAttempts++;
        series.LastError = error;
        // 15 minutes, 30, an hour, two, four: then the series waits for a person ("Erneut ausführen").
        series.RetryAfterDate = series.FailedAttempts < MaxAutomaticAttempts ? now.AddMinutes(15 * Math.Pow(2, series.FailedAttempts - 1)) : null;
        db.RecurringRun.Add(new RecurringRun { RecurringInvoiceId = series.Id, ScheduledDate = date, RunDate = now, Status = RecurringRunStatus.Failed, Message = error, IsManual = manual });
        await db.SaveChangesAsync(ct);

        var giveUp = series.FailedAttempts >= MaxAutomaticAttempts;
        var name = series.Name;
        await notifications.NotifyAllAsync(NotificationTone.Error,
            () => (Text.T("Notify.SeriesFailed.Title", name),
                giveUp ? Text.T("Notify.SeriesFailed.GiveUp", error) : manual ? error : Text.T("Notify.SeriesFailed.Retry", error)),
            $"/Recurring/Edit?id={series.Id}", $"failed-{series.Id}-{date:yyyyMMdd}-{series.FailedAttempts}", ct);
        await activity.LogAsync(Text.T("Billing.Activity.SeriesInvoiceFailed"), nameof(RecurringInvoice), series.Id, $"{series.Name}: {error}");
    }

    private async Task<Invoice> BuildInvoiceAsync(RecurringInvoice series, DateOnly date, CancellationToken ct)
    {
        var customer = await db.Customer.FirstOrDefaultAsync(c => c.Id == series.CustomerId, ct)
            ?? throw new InvoiceException(Text.T("Billing.Series.CustomerDeleted"));
        if (series.Lines.Count == 0)
        {
            throw new InvoiceException(Text.T("Billing.Series.TemplateEmpty"));
        }

        if (series.AutoSend && string.IsNullOrWhiteSpace(customer.Email))
        {
            throw new InvoiceException(Text.T("Billing.Series.CustomerNoEmail", customer.Name));
        }

        // The invoice is the preview of this date, made real: the same code draws what the series page shows and what the series makes.
        series.Customer = customer;
        series.NextRunDate = date;
        var invoice = RecurringPreview.Of(series, invoices, config.Current.Company.SmallBusiness);
        invoice.RecurringInvoiceId = series.Id;
        invoice.RecurringDate = date;
        return invoice;
    }

    /// <summary>Notes for the series that run within the next days (once per series and date).</summary>
    public async Task NotifyUpcomingAsync(CancellationToken ct = default)
    {
        var days = Math.Clamp(config.Current.Invoicing.NoticeDays, 0, 30);
        if (days == 0)
        {
            return;
        }

        var today = Today;
        var until = today.AddDays(days);
        var upcoming = await db.RecurringInvoice.AsNoTracking().Include(s => s.Customer)
            .Where(s => s.Status == RecurringStatus.Active && s.NextRunDate != null && s.NextRunDate > today && s.NextRunDate <= until)
            .ToListAsync(ct);
        foreach (var s in upcoming)
        {
            var net = s.Lines.Count == 0 ? await db.RecurringInvoiceLine.Where(l => l.RecurringInvoiceId == s.Id).ToListAsync(ct) : s.Lines;
            var totals = InvoiceMath.Totals(net.Select(l => l.Amounts), config.Current.Company.SmallBusiness);
            var customer = s.Customer?.Name ?? "";
            await notifications.NotifyAllAsync(NotificationTone.Info,
                () => (Text.T("Notify.Upcoming.Title", Fmt.Date(s.NextRunDate), s.Name),
                    s.AutoSend ? Text.T("Notify.Upcoming.TextAutoSend", customer, Fmt.Money(totals.Gross)) : Text.T("Notify.Upcoming.Text", customer, Fmt.Money(totals.Gross))),
                $"/Recurring/Edit?id={s.Id}", $"upcoming-{s.Id}-{s.NextRunDate:yyyyMMdd}", ct);
        }
    }

    public Task<RecurringInvoice?> LoadAsync(long id, CancellationToken ct = default)
        => db.RecurringInvoice.Include(s => s.Lines.OrderBy(l => l.Position)).Include(s => s.Customer).FirstOrDefaultAsync(s => s.Id == id, ct);
}

/// <summary>Runs the series and sends the waiting mails every minute; writes the notes about upcoming invoices.</summary>
public sealed class BillingScheduler(IServiceScopeFactory scopes, ILogger<BillingScheduler> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken);
            var lastNotice = DateOnly.MinValue;
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    using var scope = scopes.CreateScope();
                    var user = scope.ServiceProvider.GetRequiredService<ICurrentUser>();
                    using (user.RunAs(null, Text.T("System.Automatic")))
                    {
                        var recurring = scope.ServiceProvider.GetRequiredService<RecurringService>();
                        await recurring.RunDueAsync(stoppingToken);
                        await scope.ServiceProvider.GetRequiredService<MailService>().SendPendingAsync(stoppingToken);
                        if (recurring.Today != lastNotice)
                        {
                            await recurring.NotifyUpcomingAsync(stoppingToken);
                            lastNotice = recurring.Today;
                        }
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "The billing run failed.");
                }

                await Task.Delay(Interval, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // the application is stopping
        }
    }
}
