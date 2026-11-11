using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MatBill.Core.Billing;

namespace MatBill.Core.Import;

/// <summary>A problem of the data, as the key of a text (<c>Import.*</c> in Resources/Texts) and its arguments: Core knows no language.</summary>
public sealed record ImportProblem(string Key, params object[] Args);

public sealed record LegacyCustomer(string SourceId, string Number, bool IsPerson, string Name, string? Email, string? Phone, string? Website,
    string? Street, string? PostalCode, string? City, string Country);

public sealed record LegacyProduct(string SourceId, string Number, string Name, decimal UnitPrice);

/// <summary>A position as the old program had it: the discount is already in <see cref="Net"/>.</summary>
public sealed record LegacyLine(string Name, string? Description, decimal Quantity, string? Unit, decimal UnitPrice, decimal DiscountPercent, decimal Net, string? ProductNumber);

public sealed record LegacyPayment(DateOnly Date, decimal Amount);

public sealed record LegacyInvoice(string SourceId, string Number, bool IsDraft, DateOnly Date, DateOnly? ServiceFrom, DateOnly? ServiceTo, DateOnly? DueDate,
    string Title, string CustomerSourceId, string CustomerName, decimal Net, decimal Gross, decimal Open, IReadOnlyList<LegacyPayment> Payments, IReadOnlyList<LegacyLine> Lines)
{
    /// <summary>What was paid in the end (a payment taken back is a negative one).</summary>
    public decimal Paid => Payments.Sum(p => p.Amount);

    /// <summary>The day the invoice was settled: the last payment.</summary>
    public DateOnly? PaidDate => Payments.Count == 0 ? null : Payments.Max(p => p.Date);
}

public sealed record LegacyExpense(int Row, string? Number, DateOnly Date, string Supplier, string? SupplierInvoiceNumber, string Category, string? Description,
    decimal Net, decimal TaxRate, decimal Tax, decimal Gross, bool Paid, DateOnly? PaidDate, bool FromEInvoice, string File, string? SourceId, string? Note)
{
    /// <summary>A hint that starts with "ZU PRÜFEN" asks a person to look at the receipt again.</summary>
    public bool NeedsReview => Note is { } n && n.TrimStart().StartsWith("ZU PRÜFEN", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Everything read from an export, and what could not be read.</summary>
public sealed class LegacyData
{
    public List<LegacyCustomer> Customers { get; } = [];

    public List<LegacyProduct> Products { get; } = [];

    public List<LegacyInvoice> Invoices { get; } = [];

    public List<LegacyExpense> Expenses { get; } = [];

    public List<ImportProblem> Problems { get; } = [];

    /// <summary>The files of the export by their path inside it ("Rechnungen/2026-10-06_0068.pdf").</summary>
    public Dictionary<string, byte[]> Files { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The original PDF of an invoice: the file in "Rechnungen/" whose name ends with "_&lt;number&gt;.pdf".</summary>
    public (string Path, byte[] Bytes)? InvoicePdf(string number)
    {
        var suffix = "_" + number + ".pdf";
        var hits = Files.Where(f => f.Key.StartsWith(InvoizExport.InvoiceFolder, StringComparison.OrdinalIgnoreCase) && f.Key.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)).ToList();
        return hits.Count == 1 ? (hits[0].Key, hits[0].Value) : null;
    }

    /// <summary>The receipt of an expense, by the path the CSV names (relative to the folder "Ausgaben/").</summary>
    public byte[]? Receipt(string file)
        => Files.TryGetValue(InvoizExport.ExpenseFolder + file.Replace('\\', '/').TrimStart('/'), out var bytes) ? bytes : null;
}

/// <summary>
/// The export of the invoice program Invoiz, as it was written for the move to MatBill: <c>daten/kunden-artikel.json</c> (customers and articles),
/// <c>daten/rechnungen.psv</c> (invoices: an R line, then its P lines; "|" separated, amounts with a dot), <c>Rechnungen/&lt;date&gt;_&lt;number&gt;.pdf</c>
/// (the PDFs as they went out), <c>Ausgaben/Ausgaben.csv</c> (the columns of MatBill's expense export plus the Invoiz id and a hint; ";" separated,
/// German numbers) with the receipts below <c>Ausgaben/</c>. The files may stand in a folder of their own inside the ZIP.
/// </summary>
public static partial class InvoizExport
{
    public const string Source = "Invoiz";

    public const string CustomerFile = "daten/kunden-artikel.json";

    public const string InvoiceFile = "daten/rechnungen.psv";

    public const string InvoiceFolder = "Rechnungen/";

    public const string ExpenseFolder = "Ausgaben/";

    public const string ExpenseFile = "Ausgaben/Ausgaben.csv";

    /// <summary>Reads the files of an export (paths inside it, any root folder above them is cut off).</summary>
    public static LegacyData Read(IReadOnlyDictionary<string, byte[]> files)
    {
        var data = new LegacyData();
        var root = FindRoot(files.Keys);
        if (root is null)
        {
            data.Problems.Add(new("Import.Problem.NotAnExport"));
            return data;
        }

        foreach (var (path, bytes) in files)
        {
            var p = path.Replace('\\', '/');
            if (p.StartsWith(root, StringComparison.OrdinalIgnoreCase) && !p.EndsWith('/'))
            {
                data.Files[p[root.Length..]] = bytes;
            }
        }

        if (data.Files.TryGetValue(CustomerFile, out var json))
        {
            ReadCustomers(Decode(json), data);
        }

        if (data.Files.TryGetValue(InvoiceFile, out var psv))
        {
            ReadInvoices(Decode(psv), data);
        }

        if (data.Files.TryGetValue(ExpenseFile, out var csv))
        {
            ReadExpenses(Decode(csv), data);
        }

        return data;
    }

    /// <summary>The folder above "daten/" (or "Ausgaben/") inside the ZIP: "" or "Invoiz-Export/".</summary>
    private static string? FindRoot(IEnumerable<string> paths)
    {
        foreach (var marker in new[] { InvoiceFile, CustomerFile, ExpenseFile })
        {
            var hit = paths.Select(p => p.Replace('\\', '/')).Where(p => p.EndsWith(marker, StringComparison.OrdinalIgnoreCase)).OrderBy(p => p.Length).FirstOrDefault();
            if (hit is not null)
            {
                return hit[..^marker.Length];
            }
        }

        return null;
    }

    private static string Decode(byte[] bytes)
    {
        var text = new UTF8Encoding(false).GetString(bytes);
        return text.Length > 0 && text[0] == '﻿' ? text[1..] : text;
    }

    public static void ReadCustomers(string json, LegacyData data)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("customers", out var customers))
            {
                foreach (var c in customers.EnumerateArray())
                {
                    var name = Str(c, "name");
                    var number = Str(c, "number");
                    if (name is null || number is null)
                    {
                        data.Problems.Add(new("Import.Problem.CustomerIncomplete", Str(c, "invoizId") ?? "?"));
                        continue;
                    }

                    data.Customers.Add(new LegacyCustomer(Str(c, "invoizId") ?? number, number, Str(c, "kind") == "person", name, Str(c, "email"), Str(c, "phone"),
                        Str(c, "website"), Str(c, "street"), Str(c, "zip"), Str(c, "city"), Country(Str(c, "country"))));
                }
            }

            if (doc.RootElement.TryGetProperty("articles", out var articles))
            {
                foreach (var a in articles.EnumerateArray())
                {
                    var title = Str(a, "title");
                    if (title is null)
                    {
                        data.Problems.Add(new("Import.Problem.ProductIncomplete", Str(a, "invoizId") ?? "?"));
                        continue;
                    }

                    var price = a.TryGetProperty("priceNet", out var p) && p.ValueKind == JsonValueKind.Number ? p.GetDecimal() : 0m;
                    data.Products.Add(new LegacyProduct(Str(a, "invoizId") ?? title, Str(a, "number") ?? "", title, price));
                }
            }
        }
        catch (JsonException)
        {
            data.Problems.Add(new("Import.Problem.FileUnreadable", CustomerFile));
        }
    }

    /// <summary>A string of a JSON object: a number is written as it stands, an empty string is null.</summary>
    private static string? Str(JsonElement e, string name)
        => !e.TryGetProperty(name, out var v) ? null
            : v.ValueKind == JsonValueKind.Number ? v.GetRawText()
            : v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()) ? v.GetString()!.Trim()
            : null;

    /// <summary>The country as MatBill writes it ("Deutschland"); an ISO code of the neighbours becomes its German name.</summary>
    public static string Country(string? code) => (code ?? "").Trim().ToUpperInvariant() switch
    {
        "" or "DE" or "DEU" => "Deutschland",
        "AT" or "AUT" => "Österreich",
        "CH" or "CHE" => "Schweiz",
        _ => code!.Trim(),
    };

    public static void ReadInvoices(string psv, LegacyData data)
    {
        LegacyInvoice? current = null;
        var lines = new List<LegacyLine>();
        var row = 0;
        void Close()
        {
            if (current is not null)
            {
                data.Invoices.Add(current with { Lines = [.. lines] });
            }

            current = null;
            lines.Clear();
        }

        foreach (var raw in psv.Split('\n'))
        {
            row++;
            var line = raw.TrimEnd('\r');
            if (line.Length == 0)
            {
                continue;
            }

            var f = line.Split('|');
            if (f[0] == "R" && f.Length >= 15)
            {
                Close();
                if (!Date(f[4], out var date) || !Dec(f[11], out var net) || !Dec(f[12], out var gross) || !Dec(f[13], out var open))
                {
                    data.Problems.Add(new("Import.Problem.InvoiceRow", row));
                    continue;
                }

                var payments = new List<LegacyPayment>();
                foreach (var part in f[14].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var at = part.Split('@');
                    if (at.Length == 2 && Date(at[0], out var paidOn) && Dec(at[1], out var amount))
                    {
                        payments.Add(new LegacyPayment(paidOn, amount));
                    }
                    else
                    {
                        data.Problems.Add(new("Import.Problem.InvoiceRow", row));
                    }
                }

                var (from, to) = Period(f[6]);
                from ??= Date(f[5], out var delivered) ? delivered : null;
                current = new LegacyInvoice(f[1].Trim(), f[2].Trim(), f[3].Trim().Equals("draft", StringComparison.OrdinalIgnoreCase), date, from, to,
                    Date(f[7], out var due) ? due : null, string.IsNullOrWhiteSpace(f[8]) ? "Rechnung" : f[8].Trim(), f[9].Trim(), f[10].Trim(), net, gross, open, payments, []);
            }
            else if (f[0] == "P" && f.Length >= 9 && current is not null)
            {
                if (!Dec(f[3], out var quantity) || !Dec(f[5], out var price) || !Dec(f[7], out var total))
                {
                    data.Problems.Add(new("Import.Problem.InvoiceRow", row));
                    continue;
                }

                lines.Add(new LegacyLine(f[1].Trim(), Empty(f[2]), quantity, Empty(f[4]), price, Dec(f[6], out var discount) ? discount : 0m, total, Empty(f[8])));
            }
            else
            {
                data.Problems.Add(new("Import.Problem.InvoiceRow", row));
            }
        }

        Close();
    }

    /// <summary>A period of service ("2026-09-01 - 2026-09-30", or two dates in German format); null when it has none.</summary>
    private static (DateOnly? From, DateOnly? To) Period(string text)
    {
        var dates = DateRegex().Matches(text).Select(m => Date(m.Value, out var d) ? d : (DateOnly?)null).Where(d => d is not null).ToList();
        return dates.Count == 2 ? (dates[0], dates[1]) : (null, null);
    }

    [GeneratedRegex(@"\d{4}-\d{2}-\d{2}|\d{2}\.\d{2}\.\d{4}")]
    private static partial Regex DateRegex();

    public static void ReadExpenses(string csv, LegacyData data)
    {
        var rows = CsvRows(csv);
        if (rows.Count == 0)
        {
            return;
        }

        var head = rows[0].Select(h => h.Trim()).ToList();
        int Col(string name) => head.FindIndex(h => h.Equals(name, StringComparison.OrdinalIgnoreCase));
        int number = Col("Belegnummer"), date = Col("Belegdatum"), supplier = Col("Lieferant"), invoiceNo = Col("Rechnungsnummer Lieferant"), category = Col("Kategorie"),
            description = Col("Beschreibung"), net = Col("Netto"), rate = Col("USt-Satz"), gross = Col("Brutto"), status = Col("Status"), paid = Col("Bezahlt am"),
            einvoice = Col("E-Rechnung"), file = Col("Datei"), id = Col("Invoiz-ID"), note = Col("Hinweis");
        var tax = head.FindIndex(h => h.StartsWith("USt (", StringComparison.OrdinalIgnoreCase) || h.Equals("Vorsteuer", StringComparison.OrdinalIgnoreCase) || h.Equals("USt", StringComparison.OrdinalIgnoreCase));
        if (date < 0 || supplier < 0 || gross < 0 || file < 0)
        {
            data.Problems.Add(new("Import.Problem.ExpenseColumns"));
            return;
        }

        for (var r = 1; r < rows.Count; r++)
        {
            var f = rows[r];
            if (f.All(string.IsNullOrWhiteSpace))
            {
                continue;
            }

            string? At(int i) => i >= 0 && i < f.Count ? Empty(f[i]) : null;
            if (!GermanDate(At(date), out var on) || At(supplier) is not { } who || !Amount.TryParse(At(gross), out var g) || At(file) is not { } path)
            {
                data.Problems.Add(new("Import.Problem.ExpenseRow", r + 1));
                continue;
            }

            var taxRate = Amount.TryParse(At(rate), out var tr) ? tr : 0m;
            var n = Amount.TryParse(At(net), out var nn) ? nn : g;
            var t = Amount.TryParse(At(tax), out var tt) ? tt : g - n;
            var isPaid = (At(status) ?? "").Equals("bezahlt", StringComparison.OrdinalIgnoreCase);
            data.Expenses.Add(new LegacyExpense(r + 1, At(number), on, who, At(invoiceNo), At(category) ?? "Sonstiges", At(description), n, taxRate, t, g, isPaid,
                GermanDate(At(paid), out var paidOn) ? paidOn : isPaid ? on : null, (At(einvoice) ?? "").Equals("ja", StringComparison.OrdinalIgnoreCase), path, At(id), At(note)));
        }
    }

    /// <summary>The rows of a CSV with ";" (fields in quotes may hold ";", quotes and line breaks).</summary>
    public static List<List<string>> CsvRows(string text)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"')
                {
                    field.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    quoted = false;
                }
                else
                {
                    field.Append(c);
                }
            }
            else if (c == '"' && field.Length == 0)
            {
                quoted = true;
            }
            else if (c == ';')
            {
                row.Add(field.ToString());
                field.Clear();
            }
            else if (c == '\n')
            {
                row.Add(field.ToString().TrimEnd('\r'));
                field.Clear();
                rows.Add(row);
                row = [];
            }
            else
            {
                field.Append(c);
            }
        }

        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString().TrimEnd('\r'));
            rows.Add(row);
        }

        return rows;
    }

    private static string? Empty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static bool Dec(string s, out decimal value) => decimal.TryParse(s.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out value);

    private static bool Date(string s, out DateOnly value)
        => DateOnly.TryParseExact(s.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out value) || GermanDate(s, out value);

    private static bool GermanDate(string? s, out DateOnly value)
    {
        value = default;
        return s is not null && DateOnly.TryParseExact(s.Trim(), "dd.MM.yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out value);
    }

    /// <summary>
    /// The invoices whose date does not fit the order of the numbers: dated more than <paramref name="days"/> days before an invoice with a lower number
    /// (a typing error of the year, as "09.01.2023" between January and March 2024). Returns the invoice and the one before it that it clashes with.
    /// </summary>
    public static List<(LegacyInvoice Invoice, LegacyInvoice Before)> OutOfOrder(IEnumerable<LegacyInvoice> invoices, int days = 60)
    {
        var result = new List<(LegacyInvoice, LegacyInvoice)>();
        LegacyInvoice? latest = null;
        foreach (var i in invoices.Where(i => !i.IsDraft).OrderBy(i => i.Number.Length).ThenBy(i => i.Number, StringComparer.Ordinal))
        {
            if (latest is not null && i.Date.DayNumber < latest.Date.DayNumber - days)
            {
                result.Add((i, latest));
            }

            if (latest is null || i.Date > latest.Date)
            {
                latest = i;
            }
        }

        return result;
    }

    /// <summary>
    /// The pattern the numbers were written with: a fixed beginning and a counter at the end ("0069" → "{NNNN}", "RE-0069" → "RE-{NNNN}"), and the highest counter;
    /// null when the numbers have no common form.
    /// </summary>
    public static (string Pattern, long Last)? NumberForm(IEnumerable<string> numbers)
    {
        string? prefix = null;
        var width = 0;
        long last = 0;
        foreach (var n in numbers)
        {
            var m = CounterRegex().Match(n);
            if (!m.Success || !long.TryParse(m.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var counter))
            {
                return null;
            }

            if (prefix is not null && (prefix != m.Groups[1].Value || width != m.Groups[2].Length))
            {
                return null;
            }

            prefix = m.Groups[1].Value;
            width = m.Groups[2].Length;
            last = Math.Max(last, counter);
        }

        return prefix is null || prefix.Contains('{') || prefix.Contains('}') ? null : (prefix + "{" + new string('N', width) + "}", last);
    }

    [GeneratedRegex(@"^(\D*)(\d{1,12})$")]
    private static partial Regex CounterRegex();
}
