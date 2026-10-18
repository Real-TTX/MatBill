using System.ComponentModel.DataAnnotations;
using MatBill.Localization;
using MatBill.Services;
using MatBill.Services.Backup;
using Microsoft.AspNetCore.Mvc;

namespace MatBill.Pages.Admin.Backup.Restore;

/// <summary>Step two of a restore: what is in the backup, what to restore, and the second question ("type RESTORE") before anything is replaced.</summary>
public sealed class ConfirmModel(BackupService backups) : PageModelBase
{
    /// <summary>The word to type, in the language of the interface ("RESTORE", "WIEDERHERSTELLEN"); the word of any language is accepted.</summary>
    public static string Phrase => Text.T("Backup.Restore.Phrase");

    private static bool IsPhrase(string? typed)
        => Languages.Codes.Any(code => string.Equals(typed?.Trim(), TextCatalog.Find("Backup.Restore.Phrase", code), StringComparison.OrdinalIgnoreCase));

    public sealed class InputModel
    {
        public bool Files { get; set; } = true;

        public bool Settings { get; set; }

        [Display(Name = "Backup.Restore.Confirmation")]
        public string? Confirmation { get; set; }
    }

    [BindProperty(SupportsGet = true, Name = "file")]
    public string? Staged { get; set; }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public BackupManifest? Manifest { get; private set; }

    public RestoreResult? Result { get; private set; }

    public async Task<IActionResult> OnGetAsync()
    {
        Prepare();
        return await LoadAsync() ? Page() : RedirectToPage("/Admin/Backup/Restore/Index");
    }

    public async Task<IActionResult> OnPostAsync()
    {
        Prepare();
        if (!await LoadAsync())
        {
            return RedirectToPage("/Admin/Backup/Restore/Index");
        }

        if (!IsPhrase(Input.Confirmation))
        {
            ModelState.AddModelError("Input.Confirmation", L["Backup.Restore.Type.Required", Phrase]);
            return Page();
        }

        var path = backups.StagedPath(Staged)!;
        try
        {
            Result = await backups.RestoreAsync(path, new RestoreOptions(Input.Files, Input.Settings), HttpContext.RequestAborted);
        }
        catch (BackupException ex)
        {
            ModelState.AddModelError("Input.Confirmation", ex.Message);
            return Page();
        }
        finally
        {
            backups.DeleteStaged(path);
        }

        return Page();
    }

    public IActionResult OnPostCancel()
    {
        if (backups.StagedPath(Staged) is { } path)
        {
            backups.DeleteStaged(path);
        }

        return RedirectToPage("/Admin/Backup/Index");
    }

    private async Task<bool> LoadAsync()
    {
        var path = backups.StagedPath(Staged);
        if (path is null)
        {
            if (Result is null)
            {
                FlashError(L["Backup.Restore.Expired"]);
            }

            return false;
        }

        try
        {
            Manifest = await backups.ReadManifestAsync(path, HttpContext.RequestAborted);
        }
        catch (Exception ex) when (ex is BackupException or InvalidDataException)
        {
            FlashError(ex is BackupException ? ex.Message : L["Backup.Restore.Invalid"].Value);
            backups.DeleteStaged(path);
            return false;
        }

        return true;
    }

    private void Prepare()
    {
        Head(L["Backup.Restore"], L["Backup.Restore.Confirm.Sub"], (L["Backup.Settings"], null), (L["Backup.Title"], "/Admin/Backup"), (L["Backup.Restore"], "/Admin/Backup/Restore"), (L["Backup.Restore.Confirm"], null));
        ViewData["ActiveHref"] = "/Admin/Backup/Restore";
        ViewData["Width"] = "narrow";
    }
}
