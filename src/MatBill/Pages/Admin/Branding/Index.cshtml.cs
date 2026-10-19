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
    }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    [BindProperty]
    public IFormFile? Logo { get; set; }

    public string? LogoUrl => Brand.LogoUrl(config.Current.Company);

    public DocumentViewModel Preview { get; private set; } = null!;

    public void OnGet()
    {
        var c = config.Current.Company;
        Input = new InputModel { AccentColor = c.AccentColor, Signature = c.Signature };
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
        });
        await log.LogAsync(L["Branding.Log.Changed"], "Company");
        Flash(L["Branding.Saved"]);
        return RedirectToPage();
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
        Preview = SampleDocument.Of(config.Current, LogoUrl);
    }
}
