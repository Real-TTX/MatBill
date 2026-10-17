using System.Globalization;
using System.Text.RegularExpressions;

namespace MatBill.Core.Billing;

/// <summary>
/// The pattern of a number range ("RE-{JJJJ}-{NNNN}"): <c>{JJJJ}</c> the year, <c>{JJ}</c> its last two digits, <c>{MM}</c> the month and <c>{N…}</c> the
/// counter with as many digits as there are Ns. The counter starts again with a new year when the pattern has the year, and with a new month when it has the month.
/// </summary>
public static partial class NumberPattern
{
    [GeneratedRegex(@"\{(JJJJ|JJ|MM|N+)\}")]
    private static partial Regex TokenRegex();

    [GeneratedRegex(@"\{N+\}")]
    private static partial Regex CounterRegex();

    /// <summary>What is wrong with a pattern, or null when it can be used.</summary>
    public static string? Problem(string? pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return "Das Muster darf nicht leer sein.";
        }

        if (CounterRegex().Matches(pattern).Count != 1)
        {
            return "Das Muster braucht genau einen Zähler, zum Beispiel {NNNN}.";
        }

        if (pattern.Length > 40)
        {
            return "Das Muster ist zu lang (höchstens 40 Zeichen).";
        }

        var rest = TokenRegex().Replace(pattern, string.Empty);
        return rest.Contains('{') || rest.Contains('}') ? "Unbekannter Platzhalter. Erlaubt sind {JJJJ}, {JJ}, {MM} und {NNNN}." : null;
    }

    /// <summary>The period in which the counter counts: "2026", "2026-10" or "" for a counter that never starts again.</summary>
    public static string PeriodKey(string pattern, DateOnly date)
    {
        var year = pattern.Contains("{JJJJ}", StringComparison.Ordinal) || pattern.Contains("{JJ}", StringComparison.Ordinal);
        var month = pattern.Contains("{MM}", StringComparison.Ordinal);
        return (year, month) switch
        {
            (true, true) => date.ToString("yyyy-MM", CultureInfo.InvariantCulture),
            (true, false) => date.ToString("yyyy", CultureInfo.InvariantCulture),
            (false, true) => date.ToString("yyyy-MM", CultureInfo.InvariantCulture),
            _ => string.Empty,
        };
    }

    public static string Format(string pattern, DateOnly date, long counter)
        => TokenRegex().Replace(pattern, m => m.Groups[1].Value switch
        {
            "JJJJ" => date.ToString("yyyy", CultureInfo.InvariantCulture),
            "JJ" => date.ToString("yy", CultureInfo.InvariantCulture),
            "MM" => date.ToString("MM", CultureInfo.InvariantCulture),
            var n => counter.ToString(new string('0', n.Length), CultureInfo.InvariantCulture),
        });
}
