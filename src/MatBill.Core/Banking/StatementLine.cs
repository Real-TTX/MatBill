using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace MatBill.Core.Banking;

/// <summary>
/// One booking of a bank statement, whatever it came from (CSV, CAMT, MT940, an online connection). <see cref="Amount"/> is signed: money that came in is positive.
/// </summary>
public sealed record StatementLine(
    DateOnly BookingDate,
    DateOnly? ValueDate,
    decimal Amount,
    string Currency,
    string? CounterpartyName,
    string? CounterpartyIban,
    string? Purpose,
    string? Reference = null,
    string? EndToEndId = null)
{
    /// <summary>
    /// What identifies a booking when the source gives no id of its own: date, amount, counterparty and purpose. Two equal bookings on one day are told apart
    /// by their place among the equal ones (<see cref="StatementDedup"/>).
    /// </summary>
    public string Fingerprint()
    {
        var text = string.Join('|', BookingDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), Amount.ToString("0.00", CultureInfo.InvariantCulture),
            Normalize(CounterpartyName), Normalize(CounterpartyIban), Normalize(Purpose));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..32].ToLowerInvariant();
    }

    private static string Normalize(string? s) => Regex.Replace((s ?? "").ToLowerInvariant(), @"\s+", "");
}

/// <summary>The keys that keep a booking from being imported twice, also when the same file (or an overlapping one) comes again.</summary>
public static class StatementDedup
{
    /// <summary>
    /// One key per line: the id of the bank when there is one, otherwise the fingerprint and its number among equal lines of this import
    /// ("...:1", "...:2"), so that two equal coffee payments on one day stay two bookings.
    /// </summary>
    public static IReadOnlyList<string> Keys(IReadOnlyList<StatementLine> lines)
    {
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        var keys = new List<string>(lines.Count);
        foreach (var line in lines)
        {
            if (!string.IsNullOrWhiteSpace(line.Reference))
            {
                keys.Add("ref:" + line.Reference.Trim());
                continue;
            }

            var fp = line.Fingerprint();
            var n = seen.TryGetValue(fp, out var count) ? count + 1 : 1;
            seen[fp] = n;
            keys.Add($"fp:{fp}:{n}");
        }

        return keys;
    }
}

/// <summary>The purpose of a SEPA transfer as banks write it: tags such as "EREF+", "SVWZ+", "MREF+" are taken apart.</summary>
public static partial class SepaPurpose
{
    // No word boundary before a tag: the subfields of MT940 are joined without a separator ("...210SVWZ+"). Upper case only, as banks write the tags.
    [GeneratedRegex(@"(?<![A-Za-z])(EREF|KREF|MREF|CRED|DEBT|SVWZ|ABWA|ABWE|IBAN|BIC|COAM|OAMT)\+")]
    private static partial Regex TagRegex();

    /// <summary>The text of the purpose ("SVWZ+") without the other tags; the whole text when it has no tags.</summary>
    public static string Clean(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "";
        }

        var tags = Split(text);
        if (tags.Count == 0)
        {
            return Squeeze(text);
        }

        return tags.TryGetValue("SVWZ", out var svwz) ? svwz : Squeeze(TagRegex().Split(text)[0]);
    }

    /// <summary>The end-to-end reference ("EREF+"), or null when there is none or it says NOTPROVIDED.</summary>
    public static string? EndToEnd(string? text)
        => text is not null && Split(text).TryGetValue("EREF", out var eref) && !eref.Equals("NOTPROVIDED", StringComparison.OrdinalIgnoreCase) ? eref : null;

    public static Dictionary<string, string> Split(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var matches = TagRegex().Matches(text);
        for (var i = 0; i < matches.Count; i++)
        {
            var start = matches[i].Index + matches[i].Length;
            var end = i + 1 < matches.Count ? matches[i + 1].Index : text.Length;
            result[matches[i].Groups[1].Value.ToUpperInvariant()] = Squeeze(text[start..end]);
        }

        return result;
    }

    public static string Squeeze(string text) => Regex.Replace(text, @"\s+", " ").Trim();
}
