using MatBill.Configuration;
using MatBill.Data.Entities;
using MatBill.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MatBill.Pages.Share;

/// <summary>
/// The page behind a share link (the role "Anonym"): one invoice, read only, with its PDF. It says in words why it cannot show it (a link that has run out is no secret).
/// </summary>
[AllowAnonymous]
public sealed class IndexModel(ShareService shares, ConfigStore config, TimeProvider clock) : PageModelBase
{
    [BindProperty(SupportsGet = true)]
    public Guid Token { get; set; }

    [BindProperty]
    public string? Password { get; set; }

    public ShareLink Link { get; private set; } = null!;

    public ShareState State { get; private set; } = ShareState.Unknown;

    public bool NeedsPassword { get; private set; }

    public bool Locked { get; private set; }

    public bool CanShow => State == ShareState.Active && !NeedsPassword;

    public string Company => shares.CompanyName;

    public string? LogoUrl => Brand.LogoUrl(config.Current.Company);

    public DocumentViewModel? View { get; private set; }

    /// <summary>How long the link still works, as a sentence in the language of the visitor.</summary>
    public string RemainingText
    {
        get
        {
            var span = Link.ExpiresDate - clock.GetUtcNow().UtcDateTime;
            return span.TotalDays >= 2 ? L["Share.Valid.Days", (int)span.TotalDays]
                : span.TotalHours >= 1 ? L["Share.Valid.Hours", (int)span.TotalHours]
                : L["Share.Valid.Soon"];
        }
    }

    public async Task<IActionResult> OnGetAsync()
    {
        if (await InitAsync())
        {
            await shares.CountViewAsync(Link, HttpContext.RequestAborted);
            View = new DocumentViewModel(InvoiceDocument.Of(Link.Invoice!, config.Current.Invoicing), config.Current.Company, LogoUrl);
        }

        return Page();
    }

    public async Task<IActionResult> OnPostPasswordAsync()
    {
        await InitAsync();
        if (State != ShareState.Active)
        {
            return Page();
        }

        if (!NeedsPassword || shares.TryPassword(Link, Password, HttpContext))
        {
            return RedirectToPage(new { token = Token });
        }

        Locked = shares.IsLocked(Link);
        ModelState.AddModelError(nameof(Password), Locked ? L["Share.Password.Locked"] : L["Share.Password.Wrong"]);
        return Page();
    }

    private async Task<bool> InitAsync()
    {
        ViewData["Wide"] = true;
        ViewData["Title"] = L["Share.Invoice"].Value;
        var resolution = await shares.ResolveAsync(Token, HttpContext.RequestAborted);
        State = resolution.State;
        if (resolution.Link is null)
        {
            Response.StatusCode = StatusCodes.Status404NotFound;
            return false;
        }

        Link = resolution.Link;
        if (State != ShareState.Active)
        {
            Response.StatusCode = StatusCodes.Status410Gone;
            return false;
        }

        NeedsPassword = shares.NeedsPassword(Link, HttpContext);
        Locked = NeedsPassword && shares.IsLocked(Link);
        ViewData["Title"] = NeedsPassword ? L["Share.Invoice"].Value : Link.Title;
        return !NeedsPassword;
    }
}
