using MatBill.Core.Billing;
using MatBill.Data.Entities;
using Microsoft.AspNetCore.Html;

namespace MatBill.UiKit;

/// <summary>The German words for the enums of the data model, and the tone of a status pill (the colour is never alone: the pill carries the word).</summary>
public static class Labels
{
    public static string Of(UserRole r) => r == UserRole.Admin ? "Administrator" : "Benutzer";

    public static string Of(InvoiceStatus s) => s switch
    {
        InvoiceStatus.Draft => "Entwurf",
        InvoiceStatus.Open => "Offen",
        InvoiceStatus.Paid => "Bezahlt",
        _ => "Storniert",
    };

    /// <summary>The status as a person reads it: an open invoice past its due date is "Überfällig".</summary>
    public static IHtmlContent Pill(Invoice invoice, DateOnly today)
        => invoice.IsOverdue(today) ? Ui.Pill("Überfällig", "error") : invoice.Status switch
        {
            InvoiceStatus.Draft => Ui.Pill("Entwurf", ""),
            InvoiceStatus.Open => Ui.Pill("Offen", "info"),
            InvoiceStatus.Paid => Ui.Pill("Bezahlt", "ok"),
            _ => Ui.Pill("Storniert", "plain"),
        };

    public static string Of(RecurrenceInterval i) => i switch
    {
        RecurrenceInterval.Weekly => "Wöchentlich",
        RecurrenceInterval.Monthly => "Monatlich",
        RecurrenceInterval.Quarterly => "Quartalsweise",
        RecurrenceInterval.HalfYearly => "Halbjährlich",
        _ => "Jährlich",
    };

    public static string Of(RecurringStatus s) => s switch
    {
        RecurringStatus.Active => "Aktiv",
        RecurringStatus.Paused => "Pausiert",
        _ => "Beendet",
    };

    /// <summary>The status of a series; a series whose last attempt failed says so first.</summary>
    public static IHtmlContent Pill(RecurringInvoice s)
        => s.LastError is not null && s.Status == RecurringStatus.Active ? Ui.Pill("Fehler", "error") : s.Status switch
        {
            RecurringStatus.Active => Ui.Pill("Aktiv", "ok"),
            RecurringStatus.Paused => Ui.Pill("Pausiert", "warn"),
            _ => Ui.Pill("Beendet", "plain"),
        };

    public static string Of(CustomerKind k) => k == CustomerKind.Company ? "Firma" : "Person";

    public static string Of(ProductKind k) => k == ProductKind.Service ? "Leistung" : "Produkt";

    public static string Of(SendState s) => s switch
    {
        SendState.Pending => "wartet auf Versand",
        SendState.Sent => "versendet",
        SendState.Failed => "Versand fehlgeschlagen",
        _ => "nicht versendet",
    };

    public static string Of(BackupScope s) => s == BackupScope.SystemOnly ? "System (Datenbank und Einstellungen)" : "System und Dateien";

    public static string Of(BackupTargetKind k) => k == BackupTargetKind.Smb ? "NAS (SMB-Freigabe)" : "Ordner auf dem Server";

    public static string Of(ScheduleKind k) => k switch
    {
        ScheduleKind.Hourly => "Stündlich",
        ScheduleKind.Daily => "Täglich",
        ScheduleKind.Weekly => "Wöchentlich",
        _ => "Monatlich",
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
