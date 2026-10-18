using System.Globalization;
using System.Text;
using MatBill.Core.Billing;

namespace MatBill.Core.Banking;

/// <summary>A CSV file as rows of fields: the header row (found below any lines a bank writes before it) and the data rows.</summary>
public sealed record CsvTable(IReadOnlyList<string> Headers, IReadOnlyList<string[]> Rows, char Delimiter);

/// <summary>Which column holds what. Columns are indexes into <see cref="CsvTable.Headers"/>; null means the file has no such column.</summary>
public sealed class CsvMapping
{
    public int? BookingDate { get; set; }

    public int? ValueDate { get; set; }

    /// <summary>One column with a signed amount ...</summary>
    public int? Amount { get; set; }

    /// <summary>... or two columns, money out and money in (both written without sign or with).</summary>
    public int? Debit { get; set; }

    public int? Credit { get; set; }

    /// <summary>A column that says "S"/"H" or "Soll"/"Haben" or "D"/"C" for the amount (some banks write the sign there).</summary>
    public int? DebitCreditMark { get; set; }

    public int? Name { get; set; }

    public int? Iban { get; set; }

    /// <summary>The purpose; several columns are joined (some banks split it over many).</summary>
    public List<int> Purpose { get; set; } = [];

    public int? Currency { get; set; }

    public int? Reference { get; set; }

    /// <summary>Turn every amount around (a credit card statement that writes charges as positive numbers).</summary>
    public bool InvertSign { get; set; }

    /// <summary>Skip a row when this column says the booking is not final ("vorgemerkt", "pending").</summary>
    public int? Status { get; set; }

    public bool IsUsable => BookingDate is not null && (Amount is not null || Debit is not null || Credit is not null);
}

/// <summary>
/// A problem of a statement file as a text key and its arguments in one string (Core has no localizer, and the list of problems stays a list of strings):
/// the key, then each argument after a unit separator. <see cref="Split"/> takes it apart again; a text without the separator is a key or a finished text.
/// </summary>
public static class StatementProblem
{
    public const char Separator = '\u001F';

    public const string MissingColumns = "Banking.Problem.MissingColumns";

    /// <summary>{0} the row, {1} the text that is not a date.</summary>
    public const string BadDate = "Banking.Problem.BadDate";

    /// <summary>{0} the row, {1} the text that is not a number.</summary>
    public const string BadAmount = "Banking.Problem.BadAmount";

    public static string Of(string key, params object?[] arguments)
        => arguments.Length == 0
            ? key
            : key + Separator + string.Join(Separator, arguments.Select(a => (Convert.ToString(a, CultureInfo.InvariantCulture) ?? "").Replace(Separator, ' ')));

    public static (string Key, string[] Arguments) Split(string problem)
    {
        var parts = problem.Split(Separator);
        return (parts[0], parts[1..]);
    }
}

/// <summary>Reads the CSV exports of any bank:the delimiter, the encoding, the header row, the columns, German or English numbers and dates.</summary>
public static class CsvStatement
{
    private static readonly char[] Delimiters = [';', ',', '\t', '|'];

    /// <summary>The text of a file: UTF-8 when it is valid UTF-8 (with or without BOM), else Windows-1252, which most German banks still write.</summary>
    public static string Decode(byte[] bytes)
    {
        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes).TrimStart('﻿');
        }
        catch (DecoderFallbackException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(1252).GetString(bytes);
        }
    }

    public static CsvTable Read(string text)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var delimiter = DetectDelimiter(lines);
        var records = Parse(lines, delimiter).Where(r => r.Any(f => f.Length > 0)).ToList();
        if (records.Count == 0)
        {
            return new CsvTable([], [], delimiter);
        }

        // The header is the first row (of the first 40) that has the most fields and is followed by a row with a date in it: banks put account details above it.
        var width = records.Take(40).Max(r => r.Length);
        var header = 0;
        for (var i = 0; i < Math.Min(records.Count - 1, 40); i++)
        {
            if (records[i].Length >= Math.Max(3, width - 1) && !records[i].Any(f => TryDate(f, out _)) && records[i + 1].Any(f => TryDate(f, out _)))
            {
                header = i;
                break;
            }
        }

        var headers = records[header].Select(h => h.Trim()).ToArray();
        var rows = records.Skip(header + 1).Where(r => r.Length >= Math.Min(headers.Length, 3)).ToList();
        return new CsvTable(headers, rows, delimiter);
    }

    private static char DetectDelimiter(string text)
    {
        var sample = text.Split('\n').Where(l => l.Length > 0).Take(30).ToList();
        return Delimiters.OrderByDescending(d => sample.Select(l => CountOutsideQuotes(l, d)).Where(c => c > 0).GroupBy(c => c).Select(g => g.Count() * g.Key).DefaultIfEmpty(0).Max()).First();
    }

    private static int CountOutsideQuotes(string line, char d)
    {
        var count = 0;
        var quoted = false;
        foreach (var c in line)
        {
            if (c == '"') { quoted = !quoted; }
            else if (c == d && !quoted) { count++; }
        }

        return count;
    }

    /// <summary>RFC 4180 with the liberties of bank exports: quoted fields with "" for a quote and line breaks inside quotes.</summary>
    public static List<string[]> Parse(string text, char delimiter)
    {
        var result = new List<string[]>();
        var fields = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                else if (c == '"') { quoted = false; }
                else { field.Append(c); }
                continue;
            }

            if (c == '"' && field.Length == 0) { quoted = true; }
            else if (c == delimiter) { fields.Add(field.ToString().Trim()); field.Clear(); }
            else if (c == '\n') { fields.Add(field.ToString().Trim()); field.Clear(); result.Add([.. fields]); fields.Clear(); }
            else { field.Append(c); }
        }

        if (field.Length > 0 || fields.Count > 0)
        {
            fields.Add(field.ToString().Trim());
            result.Add([.. fields]);
        }

        return result;
    }

    // ------------------------------------------------------------------------------------------------ the columns

    private static readonly (string Field, string[] Names)[] Synonyms =
    [
        ("BookingDate", ["buchungstag", "buchungsdatum", "buchung", "datum", "date", "booking date", "transaction date", "belegdatum", "umsatzdatum", "started date", "completed date"]),
        ("ValueDate", ["valuta", "valutadatum", "wertstellung", "wert", "value date"]),
        ("Amount", ["betrag", "betrag (eur)", "betrag in eur", "umsatz", "umsatz in eur", "amount", "amount (eur)", "betrag eur", "summe"]),
        ("Debit", ["soll", "ausgang", "lastschrift", "belastung", "debit", "abgang", "auszahlung"]),
        ("Credit", ["haben", "eingang", "gutschrift", "credit", "zugang", "einzahlung"]),
        ("DebitCreditMark", ["soll/haben", "s/h", "soll / haben", "credit/debit", "debit/credit"]),
        ("Name", ["beguenstigter/zahlungspflichtiger", "begünstigter/zahlungspflichtiger", "auftraggeber/empfänger", "auftraggeber / begünstigter", "zahlungsempfänger*in", "zahlungspflichtige*r", "name zahlungsbeteiligter", "empfänger", "auftraggeber", "zahlungsempfänger", "name", "counterparty", "payee", "beschreibung", "description", "gegenkonto name"]),
        ("Iban", ["kontonummer/iban", "iban zahlungsbeteiligter", "iban", "konto-nr.", "kontonummer", "iban/kontonummer", "gegenkonto iban", "account"]),
        ("Purpose", ["verwendungszweck", "buchungstext", "zahlungsreferenz", "purpose", "reference", "referenz", "text", "vorgang/verwendungszweck", "buchungsdetails", "verwendungszweck 1", "verwendungszweck 2"]),
        ("Currency", ["währung", "waehrung", "currency", "whrg"]),
        ("Reference", ["kundenreferenz", "end-to-end-referenz", "transaktions-id", "transaction id", "id", "buchungsreferenz", "primanota"]),
        ("Status", ["status", "buchungsstatus", "state", "info"]),
    ];

    /// <summary>A first guess of the columns from the headers (German and English names of the common banks) and from what the rows hold.</summary>
    public static CsvMapping Guess(CsvTable table)
    {
        var mapping = new CsvMapping();
        var names = table.Headers.Select(h => h.Trim().Trim('"').ToLowerInvariant()).ToArray();
        var used = new HashSet<int>();

        int? Find(string field, bool exactFirst = true)
        {
            var list = Synonyms.First(s => s.Field == field).Names;
            foreach (var n in list)
            {
                for (var i = 0; i < names.Length; i++)
                {
                    if (!used.Contains(i) && names[i] == n) { used.Add(i); return i; }
                }
            }

            if (!exactFirst)
            {
                return null;
            }

            foreach (var n in list.Where(n => n.Length >= 4))
            {
                for (var i = 0; i < names.Length; i++)
                {
                    if (!used.Contains(i) && names[i].Contains(n, StringComparison.Ordinal)) { used.Add(i); return i; }
                }
            }

            return null;
        }

        mapping.BookingDate = Find("BookingDate");
        mapping.ValueDate = Find("ValueDate");
        mapping.DebitCreditMark = Find("DebitCreditMark", exactFirst: false);
        mapping.Amount = Find("Amount");
        if (mapping.Amount is null)
        {
            mapping.Debit = Find("Debit");
            mapping.Credit = Find("Credit");
        }

        mapping.Iban = Find("Iban");
        mapping.Name = Find("Name");
        mapping.Currency = Find("Currency");
        mapping.Reference = Find("Reference", exactFirst: false);
        mapping.Status = Find("Status", exactFirst: false);
        while (Find("Purpose") is { } purpose)
        {
            mapping.Purpose.Add(purpose);
        }

        // Without a known name: the first column that holds dates and the first one that holds amounts.
        mapping.BookingDate ??= FirstColumn(table, used, f => TryDate(f, out _));
        if (mapping.Amount is null && mapping.Debit is null && mapping.Credit is null)
        {
            mapping.Amount = FirstColumn(table, used, f => f.Any(char.IsDigit) && Amount.TryParse(f, out _) && (f.Contains(',') || f.Contains('.')));
        }

        return mapping;
    }

    private static int? FirstColumn(CsvTable table, HashSet<int> used, Func<string, bool> test)
    {
        for (var i = 0; i < table.Headers.Count; i++)
        {
            if (!used.Contains(i) && table.Rows.Take(10).Where(r => i < r.Length && r[i].Length > 0).DefaultIfEmpty().All(r => r is not null && test(r[i])) && table.Rows.Any(r => i < r.Length && r[i].Length > 0))
            {
                used.Add(i);
                return i;
            }
        }

        return null;
    }

    // ------------------------------------------------------------------------------------------------ the bookings

    /// <summary>The bookings of the table with <paramref name="mapping"/>. A row that cannot be read is skipped and named in <paramref name="problems"/> (as <see cref="StatementProblem"/>).</summary>
    public static List<StatementLine> Lines(CsvTable table, CsvMapping mapping, List<string> problems, string defaultCurrency = "EUR")
    {
        var result = new List<StatementLine>();
        if (!mapping.IsUsable)
        {
            problems.Add(StatementProblem.Of(StatementProblem.MissingColumns));
            return result;
        }

        for (var r = 0; r < table.Rows.Count; r++)
        {
            var row = table.Rows[r];
            string? Get(int? i) => i is { } x && x < row.Length && row[x].Length > 0 ? row[x] : null;

            if (Get(mapping.Status) is { } status && IsPending(status))
            {
                continue;
            }

            if (!TryDate(Get(mapping.BookingDate), out var date))
            {
                // the sum lines at the end of many exports have no date: no problem worth naming
                if (row.Count(f => f.Length > 0) > 2 && Get(mapping.BookingDate) is not null)
                {
                    problems.Add(StatementProblem.Of(StatementProblem.BadDate, r + 1, Get(mapping.BookingDate)));
                }

                continue;
            }

            decimal amount;
            if (mapping.Amount is not null)
            {
                if (!Amount.TryParse(Get(mapping.Amount), out amount))
                {
                    problems.Add(StatementProblem.Of(StatementProblem.BadAmount, r + 1, Get(mapping.Amount)));
                    continue;
                }
            }
            else
            {
                var debit = Amount.ParseOr(Get(mapping.Debit), 0m);
                var credit = Amount.ParseOr(Get(mapping.Credit), 0m);
                amount = Math.Abs(credit) - Math.Abs(debit);
            }

            if (Get(mapping.DebitCreditMark) is { } mark)
            {
                var m = mark.Trim().ToUpperInvariant();
                if (m is "S" or "SOLL" or "D" or "DEBIT" or "DBIT" or "-") { amount = -Math.Abs(amount); }
                else if (m is "H" or "HABEN" or "C" or "CREDIT" or "CRDT" or "+") { amount = Math.Abs(amount); }
            }

            if (mapping.InvertSign)
            {
                amount = -amount;
            }

            TryDate(Get(mapping.ValueDate), out var valueDate);
            var purpose = string.Join(" ", mapping.Purpose.Select(i => Get(i)).Where(p => p is not null));
            result.Add(new StatementLine(
                date,
                Get(mapping.ValueDate) is null ? null : valueDate,
                amount,
                (Get(mapping.Currency) ?? defaultCurrency).ToUpperInvariant(),
                Get(mapping.Name),
                Get(mapping.Iban)?.Replace(" ", "", StringComparison.Ordinal),
                purpose.Length == 0 ? null : SepaPurpose.Squeeze(purpose),
                Get(mapping.Reference),
                SepaPurpose.EndToEnd(purpose)));
        }

        return result;
    }

    private static bool IsPending(string status)
    {
        var s = status.ToLowerInvariant();
        return s.Contains("vorgemerkt", StringComparison.Ordinal) || s.Contains("pending", StringComparison.Ordinal) || s.Contains("nicht gebucht", StringComparison.Ordinal);
    }

    private static readonly string[] DateFormats = ["dd.MM.yyyy", "d.M.yyyy", "dd.MM.yy", "d.M.yy", "yyyy-MM-dd", "dd/MM/yyyy", "yyyy/MM/dd", "dd-MM-yyyy", "yyyyMMdd", "yyyy-MM-dd HH:mm:ss", "dd.MM.yyyy HH:mm", "yyyy-MM-ddTHH:mm:ss"];

    public static bool TryDate(string? text, out DateOnly date)
    {
        date = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var t = text.Trim().Trim('"');
        if (DateTime.TryParseExact(t, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var dt) && dt.Year is > 1990 and < 2100)
        {
            date = DateOnly.FromDateTime(dt);
            return true;
        }

        return false;
    }
}
