namespace MatBill.Services;

/// <summary>One ready-made text of a document: the label the writer picks (a key of the interface) and the text that goes into the field (German, like the document).</summary>
/// <param name="Days">Setting a term: the payment term this text belongs to (0 = due at once), or null to leave the term as it is.</param>
public sealed record TextPreset(string Label, string Text, int? Days = null);

/// <summary>
/// The texts of an invoice or quote the editor offers to pick from (a text can always be written by hand): how it is paid, the introduction, the sign-off.
/// The first entry of each list is the standard of the invoice settings. "Due at once" is a text of its own, not a term of 0 days with the words of another text.
/// </summary>
public static class DocumentTextPresets
{
    public static IReadOnlyList<TextPreset> Terms(Configuration.InvoicingConfig invoicing) =>
    [
        new("Editor.Preset.Standard", invoicing.PaymentTerms),
        new("Editor.Preset.Terms.Days", "Bitte überweisen Sie den Rechnungsbetrag innerhalb von {Days} Tagen ohne Abzug auf das unten angegebene Konto."),
        new("Editor.Preset.Terms.DueDate", "Bitte überweisen Sie den Rechnungsbetrag bis zum {DueDate} auf das unten angegebene Konto."),
        new("Editor.Preset.Terms.Immediately", "Der Rechnungsbetrag ist sofort nach Erhalt der Rechnung ohne Abzug fällig. Bitte überweisen Sie ihn auf das unten angegebene Konto.", 0),
        new("Editor.Preset.Terms.Prepayment", "Bitte überweisen Sie den Rechnungsbetrag vor der Lieferung auf das unten angegebene Konto. Die Leistung erbringen wir nach Zahlungseingang.", 0),
        new("Editor.Preset.Terms.Discount", "Zahlbar innerhalb von {Days} Tagen ohne Abzug. Bei Zahlung innerhalb von 7 Tagen gewähren wir 2 % Skonto."),
        new("Editor.Preset.Terms.DirectDebit", "Der Rechnungsbetrag wird zum {DueDate} von Ihrem Konto eingezogen. Bitte sorgen Sie für ausreichende Deckung."),
        new("Editor.Preset.Terms.Paid", "Der Rechnungsbetrag wurde bereits beglichen. Vielen Dank!", 0),
    ];

    public static IReadOnlyList<TextPreset> QuoteTerms(Configuration.InvoicingConfig invoicing) =>
    [
        new("Editor.Preset.Standard", invoicing.QuoteTerms),
        new("Editor.Preset.Quote.Valid", "Dieses Angebot ist gültig bis zum {ValidUntil}."),
        new("Editor.Preset.Quote.Free", "Dieses Angebot ist freibleibend und gültig bis zum {ValidUntil}. Zwischenverkauf vorbehalten."),
        new("Editor.Preset.Quote.Deposit", "Dieses Angebot ist gültig bis zum {ValidUntil}. Mit der Beauftragung wird eine Anzahlung von 30 % des Auftragswertes fällig."),
    ];

    public static IReadOnlyList<TextPreset> Intro(Configuration.InvoicingConfig invoicing) =>
    [
        new("Editor.Preset.Standard", invoicing.IntroText),
        new("Editor.Preset.Intro.Thanks", "Hallo,\nvielen Dank für Ihren Auftrag. Wir berechnen Ihnen folgende Leistungen:"),
        new("Editor.Preset.Intro.Formal", "Sehr geehrte Damen und Herren,\nfür unsere Leistungen erlauben wir uns, Ihnen Folgendes in Rechnung zu stellen:"),
        new("Editor.Preset.Intro.Short", "Wir stellen Ihnen folgende Leistungen in Rechnung:"),
    ];

    public static IReadOnlyList<TextPreset> QuoteIntro(Configuration.InvoicingConfig invoicing) =>
    [
        new("Editor.Preset.Standard", invoicing.QuoteIntroText),
        new("Editor.Preset.Intro.QuoteThanks", "Hallo,\nvielen Dank für Ihre Anfrage. Gern bieten wir Ihnen Folgendes an:"),
        new("Editor.Preset.Intro.QuoteFormal", "Sehr geehrte Damen und Herren,\nvielen Dank für Ihr Interesse. Wir unterbreiten Ihnen folgendes Angebot:"),
    ];

    public static IReadOnlyList<TextPreset> Closing(Configuration.InvoicingConfig invoicing) =>
    [
        new("Editor.Preset.Standard", invoicing.ClosingText),
        new("Editor.Preset.Closing.Kind", "Viele Grüße"),
        new("Editor.Preset.Closing.Formal", "Mit freundlichen Grüßen"),
        new("Editor.Preset.Closing.Thanks", "Vielen Dank für Ihr Vertrauen und freundliche Grüße"),
    ];
}

/// <summary>A pick list for one text field of the editor (<c>_TextPresets</c>): the id of the field and its ready-made texts.</summary>
public sealed record TextPresetPicker(string Target, IReadOnlyList<TextPreset> Presets);
