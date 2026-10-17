using System.Text;
using MatBill.Core.Banking;

namespace MatBill.Tests.Core;

public sealed class CsvStatementTests
{
    // The export of a German savings bank: account lines above the header, semicolons, quotes, German numbers, Windows-1252.
    private const string Sparkasse = """
        "Umsätze Girokonto";"DE12345678901234567890"
        "Zeitraum:";"01.10.2026 - 17.10.2026"

        "Auftragskonto";"Buchungstag";"Valutadatum";"Buchungstext";"Verwendungszweck";"Beguenstigter/Zahlungspflichtiger";"Kontonummer/IBAN";"BIC (SWIFT-Code)";"Betrag";"Waehrung";"Info"
        "DE12345678901234567890";"15.10.26";"15.10.26";"GUTSCHR. UEBERWEISUNG";"Rechnung RE-2026-0004 vielen Dank";"Studio Nord GmbH";"DE44500105175407324931";"INGDDEFFXXX";"2.856,00";"EUR";"Umsatz gebucht"
        "DE12345678901234567890";"14.10.26";"14.10.26";"FOLGELASTSCHRIFT";"EREF+TK-554210 MREF+M-1 SVWZ+Kundennr 1234 Rechnung TK-554210";"Telekom Deutschland GmbH";"DE89370400440532013000";"COBADEFFXXX";"-59,95";"EUR";"Umsatz gebucht"
        "DE12345678901234567890";"14.10.26";"14.10.26";"KARTENZAHLUNG";"Café am Markt";"Café am Markt";"";"";"-12,49";"EUR";"Umsatz vorgemerkt"
        """;

    [Fact]
    public void A_bank_export_with_account_lines_above_the_header_is_read_with_its_columns()
    {
        var table = CsvStatement.Read(Sparkasse);
        Assert.Equal(';', table.Delimiter);
        Assert.Equal("Auftragskonto", table.Headers[0]);
        Assert.Equal(3, table.Rows.Count);

        var mapping = CsvStatement.Guess(table);
        Assert.Equal(1, mapping.BookingDate);
        Assert.Equal(8, mapping.Amount);
        Assert.Equal(5, mapping.Name);
        Assert.Equal(6, mapping.Iban);
        Assert.Contains(4, mapping.Purpose);

        var problems = new List<string>();
        var lines = CsvStatement.Lines(table, mapping, problems);
        Assert.Empty(problems);
        Assert.Equal(2, lines.Count); // the pending card payment is left out
        Assert.Equal(2856.00m, lines[0].Amount);
        Assert.Equal(new DateOnly(2026, 10, 15), lines[0].BookingDate);
        Assert.Equal("Studio Nord GmbH", lines[0].CounterpartyName);
        Assert.Equal(-59.95m, lines[1].Amount);
        Assert.Equal("TK-554210", lines[1].EndToEndId);
    }

    [Fact]
    public void Separate_debit_and_credit_columns_and_a_comma_separated_english_file_are_read()
    {
        var table = CsvStatement.Read("Date,Description,Debit,Credit\n2026-10-01,Office rent,950.00,\n2026-10-02,Customer payment,,1200.50\n");
        var mapping = CsvStatement.Guess(table);
        Assert.Equal(2, mapping.Debit);
        Assert.Equal(3, mapping.Credit);
        var lines = CsvStatement.Lines(table, mapping, []);
        Assert.Equal(-950m, lines[0].Amount);
        Assert.Equal(1200.50m, lines[1].Amount);
    }

    [Fact]
    public void Windows_1252_is_recognized_when_the_bytes_are_no_utf8()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var bytes = Encoding.GetEncoding(1252).GetBytes("Buchungstag;Betrag;Empfänger\n01.10.2026;-5,00;Bäckerei Müller\n");
        Assert.Contains("Bäckerei Müller", CsvStatement.Decode(bytes));
    }

    [Fact]
    public void Two_equal_bookings_on_one_day_stay_two_and_the_same_file_again_brings_nothing_new()
    {
        var coffee = new StatementLine(new DateOnly(2026, 10, 1), null, -3.5m, "EUR", "Café", null, "Kaffee");
        var keys = StatementDedup.Keys([coffee, coffee]);
        Assert.NotEqual(keys[0], keys[1]);
        Assert.Equal(keys, StatementDedup.Keys([coffee, coffee]));
        Assert.StartsWith("ref:", StatementDedup.Keys([coffee with { Reference = "X1" }])[0]);
    }
}

public sealed class BankFormatTests
{
    private const string Camt = """
        <?xml version="1.0" encoding="UTF-8"?>
        <Document xmlns="urn:iso:std:iso:20022:tech:xsd:camt.053.001.08">
          <BkToCstmrStmt>
            <Stmt>
              <Acct><Id><IBAN>DE12345678901234567890</IBAN></Id></Acct>
              <Bal><Tp><CdOrPrtry><Cd>CLBD</Cd></CdOrPrtry></Tp><Amt Ccy="EUR">12873.40</Amt><CdtDbtInd>CRDT</CdtDbtInd><Dt><Dt>2026-10-17</Dt></Dt></Bal>
              <Ntry>
                <Amt Ccy="EUR">2856.00</Amt><CdtDbtInd>CRDT</CdtDbtInd><Sts><Cd>BOOK</Cd></Sts>
                <BookgDt><Dt>2026-10-15</Dt></BookgDt><ValDt><Dt>2026-10-15</Dt></ValDt><AcctSvcrRef>2026101500001</AcctSvcrRef>
                <NtryDtls><TxDtls>
                  <Refs><EndToEndId>NOTPROVIDED</EndToEndId></Refs>
                  <RltdPties><Dbtr><Pty><Nm>Studio Nord GmbH</Nm></Pty></Dbtr><DbtrAcct><Id><IBAN>DE44500105175407324931</IBAN></Id></DbtrAcct></RltdPties>
                  <RmtInf><Ustrd>Rechnung RE-2026-0004</Ustrd></RmtInf>
                </TxDtls></NtryDtls>
              </Ntry>
              <Ntry>
                <Amt Ccy="EUR">59.95</Amt><CdtDbtInd>DBIT</CdtDbtInd><Sts><Cd>BOOK</Cd></Sts>
                <BookgDt><Dt>2026-10-14</Dt></BookgDt><AcctSvcrRef>2026101400007</AcctSvcrRef>
                <NtryDtls><TxDtls>
                  <Refs><EndToEndId>TK-554210</EndToEndId></Refs>
                  <RltdPties><Cdtr><Nm>Telekom Deutschland GmbH</Nm></Cdtr></RltdPties>
                  <RmtInf><Ustrd>Kundennr 1234</Ustrd><Ustrd>Rechnung TK-554210</Ustrd></RmtInf>
                </TxDtls></NtryDtls>
              </Ntry>
            </Stmt>
          </BkToCstmrStmt>
        </Document>
        """;

    [Fact]
    public void A_camt053_statement_gives_its_bookings_its_account_and_its_balance()
    {
        Assert.Equal(StatementFormat.Camt, StatementFormats.Detect(Camt));
        var (lines, info) = CamtStatement.Read(Camt);
        Assert.Equal("DE12345678901234567890", info.Iban);
        Assert.Equal(12873.40m, info.Balance);
        Assert.Equal(2, lines.Count);
        Assert.Equal(2856m, lines[0].Amount);
        Assert.Equal("Studio Nord GmbH", lines[0].CounterpartyName);
        Assert.Equal("DE44500105175407324931", lines[0].CounterpartyIban);
        Assert.Null(lines[0].EndToEndId);
        Assert.Equal(-59.95m, lines[1].Amount);
        Assert.Equal("Telekom Deutschland GmbH", lines[1].CounterpartyName);
        Assert.Equal("Kundennr 1234 Rechnung TK-554210", lines[1].Purpose);
        Assert.Equal("TK-554210", lines[1].EndToEndId);
    }

    private const string Mt940 = """
        :20:STARTUMS
        :25:DE12345678901234567890
        :28C:0
        :60F:C261001EUR10000,00
        :61:2610151015CR2856,00NTRFNONREF//2026101500001
        :86:166?00GUTSCHR. UEBERWEISUNG?20SVWZ+Rechnung RE-2026-0004?21 vielen Dank?30INGDDEFFXXX?31DE44500105175407324931?32Studio Nord GmbH
        :61:2610141014DR59,95NDDTNONREF//2026101400007
        :86:105?00FOLGELASTSCHRIFT?20EREF+TK-554210?21SVWZ+Rechnung TK-554210?32Telekom Deutschland?33GmbH
        :62F:C261017EUR12796,05
        -
        """;

    [Fact]
    public void An_mt940_statement_with_the_german_structured_field_86_is_read()
    {
        Assert.Equal(StatementFormat.Mt940, StatementFormats.Detect(Mt940));
        var (lines, info) = Mt940Statement.Read(Mt940);
        Assert.Equal("DE12345678901234567890", info.Iban);
        Assert.Equal(12796.05m, info.Balance);
        Assert.Equal(2, lines.Count);
        Assert.Equal(2856m, lines[0].Amount);
        Assert.Equal(new DateOnly(2026, 10, 15), lines[0].BookingDate);
        Assert.Equal("Studio Nord GmbH", lines[0].CounterpartyName);
        Assert.Equal("Rechnung RE-2026-0004 vielen Dank", lines[0].Purpose);
        Assert.Equal("DE44500105175407324931", lines[0].CounterpartyIban);
        Assert.Equal(-59.95m, lines[1].Amount);
        Assert.Equal("Telekom DeutschlandGmbH", lines[1].CounterpartyName);
        Assert.Equal("TK-554210", lines[1].EndToEndId);
        Assert.Equal("2026101400007", lines[1].Reference);
    }

    [Fact]
    public void Sepa_tags_are_taken_apart()
    {
        Assert.Equal("Rechnung 42", SepaPurpose.Clean("EREF+E1 MREF+M1 SVWZ+Rechnung 42"));
        Assert.Equal("E1", SepaPurpose.EndToEnd("EREF+E1 SVWZ+x"));
        Assert.Null(SepaPurpose.EndToEnd("EREF+NOTPROVIDED SVWZ+x"));
        Assert.Equal("Miete Oktober", SepaPurpose.Clean("Miete Oktober"));
    }
}

public sealed class PaymentMatcherTests
{
    private static readonly OpenItem Nord = new(1, "invoice", "RE-2026-0004", 2856m, "Studio Nord GmbH", new DateOnly(2026, 10, 1));
    private static readonly OpenItem Mueller = new(2, "invoice", "RE-2026-0005", 1071m, "Müller & Partner", new DateOnly(2026, 10, 3));
    private static readonly OpenItem Alex = new(3, "invoice", "RE-2026-0006", 1071m, "Alex Schneider", new DateOnly(2026, 10, 5));

    [Fact]
    public void Number_and_amount_assign_by_themselves()
    {
        var candidates = PaymentMatcher.Candidates(new PaymentInfo(2856m, new DateOnly(2026, 10, 15), "STUDIO NORD", null, "Rechnung RE 2026 0004"), [Nord, Mueller, Alex]);
        Assert.Equal(1, PaymentMatcher.Auto(candidates)?.Item.Id);
    }

    [Fact]
    public void Two_open_invoices_with_the_same_amount_are_told_apart_by_the_name()
    {
        var candidates = PaymentMatcher.Candidates(new PaymentInfo(1071m, new DateOnly(2026, 10, 10), "Mueller und Partner", null, "Zahlung"), [Nord, Mueller, Alex]);
        Assert.Equal(2, candidates[0].Item.Id);
        Assert.Null(PaymentMatcher.Auto(candidates)); // amount and name, but the amount is not unique: suggested, not assigned
    }

    [Fact]
    public void A_unique_amount_with_the_name_assigns_and_an_amount_alone_only_suggests()
    {
        Assert.Equal(1, PaymentMatcher.Auto(PaymentMatcher.Candidates(new PaymentInfo(2856m, new DateOnly(2026, 10, 15), "Studio Nord GmbH", null, "Danke"), [Nord, Mueller]))?.Item.Id);
        var alone = PaymentMatcher.Candidates(new PaymentInfo(2856m, new DateOnly(2026, 10, 15), "Unbekannt", null, null), [Nord, Mueller]);
        Assert.Equal(1, alone[0].Item.Id);
        Assert.Null(PaymentMatcher.Auto(alone));
    }

    [Fact]
    public void A_discount_and_a_number_with_another_amount_are_suggestions()
    {
        var skonto = PaymentMatcher.Candidates(new PaymentInfo(2770.32m, new DateOnly(2026, 10, 8), "Studio Nord GmbH", null, "RE-2026-0004 abzgl. 3% Skonto"), [Nord]);
        Assert.True(skonto[0].Score >= 80);
        Assert.Contains(skonto[0].Reasons, r => r.Contains("Skonto", StringComparison.Ordinal));
    }
}
