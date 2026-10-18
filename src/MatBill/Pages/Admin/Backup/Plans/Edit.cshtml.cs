using System.ComponentModel.DataAnnotations;
using MatBill.Configuration;
using MatBill.Data;
using MatBill.Data.Entities;
using MatBill.Services;
using MatBill.Services.Backup;
using MatBill.UiKit;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;

namespace MatBill.Pages.Admin.Backup.Plans;

/// <summary>One plan. The passphrase (optional) encrypts every backup of the plan; it is kept protected so that the plan can run by itself.</summary>
public sealed class EditModel(MbDbContext db, SecretProtector secrets, ActivityLogger log, TimeProvider clock, IServiceScopeFactory scopes) : PageModelBase
{
    public sealed class InputModel
    {
        [Required(ErrorMessage = "Backup.Plan.Name.Required")]
        [Display(Name = "Common.Name")]
        public string Name { get; set; } = "";

        [Display(Name = "Backup.Target")]
        public long? TargetId { get; set; }

        [Display(Name = "Backup.Scope")]
        public BackupScope Scope { get; set; } = BackupScope.SystemOnly;

        [Display(Name = "Backup.Plan.HowOften")]
        public ScheduleKind Schedule { get; set; } = ScheduleKind.Daily;

        [Range(1, 168, ErrorMessage = "Backup.Plan.Interval.Range")]
        [Display(Name = "Backup.Plan.Interval")]
        public int IntervalHours { get; set; } = 6;

        [Display(Name = "Backup.Plan.Time")]
        public string TimeOfDay { get; set; } = "02:30";

        [Display(Name = "Backup.Plan.Weekday")]
        public DayOfWeek DayOfWeek { get; set; } = DayOfWeek.Sunday;

        [Range(1, 31, ErrorMessage = "Backup.Plan.DayOfMonth.Range")]
        [Display(Name = "Backup.Plan.DayOfMonth")]
        public int DayOfMonth { get; set; } = 1;

        [Range(0, 1000, ErrorMessage = "Backup.Plan.Keep.Range")]
        [Display(Name = "Backup.Plan.KeepLast")]
        public int KeepLast { get; set; } = 7;

        [Range(0, 1000, ErrorMessage = "Backup.Plan.Keep.Range")]
        [Display(Name = "Backup.Plan.KeepDaily")]
        public int KeepDaily { get; set; }

        [Range(0, 1000, ErrorMessage = "Backup.Plan.Keep.Range")]
        [Display(Name = "Backup.Plan.KeepWeekly")]
        public int KeepWeekly { get; set; }

        [Range(0, 1000, ErrorMessage = "Backup.Plan.Keep.Range")]
        [Display(Name = "Backup.Plan.KeepMonthly")]
        public int KeepMonthly { get; set; }

        [Display(Name = "Backup.Plan.Passphrase")]
        public string? Passphrase { get; set; }

        public bool RemovePassphrase { get; set; }

        public bool IsEnabled { get; set; } = true;
    }

    [BindProperty(SupportsGet = true)]
    public long? Id { get; set; }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public BackupPlan? Plan { get; private set; }

    public List<SelectListItem> Targets { get; private set; } = [];

    public bool Encrypted => !string.IsNullOrEmpty(Plan?.PassphraseProtected);

    public async Task<IActionResult> OnGetAsync()
    {
        if (!await PrepareAsync())
        {
            return NotFound();
        }

        if (Plan is { } p)
        {
            Input = new InputModel
            {
                Name = p.Name, TargetId = p.TargetId, Scope = p.Scope, Schedule = p.Schedule, IntervalHours = p.IntervalHours, TimeOfDay = p.TimeOfDay.ToString("HH\\:mm"), DayOfWeek = p.DayOfWeek,
                DayOfMonth = p.DayOfMonth, KeepLast = p.KeepLast, KeepDaily = p.KeepDaily, KeepWeekly = p.KeepWeekly, KeepMonthly = p.KeepMonthly, IsEnabled = p.IsEnabled,
            };
        }
        else
        {
            Input.TargetId = Targets.Count > 0 ? long.Parse(Targets[0].Value) : null;
        }

        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!await PrepareAsync())
        {
            return NotFound();
        }

        if (Input.TargetId is null || !Targets.Any(t => t.Value == Input.TargetId.ToString()))
        {
            ModelState.AddModelError("Input.TargetId", L["Backup.Plan.Target.Required"]);
        }

        if (!TimeOnly.TryParseExact(Input.TimeOfDay, "HH:mm", out var time) && !TimeOnly.TryParse(Input.TimeOfDay, out time))
        {
            ModelState.AddModelError("Input.TimeOfDay", L["Backup.Plan.Time.Invalid"]);
        }

        if (!ModelState.IsValid)
        {
            return Page();
        }

        var plan = Plan is { } existing ? await db.BackupPlan.FirstAsync(p => p.Id == existing.Id) : db.BackupPlan.Add(new BackupPlan()).Entity;
        plan.Name = Input.Name.Trim();
        plan.TargetId = Input.TargetId!.Value;
        plan.Scope = Input.Scope;
        plan.Schedule = Input.Schedule;
        plan.IntervalHours = Input.IntervalHours;
        plan.TimeOfDay = time;
        plan.DayOfWeek = Input.DayOfWeek;
        plan.DayOfMonth = Input.DayOfMonth;
        plan.KeepLast = Input.KeepLast;
        plan.KeepDaily = Input.KeepDaily;
        plan.KeepWeekly = Input.KeepWeekly;
        plan.KeepMonthly = Input.KeepMonthly;
        plan.IsEnabled = Input.IsEnabled;
        if (!string.IsNullOrEmpty(Input.Passphrase))
        {
            plan.PassphraseProtected = secrets.Protect(Input.Passphrase);
        }
        else if (Input.RemovePassphrase)
        {
            plan.PassphraseProtected = null;
        }

        plan.NextRunDate = BackupSchedule.Next(plan, clock.GetUtcNow().UtcDateTime, Fmt.Zone);
        await db.SaveChangesAsync();
        await log.LogAsync(L["Backup.Plan.Log.Saved"], "BackupPlan", plan.Id, plan.Name);
        Flash(L["Backup.Plan.Saved"]);
        return RedirectToPage("/Admin/Backup/Plans/Index");
    }

    /// <summary>Starts the plan now, in the background (a backup of many files takes a while); the overview shows when it is done.</summary>
    public async Task<IActionResult> OnPostRunAsync()
    {
        var plan = await db.BackupPlan.AsNoTracking().FirstOrDefaultAsync(p => p.Id == Id);
        if (plan is null)
        {
            return NotFound();
        }

        if (BackupService.IsBusy)
        {
            FlashError(L["Backup.Busy"]);
            return RedirectToPage("/Admin/Backup/Index");
        }

        var planId = plan.Id;
        _ = Task.Run(async () =>
        {
            using var scope = scopes.CreateScope();
            var scoped = scope.ServiceProvider;
            var p = await scoped.GetRequiredService<MbDbContext>().BackupPlan.Include(x => x.Target).FirstAsync(x => x.Id == planId);
            try
            {
                await scoped.GetRequiredService<BackupService>().RunPlanAsync(p, manual: true);
            }
            catch (BackupException)
            {
                // somebody else started a backup in the meantime
            }
        });
        Flash(L["Backup.Plan.Started"]);
        return RedirectToPage("/Admin/Backup/Index");
    }

    public async Task<IActionResult> OnPostDeleteAsync()
    {
        var plan = await db.BackupPlan.FirstOrDefaultAsync(p => p.Id == Id);
        if (plan is not null)
        {
            db.BackupPlan.Remove(plan);
            await db.SaveChangesAsync();
            await log.LogAsync(L["Backup.Plan.Log.Deleted"], "BackupPlan", plan.Id, plan.Name);
            Flash(L["Backup.Plan.Deleted"]);
        }

        return RedirectToPage("/Admin/Backup/Plans/Index");
    }

    private async Task<bool> PrepareAsync()
    {
        if (Id is { } id)
        {
            Plan = await db.BackupPlan.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id);
            if (Plan is null)
            {
                return false;
            }
        }

        Targets = await db.BackupTarget.AsNoTracking().OrderBy(t => t.Name).Select(t => new SelectListItem(t.Name, t.Id.ToString())).ToListAsync();
        var title = Plan is null ? L["Backup.Plans.New"].Value : Plan.Name;
        Head(title, null, (L["Backup.Settings"], null), (L["Backup.Title"], "/Admin/Backup"), (L["Backup.Plans"], "/Admin/Backup/Plans"), (title, null));
        ViewData["ActiveHref"] = "/Admin/Backup/Plans";
        ViewData["Width"] = "narrow";
        return true;
    }
}
