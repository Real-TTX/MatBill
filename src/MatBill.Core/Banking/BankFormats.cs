using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using MatBill.Core.Billing;

namespace MatBill.Core.Banking;

/// <summary>What a statement file says besides its bookings: the account it belongs to and its closing balance.</summary>
public sealed record StatementInfo(string? Iban, decimal? Balance, DateOnly? BalanceDate);

public enum StatementFormat
{
    Csv,
    Camt,
    Mt940,
}

/// <summary>Tells the formats of statement files apart by their content.</summary>
public static class StatementFormats
{
    public static StatementFormat Detect(string text)
    {
        var head = text.TrimStart('﻿', ' ', '\r', '\n', '\t');
        if (head.StartsWith('<') && (head.Contains("camt.05", StringComparison.Ordinal) || head.Contains("<BkToCstmr", StringComparison.Ordinal)))
        {
            return StatementFormat.Camt;
        }

        return Regex.IsMatch(head[..Math.Min(head.Length, 4000)], @"(^|\n):(20|25|60F|61):") ? StatementFormat.Mt940 : StatementFormat.Csv;
    }
}

/// <summary>
/// ISO 20022 statements (camt.052, camt.053, camt.054), the format every European bank exports. Entries with several transactions (a batch) become one line per
/// transaction when the transactions carry their own amounts. Namespaces of all versions are accepted (the names of the elements decide).
/// </summary>
public static class CamtStatement
{
    public static (List<StatementLine> Lines, StatementInfo Info) Read(string xml)
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
        using var reader = XmlReader.Create(new StringReader(xml), settings);
        var doc = XDocument.Load(reader);
        var lines = new List<StatementLine>();
        string? iban = null;
        decimal? balance = null;
        DateOnly? balanceDate = null;

        foreach (var stmt in doc.Descendants().Where(e => e.Name.LocalName is "Stmt" or "Rpt" or "Ntfctn"))
        {
            iban ??= Path(stmt, "Acct", "Id", "IBAN")?.Value;
            foreach (var bal in stmt.Elements().Where(e => e.Name.LocalName == "Bal"))
            {
                var code = Path(bal, "Tp", "CdOrPrtry", "Cd")?.Value;
                if (code is "CLBD" or "CLAV" && Amount(bal) is { } amount)
                {
                    balance = amount;
                    balanceDate = Date(Path(bal, "Dt"));
                }
            }

            foreach (var entry in stmt.Elements().Where(e => e.Name.LocalName == "Ntry"))
            {
                if (Path(entry, "Sts", "Cd")?.Value is "PDNG" || Path(entry, "Sts")?.Value == "PDNG")
                {
                    continue;
                }

                var sign = Child(entry, "CdtDbtInd")?.Value == "DBIT" ? -1m : 1m;
                var booking = Date(Child(entry, "BookgDt")) ?? Date(Child(entry, "ValDt"));
                if (booking is null || Amount(entry) is not { } total)
                {
                    continue;
                }

                var valueDate = Date(Child(entry, "ValDt"));
                var currency = Child(entry, "Amt")?.Attribute("Ccy")?.Value ?? "EUR";
                var entryRef = Child(entry, "AcctSvcrRef")?.Value;
                var details = entry.Descendants().Where(e => e.Name.LocalName == "TxDtls").ToList();
                var split = details.Count > 1 && details.All(d => Amount(d) is not null);

                if (details.Count == 0)
                {
                    lines.Add(new StatementLine(booking.Value, valueDate, sign * total, currency, null, null, Child(entry, "AddtlNtryInf")?.Value, entryRef));
                    continue;
                }

                var index = 0;
                foreach (var tx in split ? details : details.Take(1))
                {
                    index++;
                    var txSign = Child(tx, "CdtDbtInd")?.Value is { } ind ? (ind == "DBIT" ? -1m : 1m) : sign;
                    var party = txSign > 0 ? "Dbtr" : "Cdtr";
                    var name = PartyName(tx, party) ?? PartyName(tx, txSign > 0 ? "UltmtDbtr" : "UltmtCdtr");
                    var partyIban = Path(tx, "RltdPties", party + "Acct", "Id", "IBAN")?.Value;
                    var purpose = string.Join(" ", tx.Descendants().Where(e => e.Name.LocalName == "Ustrd").Select(e => e.Value.Trim()));
                    var structured = tx.Descendants().FirstOrDefault(e => e.Name.LocalName == "CdtrRefInf")?.Descendants().FirstOrDefault(e => e.Name.LocalName == "Ref")?.Value;
                    if (string.IsNullOrWhiteSpace(purpose))
                    {
                        purpose = structured ?? Child(entry, "AddtlNtryInf")?.Value ?? "";
                    }

                    var e2e = Path(tx, "Refs", "EndToEndId")?.Value;
                    var txRef = Path(tx, "Refs", "AcctSvcrRef")?.Value ?? (entryRef is null ? null : split ? $"{entryRef}/{index}" : entryRef);
                    lines.Add(new StatementLine(booking.Value, valueDate, txSign * (split ? Amount(tx)!.Value : total), currency, name, partyIban,
                        string.IsNullOrWhiteSpace(purpose) ? null : SepaPurpose.Squeeze(purpose), txRef, e2e is null or "NOTPROVIDED" ? null : e2e));
                }
            }
        }

        return (lines, new StatementInfo(iban, balance, balanceDate));
    }

    private static string? PartyName(XElement tx, string party)
    {
        var p = tx.Descendants().FirstOrDefault(e => e.Name.LocalName == party && e.Parent?.Name.LocalName == "RltdPties");
        return p?.Descendants().FirstOrDefault(e => e.Name.LocalName == "Nm")?.Value.Trim();
    }

    private static XElement? Child(XElement e, string name) => e.Elements().FirstOrDefault(x => x.Name.LocalName == name);

    private static XElement? Path(XElement e, params string[] names)
    {
        XElement? current = e;
        foreach (var n in names)
        {
            current = current is null ? null : Child(current, n);
        }

        return current;
    }

    private static decimal? Amount(XElement e)
    {
        var amt = Child(e, "Amt") ?? Path(e, "AmtDtls", "TxAmt", "Amt");
        return amt is not null && decimal.TryParse(amt.Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    private static DateOnly? Date(XElement? e)
    {
        var text = e is null ? null : (Child(e, "Dt") ?? Child(e, "DtTm"))?.Value ?? (e.HasElements ? null : e.Value);
        return text is not null && DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var dt) ? DateOnly.FromDateTime(dt) : null;
    }
}

/// <summary>
/// SWIFT MT940, the older statement format that many banks and accounting programs still offer. The German structured field :86: (?20 to ?29 purpose,
/// ?31 IBAN, ?32 and ?33 name, SEPA tags inside the purpose) is read; an unstructured :86: is the purpose as a whole.
/// </summary>
public static partial class Mt940Statement
{
    [GeneratedRegex(@"^(?<date>\d{6})(?<entry>\d{4})?(?<mark>R?[CD])(?<fund>[A-Z])?(?<amount>\d+,\d*)(?<type>[A-Z]\w{3})?(?<rest>.*)$", RegexOptions.Singleline)]
    private static partial Regex Line61();

    [GeneratedRegex(@"^:(?<tag>\d{2}[A-Z]?):", RegexOptions.Multiline)]
    private static partial Regex TagRegex();

    public static (List<StatementLine> Lines, StatementInfo Info) Read(string text)
    {
        var lines = new List<StatementLine>();
        string? account = null;
        decimal? balance = null;
        DateOnly? balanceDate = null;
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        var matches = TagRegex().Matches(normalized);
        (DateOnly Date, DateOnly? Value, decimal Amount, string? Ref)? pending = null;

        void Flush(string? info)
        {
            if (pending is not { } p)
            {
                return;
            }

            var (name, iban, purpose, endToEnd) = Info86(info);
            lines.Add(new StatementLine(p.Date, p.Value, p.Amount, "EUR", name, iban, purpose, p.Ref, endToEnd));
            pending = null;
        }

        for (var i = 0; i < matches.Count; i++)
        {
            var tag = matches[i].Groups["tag"].Value;
            var start = matches[i].Index + matches[i].Length;
            var end = i + 1 < matches.Count ? matches[i + 1].Index : normalized.Length;
            var value = normalized[start..end].TrimEnd('\n', '-', ' ');

            switch (tag)
            {
                case "25":
                    account ??= value.Trim();
                    break;
                case "61":
                    Flush(null);
                    var m = Line61().Match(value.Replace("\n", "", StringComparison.Ordinal));
                    if (m.Success && TryYymmdd(m.Groups["date"].Value, out var valueDate))
                    {
                        var booking = valueDate;
                        if (m.Groups["entry"].Success && int.TryParse(m.Groups["entry"].Value[..2], out var month) && int.TryParse(m.Groups["entry"].Value[2..], out var day))
                        {
                            var year = valueDate.Year + (month == 1 && valueDate.Month == 12 ? 1 : month == 12 && valueDate.Month == 1 ? -1 : 0);
                            if (month is >= 1 and <= 12 && day >= 1 && day <= DateTime.DaysInMonth(year, month)) { booking = new DateOnly(year, month, day); }
                        }

                        var amount = decimal.Parse(m.Groups["amount"].Value.Replace(',', '.'), CultureInfo.InvariantCulture);
                        var mark = m.Groups["mark"].Value;
                        var sign = mark is "D" or "RC" ? -1m : 1m;
                        var rest = m.Groups["rest"].Value;
                        var bankRef = rest.Contains("//", StringComparison.Ordinal) ? rest[(rest.IndexOf("//", StringComparison.Ordinal) + 2)..].Trim() : null;
                        pending = (booking, valueDate, sign * amount, string.IsNullOrWhiteSpace(bankRef) ? null : bankRef);
                    }

                    break;
                case "86":
                    Flush(value);
                    break;
                case "62F":
                    Flush(null);
                    var b = Regex.Match(value, @"^(?<mark>[CD])(?<date>\d{6})(?<cur>[A-Z]{3})(?<amount>\d+,\d*)");
                    if (b.Success && TryYymmdd(b.Groups["date"].Value, out var bd))
                    {
                        var amount = decimal.Parse(b.Groups["amount"].Value.Replace(',', '.'), CultureInfo.InvariantCulture);
                        balance = b.Groups["mark"].Value == "D" ? -amount : amount;
                        balanceDate = bd;
                    }

                    break;
            }
        }

        Flush(null);
        var iban = account is not null && Regex.IsMatch(account, "^[A-Z]{2}\\d{2}[A-Z0-9]{10,30}$") ? account : null;
        return (lines, new StatementInfo(iban, balance, balanceDate));
    }

    /// <summary>The field :86:: structured (German "?20" subfields) or plain text.</summary>
    public static (string? Name, string? Iban, string? Purpose, string? EndToEnd) Info86(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return (null, null, null, null);
        }

        var flat = text.Replace("\n", "", StringComparison.Ordinal);
        var separator = flat.Length > 3 && !char.IsLetterOrDigit(flat[3]) && flat[3] != ' ' ? flat[3] : '?';
        if (!flat.Contains(separator + "20", StringComparison.Ordinal) && !flat.Contains(separator + "32", StringComparison.Ordinal))
        {
            return (null, null, SepaPurpose.Clean(flat), SepaPurpose.EndToEnd(flat));
        }

        var fields = new Dictionary<int, StringBuilder>();
        foreach (var part in flat.Split(separator).Skip(1))
        {
            if (part.Length >= 2 && int.TryParse(part[..2], out var code))
            {
                if (!fields.TryGetValue(code, out var sb)) { fields[code] = sb = new StringBuilder(); }
                sb.Append(part[2..]);
            }
        }

        string Join(IEnumerable<int> codes) => string.Concat(codes.Where(fields.ContainsKey).Select(c => fields[c].ToString()));
        var raw = Join(Enumerable.Range(20, 10).Concat(Enumerable.Range(60, 4)));
        var purpose = SepaPurpose.Clean(raw);
        var name = Join([32, 33]).Trim();
        var iban = fields.TryGetValue(31, out var ib) ? ib.ToString().Trim() : null;
        return (name.Length == 0 ? null : name, string.IsNullOrWhiteSpace(iban) ? null : iban, purpose.Length == 0 ? null : purpose, SepaPurpose.EndToEnd(raw));
    }

    private static bool TryYymmdd(string s, out DateOnly date)
    {
        date = default;
        return s.Length == 6 && int.TryParse(s[..2], out var y) && int.TryParse(s[2..4], out var m) && int.TryParse(s[4..], out var d)
            && m is >= 1 and <= 12 && d >= 1 && d <= DateTime.DaysInMonth(2000 + y, m) && (date = new DateOnly(2000 + y, m, d)) != default;
    }
}
