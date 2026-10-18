using System.Globalization;
using System.Text.RegularExpressions;

namespace MatBill.Core.Billing;

/// <summary>
/// The pattern of a number range ("RE-{YYYY}-{NNNN}"): <c>{YYYY}</c> the year, <c>{YY}</c> its last two digits, <c>{MM}</c> the month and <c>{N…}</c> the
/// counter with as many digits as there are Ns. The counter starts again with a new year when the pattern has the year, and with a new month when it has the month.
/// The German tokens of earlier versions (<c>{JJJJ}</c>, <c>{JJ}</c>) still work, so saved patterns keep their meaning.
/// </summary>
public static partial class NumberPattern
{
    [GeneratedRegex(@"\{(YYYY|YY|JJJJ|JJ|MM|N+)\}")]
    private static partial Regex TokenRegex();

    [GeneratedRegex(@"\{N+\}")]
    private static partial Regex CounterRegex();

    [GeneratedRegex(@"\{(YYYY|YY|JJJJ|JJ)\}")]
    private static partial Regex YearRegex();

    /// <summary>What is wrong with a pattern, as the key of a text (<c>NumberPattern.*</c> in Resources/Texts), or null when it can be used.</summary>
    public static string? Problem(string? pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return "NumberPattern.Empty";
        }

        if (CounterRegex().Matches(pattern).Count != 1)
        {
            return "NumberPattern.Counter";
        }

        if (pattern.Length > 40)
        {
            return "NumberPattern.TooLong";
        }

        var rest = TokenRegex().Replace(pattern, string.Empty);
        return rest.Contains('{') || rest.Contains('}') ? "NumberPattern.Unknown" : null;
    }

    /// <summary>The period in which the counter counts: "2026", "2026-10" or "" for a counter that never starts again.</summary>
    public static string PeriodKey(string pattern, DateOnly date)
    {
        var year = YearRegex().IsMatch(pattern);
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
            "YYYY" or "JJJJ" => date.ToString("yyyy", CultureInfo.InvariantCulture),
            "YY" or "JJ" => date.ToString("yy", CultureInfo.InvariantCulture),
            "MM" => date.ToString("MM", CultureInfo.InvariantCulture),
            var n => counter.ToString(new string('0', n.Length), CultureInfo.InvariantCulture),
        });
}
