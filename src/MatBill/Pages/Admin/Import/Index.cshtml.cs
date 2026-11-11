using MatBill.Services;
using Microsoft.AspNetCore.Mvc;

namespace MatBill.Pages.Admin.Import;

/// <summary>
/// Taking over the data of an earlier invoice program (<see cref="LegacyImportService"/>): the export as a ZIP is checked first (nothing is saved, the page shows
/// what would come, the sums per year and every problem), then imported. Administrators only (the folder /Admin); the rules are the service's.
/// </summary>
[RequestSizeLimit(LegacyImportService.MaxZipBytes)]
[RequestFormLimits(MultipartBodyLengthLimit = LegacyImportService.MaxZipBytes)]
public sealed class IndexModel(LegacyImportService import) : PageModelBase
{
    public sealed class InputModel
    {
        public bool ContinueNumbers { get; set; } = true;
    }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public LegacyImportResult? Result { get; private set; }

    public void OnGet() => Build();

    public Task<IActionResult> OnPostCheckAsync(IFormFile? file) => RunAsync(file, apply: false);

    public Task<IActionResult> OnPostImportAsync(IFormFile? file) => RunAsync(file, apply: true);

    private async Task<IActionResult> RunAsync(IFormFile? file, bool apply)
    {
        Build();
        if (file is null || file.Length == 0)
        {
            ModelState.AddModelError("file", L["Import.File.Missing"]);
            return Page();
        }

        await using var stream = file.OpenReadStream();
        Result = await import.RunAsync(stream, apply, Input.ContinueNumbers, HttpContext.RequestAborted);
        return Page();
    }

    private void Build()
    {
        Head(L["Import.Title"], L["Import.Sub"], (L["Admin.Settings"], null), (L["Import.Title"], null));
        ViewData["ActiveHref"] = "/Admin/Import";
    }
}
