using MatBill.Services;
using Microsoft.AspNetCore.Mvc;

namespace MatBill.Pages.Export;

/// <summary>The figures by month and the export for the tax office or the tax adviser: a ZIP with every invoice and receipt of a period, named by date and number, and the lists as CSV.</summary>
public sealed class IndexModel(ExportService export, ActivityLogger log, TimeProvider clock) : PageModelBase
{
    [BindProperty(SupportsGet = true)]
    public int? Year { get; set; }

    [BindProperty(SupportsGet = true)]
    public string Basis { get; set; } = "accrual";

    [BindProperty]
    public DateOnly? From { get; set; }

    [BindProperty]
    public DateOnly? To { get; set; }

    [BindProperty]
    public bool WithBank { get; set; } = true;

    public List<MonthFigures> Months { get; private set; } = [];

    public async Task OnGetAsync()
    {
        var today = Fmt.Today(clock);
        Year ??= today.Year;
        Head(L["Export.Title"], L["Export.Sub"], (L["Export.Crumb"], null));
        ViewData["Width"] = "wide";
        From ??= new DateOnly(Year.Value, 1, 1);
        To ??= new DateOnly(Year.Value, 12, 31);
        Months = await export.MonthsAsync(new DateOnly(Year.Value, 1, 1), new DateOnly(Year.Value, 12, 31), Basis == "cash", HttpContext.RequestAborted);
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var from = From ?? new DateOnly(Fmt.Today(clock).Year, 1, 1);
        var to = To ?? new DateOnly(from.Year, 12, 31);
        if (to < from)
        {
            FlashError(L["Export.EndBeforeStart"]);
            return RedirectToPage();
        }

        var result = await export.ExportAsync(from, to, WithBank, HttpContext.RequestAborted);
        await log.LogAsync(L["Export.Log.Created"], "Export", null, L["Export.Log.Summary", Fmt.Date(from), Fmt.Date(to), result.Invoices, result.Expenses]);
        return File(result.Zip, "application/zip", result.FileName);
    }
}
