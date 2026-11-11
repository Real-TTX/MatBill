using System.IO.Compression;
using System.Text;
using MatBill.Core.Import;

namespace MatBill.Tests.Core;

/// <summary>A small export of Invoiz in the form the move to MatBill wrote it (see <see cref="InvoizExport"/>): made up, never real data.</summary>
public static class SampleExport
{
    public const string Customers = """
        {
          "customers": [
            { "invoizId": 11, "number": "{P}010", "kind": "company", "name": "Nordlicht {P} GmbH", "email": "rechnung@nordlicht.test", "street": "Hafenweg 2", "zip": "20457", "city": "Hamburg", "country": "DE" },
            { "invoizId": 12, "number": "{P}011", "kind": "person", "name": "Erika {P} Muster", "street": "Ringstr. 1", "zip": "70173", "city": "Stuttgart", "country": "DE", "website": "www.muster.test" }
          ],
          "articles": [
            { "invoizId": 21, "number": "{P}-1", "title": "Wartung {P} - Monatlich", "priceNet": 100 },
            { "invoizId": 22, "number": "{P}-2", "title": "Lizenz {P}", "priceNet": 175 }
          ]
        }
        """;

    // three issued invoices (one dated a year too early, one with a discount, one with a payment taken back) and a draft without a number
    public const string Invoices = """
        R|{P}901|{N}2|paid|2024-02-01|2024-02-01||2024-02-01|Rechnung|11|Nordlicht {P} GmbH|100|100|0|2024-02-03@100
        P|Wartung {P} - Monatlich|02/24|1|Stk.|100|0|100|{P}-1
        R|{P}900|{N}1|paid|2024-01-02|2024-01-02||2024-01-02|Rechnung|11|Nordlicht {P} GmbH|100|100|0|2024-01-05@100,2024-01-05@-100,2024-01-05@100
        P|Wartung {P} - Monatlich|01/24|1|Stk.|100|0|100|{P}-1
        R|{P}902|{N}3|paid|2023-01-09|2023-01-09||2023-01-09|Rechnung|12|Erika {P} Muster|383.05|383.05|0|2024-02-10@383.05
        P|Lizenz {P}|für eine Schule|2|Stk.|175|6|329|{P}-2
        P|Hosting||1|Stk.|54.05|0|54.05|
        R|{P}903|a0069039dba61a290363|draft|2024-03-01|2024-03-01|||Rechnung|11|Nordlicht {P} GmbH|100|100|100|
        P|Wartung {P} - Monatlich|03/24|1|Stk.|100|0|100|{P}-1
        """;

    public const string Expenses = """
        Belegnummer;Belegdatum;Lieferant;Rechnungsnummer Lieferant;Kategorie;Beschreibung;Netto;USt-Satz;USt (keine Vorsteuer);Brutto;Status;Bezahlt am;E-Rechnung;Datei;Invoiz-ID;Hinweis
        ;14.08.2023;Gemeinde {P};20;Sonstiges;Gewerbeanmeldung;10,00;0;0,00;10,00;bezahlt;14.08.2023;nein;Belege/2023-08-14_Gemeinde_{P}31.jpg;{P}31;
        ;21.01.2025;"Händler {P}; Versand";2154683;IT-Hardware {P};2x Festplatte;368,07;19;69,93;438,00;bezahlt;21.01.2025;nein;Belege/2025-01-21_Haendler_{P}32.pdf;{P}32;ZU PRÜFEN: Rechnung nennt ein anderes Modell
        """;

    /// <summary>The files of the export; <paramref name="p"/> makes names and ids unique, <paramref name="n"/> is the start of the invoice numbers ("{N}1").</summary>
    public static Dictionary<string, byte[]> Files(string p, string n, bool withPdfs = true)
    {
        string Fill(string s) => s.Replace("{P}", p).Replace("{N}", n);
        var files = new Dictionary<string, byte[]>
        {
            ["Invoiz-Export/daten/kunden-artikel.json"] = Encoding.UTF8.GetBytes(Fill(Customers)),
            ["Invoiz-Export/daten/rechnungen.psv"] = Encoding.UTF8.GetBytes(Fill(Invoices).Replace("\r\n", "\n")),
            ["Invoiz-Export/Ausgaben/Ausgaben.csv"] = [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(Fill(Expenses))],
            [$"Invoiz-Export/Ausgaben/Belege/2023-08-14_Gemeinde_{p}31.jpg"] = Encoding.ASCII.GetBytes("JPEG " + p),
            [$"Invoiz-Export/Ausgaben/Belege/2025-01-21_Haendler_{p}32.pdf"] = Encoding.ASCII.GetBytes("%PDF-1.4 receipt " + p),
        };
        if (withPdfs)
        {
            files[$"Invoiz-Export/Rechnungen/2024-01-02_{n}1.pdf"] = Pdf(n + "1");
            files[$"Invoiz-Export/Rechnungen/2024-02-01_{n}2.pdf"] = Pdf(n + "2");
            files[$"Invoiz-Export/Rechnungen/2023-01-09_{n}3.pdf"] = Pdf(n + "3");
        }

        return files;
    }

    /// <summary>The "original" PDF of an invoice: bytes no renderer of MatBill would write, so a test sees that they come back unchanged.</summary>
    public static byte[] Pdf(string number) => Encoding.ASCII.GetBytes($"%PDF-1.4\n% Original aus Invoiz {number}\n%%EOF\n");

    public static byte[] Zip(Dictionary<string, byte[]> files)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, bytes) in files)
            {
                using var stream = zip.CreateEntry(name).Open();
                stream.Write(bytes);
            }
        }

        return buffer.ToArray();
    }
}

public sealed class InvoizExportTests
{
    [Fact]
    public void An_export_is_read_below_its_folder_with_customers_products_invoices_and_expenses()
    {
        var data = InvoizExport.Read(SampleExport.Files("T", "00"));
        Assert.Empty(data.Problems);
        Assert.Equal(2, data.Customers.Count);
        Assert.Equal(2, data.Products.Count);
        Assert.Equal(4, data.Invoices.Count);
        Assert.Equal(2, data.Expenses.Count);

        var person = data.Customers.Single(c => c.IsPerson);
        Assert.Equal("T011", person.Number);
        Assert.Equal("Deutschland", person.Country);
        Assert.Equal("www.muster.test", person.Website);

        var draft = Assert.Single(data.Invoices, i => i.IsDraft);
        Assert.Equal("T903", draft.SourceId);

        // a payment taken back counts: +100 −100 +100 is 100 paid on the 5th
        var first = data.Invoices.Single(i => i.Number == "001");
        Assert.Equal(100m, first.Paid);
        Assert.Equal(new DateOnly(2024, 1, 5), first.PaidDate);
        Assert.Equal(new DateOnly(2024, 1, 2), first.ServiceFrom);

        var third = data.Invoices.Single(i => i.Number == "003");
        Assert.Equal(6m, third.Lines[0].DiscountPercent);
        Assert.Equal(329m, third.Lines[0].Net);
        Assert.Null(third.Lines[1].ProductNumber);
        Assert.NotNull(data.InvoicePdf("003"));
        Assert.Null(data.InvoicePdf("004"));
    }

    [Fact]
    public void The_expense_csv_keeps_quoted_fields_german_amounts_and_the_review_hint()
    {
        var data = InvoizExport.Read(SampleExport.Files("T", "00"));
        var review = data.Expenses.Single(e => e.NeedsReview);
        Assert.Equal("Händler T; Versand", review.Supplier);
        Assert.Equal(368.07m, review.Net);
        Assert.Equal(69.93m, review.Tax);
        Assert.Equal(438.00m, review.Gross);
        Assert.Equal("IT-Hardware T", review.Category);
        Assert.True(review.Paid);
        Assert.Equal(new DateOnly(2025, 1, 21), review.PaidDate);
        Assert.NotNull(data.Receipt(review.File));
        Assert.False(data.Expenses.Single(e => !e.NeedsReview).NeedsReview);
    }

    [Fact]
    public void An_invoice_dated_long_before_a_lower_number_is_found()
    {
        var data = InvoizExport.Read(SampleExport.Files("T", "00"));
        var hit = Assert.Single(InvoizExport.OutOfOrder(data.Invoices));
        Assert.Equal("003", hit.Invoice.Number);
        Assert.Equal("002", hit.Before.Number);
    }

    [Theory]
    [InlineData(new[] { "0001", "0069", "0010" }, "{NNNN}", 69)]
    [InlineData(new[] { "RE-001", "RE-012" }, "RE-{NNN}", 12)]
    public void The_form_of_the_numbers_becomes_a_pattern(string[] numbers, string pattern, long last)
    {
        var form = InvoizExport.NumberForm(numbers);
        Assert.Equal((pattern, last), form);
    }

    [Fact]
    public void Numbers_without_a_common_form_give_no_pattern()
    {
        Assert.Null(InvoizExport.NumberForm(["0001", "RE-0002"]));
        Assert.Null(InvoizExport.NumberForm(["0001", "00002"]));
        Assert.Null(InvoizExport.NumberForm(["2024-A"]));
    }

    [Fact]
    public void A_zip_without_the_files_of_an_export_is_no_export()
    {
        var data = InvoizExport.Read(new Dictionary<string, byte[]> { ["readme.txt"] = [1] });
        Assert.Equal("Import.Problem.NotAnExport", Assert.Single(data.Problems).Key);
    }
}
