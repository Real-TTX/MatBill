using MatBill.Core.Billing;
using MatBill.Data.Entities;
using MatBill.Localization;
using Microsoft.AspNetCore.Html;

namespace MatBill.UiKit;

/// <summary>
/// The words for the enums of the data model in the language of the interface (Resources/Texts/Labels.json), and the tone of a status pill
/// (the colour is never alone: the pill carries the word).
/// </summary>
public static class Labels
{
    public static string Of(UserRole r) => Text.T(r == UserRole.Admin ? "Labels.Role.Admin" : "Labels.Role.User");

    public static string Of(InvoiceStatus s) => s switch
    {
        InvoiceStatus.Draft => Text.T("Labels.Invoice.Draft"),
        InvoiceStatus.Open => Text.T("Labels.Invoice.Open"),
        InvoiceStatus.Paid => Text.T("Labels.Invoice.Paid"),
        _ => Text.T("Labels.Invoice.Cancelled"),
    };

    /// <summary>The status as a person reads it: an open invoice past its due date is "Overdue".</summary>
    public static IHtmlContent Pill(Invoice invoice, DateOnly today)
        => invoice.Kind == InvoiceKind.Cancellation ? Ui.Pill(Text.T("Labels.Invoice.Cancellation"), "plain")
            : invoice.IsOverdue(today) ? Ui.Pill(Text.T("Labels.Invoice.Overdue"), "error")
            : invoice.Status switch
            {
                InvoiceStatus.Draft => Ui.Pill(Of(invoice.Status), ""),
                InvoiceStatus.Open => Ui.Pill(Of(invoice.Status), "info"),
                InvoiceStatus.Paid => Ui.Pill(Of(invoice.Status), "ok"),
                _ => Ui.Pill(Of(invoice.Status), "plain"),
            };

    public static string Of(RecurrenceInterval i) => i switch
    {
        RecurrenceInterval.Weekly => Text.T("Labels.Interval.Weekly"),
        RecurrenceInterval.Monthly => Text.T("Labels.Interval.Monthly"),
        RecurrenceInterval.Quarterly => Text.T("Labels.Interval.Quarterly"),
        RecurrenceInterval.HalfYearly => Text.T("Labels.Interval.HalfYearly"),
        _ => Text.T("Labels.Interval.Yearly"),
    };

    public static string Of(RecurringStatus s) => s switch
    {
        RecurringStatus.Active => Text.T("Labels.Recurring.Active"),
        RecurringStatus.Paused => Text.T("Labels.Recurring.Paused"),
        _ => Text.T("Labels.Recurring.Ended"),
    };

    /// <summary>The status of a series; a series whose last attempt failed says so first.</summary>
    public static IHtmlContent Pill(RecurringInvoice s)
        => s.LastError is not null && s.Status == RecurringStatus.Active ? Ui.Pill(Text.T("Labels.Recurring.Failed"), "error") : s.Status switch
        {
            RecurringStatus.Active => Ui.Pill(Of(s.Status), "ok"),
            RecurringStatus.Paused => Ui.Pill(Of(s.Status), "warn"),
            _ => Ui.Pill(Of(s.Status), "plain"),
        };

    public static string Of(QuoteStatus s) => s switch
    {
        QuoteStatus.Draft => Text.T("Labels.Quote.Draft"),
        QuoteStatus.Open => Text.T("Labels.Quote.Open"),
        QuoteStatus.Accepted => Text.T("Labels.Quote.Accepted"),
        QuoteStatus.Declined => Text.T("Labels.Quote.Declined"),
        _ => Text.T("Labels.Quote.Invoiced"),
    };

    /// <summary>The status of a quote; an open quote past its date is "Expired".</summary>
    public static IHtmlContent Pill(Quote q, DateOnly today)
        => q.IsExpired(today) ? Ui.Pill(Text.T("Labels.Quote.Expired"), "warn") : q.Status switch
        {
            QuoteStatus.Draft => Ui.Pill(Of(q.Status), ""),
            QuoteStatus.Open => Ui.Pill(Of(q.Status), "info"),
            QuoteStatus.Accepted => Ui.Pill(Of(q.Status), "ok"),
            QuoteStatus.Declined => Ui.Pill(Of(q.Status), "plain"),
            _ => Ui.Pill(Of(q.Status), "accent"),
        };

    public static string Of(CustomerKind k) => Text.T(k == CustomerKind.Company ? "Labels.Customer.Company" : "Labels.Customer.Person");

    public static string Of(ProductKind k) => Text.T(k == ProductKind.Service ? "Labels.Product.Service" : "Labels.Product.Product");

    public static string Of(SendState s) => s switch
    {
        SendState.Pending => Text.T("Labels.Send.Pending"),
        SendState.Sent => Text.T("Labels.Send.Sent"),
        SendState.Failed => Text.T("Labels.Send.Failed"),
        _ => Text.T("Labels.Send.NotSent"),
    };

    public static string Of(BackupScope s) => Text.T(s == BackupScope.SystemOnly ? "Labels.BackupScope.SystemOnly" : "Labels.BackupScope.All");

    public static string Of(BackupTargetKind k) => Text.T(k == BackupTargetKind.Smb ? "Labels.BackupTarget.Smb" : "Labels.BackupTarget.Folder");

    public static string Of(ScheduleKind k) => k switch
    {
        ScheduleKind.Hourly => Text.T("Labels.Schedule.Hourly"),
        ScheduleKind.Daily => Text.T("Labels.Schedule.Daily"),
        ScheduleKind.Weekly => Text.T("Labels.Schedule.Weekly"),
        _ => Text.T("Labels.Schedule.Monthly"),
    };

    public static string Of(DayOfWeek d) => Services.Fmt.Culture.DateTimeFormat.GetDayName(d);

    public static string Tone(NotificationTone t) => t switch
    {
        NotificationTone.Success => "ok",
        NotificationTone.Warning => "warn",
        NotificationTone.Error => "error",
        _ => "info",
    };

    public static string Icon(NotificationTone t) => t switch
    {
        NotificationTone.Success => "check-circle",
        NotificationTone.Warning => "alert",
        NotificationTone.Error => "x-circle",
        _ => "info",
    };
}
