using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MatBill.Core.Banking;

/// <summary>Something a payment can settle: an open invoice (money comes in) or an open expense (money goes out). <see cref="Amount"/> is positive.</summary>
public sealed record OpenItem(long Id, string Kind, string? Number, decimal Amount, string? PartyName, DateOnly Date, string? PartyIban = null);

/// <summary>A booking as the matcher sees it.</summary>
public sealed record PaymentInfo(decimal Amount, DateOnly Date, string? Name, string? Iban, string? Purpose);

public sealed record MatchCandidate(OpenItem Item, int Score, IReadOnlyList<string> Reasons);

/// <summary>
/// Finds what a booking pays. Each open item gets points: its number in the purpose (60), the exact amount (40), the amount after a discount of up to 3 % (15),
/// the name or the IBAN of the counterparty (25), the only open item with this amount (20); a payment long before the item was written loses 30.
/// A booking is assigned by itself when the best item has 80 points or more and 20 more than the second; below that the items are only suggested.
/// </summary>
public static partial class PaymentMatcher
{
    public const int AutoThreshold = 80;

    public const int AutoMargin = 20;

    private static readonly HashSet<string> Noise = new(StringComparer.Ordinal)
    {
        "gmbh", "ag", "kg", "ug", "ohg", "gbr", "ek", "e", "k", "co", "und", "the", "ltd", "inc", "mbh", "haftungsbeschrankt", "herr", "frau", "dr", "von", "der", "die", "das", "partner",
    };

    public static IReadOnlyList<MatchCandidate> Candidates(PaymentInfo payment, IEnumerable<OpenItem> items)
    {
        var list = items.Where(i => i.Amount > 0).ToList();
        var amount = Math.Abs(payment.Amount);
        var purpose = Compact(payment.Purpose);
        var nameTokens = Tokens(payment.Name);
        var sameAmount = list.Count(i => i.Amount == amount);
        var result = new List<MatchCandidate>();

        foreach (var item in list)
        {
            var score = 0;
            var reasons = new List<string>();
            if (!string.IsNullOrWhiteSpace(item.Number) && Compact(item.Number) is { Length: >= 4 } number && purpose.Contains(number, StringComparison.Ordinal))
            {
                score += 60;
                reasons.Add($"Nummer {item.Number} im Verwendungszweck");
            }

            if (item.Amount == amount)
            {
                score += 40;
                reasons.Add("Betrag stimmt genau");
                if (sameAmount == 1)
                {
                    score += 20;
                    reasons.Add("einziger offener Posten mit diesem Betrag");
                }
            }
            else if (amount < item.Amount && amount >= item.Amount * 0.97m)
            {
                score += 15;
                reasons.Add("Betrag mit bis zu 3 % Abzug (Skonto?)");
            }

            var iban = (payment.Iban ?? "").Replace(" ", "", StringComparison.Ordinal);
            if (iban.Length > 10 && string.Equals(iban, item.PartyIban?.Replace(" ", "", StringComparison.Ordinal), StringComparison.OrdinalIgnoreCase))
            {
                score += 25;
                reasons.Add("IBAN des Partners");
            }
            else if (NameMatches(nameTokens, Tokens(item.PartyName)) || NameMatches(Tokens(item.PartyName), Tokens(payment.Purpose), all: true))
            {
                score += 25;
                reasons.Add("Name des Partners");
            }

            if (payment.Date < item.Date.AddDays(-7))
            {
                score -= 30;
                reasons.Add("Zahlung liegt vor dem Beleg");
            }

            if (score >= 25)
            {
                result.Add(new MatchCandidate(item, score, reasons));
            }
        }

        return [.. result.OrderByDescending(c => c.Score).ThenBy(c => Math.Abs(c.Item.Date.DayNumber - payment.Date.DayNumber))];
    }

    /// <summary>The item to assign without asking, or null when there is none that is clear enough.</summary>
    public static MatchCandidate? Auto(IReadOnlyList<MatchCandidate> candidates)
    {
        if (candidates.Count == 0 || candidates[0].Score < AutoThreshold)
        {
            return null;
        }

        return candidates.Count == 1 || candidates[0].Score - candidates[1].Score >= AutoMargin ? candidates[0] : null;
    }

    /// <summary>Letters and digits only, lower case: "RE-2026 / 0042" and "re20260042" are the same.</summary>
    public static string Compact(string? text) => NonAlnum().Replace(Fold(text), "");

    private static HashSet<string> Tokens(string? text)
        => [.. Separators().Split(Fold(text)).Where(t => t.Length >= 3 && !Noise.Contains(t))];

    /// <summary>The words of a name are found: at least half of them (or all of the item's in the purpose, with <paramref name="all"/>).</summary>
    private static bool NameMatches(HashSet<string> a, HashSet<string> b, bool all = false)
    {
        if (a.Count == 0 || b.Count == 0)
        {
            return false;
        }

        var common = a.Count(b.Contains);
        return all ? common == a.Count : common * 2 >= Math.Min(a.Count, b.Count) && common > 0;
    }

    private static string Fold(string? text)
    {
        // German spelling first: "Müller" and "Mueller" are the same name.
        var s = (text ?? "").ToLowerInvariant().Replace("ß", "ss", StringComparison.Ordinal).Replace("ä", "ae", StringComparison.Ordinal)
            .Replace("ö", "oe", StringComparison.Ordinal).Replace("ü", "ue", StringComparison.Ordinal).Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }

    [GeneratedRegex("[^a-z0-9]")]
    private static partial Regex NonAlnum();

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex Separators();
}
