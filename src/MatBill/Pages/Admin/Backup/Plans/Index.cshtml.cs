using MatBill.Data;
using MatBill.Data.Entities;
using MatBill.Localization;
using MatBill.Services;
using MatBill.Services.Backup;
using MatBill.UiKit;
using Microsoft.EntityFrameworkCore;

namespace MatBill.Pages.Admin.Backup.Plans;

/// <summary>The backup plans: what is saved, where to, how often, how long the old ones are kept.</summary>
public sealed class IndexModel(MbDbContext db) : PageModelBase
{
    public ToolbarModel Toolbar { get; private set; } = null!;

    public PagerModel Pager { get; private set; } = null!;

    public TableModel Table { get; private set; } = null!;

    public static string Of(BackupScope s) => s == BackupScope.SystemOnly ? Text.T("Backup.Scope.SystemOnly") : Text.T("Backup.Scope.SystemAndFiles");

    public async Task OnGetAsync()
    {
        Head(L["Backup.Plans.Title"], L["Backup.Plans.Sub"], (L["Backup.Settings"], null), (L["Backup.Title"], "/Admin/Backup"), (L["Backup.Plans"], null));
        ViewData["ActiveHref"] = "/Admin/Backup/Plans";

        var spec = new ListSpec<BackupPlan>
        {
            Id = "plans",
            Action = "/Admin/Backup/Plans",
            Label = L["Backup.Plans"],
            Noun = L["Backup.Plans"],
            SearchPlaceholder = L["Backup.Plans.Search"],
            Search = (q, text) => q.Where(p => EF.Functions.ILike(p.Name, Like.Contains(text))),
            Filters =
            [
                new FilterSpec<BackupPlan>("scope", L["Backup.Scope"], ListBuilder.EnumOptions<BackupScope>(Of), (q, v) => q.Where(p => p.Scope == Enum.Parse<BackupScope>(v))),
                new FilterSpec<BackupPlan>("status", L["Common.Status"], [new("active", L["Backup.Plans.Status.Active"]), new("off", L["Backup.Plans.Status.Off"])], (q, v) => v == "off" ? q.Where(p => !p.IsEnabled) : q.Where(p => p.IsEnabled)),
            ],
            Sorts = new Dictionary<string, (string, Func<IQueryable<BackupPlan>, bool, IOrderedQueryable<BackupPlan>>)>
            {
                ["name"] = (L["Common.Name"], (q, d) => d ? q.OrderByDescending(p => p.Name) : q.OrderBy(p => p.Name)),
                ["next"] = (L["Backup.Plans.Sort.Next"], (q, d) => d ? q.OrderByDescending(p => p.NextRunDate) : q.OrderBy(p => p.NextRunDate)),
            },
            DefaultSort = "name",
        };

        var result = await ListBuilder.RunAsync(db.BackupPlan.AsNoTracking().Include(p => p.Target), Request, spec, HttpContext.RequestAborted);
        Toolbar = result.Toolbar;
        Pager = result.Pager;
        Table = TableModel.Of("plan-table", L["Backup.Plans.Title"], result.Items,
        [
            (new TableColumn(L["Common.Name"]), p => Ui.NameWithSub(p.Name, $"/Admin/Backup/Plans/Edit?id={p.Id}", Of(p.Scope) + (string.IsNullOrEmpty(p.PassphraseProtected) ? "" : " · " + L["Backup.Encrypted"].Value))),
            (new TableColumn(L["Backup.Target"], HideOnPhone: true), p => Ui.Text(p.Target?.Name)),
            (new TableColumn(L["Backup.Plans.Col.Schedule"], HideOnPhone: true), p => Ui.Text(BackupSchedule.Describe(p))),
            (new TableColumn(L["Backup.Plans.Col.Next"], HideOnPhone: true), p => Ui.Text(p.IsEnabled ? Fmt.Moment(p.NextRunDate) : "")),
            (new TableColumn(L["Backup.Plans.Col.Last"]), p => p.LastRunDate is null ? Ui.Text(L["Backup.Never"].Value) : BackupService.IsFailedStatus(p.LastStatus) ? Ui.Pill(L["Backup.Failed"].Value, "error") : Ui.Pill(L["Backup.Ok"].Value, "ok")),
            (new TableColumn(L["Common.Status"]), p => p.IsEnabled ? Ui.Pill(L["Backup.Active"].Value, "ok") : Ui.Pill(L["Backup.Off"].Value, "plain")),
        ],
        p => [new RowAction("edit", L["Common.Edit"].Value, $"/Admin/Backup/Plans/Edit?id={p.Id}")]);
        Table = Table with
        {
            EmptyTitle = Toolbar.Total == 0 ? L["Backup.Plans.Empty.Title"] : L["Backup.NothingFound"],
            EmptyText = Toolbar.Total == 0 ? L["Backup.Plans.Empty.Text"] : L["Backup.ChangeSearchOrFilter"],
            EmptyIcon = "calendar",
        };
    }
}
