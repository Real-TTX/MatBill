using MatBill.Data;
using MatBill.Data.Entities;
using MatBill.Services;
using MatBill.UiKit;
using Microsoft.EntityFrameworkCore;

namespace MatBill.Pages.Admin.Backup.Targets;

/// <summary>The places backups are written to: a folder in the volume or on the machine, or a share of a NAS.</summary>
public sealed class IndexModel(MbDbContext db) : PageModelBase
{
    public ToolbarModel Toolbar { get; private set; } = null!;

    public PagerModel Pager { get; private set; } = null!;

    public TableModel Table { get; private set; } = null!;

    public static string Where(BackupTarget t) => t.Kind == BackupTargetKind.Local
        ? (string.IsNullOrWhiteSpace(t.Path) ? "Volume: backups" : t.Path)
        : $"\\\\{t.Host}\\{t.Share}\\{t.Path}".TrimEnd('\\');

    public async Task OnGetAsync()
    {
        Head(L["Backup.Targets.Title"], L["Backup.Targets.Sub"], (L["Backup.Settings"], null), (L["Backup.Title"], "/Admin/Backup"), (L["Backup.Targets"], null));
        ViewData["ActiveHref"] = "/Admin/Backup/Targets";

        var spec = new ListSpec<BackupTarget>
        {
            Id = "targets",
            Action = "/Admin/Backup/Targets",
            Label = L["Backup.Targets"],
            Noun = L["Backup.Targets"],
            SearchPlaceholder = L["Backup.Targets.Search"],
            Search = (q, text) =>
            {
                var like = Like.Contains(text);
                return q.Where(t => EF.Functions.ILike(t.Name, like) || EF.Functions.ILike(t.Path, like) || EF.Functions.ILike(t.Host ?? "", like));
            },
            Sorts = new Dictionary<string, (string, Func<IQueryable<BackupTarget>, bool, IOrderedQueryable<BackupTarget>>)>
            {
                ["name"] = (L["Common.Name"], (q, d) => d ? q.OrderByDescending(t => t.Name) : q.OrderBy(t => t.Name)),
            },
            DefaultSort = "name",
        };

        var result = await ListBuilder.RunAsync(db.BackupTarget.AsNoTracking(), Request, spec, HttpContext.RequestAborted);
        Toolbar = result.Toolbar;
        Pager = result.Pager;
        Table = TableModel.Of("target-table", L["Backup.Targets.Title"], result.Items,
        [
            (new TableColumn(L["Common.Name"]), t => Ui.NameWithSub(t.Name, $"/Admin/Backup/Targets/Edit?id={t.Id}", t.Kind == BackupTargetKind.Local ? L["Backup.Target.Folder"].Value : L["Backup.Target.Nas"].Value)),
            (new TableColumn(L["Backup.Targets.Col.Location"], Mono: true, HideOnPhone: true), t => Ui.Text(Where(t))),
        ],
        t => [new RowAction("edit", L["Common.Edit"].Value, $"/Admin/Backup/Targets/Edit?id={t.Id}")]);
        Table = Table with
        {
            EmptyTitle = Toolbar.Total == 0 ? L["Backup.Targets.Empty.Title"] : L["Backup.NothingFound"],
            EmptyText = Toolbar.Total == 0 ? L["Backup.Targets.Empty.Text"] : L["Backup.ChangeSearch"],
            EmptyIcon = "hard-drive",
        };
    }
}
