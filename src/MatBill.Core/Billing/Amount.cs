using System.Globalization;

namespace MatBill.Core.Billing;

/// <summary>
/// Reads an amount as people type it into the document: "1.200,50", "1200,5", "1 200,50 €", but also "12.5" (from a script or a paste).
/// When there is both a dot and a comma, the last one is the decimal separator. A lone dot followed by exactly three digits groups thousands ("1.200" is 1200).
/// </summary>
public static class Amount
{
    public static bool TryParse(string? text, out decimal value)
    {
        value = 0m;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var s = new string(text.Where(c => !char.IsWhiteSpace(c) && c is not ('€' or ' ' or '\'' or '%')).ToArray());
        if (s.Length == 0)
        {
            return false;
        }

        var lastDot = s.LastIndexOf('.');
        var lastComma = s.LastIndexOf(',');
        if (lastDot >= 0 && lastComma >= 0)
        {
            var decimalSeparator = lastDot > lastComma ? '.' : ',';
            var thousands = decimalSeparator == '.' ? ',' : '.';
            s = s.Replace(thousands.ToString(), string.Empty, StringComparison.Ordinal).Replace(decimalSeparator, '.');
        }
        else if (lastComma >= 0)
        {
            if (s.Count(c => c == ',') > 1)
            {
                return false;
            }

            s = s.Replace(',', '.');
        }
        else if (lastDot >= 0)
        {
            var groups = s.Split('.');
            var isGrouping = groups.Skip(1).All(g => g.Length == 3) && (groups.Length > 2 || groups[0].TrimStart('-') is { Length: > 0 and <= 3 } head && head != "0");
            if (isGrouping)
            {
                s = s.Replace(".", string.Empty, StringComparison.Ordinal);
            }
            else if (groups.Length > 2)
            {
                return false;
            }
        }

        return decimal.TryParse(s, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value);
    }

    public static decimal ParseOr(string? text, decimal fallback) => TryParse(text, out var v) ? v : fallback;

    /// <summary>An amount as the document shows it in a field: German, with two decimals ("1.200,00").</summary>
    public static string Edit(decimal value, int decimals = 2) => value.ToString("N" + decimals, CultureInfo.GetCultureInfo("de-DE"));

    /// <summary>A quantity: no trailing zeros ("2", "1,5", "0,25").</summary>
    public static string Quantity(decimal value) => value.ToString("#,0.###", CultureInfo.GetCultureInfo("de-DE"));
}
