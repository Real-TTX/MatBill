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
        Head("Sicherungsziele", "Wohin die Sicherungen geschrieben werden.", ("Einstellungen", null), ("Sicherung", "/Admin/Backup"), ("Ziele", null));
        ViewData["ActiveHref"] = "/Admin/Backup/Targets";

        var spec = new ListSpec<BackupTarget>
        {
            Id = "targets",
            Action = "/Admin/Backup/Targets",
            Label = "Ziele",
            Noun = "Ziele",
            SearchPlaceholder = "Name oder Ort ...",
            Search = (q, text) =>
            {
                var like = Like.Contains(text);
                return q.Where(t => EF.Functions.ILike(t.Name, like) || EF.Functions.ILike(t.Path, like) || EF.Functions.ILike(t.Host ?? "", like));
            },
            Sorts = new Dictionary<string, (string, Func<IQueryable<BackupTarget>, bool, IOrderedQueryable<BackupTarget>>)>
            {
                ["name"] = ("Name", (q, d) => d ? q.OrderByDescending(t => t.Name) : q.OrderBy(t => t.Name)),
            },
            DefaultSort = "name",
        };

        var result = await ListBuilder.RunAsync(db.BackupTarget.AsNoTracking(), Request, spec, HttpContext.RequestAborted);
        Toolbar = result.Toolbar;
        Pager = result.Pager;
        Table = TableModel.Of("target-table", "Sicherungsziele", result.Items,
        [
            (new TableColumn("Name"), t => Ui.NameWithSub(t.Name, $"/Admin/Backup/Targets/Edit?id={t.Id}", t.Kind == BackupTargetKind.Local ? "Ordner" : "NAS (SMB)")),
            (new TableColumn("Ort", Mono: true, HideOnPhone: true), t => Ui.Text(Where(t))),
        ],
        t => [new RowAction("edit", "Bearbeiten", $"/Admin/Backup/Targets/Edit?id={t.Id}")]);
        Table = Table with { EmptyTitle = Toolbar.Total == 0 ? "Noch kein Ziel" : "Nichts gefunden", EmptyText = Toolbar.Total == 0 ? "Lege einen Ordner oder ein NAS an, auf dem Sicherungen liegen sollen." : "Ändere die Suche.", EmptyIcon = "hard-drive" };
    }
}
