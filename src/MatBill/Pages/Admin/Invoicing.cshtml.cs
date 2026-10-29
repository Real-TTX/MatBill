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
        [Required(ErrorMessage = "Admin.Invoicing.Pattern.Required")]
        public string InvoiceNumberPattern { get; set; } = "";

        [Required(ErrorMessage = "Admin.Invoicing.Pattern.Required")]
        public string CustomerNumberPattern { get; set; } = "";

        public bool CustomerNumberManual { get; set; }

        [Range(0, 365, ErrorMessage = "Admin.Invoicing.Range0To365")]
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

        [Range(0, 30, ErrorMessage = "Admin.Invoicing.Range0To30")]
        public int NoticeDays { get; set; } = 3;

        [Required(ErrorMessage = "Admin.Invoicing.Pattern.Required")]
        public string QuoteNumberPattern { get; set; } = "";

        [Required(ErrorMessage = "Admin.Invoicing.Pattern.Required")]
        public string ExpenseNumberPattern { get; set; } = "";

        [Range(1, 365, ErrorMessage = "Admin.Invoicing.Range1To365")]
        public int QuoteValidityDays { get; set; } = 30;

        [StringLength(4000)]
        public string? QuoteIntroText { get; set; }

        [StringLength(2000)]
        public string? QuoteTerms { get; set; }

        public bool ZugferdPdf { get; set; } = true;

        /// <summary>One category per line.</summary>
        public string? Categories { get; set; }

        // The counter the next number of each range gets in the current period (a range can go on where another program stopped).
        [Range(1, 999_999_999, ErrorMessage = "Admin.Invoicing.Next.Range")]
        public long InvoiceNext { get; set; } = 1;

        [Range(1, 999_999_999, ErrorMessage = "Admin.Invoicing.Next.Range")]
        public long CustomerNext { get; set; } = 1;

        [Range(1, 999_999_999, ErrorMessage = "Admin.Invoicing.Next.Range")]
        public long QuoteNext { get; set; } = 1;

        [Range(1, 999_999_999, ErrorMessage = "Admin.Invoicing.Next.Range")]
        public long ExpenseNext { get; set; } = 1;
    }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public string NextInvoice { get; private set; } = "";

    /// <summary>The date the previews of the patterns are drawn for (the current period).</summary>
    public DateOnly Today => Fmt.Today(clock);

    /// <summary>What the next number of each range will be, by the name of its field.</summary>
    public Dictionary<string, string> NextNumbers { get; } = [];

    /// <summary>The ranges: the field of the pattern, the field of the next counter, the name of the range, the pattern.</summary>
    private (string PatternKey, string NextKey, string Range, string Pattern, long Next)[] Ranges =>
    [
        ("Input.InvoiceNumberPattern", "Input.InvoiceNext", NumberService.InvoiceRange, Input.InvoiceNumberPattern, Input.InvoiceNext),
        ("Input.CustomerNumberPattern", "Input.CustomerNext", NumberService.CustomerRange, Input.CustomerNumberPattern, Input.CustomerNext),
        ("Input.QuoteNumberPattern", "Input.QuoteNext", QuoteService.QuoteRange, Input.QuoteNumberPattern, Input.QuoteNext),
        ("Input.ExpenseNumberPattern", "Input.ExpenseNext", NumberService.ExpenseRange, Input.ExpenseNumberPattern, Input.ExpenseNext),
    ];

    public async Task OnGetAsync()
    {
        var i = config.Current.Invoicing;
        Input = new InputModel
        {
            InvoiceNumberPattern = i.InvoiceNumberPattern, CustomerNumberPattern = i.CustomerNumberPattern, CustomerNumberManual = i.CustomerNumberManual, PaymentTermDays = i.PaymentTermDays,
            DefaultTaxRate = Amount.Quantity(i.DefaultTaxRate), IntroText = i.IntroText, PaymentTerms = i.PaymentTerms, ClosingText = i.ClosingText,
            SmallBusinessNote = i.SmallBusinessNote, NoticeDays = i.NoticeDays, QuoteNumberPattern = i.QuoteNumberPattern, ExpenseNumberPattern = i.ExpenseNumberPattern,
            QuoteValidityDays = i.QuoteValidityDays, QuoteIntroText = i.QuoteIntroText, QuoteTerms = i.QuoteTerms, ZugferdPdf = i.ZugferdPdf, Categories = string.Join("\n", i.ExpenseCategories),
        };
        var today = Fmt.Today(clock);
        Input.InvoiceNext = await numbers.NextCounterAsync(NumberService.InvoiceRange, i.InvoiceNumberPattern, today);
        Input.CustomerNext = await numbers.NextCounterAsync(NumberService.CustomerRange, i.CustomerNumberPattern, today);
        Input.QuoteNext = await numbers.NextCounterAsync(QuoteService.QuoteRange, i.QuoteNumberPattern, today);
        Input.ExpenseNext = await numbers.NextCounterAsync(NumberService.ExpenseRange, i.ExpenseNumberPattern, today);
        await PrepareAsync();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var today = Fmt.Today(clock);
        foreach (var r in Ranges)
        {
            // Problem names the text (NumberPattern.*), not the text itself.
            if (NumberPattern.Problem(r.Pattern) is { } problem)
            {
                ModelState.AddModelError(r.PatternKey, L[problem]);
            }
            else if (ModelState.GetFieldValidationState(r.NextKey) != Microsoft.AspNetCore.Mvc.ModelBinding.ModelValidationState.Invalid
                && !await numbers.CanContinueAtAsync(r.Range, r.Pattern.Trim(), today, r.Next))
            {
                ModelState.AddModelError(r.NextKey, L["Admin.Invoicing.Next.Taken", await numbers.NextCounterAsync(r.Range, r.Pattern.Trim(), today)]);
            }
        }

        var categories = (Input.Categories ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase).Take(60).ToList();
        if (categories.Count == 0)
        {
            ModelState.AddModelError("Input.Categories", L["Admin.Invoicing.Categories.Required"]);
        }

        if (!Amount.TryParse(Input.DefaultTaxRate, out var rate) || rate is < 0 or > 100)
        {
            ModelState.AddModelError("Input.DefaultTaxRate", L["Admin.Invoicing.TaxRate.Range"]);
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
            c.Invoicing.CustomerNumberManual = Input.CustomerNumberManual;
            c.Invoicing.PaymentTermDays = Input.PaymentTermDays;
            c.Invoicing.DefaultTaxRate = rate;
            c.Invoicing.IntroText = Input.IntroText?.Trim() ?? "";
            c.Invoicing.PaymentTerms = Input.PaymentTerms?.Trim() ?? "";
            c.Invoicing.ClosingText = Input.ClosingText?.Trim() ?? "";
            c.Invoicing.SmallBusinessNote = Input.SmallBusinessNote?.Trim() ?? "";
            c.Invoicing.NoticeDays = Input.NoticeDays;
            c.Invoicing.QuoteNumberPattern = Input.QuoteNumberPattern.Trim();
            c.Invoicing.ExpenseNumberPattern = Input.ExpenseNumberPattern.Trim();
            c.Invoicing.QuoteValidityDays = Input.QuoteValidityDays;
            c.Invoicing.QuoteIntroText = Input.QuoteIntroText?.Trim() ?? "";
            c.Invoicing.QuoteTerms = Input.QuoteTerms?.Trim() ?? "";
            c.Invoicing.ZugferdPdf = Input.ZugferdPdf;
            c.Invoicing.ExpenseCategories = categories;
        });
        foreach (var r in Ranges)
        {
            await numbers.SetNextCounterAsync(r.Range, r.Pattern.Trim(), today, r.Next);
        }

        await log.LogAsync(L["Admin.Invoicing.Log.Changed"], "Invoicing");
        Flash(L["Admin.Invoicing.Saved"]);
        return RedirectToPage();
    }

    private async Task PrepareAsync()
    {
        Head(L["Admin.Invoicing.Title"], L["Admin.Invoicing.Sub"], (L["Admin.Settings"], null), (L["Admin.Invoicing.Title"], null));
        ViewData["Width"] = "narrow";
        var today = Fmt.Today(clock);
        NextInvoice = await numbers.PeekInvoiceNumberAsync(today);
        foreach (var r in Ranges)
        {
            NextNumbers[r.NextKey] = NumberPattern.Problem(r.Pattern) is null ? await numbers.FirstFreeAsync(r.Range, r.Pattern.Trim(), today, Math.Max(1, r.Next)) : "–";
        }
    }
}
