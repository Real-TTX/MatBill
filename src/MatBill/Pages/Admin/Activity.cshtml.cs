using MatBill.Data;
using MatBill.Data.Entities;
using MatBill.Services;
using MatBill.UiKit;
using Microsoft.EntityFrameworkCore;

namespace MatBill.Pages.Admin;

/// <summary>What was done here and by whom: sign-ins, changes of settings, invoices issued, exports. For the administrator, and for the question "who did that?".</summary>
public sealed class ActivityModel(MbDbContext db, TimeProvider clock) : PageModelBase
{
    public ToolbarModel Toolbar { get; private set; } = null!;

    public PagerModel Pager { get; private set; } = null!;

    public TableModel Table { get; private set; } = null!;

    public async Task OnGetAsync()
    {
        Head(L["Admin.Activity.Title"], L["Admin.Activity.Sub"], (L["Admin.Settings"], null), (L["Admin.Activity.Title"], null));
        var now = clock.GetUtcNow().UtcDateTime;

        var spec = new ListSpec<ActivityLog>
        {
            Id = "activity",
            Action = "/Admin/Activity",
            Label = L["Admin.Activity.Title"],
            Noun = L["Admin.Activity.Noun"],
            SearchPlaceholder = L["Admin.Activity.Search"],
            Search = (q, text) =>
            {
                var like = Like.Contains(text);
                return q.Where(a => EF.Functions.ILike(a.Action, like) || EF.Functions.ILike(a.UserName ?? "", like) || EF.Functions.ILike(a.Summary ?? "", like) || EF.Functions.ILike(a.EntityType ?? "", like));
            },
            Filters =
            [
                new FilterSpec<ActivityLog>("period", L["Admin.Activity.Period"], [new("today", L["Admin.Activity.Period.Today"]), new("week", L["Admin.Activity.Period.Week"]), new("month", L["Admin.Activity.Period.Month"])],
                    (q, v) =>
                    {
                        var from = v switch { "today" => now.AddDays(-1), "week" => now.AddDays(-7), _ => now.AddDays(-30) };
                        return q.Where(a => a.OccurredDate >= from);
                    }),
                new FilterSpec<ActivityLog>("area", L["Admin.Activity.Area"], [new("login", L["Admin.Activity.Area.Login"]), new("user", L["Admin.Activity.Area.User"]), new("work", L["Admin.Activity.Area.Work"]), new("share", L["Admin.Activity.Area.Share"]), new("backup", L["Admin.Activity.Area.Backup"])],
                    (q, v) => v switch
                    {
                        // The entries are stored in the language of whoever acted: sign-ins are matched in German and in English.
                        "login" => q.Where(a => EF.Functions.ILike(a.Action, "Anmeldung%") || EF.Functions.ILike(a.Action, "Abmeldung%") || EF.Functions.ILike(a.Action, "%Faktor%")
                            || EF.Functions.ILike(a.Action, "%Passwort%") || EF.Functions.ILike(a.Action, "Sign%") || EF.Functions.ILike(a.Action, "%factor%")
                            || EF.Functions.ILike(a.Action, "%password%")),
                        "user" => q.Where(a => a.EntityType == "User"),
                        "work" => q.Where(a => a.EntityType == "Invoice" || a.EntityType == "RecurringInvoice" || a.EntityType == "Customer" || a.EntityType == "Product"),
                        "share" => q.Where(a => a.EntityType == "ShareLink" || a.Action.StartsWith("Freigabe") || a.Action.StartsWith("Share")),
                        _ => q.Where(a => a.EntityType != null && a.EntityType.StartsWith("Backup")),
                    }),
            ],
            Sorts = new Dictionary<string, (string, Func<IQueryable<ActivityLog>, bool, IOrderedQueryable<ActivityLog>>)>
            {
                ["date"] = (L["Admin.Activity.Time"], (q, d) => d ? q.OrderByDescending(a => a.OccurredDate).ThenByDescending(a => a.Id) : q.OrderBy(a => a.OccurredDate).ThenBy(a => a.Id)),
                ["user"] = (L["Admin.Activity.Person"], (q, d) => d ? q.OrderByDescending(a => a.UserName) : q.OrderBy(a => a.UserName)),
                ["action"] = (L["Admin.Activity.Action"], (q, d) => d ? q.OrderByDescending(a => a.Action) : q.OrderBy(a => a.Action)),
            },
            DefaultSort = "date",
            DefaultDescending = true,
            PageSize = 30,
        };

        var result = await ListBuilder.RunAsync(db.ActivityLog.AsNoTracking(), Request, spec, HttpContext.RequestAborted);
        Toolbar = result.Toolbar;
        Pager = result.Pager;
        var system = L["Admin.Activity.System"].Value;
        Table = TableModel.Of("activity-table", L["Admin.Activity.Title"], result.Items,
        [
            (new TableColumn(L["Admin.Activity.When"]), a => Ui.Text(Fmt.Moment(a.OccurredDate))),
            (new TableColumn(L["Admin.Activity.Person"]), a => Ui.Text(a.UserName ?? system)),
            (new TableColumn(L["Admin.Activity.Action"]), a => Ui.NameWithSub(a.Action, null, a.Summary)),
            (new TableColumn(L["Admin.Activity.Address"], HideOnPhone: true, Mono: true), a => Ui.Text(a.IpAddress)),
        ],
        a => []);
        Table = Table with
        {
            EmptyTitle = Toolbar.Total == 0 ? L["Admin.Activity.Empty.Title"] : L["Admin.Empty.Title"],
            EmptyText = Toolbar.Total == 0 ? L["Admin.Activity.Empty.Text"] : L["Admin.Empty.Text"],
            EmptyIcon = "activity",
        };
    }
}
