using MatBill.Configuration;
using MatBill.Data;
using MatBill.Data.Entities;
using MatBill.Services;
using MatBill.Services.Backup;
using MatBill.UiKit;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MatBill.Pages.Admin.Backup;

/// <summary>
/// The backup at a glance: is it on, what ran last, what runs next, and a button to take a backup to download right now (for the case that there is no NAS yet,
/// or before an update).
/// </summary>
public sealed class IndexModel(MbDbContext db, ConfigStore config, BackupService backups, ActivityLogger log, TimeProvider clock) : PageModelBase
{
    public List<BackupPlan> Plans { get; private set; } = [];

    public TableModel Runs { get; private set; } = null!;

    public PagerModel Pager { get; private set; } = null!;

    public BackupRun? LastRun { get; private set; }

    public DateTime? NextRun => Plans.Where(p => p.IsEnabled && p.NextRunDate is not null).Min(p => p.NextRunDate);

    public bool Enabled => config.Current.Backup.Enabled;

    public bool Running => BackupService.IsBusy;

    public int TargetCount { get; private set; }

    public async Task OnGetAsync()
    {
        Head(L["Backup.Title"], L["Backup.Sub"], (L["Backup.Settings"], null), (L["Backup.Title"], null));
        Plans = await db.BackupPlan.AsNoTracking().Include(p => p.Target).OrderBy(p => p.Name).ToListAsync();
        TargetCount = await db.BackupTarget.CountAsync();
        LastRun = await db.BackupRun.AsNoTracking().OrderByDescending(r => r.StartDate).FirstOrDefaultAsync();

        var total = await db.BackupRun.CountAsync();
        var page = Math.Min(Math.Max(1, ListQuery.Page(Request)), Math.Max(1, (int)Math.Ceiling(total / 10.0)));
        var runs = await db.BackupRun.AsNoTracking().OrderByDescending(r => r.StartDate).Skip((page - 1) * 10).Take(10).ToListAsync();
        Pager = new PagerModel { Page = page, PageSize = 10, Total = total, Url = p => Url.Page("/Admin/Backup/Index", new { page = p })! };
        Runs = TableModel.Of("run-table", L["Backup.Recent"], runs,
        [
            (new TableColumn(L["Backup.When"]), r => Ui.Text(Fmt.Moment(r.StartDate))),
            (new TableColumn(L["Backup.Col.Plan"]), r => Ui.NameWithSub(r.PlanName ?? L["Backup.SingleBackup"].Value, null, (r.IsManual ? L["Backup.Manual"].Value : L["Backup.Automatic"].Value) + (r.TargetName is null ? "" : " → " + r.TargetName))),
            (new TableColumn(L["Backup.Size"], Numeric: true, HideOnPhone: true), r => Ui.Text(r.Status == BackupRunStatus.Success ? Fmt.FileSize(r.SizeBytes) : "")),
            (new TableColumn(L["Backup.Col.Duration"], Numeric: true, HideOnPhone: true), r => Ui.Text($"{Math.Max(1, (int)(r.EndDate - r.StartDate).TotalSeconds)} s")),
            (new TableColumn(L["Backup.Col.Result"]), r => Ui.NameWithSub(r.Status == BackupRunStatus.Success ? L["Backup.Succeeded"].Value : L["Backup.Failed"].Value, null, r.Message)),
        ],
        r => []);
        Runs = Runs with { EmptyTitle = L["Backup.Empty.Title"], EmptyText = L["Backup.Empty.Text"], EmptyIcon = "shield" };
    }

    public IActionResult OnPostToggle()
    {
        var now = !config.Current.Backup.Enabled;
        config.Update(c => c.Backup.Enabled = now);
        Flash(now ? L["Backup.Resumed"] : L["Backup.Paused"]);
        return RedirectToPage();
    }

    /// <summary>A backup to download, made now: the file is built in the temporary folder and removed when the download is done.</summary>
    public async Task<IActionResult> OnPostDownloadAsync(BackupScope scope, string? passphrase)
    {
        if (BackupService.IsBusy)
        {
            FlashError(L["Backup.Busy"]);
            return RedirectToPage();
        }

        try
        {
            var path = await backups.CreateFileAsync(scope, passphrase, HttpContext.RequestAborted);
            var name = BackupNames.Make("manual", clock.GetUtcNow().UtcDateTime, !string.IsNullOrEmpty(passphrase));
            await log.LogAsync(L["Backup.Log.Downloaded"], "Backup", null, scope.ToString());
            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.DeleteOnClose | FileOptions.Asynchronous);
            return File(stream, "application/octet-stream", name);
        }
        catch (Exception ex) when (ex is IOException or Npgsql.NpgsqlException or BackupException)
        {
            FlashError(L["Backup.Download.Failed", ex.Message]);
            return RedirectToPage();
        }
    }
}
