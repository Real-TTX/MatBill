using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using MatBill.Configuration;
using MatBill.Data;
using MatBill.Data.Entities;
using MatBill.Storage;
using Microsoft.EntityFrameworkCore;

namespace MatBill.Services;

/// <summary>One month of the report: income and expenses (net), the tax charged and the input tax, the result.</summary>
public sealed record MonthFigures(int Year, int Month, decimal IncomeNet, decimal OutputTax, decimal ExpenseNet, decimal InputTax)
{
    public decimal Result => IncomeNet - ExpenseNet;

    /// <summary>What is owed to the tax office (negative: what it pays back).</summary>
    public decimal TaxDue => OutputTax - InputTax;
}

/// <summary>
/// The figures of a year and the export for the tax office or the tax adviser: a ZIP with every invoice and every receipt of the period, named so that a person
/// finds them ("2026-10-17_RE-2026-0004_Studio Nord GmbH.pdf"), and the lists of invoices, expenses and bank bookings as CSV (semicolon, German numbers, opens in Excel).
/// </summary>
public sealed partial class ExportService(MbDbContext db, FileStoreService files, InvoiceService invoices, ConfigStore config, ILogger<ExportService> logger)
{
    /// <summary>The months of a period. <paramref name="cash"/>: income and expenses by the day they were paid (Ist-Versteuerung), else by their date (Soll).</summary>
    public async Task<List<MonthFigures>> MonthsAsync(DateOnly from, DateOnly to, bool cash, CancellationToken ct = default)
    {
        var issued = await db.Invoice.AsNoTracking().Where(i => i.Status != InvoiceStatus.Draft)
            .Select(i => new { i.InvoiceDate, i.PaidDate, i.Status, i.Kind, i.CancelsInvoiceId, i.NetTotal, i.TaxTotal }).ToListAsync(ct);
        var expenses = await db.Expense.AsNoTracking().Select(e => new { e.ExpenseDate, e.PaidDate, e.Status, e.NetAmount, e.TaxAmount }).ToListAsync(ct);

        var result = new List<MonthFigures>();
        for (var m = new DateOnly(from.Year, from.Month, 1); m <= to; m = m.AddMonths(1))
        {
            var end = m.AddMonths(1).AddDays(-1);
            bool In(DateOnly? d) => d is { } x && x >= m && x <= end && x >= from && x <= to;
            var inc = issued.Where(i => cash ? i.Kind == InvoiceKind.Invoice && i.Status == InvoiceStatus.Paid && In(i.PaidDate) : In(i.InvoiceDate)).ToList();
            var exp = expenses.Where(e => cash ? e.Status == ExpenseStatus.Paid && In(e.PaidDate) : In(e.ExpenseDate)).ToList();
            result.Add(new MonthFigures(m.Year, m.Month, inc.Sum(i => i.NetTotal), inc.Sum(i => i.TaxTotal), exp.Sum(e => e.NetAmount), exp.Sum(e => e.TaxAmount)));
        }

        return result;
    }

    public sealed record ExportResult(byte[] Zip, string FileName, int Invoices, int Expenses, int Missing);

    public async Task<ExportResult> ExportAsync(DateOnly from, DateOnly to, bool withBank, CancellationToken ct = default)
    {
        var company = config.Current.Company;
        var root = $"MatBill-Export_{from:yyyy-MM-dd}_bis_{to:yyyy-MM-dd}";
        var invoiceList = await db.Invoice.AsNoTracking().Include(i => i.Lines).Include(i => i.Customer).Include(i => i.PdfFile)
            .Where(i => i.Status != InvoiceStatus.Draft && i.InvoiceDate >= from && i.InvoiceDate <= to).OrderBy(i => i.InvoiceDate).ThenBy(i => i.Number).ToListAsync(ct);
        var expenseList = await db.Expense.AsNoTracking().Include(e => e.ReceiptFile)
            .Where(e => e.ExpenseDate >= from && e.ExpenseDate <= to).OrderBy(e => e.ExpenseDate).ThenBy(e => e.Number).ToListAsync(ct);
        var missing = 0;

        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var i in invoiceList)
            {
                var name = Unique(used, $"{root}/Rechnungen/{i.InvoiceDate:yyyy-MM-dd}_{Safe(i.Number)}_{Safe(i.RecipientName)}.pdf");
                await WriteAsync(zip, name, await invoices.PdfAsync(i, ct));
            }

            foreach (var e in expenseList)
            {
                if (e.ReceiptFile is null)
                {
                    missing++;
                    continue;
                }

                try
                {
                    var ext = Path.GetExtension(e.ReceiptFile.FileName);
                    var name = Unique(used, $"{root}/Ausgaben/{e.ExpenseDate:yyyy-MM-dd}_{Safe(e.Number)}_{Safe(e.Supplier)}{(string.IsNullOrEmpty(ext) ? ".pdf" : ext.ToLowerInvariant())}");
                    await WriteAsync(zip, name, await files.ReadAllAsync(e.ReceiptFile, ct));
                }
                catch (Exception ex) when (ex is StorageException or IOException)
                {
                    logger.LogWarning(ex, "The receipt of expense {Number} could not be read for the export.", e.Number);
                    missing++;
                }
            }

            await WriteAsync(zip, $"{root}/Rechnungen.csv", Csv(
                ["Rechnungsnummer", "Art", "Rechnungsdatum", "Leistungsdatum", "Kundennummer", "Kunde", "USt-IdNr. Kunde", "Netto", "Umsatzsteuer", "Brutto", "Status", "Fällig am", "Bezahlt am", "Storno zu", "Datei"],
                invoiceList.Select(i => new[]
                {
                    i.Number ?? "", i.Kind == InvoiceKind.Cancellation ? "Stornorechnung" : "Rechnung", D(i.InvoiceDate), D(i.ServiceFrom), i.Customer?.Number ?? "", i.RecipientName ?? "",
                    i.Customer?.VatId ?? "", M(i.NetTotal), M(i.TaxTotal), M(i.GrossTotal), UiKit.Labels.Of(i.Status), D(i.DueDate), D(i.PaidDate), i.CancelsNumber ?? "",
                    $"Rechnungen/{i.InvoiceDate:yyyy-MM-dd}_{Safe(i.Number)}_{Safe(i.RecipientName)}.pdf",
                })));

            await WriteAsync(zip, $"{root}/Ausgaben.csv", Csv(
                ["Belegnummer", "Belegdatum", "Lieferant", "Rechnungsnummer Lieferant", "Kategorie", "Beschreibung", "Netto", "USt-Satz", "Vorsteuer", "Brutto", "Status", "Bezahlt am", "E-Rechnung", "Datei"],
                expenseList.Select(e => new[]
                {
                    e.Number, D(e.ExpenseDate), e.Supplier, e.SupplierInvoiceNumber ?? "", e.Category, e.Description ?? "", M(e.NetAmount), M(e.TaxRate), M(e.TaxAmount), M(e.GrossAmount),
                    e.Status == ExpenseStatus.Paid ? "bezahlt" : "offen", D(e.PaidDate), e.FromEInvoice ? "ja" : "nein",
                    e.ReceiptFile is null ? "(kein Beleg)" : $"Ausgaben/{e.ExpenseDate:yyyy-MM-dd}_{Safe(e.Number)}_{Safe(e.Supplier)}{Path.GetExtension(e.ReceiptFile.FileName).ToLowerInvariant()}",
                })));

            var months = await MonthsAsync(from, to, cash: false, ct);
            await WriteAsync(zip, $"{root}/Uebersicht.csv", Csv(
                ["Monat", "Einnahmen netto", "Umsatzsteuer", "Ausgaben netto", "Vorsteuer", "Ergebnis", "USt-Zahllast"],
                months.Select(m => new[] { $"{m.Year}-{m.Month:00}", M(m.IncomeNet), M(m.OutputTax), M(m.ExpenseNet), M(m.InputTax), M(m.Result), M(m.TaxDue) })
                    .Append(["Summe", M(months.Sum(m => m.IncomeNet)), M(months.Sum(m => m.OutputTax)), M(months.Sum(m => m.ExpenseNet)), M(months.Sum(m => m.InputTax)), M(months.Sum(m => m.Result)), M(months.Sum(m => m.TaxDue))])));

            if (withBank)
            {
                var bank = await db.BankTransaction.AsNoTracking().Include(t => t.BankAccount).Include(t => t.Invoice).Include(t => t.Expense)
                    .Where(t => t.BookingDate >= from && t.BookingDate <= to).OrderBy(t => t.BookingDate).ToListAsync(ct);
                await WriteAsync(zip, $"{root}/Kontoumsaetze.csv", Csv(
                    ["Konto", "Buchungstag", "Valuta", "Betrag", "Währung", "Name", "IBAN", "Verwendungszweck", "Zuordnung", "Beleg"],
                    bank.Select(t => new[]
                    {
                        t.BankAccount?.Name ?? "", D(t.BookingDate), D(t.ValueDate), M(t.Amount), t.Currency, t.CounterpartyName ?? "", t.CounterpartyIban ?? "", t.Purpose ?? "",
                        t.State switch { MatchState.Matched => "zugeordnet", MatchState.Ignored => "ignoriert", _ => "offen" },
                        t.Invoice?.Number ?? t.Expense?.Number ?? "",
                    })));
            }

            await WriteAsync(zip, $"{root}/LIESMICH.txt", Encoding.UTF8.GetBytes(
                $"Export von MatBill für {company.Name}\r\nZeitraum: {DocFmt.Date(from)} bis {DocFmt.Date(to)}\r\nErstellt: {DocFmt.Moment(DateTime.UtcNow)}\r\n\r\n" +
                $"Rechnungen: {invoiceList.Count} (Ordner Rechnungen, Liste Rechnungen.csv; ausgestellte Rechnungen sind ZUGFeRD-E-Rechnungen, die XML-Daten stecken in der PDF)\r\n" +
                $"Ausgaben: {expenseList.Count} (Ordner Ausgaben, Liste Ausgaben.csv){(missing > 0 ? $"; ohne Beleg: {missing}" : "")}\r\n" +
                "Übersicht.csv: Einnahmen, Ausgaben, Umsatzsteuer und Vorsteuer je Monat (nach Rechnungs- und Belegdatum).\r\n" +
                "Die CSV-Dateien sind mit Semikolon getrennt, Beträge mit Komma (öffnen in Excel oder LibreOffice).\r\n"));
        }

        return new ExportResult(buffer.ToArray(), root + ".zip", invoiceList.Count, expenseList.Count, missing);
    }

    private static async Task WriteAsync(ZipArchive zip, string name, byte[] bytes)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        await using var stream = entry.Open();
        await stream.WriteAsync(bytes);
    }

    private static string Unique(HashSet<string> used, string name)
    {
        var candidate = name;
        var n = 2;
        while (!used.Add(candidate))
        {
            candidate = Path.ChangeExtension(name, null) + $"_{n++}" + Path.GetExtension(name);
        }

        return candidate;
    }

    /// <summary>A part of a file name: no characters a file system refuses, at most 60 characters.</summary>
    public static string Safe(string? text)
    {
        var s = SafeRegex().Replace((text ?? "").Trim(), "-").Trim('-', '.', ' ');
        return s.Length == 0 ? "ohne" : s.Length > 60 ? s[..60] : s;
    }

    [GeneratedRegex(@"[\\/:*?""<>|\r\n\t]+")]
    private static partial Regex SafeRegex();

    private static string D(DateOnly? d) => d is null ? "" : d.Value.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);

    private static string M(decimal v) => v.ToString("0.00", DocFmt.Culture);

    /// <summary>A CSV for Excel: UTF-8 with BOM, semicolons, every field in quotes when it needs them; a field that starts with = + - @ is made harmless.</summary>
    public static byte[] Csv(string[] header, IEnumerable<string[]> rows)
    {
        var sb = new StringBuilder();
        void Line(IEnumerable<string> fields) => sb.Append(string.Join(';', fields.Select(Field))).Append("\r\n");
        Line(header);
        foreach (var r in rows)
        {
            Line(r);
        }

        return [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(sb.ToString())];
    }

    private static string Field(string value)
    {
        var v = value ?? "";
        if (v.Length > 0 && "=+@".Contains(v[0]) || (v.StartsWith('-') && !decimal.TryParse(v, NumberStyles.Number, DocFmt.Culture, out _)))
        {
            v = "'" + v;
        }

        return v.IndexOfAny([';', '"', '\n', '\r']) >= 0 ? "\"" + v.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : v;
    }
}
