using System.ComponentModel.DataAnnotations;
using MatBill.Configuration;
using MatBill.Services;
using Microsoft.AspNetCore.Mvc;

namespace MatBill.Pages.Admin.Branding;

/// <summary>
/// The look of the invoices: the logo, the colour of the document and the greeting in handwriting. A preview of a sample invoice stands beside the form.
/// The company itself (sender, tax, bank) is on its own page. Issued invoices keep their look (their PDF is stored): a change here shows on what comes next.
/// </summary>
public sealed class IndexModel(ConfigStore config, BrandingService branding, ActivityLogger log) : PageModelBase
{
    public sealed class InputModel
    {
        [RegularExpression("^#[0-9a-fA-F]{6}$", ErrorMessage = "Branding.Accent.Invalid")]
        [Display(Name = "Branding.Accent")]
        public string AccentColor { get; set; } = "#1d4ed8";

        [StringLength(40)]
        [Display(Name = "Branding.Signature")]
        public string? Signature { get; set; }

        [Display(Name = "Branding.Layout")]
        public string Layout { get; set; } = DocumentLayouts.Classic;
    }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    [BindProperty]
    public IFormFile? Logo { get; set; }

    public string? LogoUrl => Brand.LogoUrl(config.Current.Company);

    /// <summary>Changes with every save, so the browser loads the preview again after one.</summary>
    public string PreviewVersion => System.Text.Json.JsonSerializer.Serialize(config.Current.Company).GetHashCode().ToString(System.Globalization.CultureInfo.InvariantCulture);

    public void OnGet()
    {
        var c = config.Current.Company;
        Input = new InputModel { AccentColor = c.AccentColor, Signature = c.Signature, Layout = DocumentLayouts.Of(c.Layout) };
        Prepare();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (Logo is { Length: > 0 })
        {
            var problem = await branding.SaveLogoAsync(Logo);
            if (problem is not null)
            {
                ModelState.AddModelError(nameof(Logo), problem);
            }
        }

        if (!ModelState.IsValid)
        {
            Prepare();
            return Page();
        }

        config.Update(c =>
        {
            c.Company.AccentColor = Input.AccentColor.ToLowerInvariant();
            c.Company.Signature = string.IsNullOrWhiteSpace(Input.Signature) ? null : Input.Signature.Trim();
            c.Company.Layout = DocumentLayouts.Of(Input.Layout);
        });
        await log.LogAsync(L["Branding.Log.Changed"], "Company");
        Flash(L["Branding.Saved"]);
        return RedirectToPage();
    }

    /// <summary>
    /// The first page of the sample invoice as the PDF draws it, as a PNG: the preview beside the forms of branding and company. An image scales as a whole,
    /// where the paper in HTML would set its text anew in a narrow column. Layout, colour and greeting can be tried before they are saved.
    /// </summary>
    public async Task<IActionResult> OnGetPreviewAsync(string? layout, string? accent, string? signature)
    {
        var copy = System.Text.Json.JsonSerializer.Deserialize<AppConfig>(System.Text.Json.JsonSerializer.Serialize(config.Current))!;
        if (layout is not null)
        {
            copy.Company.Layout = DocumentLayouts.Of(layout);
        }

        if (InvoicePdf.IsColour(accent))
        {
            copy.Company.AccentColor = accent!;
        }

        if (signature is not null)
        {
            copy.Company.Signature = string.IsNullOrWhiteSpace(signature) ? null : signature.Trim()[..Math.Min(40, signature.Trim().Length)];
        }

        var sample = SampleDocument.Of(copy, null);
        var png = InvoicePdf.RenderImages(sample.Doc, sample.Company, await branding.GetLogoAsync(HttpContext.RequestAborted), dpi: 144).First();
        Response.Headers.CacheControl = "private, no-store";
        return File(png, "image/png");
    }

    public async Task<IActionResult> OnPostRemoveLogoAsync()
    {
        branding.RemoveLogo();
        await log.LogAsync(L["Branding.Log.LogoRemoved"], "Company");
        Flash(L["Branding.LogoRemoved"]);
        return RedirectToPage();
    }

    private void Prepare()
    {
        Head(L["Branding.Title"], L["Branding.Sub"], (L["Admin.Settings"], null), (L["Branding.Title"], null));
        ViewData["Width"] = "wide";
    }
}
