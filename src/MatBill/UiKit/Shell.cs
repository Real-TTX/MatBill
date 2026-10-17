using System.Net;
using MatBill.Data;
using MatBill.Data.Entities;
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

    public string Tagline { get; init; } = "Rechnungen und Finanzen";

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
            UserName = user.Name ?? "Gast",
            UserRole = user.IsAdmin ? "Administrator" : "Benutzer",
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
        var invoiceBadge = c.Overdue > 0 ? NavBadge.Count(c.Overdue, "warn", "überfällige Rechnungen")
            : c.SendProblems > 0 ? NavBadge.Count(c.SendProblems, "error", "Rechnungen, deren Versand fehlschlug") : null;
        var groups = new List<NavGroup>
        {
            new(null,
            [
                new("Neue Rechnung", "/Invoices/Edit", "plus", Primary: true),
                new("Übersicht", "/", "dashboard"),
            ]),
            new("Verkauf",
            [
                new("Angebote", "/Quotes", "file-document"),
                new("Rechnungen", "/Invoices", "file-text", invoiceBadge),
                new("Wiederkehrend", "/Recurring", "refresh", c.SeriesProblems > 0 ? NavBadge.Count(c.SeriesProblems, "error", "Serien mit Fehler") : null),
            ]),
            new("Finanzen",
            [
                new("Ausgaben", "/Expenses", "euro", c.ExpensesToReview > 0 ? NavBadge.Count(c.ExpensesToReview, "warn", "Belege zu prüfen") : null),
                new("Banking", "/Banking", "credit-card", c.OpenBookings > 0 ? NavBadge.Count(c.OpenBookings, "accent", "Umsätze ohne Zuordnung") : null),
                new("Auswertung", "/Export", "bar-chart"),
            ]),
            new("Stammdaten",
            [
                new("Kunden", "/Customers", "users"),
                new("Produkte", "/Products", "box"),
            ]),
        };
        if (user.IsAdmin)
        {
            groups[3] = groups[3] with { Items = [.. groups[3].Items, new("Branding", "/Branding", "palette")] };
        }

        return new NavSet("work", "Rechnungen", "file-text", groups);
    }

    private static NavSet AdminMenu() => new("admin", "Einstellungen", "settings",
    [
        new("Organisation",
        [
            new("Benutzer", "/Admin/Users", "user"),
            new("Freigaben", "/Admin/Shares", "share"),
            new("Rechnungen", "/Admin/Invoicing", "sliders"),
            new("E-Mail", "/Admin/Mail", "mail"),
            new("Banking", "/Admin/Banking", "credit-card"),
        ]),
        new("System",
        [
            new("Speicher (NAS)", "/Admin/Storage", "hard-drive"),
            new("Backup", null, "archive", Children:
            [
                new("Übersicht", "/Admin/Backup", null),
                new("Pläne", "/Admin/Backup/Plans", null),
                new("Ziele", "/Admin/Backup/Targets", null),
                new("Wiederherstellung", "/Admin/Backup/Restore", null),
            ]),
            new("Sicherheit", "/Admin/Security", "shield"),
        ]),
        new("Protokolle",
        [
            new("Aktivität", "/Admin/Activity", "activity"),
            new("System", "/Admin/System", "server"),
        ]),
    ]);

    private static List<UserMenuItem> UserMenu() =>
    [
        new("Mein Konto", "/Account/Profile", "user"),
        new("Sicherheit", "/Account/Security", "shield"),
        new("Benachrichtigungen", "/Notifications", "bell"),
        new("Abmelden", "/Account/Logout", "logout", Danger: true, SeparatorBefore: true, Post: true),
    ];
}
