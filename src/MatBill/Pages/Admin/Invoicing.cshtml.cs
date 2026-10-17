using System.ComponentModel.DataAnnotations;
using MatBill.Configuration;
using MatBill.Core.Billing;
using MatBill.Services;
using Microsoft.AspNetCore.Mvc;

namespace MatBill.Pages.Admin;

/// <summary>The number ranges of invoices and customers and the texts a new invoice starts with.</summary>
public sealed class InvoicingModel(ConfigStore config, NumberService numbers, ActivityLogger log, TimeProvider clock) : PageModelBase
{
    public sealed class InputModel
    {
        [Required(ErrorMessage = "Bitte gib ein Muster ein.")]
        public string InvoiceNumberPattern { get; set; } = "";

        [Required(ErrorMessage = "Bitte gib ein Muster ein.")]
        public string CustomerNumberPattern { get; set; } = "";

        [Range(0, 365, ErrorMessage = "Zwischen 0 und 365 Tagen.")]
        public int PaymentTermDays { get; set; } = 14;

        public string DefaultTaxRate { get; set; } = "19";

        [StringLength(4000)]
        public string? IntroText { get; set; }

        [StringLength(2000)]
        public string? PaymentTerms { get; set; }

        [StringLength(2000)]
        public string? ClosingText { get; set; }

        [StringLength(500)]
        public string? SmallBusinessNote { get; set; }

        [Range(0, 30, ErrorMessage = "Zwischen 0 und 30 Tagen.")]
        public int NoticeDays { get; set; } = 3;
    }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public string InvoiceExample { get; private set; } = "";

    public string CustomerExample { get; private set; } = "";

    public string NextInvoice { get; private set; } = "";

    public async Task OnGetAsync()
    {
        var i = config.Current.Invoicing;
        Input = new InputModel
        {
            InvoiceNumberPattern = i.InvoiceNumberPattern, CustomerNumberPattern = i.CustomerNumberPattern, PaymentTermDays = i.PaymentTermDays,
            DefaultTaxRate = Amount.Quantity(i.DefaultTaxRate), IntroText = i.IntroText, PaymentTerms = i.PaymentTerms, ClosingText = i.ClosingText,
            SmallBusinessNote = i.SmallBusinessNote, NoticeDays = i.NoticeDays,
        };
        await PrepareAsync();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (NumberPattern.Problem(Input.InvoiceNumberPattern) is { } p1)
        {
            ModelState.AddModelError("Input.InvoiceNumberPattern", p1);
        }

        if (NumberPattern.Problem(Input.CustomerNumberPattern) is { } p2)
        {
            ModelState.AddModelError("Input.CustomerNumberPattern", p2);
        }

        if (!Amount.TryParse(Input.DefaultTaxRate, out var rate) || rate is < 0 or > 100)
        {
            ModelState.AddModelError("Input.DefaultTaxRate", "Der Steuersatz liegt zwischen 0 und 100 %.");
        }

        if (!ModelState.IsValid)
        {
            await PrepareAsync();
            return Page();
        }

        config.Update(c =>
        {
            c.Invoicing.InvoiceNumberPattern = Input.InvoiceNumberPattern.Trim();
            c.Invoicing.CustomerNumberPattern = Input.CustomerNumberPattern.Trim();
            c.Invoicing.PaymentTermDays = Input.PaymentTermDays;
            c.Invoicing.DefaultTaxRate = rate;
            c.Invoicing.IntroText = Input.IntroText?.Trim() ?? "";
            c.Invoicing.PaymentTerms = Input.PaymentTerms?.Trim() ?? "";
            c.Invoicing.ClosingText = Input.ClosingText?.Trim() ?? "";
            c.Invoicing.SmallBusinessNote = Input.SmallBusinessNote?.Trim() ?? "";
            c.Invoicing.NoticeDays = Input.NoticeDays;
        });
        await log.LogAsync("Einstellungen der Rechnungen geändert", "Invoicing");
        Flash("Die Einstellungen sind gespeichert. Sie gelten für neue Rechnungen.");
        return RedirectToPage();
    }

    private async Task PrepareAsync()
    {
        Head("Rechnungen", "Nummernkreise und die Texte, mit denen eine neue Rechnung beginnt.", ("Einstellungen", null), ("Rechnungen", null));
        ViewData["Width"] = "narrow";
        var today = Fmt.Today(clock);
        InvoiceExample = NumberPattern.Problem(Input.InvoiceNumberPattern) is null ? NumberPattern.Format(Input.InvoiceNumberPattern, today, 42) : "–";
        CustomerExample = NumberPattern.Problem(Input.CustomerNumberPattern) is null ? NumberPattern.Format(Input.CustomerNumberPattern, today, 7) : "–";
        NextInvoice = await numbers.PeekInvoiceNumberAsync(today);
    }
}
