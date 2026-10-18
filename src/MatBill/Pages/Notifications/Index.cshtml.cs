using MatBill.Data;
using MatBill.Data.Entities;
using MatBill.Services;
using MatBill.UiKit;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MatBill.Pages.Notifications;

/// <summary>The notes of the person: upcoming invoices of series, invoices that were made or sent, problems. Opening a note marks it as read.</summary>
public sealed class IndexModel(MbDbContext db, NotificationService notifications, ICurrentUser current, TimeProvider clock) : PageModelBase
{
    public List<Notification> Items { get; private set; } = [];

    public PagerModel Pager { get; private set; } = null!;

    public bool UnreadOnly { get; private set; }

    public int Unread { get; private set; }

    public DateTime Now => clock.GetUtcNow().UtcDateTime;

    public async Task OnGetAsync(bool unread = false)
    {
        var title = L["Notifications.Title"].Value;
        Head(title, L["Notifications.Sub"], (title, null));
        ViewData["Width"] = "narrow";
        var me = current.UserId ?? 0;
        UnreadOnly = unread;
        var query = db.Notification.AsNoTracking().Where(n => n.UserId == me);
        Unread = await query.CountAsync(n => n.ReadDate == null);
        if (unread)
        {
            query = query.Where(n => n.ReadDate == null);
        }

        var total = await query.CountAsync();
        var page = ListQuery.Page(Request);
        const int size = 30;
        Items = await query.OrderByDescending(n => n.OccurredDate).ThenByDescending(n => n.Id).Skip((page - 1) * size).Take(size).ToListAsync();
        Pager = new PagerModel { Page = page, PageSize = size, Total = total, Url = p => $"/Notifications?{(unread ? "unread=true&" : "")}page={p}" };
    }

    /// <summary>Opens what a note is about and marks it as read.</summary>
    public async Task<IActionResult> OnPostOpenAsync(long id)
    {
        var me = current.UserId ?? 0;
        var note = await db.Notification.AsNoTracking().FirstOrDefaultAsync(n => n.Id == id && n.UserId == me);
        await notifications.MarkReadAsync(me, id);
        return LocalRedirect(note?.Url is { } url && Url.IsLocalUrl(url) ? url : "/Notifications");
    }

    public async Task<IActionResult> OnPostReadAllAsync()
    {
        await notifications.MarkReadAsync(current.UserId ?? 0);
        Flash(L["Notifications.AllRead"]);
        return RedirectToPage();
    }
}
