using System.Net;
using MatBill.Data;
using MatBill.Data.Entities;
using MatBill.Localization;
using MatBill.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace MatBill.UiKit;

/// <summary>An entry of the user's context menu. A <see cref="Post"/> item changes something (sign out): a button of a small form, not a link.</summary>
public sealed record UserMenuItem(string Title, string Href, string Icon, bool Danger = false, bool SeparatorBefore = false, bool Post = false);

/// <summary>Everything the layout needs to draw the shell around a page: the menus, the user, the bell, the version.</summary>
public sealed class ShellModel
{
    public IReadOnlyList<NavSet> Menus { get; init; } = [];

    public bool HasSwitcher => Menus.Count > 1;

    public NavSet CurrentMenu(string? active) => Menus.FirstOrDefault(m => m.Contains(active)) ?? Menus[0];

    public string UserName { get; init; } = "";

    public string UserRole { get; init; } = "";

    public string UserLine { get; init; } = "";

    public IReadOnlyList<UserMenuItem> UserMenu { get; init; } = [];

    public string Version { get; init; } = "";

    public string Tagline { get; init; } = Text.T("Shell.Tagline");

    /// <summary>The unread notifications of the person (the badge of the bell); null draws no bell (a page without a person).</summary>
    public int? Unread { get; init; }

    /// <summary>"light" or "dark" when the user chose one; null follows the system.</summary>
    public string? Theme { get; init; }

    public string HomeHref { get; init; } = "/";

    /// <summary>The optional attributes of &lt;html&gt;, written only when they have a value (an empty data-theme would count as a choice).</summary>
    public string RootAttributes => Theme is "light" or "dark" ? $"data-theme=\"{WebUtility.HtmlEncode(Theme)}\"" : string.Empty;
}

/// <summary>Counts shown as badges in the menu. Cached for a minute: a badge may be a little late, a page must not wait for queries.</summary>
public sealed class NavCounts(IMemoryCache cache, MbDbContext db, TimeProvider clock)
{
    public sealed record Counts(int Overdue, int Drafts, int SeriesProblems, int SendProblems, int OpenBookings, int ExpensesToReview);

    public async Task<Counts> GetAsync()
        => (await cache.GetOrCreateAsync("nav-counts", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(60);
            var today = Fmt.Today(clock);
            return new Counts(
                await db.Invoice.CountAsync(i => i.Status == InvoiceStatus.Open && i.DueDate < today),
                await db.Invoice.CountAsync(i => i.Status == InvoiceStatus.Draft),
                await db.RecurringInvoice.CountAsync(s => s.Status == RecurringStatus.Active && s.LastError != null),
                await db.Invoice.CountAsync(i => i.Status != InvoiceStatus.Cancelled && i.SendState == SendState.Failed),
                await db.BankTransaction.CountAsync(t => t.State == MatchState.Open),
                await db.Expense.CountAsync(e => e.NeedsReview));
        }))!;

    public void Invalidate() => cache.Remove("nav-counts");
}

/// <summary>Builds the shell for the person who is signed in: the settings menu only for administrators.</summary>
public sealed class ShellFactory(ICurrentUser user, NavCounts counts, NotificationService notifications, IHttpContextAccessor accessor)
{
    public async Task<ShellModel> BuildAsync()
    {
        var principal = accessor.HttpContext?.User;
        var theme = principal?.FindFirst(AppClaims.Theme)?.Value;

        var menus = new List<NavSet> { await WorkMenuAsync() };
        if (user.IsAdmin)
        {
            menus.Add(AdminMenu());
        }

        return new ShellModel
        {
            Menus = menus,
            UserName = user.Name ?? Text.T("Shell.Guest"),
            UserRole = Labels.Of(user.IsAdmin ? UserRole.Admin : UserRole.User),
            UserLine = principal?.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value ?? "",
            UserMenu = UserMenu(),
            Version = $"MatBill {AppVersion.Current}",
            Unread = user.UserId is { } id ? await notifications.UnreadCountAsync(id) : null,
            Theme = theme,
        };
    }

    /// <summary>The shell of a page without a menu (sign-in, setup, a shared link).</summary>
    public static ShellModel Bare(string? theme = null) => new()
    {
        Menus = [new NavSet("none", "MatBill", "home", [])],
        Version = $"MatBill {AppVersion.Current}",
        Theme = theme,
    };

    private async Task<NavSet> WorkMenuAsync()
    {
        var c = await counts.GetAsync();
        var invoiceBadge = c.Overdue > 0 ? NavBadge.Count(c.Overdue, "warn", Text.T("Shell.Badge.Overdue"))
            : c.SendProblems > 0 ? NavBadge.Count(c.SendProblems, "error", Text.T("Shell.Badge.SendFailed")) : null;
        var groups = new List<NavGroup>
        {
            new(null,
            [
                new(Text.T("Shell.Nav.NewInvoice"), "/Invoices/Edit", "plus", Primary: true),
                new(Text.T("Shell.Nav.Overview"), "/", "dashboard"),
            ]),
            new(Text.T("Shell.Group.Sales"),
            [
                new(Text.T("Shell.Nav.Quotes"), "/Quotes", "file-document"),
                new(Text.T("Shell.Nav.Invoices"), "/Invoices", "file-text", invoiceBadge),
                new(Text.T("Shell.Nav.Recurring"), "/Recurring", "refresh", c.SeriesProblems > 0 ? NavBadge.Count(c.SeriesProblems, "error", Text.T("Shell.Badge.SeriesFailed")) : null),
            ]),
            new(Text.T("Shell.Group.Finance"),
            [
                new(Text.T("Shell.Nav.Expenses"), "/Expenses", "euro", c.ExpensesToReview > 0 ? NavBadge.Count(c.ExpensesToReview, "warn", Text.T("Shell.Badge.ReceiptsToReview")) : null),
                new(Text.T("Shell.Nav.Banking"), "/Banking", "credit-card", c.OpenBookings > 0 ? NavBadge.Count(c.OpenBookings, "accent", Text.T("Shell.Badge.Unmatched")) : null),
                new(Text.T("Shell.Nav.Reports"), "/Export", "bar-chart"),
            ]),
            new(Text.T("Shell.Group.MasterData"),
            [
                new(Text.T("Shell.Nav.Customers"), "/Customers", "users"),
                new(Text.T("Shell.Nav.Products"), "/Products", "box"),
            ]),
        };
        if (user.IsAdmin)
        {
            groups[3] = groups[3] with { Items = [.. groups[3].Items, new(Text.T("Shell.Nav.Branding"), "/Branding", "palette")] };
        }

        return new NavSet("work", Text.T("Shell.Menu.Work"), "file-text", groups);
    }

    private static NavSet AdminMenu() => new("admin", Text.T("Shell.Menu.Admin"), "settings",
    [
        new(Text.T("Shell.Group.Organisation"),
        [
            new(Text.T("Shell.Nav.Users"), "/Admin/Users", "user"),
            new(Text.T("Shell.Nav.Shares"), "/Admin/Shares", "share"),
            new(Text.T("Shell.Nav.Invoicing"), "/Admin/Invoicing", "sliders"),
            new(Text.T("Shell.Nav.Mail"), "/Admin/Mail", "mail"),
            new(Text.T("Shell.Nav.Banking"), "/Admin/Banking", "credit-card"),
        ]),
        new(Text.T("Shell.Group.System"),
        [
            new(Text.T("Shell.Nav.Storage"), "/Admin/Storage", "hard-drive"),
            new(Text.T("Shell.Nav.Backup"), null, "archive", Children:
            [
                new(Text.T("Shell.Nav.BackupOverview"), "/Admin/Backup", null),
                new(Text.T("Shell.Nav.BackupPlans"), "/Admin/Backup/Plans", null),
                new(Text.T("Shell.Nav.BackupTargets"), "/Admin/Backup/Targets", null),
                new(Text.T("Shell.Nav.BackupRestore"), "/Admin/Backup/Restore", null),
            ], Id: "backup"),
            new(Text.T("Shell.Nav.Security"), "/Admin/Security", "shield"),
        ]),
        new(Text.T("Shell.Group.Logs"),
        [
            new(Text.T("Shell.Nav.Activity"), "/Admin/Activity", "activity"),
            new(Text.T("Shell.Nav.System"), "/Admin/System", "server"),
        ]),
    ]);

    private static List<UserMenuItem> UserMenu() =>
    [
        new(Text.T("Shell.User.Account"), "/Account/Profile", "user"),
        new(Text.T("Shell.User.Security"), "/Account/Security", "shield"),
        new(Text.T("Shell.User.Notifications"), "/Notifications", "bell"),
        new(Text.T("Shell.User.Logout"), "/Account/Logout", "logout", Danger: true, SeparatorBefore: true, Post: true),
    ];
}
