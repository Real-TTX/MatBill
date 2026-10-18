using System.Globalization;
using MatBill.Localization;

namespace MatBill.Services;

/// <summary>
/// How the interface writes dates, times, money and numbers: in the language of the request (German or English), in the time zone of the installation.
/// Documents (invoice, quote, PDF, mail, export) use <see cref="DocFmt"/>, which is always German.
/// </summary>
public static class Fmt
{
    /// <summary>The culture of the interface in the current language (de-DE or en-GB).</summary>
    public static CultureInfo Culture => Languages.FormatCulture;

    private static TimeZoneInfo _zone = TimeZoneInfo.Utc;

    public static TimeZoneInfo Zone => _zone;

    /// <summary>Called once at start-up with the time zone of the configuration; an unknown name falls back to UTC.</summary>
    public static void Configure(string timeZoneId)
    {
        try
        {
            _zone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            _zone = TimeZoneInfo.Utc;
        }
    }

    public static DateTime Local(DateTime utc)
        => TimeZoneInfo.ConvertTimeFromUtc(utc.Kind == DateTimeKind.Utc ? utc : DateTime.SpecifyKind(utc, DateTimeKind.Utc), _zone);

    public static DateOnly Today(TimeProvider clock) => DateOnly.FromDateTime(Local(clock.GetUtcNow().UtcDateTime));

    public static DateTime UtcFromLocal(DateTime local)
        => TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), _zone);

    public static string Date(DateOnly? date) => Formats.Date(date, Culture);

    public static string Date(DateTime? utc) => utc is null ? "" : Formats.Date(DateOnly.FromDateTime(Local(utc.Value)), Culture);

    public static string Moment(DateTime? utc) => utc is null ? "" : Formats.Date(DateOnly.FromDateTime(Local(utc.Value)), Culture) + Local(utc.Value).ToString(" HH:mm", Culture);

    public static string Time(DateTime? utc) => utc is null ? "" : Local(utc.Value).ToString("HH:mm", Culture);

    /// <summary>An ISO date for the value of a date input.</summary>
    public static string Iso(DateOnly? date) => date is null ? "" : date.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static string Money(decimal amount, string currency = "EUR") => Formats.Money(amount, currency, Culture);

    public static string Number(decimal value, int decimals = 2) => value.ToString("N" + decimals, Culture);

    public static string Integer(long value) => value.ToString("N0", Culture);

    public static string FileSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double size = bytes;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes} B" : $"{size.ToString("0.#", Culture)} {units[unit]}";
    }

    /// <summary>"5 min ago" up to a week, then the date.</summary>
    public static string Ago(DateTime utc, DateTime nowUtc)
    {
        var span = nowUtc - utc;
        if (span.TotalSeconds < 60) { return Text.T("Common.Ago.Now"); }
        if (span.TotalMinutes < 60) { return Text.T("Common.Ago.Minutes", (int)span.TotalMinutes); }
        if (span.TotalHours < 24) { return Text.T("Common.Ago.Hours", (int)span.TotalHours); }
        if (span.TotalDays < 7) { return Text.T("Common.Ago.Days", (int)span.TotalDays); }
        return Date(utc);
    }

    /// <summary>The first letters of the first two words of a name, for an avatar ("Sarah Keller" becomes "SK").</summary>
    public static string Initials(string? name)
    {
        var letters = string.Concat((name ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(p => char.ToUpperInvariant(p[0])));
        return letters.Length == 0 ? "?" : letters;
    }

    /// <summary>0 to 1 as "65 %".</summary>
    public static string Percent(decimal share) => Math.Round(share * 100, 0, MidpointRounding.AwayFromZero).ToString("0", Culture) + " %";

    /// <summary>A due date as people say it: "Today", "Tomorrow", "Yesterday", "in 3 days", else the date.</summary>
    public static string Due(DateOnly? due, DateOnly today)
    {
        if (due is not { } d) { return ""; }
        var days = d.DayNumber - today.DayNumber;
        return days switch
        {
            0 => Text.T("Common.Due.Today"),
            1 => Text.T("Common.Due.Tomorrow"),
            -1 => Text.T("Common.Due.Yesterday"),
            > 1 and <= 6 => Text.T("Common.Due.InDays", days),
            _ => Date(d),
        };
    }

    /// <summary>Two dates as a period: "01/07/2026 to 31/12/2026", or one of them, or nothing.</summary>
    public static string Period(DateOnly? from, DateOnly? to)
        => (from, to) switch
        {
            (null, null) => "",
            ({ } a, null) => Text.T("Common.Period.From", Date(a)),
            (null, { } b) => Text.T("Common.Period.To", Date(b)),
            ({ } a, { } b) => Text.T("Common.Period.Range", Date(a), Date(b)),
        };
}

/// <summary>
/// How documents write dates and money: always German (dd.MM.yyyy, 1.234,50 €), whatever language the person who writes them reads.
/// The invoice, the quote, the PDF, the mail to the customer, the e-invoice and the export for the tax office use this, never <see cref="Fmt"/>.
/// </summary>
public static class DocFmt
{
    public static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("de-DE");

    public static string Date(DateOnly? date) => Formats.Date(date, Culture);

    public static string Date(DateTime? utc) => utc is null ? "" : Formats.Date(DateOnly.FromDateTime(Fmt.Local(utc.Value)), Culture);

    public static string Moment(DateTime? utc) => utc is null ? "" : Fmt.Local(utc.Value).ToString("dd.MM.yyyy HH:mm", Culture);

    public static string Money(decimal amount, string currency = "EUR") => Formats.Money(amount, currency, Culture);

    public static string Number(decimal value, int decimals = 2) => value.ToString("N" + decimals, Culture);

    public static string Integer(long value) => value.ToString("N0", Culture);

    /// <summary>"01.07.2026 bis 31.12.2026", "ab 01.07.2026", "bis 31.12.2026".</summary>
    public static string Period(DateOnly? from, DateOnly? to)
        => (from, to) switch
        {
            (null, null) => "",
            ({ } a, null) => $"ab {Date(a)}",
            (null, { } b) => $"bis {Date(b)}",
            ({ } a, { } b) => $"{Date(a)} bis {Date(b)}",
        };
}

internal static class Formats
{
    // Both cultures write the day first; only the separator differs (18.10.2026, 18/10/2026).
    public static string Date(DateOnly? date, CultureInfo culture) => date is null ? "" : date.Value.ToString(culture.Name == "de-DE" ? "dd.MM.yyyy" : "dd/MM/yyyy", culture);

    public static string Money(decimal amount, string currency, CultureInfo culture)
        => amount.ToString("N2", culture) + " " + (currency == "EUR" ? "€" : currency);
}
