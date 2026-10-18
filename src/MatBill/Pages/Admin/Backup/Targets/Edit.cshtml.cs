using System.ComponentModel.DataAnnotations;
using MatBill.Configuration;
using MatBill.Data;
using MatBill.Data.Entities;
using MatBill.Services;
using MatBill.Services.Backup;
using MatBill.Storage;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MatBill.Pages.Admin.Backup.Targets;

/// <summary>A target: a folder, or a share of a NAS with its sign-in. The password is kept protected. "Check" writes and removes a small file to see that it works.</summary>
public sealed class EditModel(MbDbContext db, SecretProtector secrets, BackupStoreFactory stores, ActivityLogger log) : PageModelBase
{
    public sealed class InputModel
    {
        [Required(ErrorMessage = "Backup.Target.Name.Required")]
        [Display(Name = "Common.Name")]
        public string Name { get; set; } = "";

        [Display(Name = "Backup.Target.Kind")]
        public BackupTargetKind Kind { get; set; } = BackupTargetKind.Smb;

        [Display(Name = "Backup.Target.Folder")]
        public string Path { get; set; } = "";

        [Display(Name = "Backup.Target.Host")]
        public string? Host { get; set; }

        [Display(Name = "Backup.Target.Share")]
        public string? Share { get; set; }

        [Display(Name = "Backup.Target.Domain")]
        public string? Domain { get; set; }

        [Display(Name = "Backup.Target.User")]
        public string? UserName { get; set; }

        [Display(Name = "Backup.Password")]
        public string? Password { get; set; }
    }

    [BindProperty(SupportsGet = true)]
    public long? Id { get; set; }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public BackupTarget? Target { get; private set; }

    public string? Test { get; private set; }

    public bool TestOk { get; private set; }

    public bool HasPassword => !string.IsNullOrEmpty(Target?.PasswordProtected);

    public async Task<IActionResult> OnGetAsync()
    {
        if (!await PrepareAsync())
        {
            return NotFound();
        }

        if (Target is { } t)
        {
            Input = new InputModel { Name = t.Name, Kind = t.Kind, Path = t.Path, Host = t.Host, Share = t.Share, Domain = t.Domain, UserName = t.UserName };
        }
        else
        {
            Input.Path = L["Backup.Target.DefaultFolder"];
        }

        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!await PrepareAsync())
        {
            return NotFound();
        }

        Validate();
        if (!ModelState.IsValid)
        {
            return Page();
        }

        var target = Target is { } existing ? await db.BackupTarget.FirstAsync(t => t.Id == existing.Id) : db.BackupTarget.Add(new BackupTarget()).Entity;
        Apply(target);
        await db.SaveChangesAsync();
        await log.LogAsync(L["Backup.Target.Log.Saved"], "BackupTarget", target.Id, target.Name);
        Flash(L["Backup.Target.Saved"]);
        return RedirectToPage("/Admin/Backup/Targets/Index");
    }

    /// <summary>Tries the settings in the form (not yet saved).</summary>
    public async Task<IActionResult> OnPostTestAsync()
    {
        if (!await PrepareAsync())
        {
            return NotFound();
        }

        Validate();
        if (!ModelState.IsValid)
        {
            return Page();
        }

        var probe = new BackupTarget { PasswordProtected = Target?.PasswordProtected };
        Apply(probe);
        try
        {
            (TestOk, Test) = await stores.Create(probe).CheckAsync(HttpContext.RequestAborted);
        }
        catch (StorageException ex)
        {
            Test = ex.Message;
        }

        return Page();
    }

    public async Task<IActionResult> OnPostDeleteAsync()
    {
        var target = await db.BackupTarget.FirstOrDefaultAsync(t => t.Id == Id);
        if (target is null)
        {
            return RedirectToPage("/Admin/Backup/Targets/Index");
        }

        if (await db.BackupPlan.AnyAsync(p => p.TargetId == target.Id))
        {
            FlashError(L["Backup.Target.InUse"]);
            return RedirectToPage(new { id = Id });
        }

        db.BackupTarget.Remove(target);
        await db.SaveChangesAsync();
        await log.LogAsync(L["Backup.Target.Log.Deleted"], "BackupTarget", target.Id, target.Name);
        Flash(L["Backup.Target.Deleted"]);
        return RedirectToPage("/Admin/Backup/Targets/Index");
    }

    private void Validate()
    {
        if (Input.Kind == BackupTargetKind.Local)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(Input.Host))
        {
            ModelState.AddModelError("Input.Host", L["Backup.Target.Host.Required"]);
        }

        if (string.IsNullOrWhiteSpace(Input.Share))
        {
            ModelState.AddModelError("Input.Share", L["Backup.Target.Share.Required"]);
        }
    }

    private void Apply(BackupTarget target)
    {
        target.Name = Input.Name?.Trim() ?? "";
        target.Kind = Input.Kind;
        target.Path = (Input.Path ?? "").Trim();
        if (Input.Kind == BackupTargetKind.Local)
        {
            return;
        }

        target.Host = Input.Host?.Trim();
        target.Share = Input.Share?.Trim();
        target.Domain = Input.Domain?.Trim();
        target.UserName = Input.UserName?.Trim();
        if (!string.IsNullOrEmpty(Input.Password))
        {
            target.PasswordProtected = secrets.Protect(Input.Password);
        }
    }

    private async Task<bool> PrepareAsync()
    {
        if (Id is { } id)
        {
            Target = await db.BackupTarget.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id);
            if (Target is null)
            {
                return false;
            }
        }

        var title = Target is null ? L["Backup.Targets.New"].Value : Target.Name;
        Head(title, null, (L["Backup.Settings"], null), (L["Backup.Title"], "/Admin/Backup"), (L["Backup.Targets"], "/Admin/Backup/Targets"), (title, null));
        ViewData["ActiveHref"] = "/Admin/Backup/Targets";
        ViewData["Width"] = "narrow";
        return true;
    }
}
