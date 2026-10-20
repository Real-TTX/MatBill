using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using MatBill.Configuration;
using MatBill.Core.Billing;
using MatBill.Data.Entities;
using QuestPDF.Fluent;
using s2industries.ZUGFeRD;
using UglyToad.PdfPig;

namespace MatBill.Services;

/// <summary>What a receipt says, read from an e-invoice (exact) or guessed from the text of a PDF.</summary>
public sealed record ReceiptData(string? Supplier, string? SupplierIban, string? Number, DateOnly? Date, DateOnly? DueDate, decimal? Net, decimal? Tax, decimal? Gross, decimal? TaxRate, bool FromEInvoice);

/// <summary>
/// E-invoices (EN 16931): the structured invoice as XML in the syntax UN/CEFACT CII, as a ZUGFeRD / Factur-X PDF (a PDF/A-3 with the XML inside, profile EN 16931)
/// and as an XRechnung for public authorities. Also reads the e-invoices of suppliers, so that an expense fills itself.
/// </summary>
public static class EInvoice
{
    public const string AttachmentName = "factur-x.xml";

    /// <summary>The XML of an issued invoice. <paramref name="xrechnung"/> writes the XRechnung profile (German public authorities), otherwise EN 16931 (ZUGFeRD "Comfort").</summary>
    public static byte[] Xml(Invoice invoice, CompanyConfig company, bool xrechnung = false)
    {
        if (invoice.Number is null)
        {
            throw new InvoiceException("Ein Entwurf hat noch keine E-Rechnung. Stelle die Rechnung zuerst aus.");
        }

        var d = InvoiceDescriptor.CreateInvoice(invoice.Number, invoice.InvoiceDate.ToDateTime(TimeOnly.MinValue), CurrencyCodes.EUR);
        d.Type = invoice.Kind == InvoiceKind.Cancellation ? InvoiceType.Correction : InvoiceType.Invoice;
        d.ReferenceOrderNo = invoice.BuyerReference ?? invoice.Customer?.BuyerReference ?? (xrechnung ? invoice.Customer?.Number ?? invoice.Number : null);
        if (invoice.Kind == InvoiceKind.Cancellation && invoice.CancelsNumber is { } cancels)
        {
            d.AddInvoiceReferencedDocument(cancels, null, null);
        }

        d.SetSeller(company.Name, company.PostalCode ?? "", company.City ?? "", company.Street ?? "", Country(company.Country), null, null, null);
        if (!string.IsNullOrWhiteSpace(company.VatId))
        {
            d.AddSellerTaxRegistration(company.VatId.Replace(" ", "", StringComparison.Ordinal), TaxRegistrationSchemeID.VA);
        }

        if (!string.IsNullOrWhiteSpace(company.TaxNumber))
        {
            d.AddSellerTaxRegistration(company.TaxNumber, TaxRegistrationSchemeID.FC);
        }

        d.SetSellerContact(name: StripRole(company.Manager) ?? (string.IsNullOrWhiteSpace(company.NameLine2) ? company.Name : company.NameLine2.Trim()), orgunit: "", emailAddress: company.Email ?? "", phoneno: company.Phone ?? "", faxno: "");
        if (!string.IsNullOrWhiteSpace(company.Email))
        {
            d.SetSellerElectronicAddress(company.Email, ElectronicAddressSchemeIdentifiers.ElectronicMailSmtp);
        }

        d.SetBuyer(invoice.RecipientName ?? "", invoice.RecipientPostalCode ?? "", invoice.RecipientCity ?? "", invoice.RecipientStreet ?? "", Country(invoice.RecipientCountry),
            invoice.Customer?.Number, null, invoice.RecipientContact, null);
        if (!string.IsNullOrWhiteSpace(invoice.Customer?.VatId))
        {
            d.AddBuyerTaxRegistration(invoice.Customer.VatId.Replace(" ", "", StringComparison.Ordinal), TaxRegistrationSchemeID.VA);
        }

        if (!string.IsNullOrWhiteSpace(invoice.RecipientEmail))
        {
            d.SetBuyerElectronicAddress(invoice.RecipientEmail, ElectronicAddressSchemeIdentifiers.ElectronicMailSmtp);
        }

        if (invoice.ServiceFrom is { } from)
        {
            d.ActualDeliveryDate = from.ToDateTime(TimeOnly.MinValue);
            if (invoice.ServiceTo is { } to && to != from)
            {
                d.BillingPeriodStart = from.ToDateTime(TimeOnly.MinValue);
                d.BillingPeriodEnd = to.ToDateTime(TimeOnly.MinValue);
            }
        }

        var exempt = invoice.TaxExempt;
        foreach (var l in invoice.Lines.OrderBy(l => l.Position))
        {
            var rate = exempt ? 0m : l.TaxRate;
            d.AddTradeLineItem(
                lineID: l.Position.ToString(CultureInfo.InvariantCulture),
                name: l.Name,
                netUnitPrice: l.UnitPrice,
                description: l.Description,
                unitCode: Unit(l.Unit),
                unitQuantity: null,
                grossUnitPrice: l.UnitPrice,
                billedQuantity: l.Quantity,
                lineTotalAmount: l.Net,
                taxType: TaxTypes.VAT,
                categoryCode: Category(exempt, rate),
                taxPercent: rate);
        }

        var totals = InvoiceMath.Totals(invoice.Lines.Select(l => l.Amounts), exempt);
        foreach (var g in totals.Taxes)
        {
            var category = Category(exempt, g.Rate);
            d.AddApplicableTradeTax(g.Net, g.Rate, g.Tax, TaxTypes.VAT, category,
                exemptionReason: category == TaxCategoryCodes.E ? "Steuerbefreit nach § 19 UStG (Kleinunternehmer)" : null);
        }

        d.SetTotals(lineTotalAmount: totals.Net, chargeTotalAmount: 0m, allowanceTotalAmount: 0m, taxBasisAmount: totals.Net, taxTotalAmount: totals.Tax,
            grandTotalAmount: totals.Gross, totalPrepaidAmount: 0m, duePayableAmount: totals.Gross);

        if (invoice.Kind == InvoiceKind.Invoice)
        {
            d.AddTradePaymentTerms(Texts.Expand(invoice.PaymentTerms, invoice.PaymentTermDays, invoice.DueDate) ?? $"Zahlbar bis {DocFmt.Date(invoice.DueDate)}", invoice.DueDate.ToDateTime(TimeOnly.MinValue));
            if (!string.IsNullOrWhiteSpace(company.Iban))
            {
                d.SetPaymentMeans(PaymentMeansTypeCodes.SEPACreditTransfer, "Überweisung");
                d.AddCreditorFinancialAccount(company.Iban.Replace(" ", "", StringComparison.Ordinal), company.Bic, bankName: company.BankName, name: company.AccountHolder ?? company.Name);
                if (!string.IsNullOrWhiteSpace(company.Bank2Iban))
                {
                    d.AddCreditorFinancialAccount(company.Bank2Iban.Replace(" ", "", StringComparison.Ordinal), company.Bank2Bic, bankName: company.Bank2Name, name: company.AccountHolder ?? company.Name);
                }
            }
        }

        using var stream = new MemoryStream();
        d.Save(stream, ZUGFeRDVersion.Version23, xrechnung ? Profile.XRechnung : Profile.Comfort, ZUGFeRDFormats.CII);
        return stream.ToArray();
    }

    /// <summary>Puts the XML into a PDF/A-3b (drawn with pdfA on) as the Factur-X attachment with the metadata the standard asks for.</summary>
    public static byte[] Embed(byte[] pdfA3, byte[] xml)
    {
        var folder = Path.Combine(Path.GetTempPath(), "matbill-zugferd-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var source = Path.Combine(folder, "invoice.pdf");
            var xmlPath = Path.Combine(folder, AttachmentName);
            var target = Path.Combine(folder, "zugferd.pdf");
            File.WriteAllBytes(source, pdfA3);
            File.WriteAllBytes(xmlPath, xml);
            DocumentOperation.LoadFile(source)
                .AddAttachment(new DocumentOperation.DocumentAttachment
                {
                    Key = AttachmentName,
                    FilePath = xmlPath,
                    AttachmentName = AttachmentName,
                    MimeType = "text/xml",
                    Description = "Factur-X Invoice",
                    Relationship = DocumentOperation.DocumentAttachmentRelationship.Alternative,
                    CreationDate = DateTime.UtcNow,
                    ModificationDate = DateTime.UtcNow,
                })
                .ExtendMetadata(XmpMetadata)
                .Save(target);
            return File.ReadAllBytes(target);
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); } catch (IOException) { /* a temp folder that stays is no harm */ }
        }
    }

    /// <summary>Reads a receipt: the XML of an e-invoice (as a file or inside a PDF) exactly, otherwise the text of a PDF as well as it goes.</summary>
    public static ReceiptData? Read(byte[] bytes, string fileName)
    {
        try
        {
            if (LooksLikeXml(bytes))
            {
                return FromXml(bytes);
            }

            if (bytes.Length > 4 && bytes[0] == '%' && bytes[1] == 'P' && bytes[2] == 'D' && bytes[3] == 'F')
            {
                using var pdf = PdfDocument.Open(bytes);
                if (pdf.Advanced.TryGetEmbeddedFiles(out var files))
                {
                    var xml = files.FirstOrDefault(f => f.Name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)) ?? files.FirstOrDefault(f => LooksLikeXml([.. f.Bytes]));
                    if (xml is not null)
                    {
                        return FromXml([.. xml.Bytes]);
                    }
                }

                var text = string.Join("\n", pdf.GetPages().Take(3).Select(p => p.Text));
                return Guess(text);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // a receipt that cannot be read is still a receipt: the person fills in the fields
        }

        return null;
    }

    private static ReceiptData FromXml(byte[] xml)
    {
        using var stream = new MemoryStream(xml);
        var d = InvoiceDescriptor.Load(stream);
        var rate = d.Taxes?.Count == 1 ? d.Taxes[0].Percent : (decimal?)null;
        DateOnly? Date(DateTime? dt) => dt is { } x ? DateOnly.FromDateTime(x) : null;
        return new ReceiptData(
            d.Seller?.Name,
            d.CreditorBankAccounts?.FirstOrDefault()?.IBAN,
            d.InvoiceNo,
            Date(d.InvoiceDate),
            Date(d.PaymentTerms?.FirstOrDefault()?.DueDate),
            d.TaxBasisAmount,
            d.TaxTotalAmount,
            d.GrandTotalAmount ?? d.DuePayableAmount,
            rate,
            FromEInvoice: true);
    }

    /// <summary>A guess from the text of an ordinary PDF: the date, the invoice number and the largest amount near "Gesamt", "Summe" or "zu zahlen".</summary>
    public static ReceiptData Guess(string text)
    {
        var dates = Regex.Matches(text, @"\b(\d{1,2})\.(\d{1,2})\.(20\d{2})\b").Select(m => DateOnly.TryParseExact(m.Value, ["d.M.yyyy", "dd.MM.yyyy"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt) ? dt : (DateOnly?)null).Where(d => d is not null).ToList();
        var number = Regex.Match(text, @"(?:Rechnungs?-?(?:nummer|nr\.?)|Invoice\s*(?:No\.?|Number))\s*[:#]?\s*([A-Z0-9][A-Z0-9\-/_.]{2,30})", RegexOptions.IgnoreCase);
        decimal? gross = null;
        foreach (Match m in Regex.Matches(text, @"(?:Gesamt(?:betrag|summe)?|Rechnungsbetrag|Endbetrag|zu zahlen|Summe|Total|Brutto)[^\d\n]{0,40}(-?\d{1,3}(?:\.\d{3})*,\d{2}|-?\d+\.\d{2})", RegexOptions.IgnoreCase))
        {
            if (Amount.TryParse(m.Groups[1].Value, out var v) && (gross is null || v > gross))
            {
                gross = v;
            }
        }

        var rate = Regex.IsMatch(text, @"\b19\s?%") ? 19m : Regex.IsMatch(text, @"\b7\s?%") ? 7m : (decimal?)null;
        decimal? net = null, tax = null;
        if (gross is { } g && rate is { } r)
        {
            net = InvoiceMath.Round(g / (1 + r / 100m));
            tax = g - net;
        }

        var supplier = text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length is > 2 and < 60 && Regex.IsMatch(l, @"(GmbH|AG|UG|KG|e\.K\.|GbR|Ltd|Inc|SE)\b"));
        return new ReceiptData(supplier, null, number.Success ? number.Groups[1].Value : null, dates.FirstOrDefault(), null, net, tax, gross, rate, FromEInvoice: false);
    }

    private static bool LooksLikeXml(byte[] bytes)
    {
        var head = Encoding.UTF8.GetString(bytes, 0, Math.Min(bytes.Length, 512)).TrimStart('﻿', ' ', '\r', '\n', '\t');
        return head.StartsWith("<?xml", StringComparison.Ordinal) || head.StartsWith("<rsm:", StringComparison.Ordinal) || head.StartsWith("<Invoice", StringComparison.Ordinal) || head.StartsWith("<ubl:", StringComparison.Ordinal);
    }

    private static TaxCategoryCodes Category(bool exempt, decimal rate) => exempt ? TaxCategoryCodes.E : rate == 0 ? TaxCategoryCodes.Z : TaxCategoryCodes.S;

    private static CountryCodes Country(string? name) => (name ?? "").Trim().ToLowerInvariant() switch
    {
        "österreich" or "austria" or "at" => CountryCodes.AT,
        "schweiz" or "switzerland" or "ch" => CountryCodes.CH,
        "niederlande" or "netherlands" or "nl" => CountryCodes.NL,
        "frankreich" or "france" or "fr" => CountryCodes.FR,
        "belgien" or "belgium" or "be" => CountryCodes.BE,
        "luxemburg" or "luxembourg" or "lu" => CountryCodes.LU,
        "italien" or "italy" or "it" => CountryCodes.IT,
        "spanien" or "spain" or "es" => CountryCodes.ES,
        "polen" or "poland" or "pl" => CountryCodes.PL,
        "dänemark" or "denmark" or "dk" => CountryCodes.DK,
        _ => CountryCodes.DE,
    };

    /// <summary>The unit of a position as the code of UN/ECE Recommendation 20 (C62 "piece" when it is no known one).</summary>
    public static QuantityCodes Unit(string? unit) => (unit ?? "").Trim().ToLowerInvariant() switch
    {
        "stunde" or "stunden" or "std" or "std." or "h" or "hour" => QuantityCodes.HUR,
        "tag" or "tage" or "day" => QuantityCodes.DAY,
        "monat" or "monate" or "month" => QuantityCodes.MON,
        "pauschal" or "pauschale" or "psch" or "psch." => QuantityCodes.LS,
        "kg" => QuantityCodes.KGM,
        "stück" or "stk" or "stk." or "st" => QuantityCodes.H87,
        _ => QuantityCodes.C62,
    };

    private static string? StripRole(string? manager)
        => string.IsNullOrWhiteSpace(manager) ? null : manager.Contains(':', StringComparison.Ordinal) ? manager[(manager.IndexOf(':') + 1)..].Trim() : manager.Trim();

    /// <summary>The XMP extension of Factur-X / ZUGFeRD 2 (profile EN 16931), as the PDF/A validators want it.</summary>
    private const string XmpMetadata = """
        <rdf:Description xmlns:fx="urn:factur-x:pdfa:CrossIndustryDocument:invoice:1p0#" rdf:about="">
            <fx:ConformanceLevel>EN 16931</fx:ConformanceLevel>
            <fx:DocumentType>INVOICE</fx:DocumentType>
            <fx:DocumentFileName>factur-x.xml</fx:DocumentFileName>
            <fx:Version>1.0</fx:Version>
        </rdf:Description>
        <rdf:Description xmlns:pdfaExtension="http://www.aiim.org/pdfa/ns/extension/" xmlns:pdfaProperty="http://www.aiim.org/pdfa/ns/property#" xmlns:pdfaSchema="http://www.aiim.org/pdfa/ns/schema#" rdf:about="">
        <pdfaExtension:schemas>
            <rdf:Bag>
                <rdf:li rdf:parseType="Resource">
                    <pdfaSchema:schema>ZUGFeRD PDFA Extension Schema</pdfaSchema:schema>
                    <pdfaSchema:namespaceURI>urn:factur-x:pdfa:CrossIndustryDocument:invoice:1p0#</pdfaSchema:namespaceURI>
                    <pdfaSchema:prefix>fx</pdfaSchema:prefix>
                    <pdfaSchema:property>
                        <rdf:Seq>
                            <rdf:li rdf:parseType="Resource"><pdfaProperty:name>DocumentFileName</pdfaProperty:name><pdfaProperty:valueType>Text</pdfaProperty:valueType><pdfaProperty:category>external</pdfaProperty:category><pdfaProperty:description>name of the embedded XML invoice file</pdfaProperty:description></rdf:li>
                            <rdf:li rdf:parseType="Resource"><pdfaProperty:name>DocumentType</pdfaProperty:name><pdfaProperty:valueType>Text</pdfaProperty:valueType><pdfaProperty:category>external</pdfaProperty:category><pdfaProperty:description>INVOICE</pdfaProperty:description></rdf:li>
                            <rdf:li rdf:parseType="Resource"><pdfaProperty:name>Version</pdfaProperty:name><pdfaProperty:valueType>Text</pdfaProperty:valueType><pdfaProperty:category>external</pdfaProperty:category><pdfaProperty:description>The actual version of the ZUGFeRD XML schema</pdfaProperty:description></rdf:li>
                            <rdf:li rdf:parseType="Resource"><pdfaProperty:name>ConformanceLevel</pdfaProperty:name><pdfaProperty:valueType>Text</pdfaProperty:valueType><pdfaProperty:category>external</pdfaProperty:category><pdfaProperty:description>The selected ZUGFeRD profile completeness</pdfaProperty:description></rdf:li>
                        </rdf:Seq>
                    </pdfaSchema:property>
                </rdf:li>
            </rdf:Bag>
        </pdfaExtension:schemas>
        </rdf:Description>
        """;
}
