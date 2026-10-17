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
        Head("Aktivität", "Was in MatBill getan wurde.", ("Einstellungen", null), ("Aktivität", null));
        var now = clock.GetUtcNow().UtcDateTime;

        var spec = new ListSpec<ActivityLog>
        {
            Id = "activity",
            Action = "/Admin/Activity",
            Label = "Aktivität",
            Noun = "Einträge",
            SearchPlaceholder = "Aktion, Person oder Inhalt ...",
            Search = (q, text) =>
            {
                var like = Like.Contains(text);
                return q.Where(a => EF.Functions.ILike(a.Action, like) || EF.Functions.ILike(a.UserName ?? "", like) || EF.Functions.ILike(a.Summary ?? "", like) || EF.Functions.ILike(a.EntityType ?? "", like));
            },
            Filters =
            [
                new FilterSpec<ActivityLog>("zeit", "Zeitraum", [new("heute", "Heute"), new("woche", "Letzte 7 Tage"), new("monat", "Letzte 30 Tage")],
                    (q, v) =>
                    {
                        var from = v switch { "heute" => now.AddDays(-1), "woche" => now.AddDays(-7), _ => now.AddDays(-30) };
                        return q.Where(a => a.OccurredDate >= from);
                    }),
                new FilterSpec<ActivityLog>("art", "Bereich", [new("login", "Anmeldung"), new("user", "Benutzer"), new("work", "Rechnungen, Serien, Kunden, Produkte"), new("share", "Freigaben"), new("backup", "Sicherung")],
                    (q, v) => v switch
                    {
                        "login" => q.Where(a => a.Action.StartsWith("Anmeldung") || a.Action.StartsWith("Abmeldung") || a.Action.Contains("Zwei-Faktor") || a.Action.Contains("Passwort")),
                        "user" => q.Where(a => a.EntityType == "User"),
                        "work" => q.Where(a => a.EntityType == "Invoice" || a.EntityType == "RecurringInvoice" || a.EntityType == "Customer" || a.EntityType == "Product"),
                        "share" => q.Where(a => a.EntityType == "ShareLink" || a.Action.StartsWith("Freigabe")),
                        _ => q.Where(a => a.EntityType != null && a.EntityType.StartsWith("Backup")),
                    }),
            ],
            Sorts = new Dictionary<string, (string, Func<IQueryable<ActivityLog>, bool, IOrderedQueryable<ActivityLog>>)>
            {
                ["date"] = ("Zeit", (q, d) => d ? q.OrderByDescending(a => a.OccurredDate).ThenByDescending(a => a.Id) : q.OrderBy(a => a.OccurredDate).ThenBy(a => a.Id)),
                ["user"] = ("Person", (q, d) => d ? q.OrderByDescending(a => a.UserName) : q.OrderBy(a => a.UserName)),
                ["action"] = ("Aktion", (q, d) => d ? q.OrderByDescending(a => a.Action) : q.OrderBy(a => a.Action)),
            },
            DefaultSort = "date",
            DefaultDescending = true,
            PageSize = 30,
        };

        var result = await ListBuilder.RunAsync(db.ActivityLog.AsNoTracking(), Request, spec, HttpContext.RequestAborted);
        Toolbar = result.Toolbar;
        Pager = result.Pager;
        Table = TableModel.Of("activity-table", "Aktivität", result.Items,
        [
            (new TableColumn("Wann"), a => Ui.Text(Fmt.Moment(a.OccurredDate))),
            (new TableColumn("Person"), a => Ui.Text(a.UserName ?? "System")),
            (new TableColumn("Aktion"), a => Ui.NameWithSub(a.Action, null, a.Summary)),
            (new TableColumn("Adresse", HideOnPhone: true, Mono: true), a => Ui.Text(a.IpAddress)),
        ],
        a => []);
        Table = Table with { EmptyTitle = Toolbar.Total == 0 ? "Noch nichts protokolliert" : "Nichts gefunden", EmptyText = Toolbar.Total == 0 ? "Sobald etwas geschieht, erscheint es hier." : "Ändere die Suche oder den Filter.", EmptyIcon = "activity" };
    }
}
