using MatBill.Data;
using MatBill.Data.Entities;
using MatBill.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MatBill.Pages.Admin.Shares;

/// <summary>One link: its address, its limits and what it showed. It can be called back, and it can be deleted.</summary>
public sealed class EditModel(MbDbContext db, ShareService shares, TimeProvider clock, ActivityLogger log) : PageModelBase
{
    [BindProperty(SupportsGet = true)]
    public long Id { get; set; }

    public ShareLink Link { get; private set; } = null!;

    public string LinkUrl => shares.LinkOf(Link);

    public bool Active => Link.IsActive(clock.GetUtcNow().UtcDateTime);

    public async Task<IActionResult> OnGetAsync() => await LoadAsync() ? Page() : NotFound();

    public async Task<IActionResult> OnPostRevokeAsync()
    {
        var link = await db.ShareLink.FirstOrDefaultAsync(l => l.Id == Id);
        if (link is null)
        {
            return NotFound();
        }

        link.RevokedDate ??= clock.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync();
        await log.LogAsync("Freigabe-Link widerrufen", "ShareLink", link.Id, link.Title);
        Flash("Der Link ist widerrufen. Wer ihn hat, sieht nichts mehr.");
        return RedirectToPage(new { id = Id });
    }

    public async Task<IActionResult> OnPostDeleteAsync()
    {
        var link = await db.ShareLink.FirstOrDefaultAsync(l => l.Id == Id);
        if (link is not null)
        {
            db.ShareLink.Remove(link);
            await db.SaveChangesAsync();
            await log.LogAsync("Freigabe-Link gelöscht", "ShareLink", link.Id, link.Title);
            Flash("Der Link ist gelöscht.");
        }

        return RedirectToPage("/Admin/Shares/Index");
    }

    private async Task<bool> LoadAsync()
    {
        var link = await db.ShareLink.AsNoTracking().Include(l => l.Invoice).FirstOrDefaultAsync(l => l.Id == Id);
        if (link is null)
        {
            return false;
        }

        Link = link;
        Head(link.Title, null, ("Einstellungen", null), ("Freigaben", "/Admin/Shares"), (link.Title, null));
        ViewData["Width"] = "narrow";
        ViewData["ActiveHref"] = "/Admin/Shares";
        return true;
    }
}
