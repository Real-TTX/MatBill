using System.ComponentModel.DataAnnotations;
using MatBill.Configuration;
using MatBill.Data;
using MatBill.Services;
using MatBill.Storage;
using MatBill.UiKit;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MatBill.Pages.Admin;

/// <summary>Where the documents and generated files are kept: the volume of the container, or a share of a NAS (SMB). Files written earlier stay where they are, or are moved on request.</summary>
public sealed class StorageModel(ConfigStore config, SecretProtector secrets, FileStoreService files, MbDbContext db, ActivityLogger log) : PageModelBase
{
    public sealed class InputModel
    {
        [Display(Name = "Admin.Storage.Location")]
        public StorageMode Mode { get; set; } = StorageMode.Local;

        [Display(Name = "Admin.Storage.Host")]
        public string Host { get; set; } = "";

        [Display(Name = "Admin.Storage.Share")]
        public string Share { get; set; } = "";

        [Display(Name = "Admin.Storage.Path")]
        public string Path { get; set; } = "MatBill";

        [Display(Name = "Admin.Storage.Domain")]
        public string? Domain { get; set; }

        [Display(Name = "Admin.UserName")]
        public string? UserName { get; set; }

        [Display(Name = "Admin.Password")]
        public string? Password { get; set; }
    }

    public sealed record Usage(int LocalFiles, long LocalBytes, int SmbFiles, long SmbBytes);

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public bool HasPassword => !string.IsNullOrEmpty(config.Current.Storage.PasswordProtected);

    public Usage Stored { get; private set; } = new(0, 0, 0, 0);

    public string? Test { get; private set; }

    public bool TestOk { get; private set; }

    public IReadOnlyList<string> Shares { get; private set; } = [];

    public async Task OnGetAsync()
    {
        await PrepareAsync();
        var s = config.Current.Storage;
        Input = new InputModel { Mode = s.Mode, Host = s.Host, Share = s.Share, Path = s.Path, Domain = s.Domain, UserName = s.UserName };
    }

    public async Task<IActionResult> OnPostAsync()
    {
        await PrepareAsync();
        if (Input.Mode == StorageMode.Smb)
        {
            if (string.IsNullOrWhiteSpace(Input.Host))
            {
                ModelState.AddModelError("Input.Host", L["Admin.Storage.Host.Required"]);
            }

            if (string.IsNullOrWhiteSpace(Input.Share))
            {
                ModelState.AddModelError("Input.Share", L["Admin.Storage.Share.Required"]);
            }
        }

        if (!ModelState.IsValid)
        {
            return Page();
        }

        // The fields of the NAS are not posted while the volume is chosen: what was set up for the NAS stays, ready for the next switch.
        config.Update(c =>
        {
            var s = c.Storage;
            s.Mode = Input.Mode;
            if (Input.Mode != StorageMode.Smb)
            {
                return;
            }

            s.Host = Input.Host.Trim();
            s.Share = Input.Share.Trim();
            s.Path = string.IsNullOrWhiteSpace(Input.Path) ? "MatBill" : Input.Path.Trim().Trim('/', '\\');
            s.Domain = Input.Domain?.Trim() ?? "";
            s.UserName = Input.UserName?.Trim() ?? "";
            if (!string.IsNullOrEmpty(Input.Password))
            {
                s.PasswordProtected = secrets.Protect(Input.Password);
            }
        });
        await log.LogAsync(L["Admin.Storage.Log.Changed"], "Storage", null, Input.Mode == StorageMode.Smb ? $"NAS {Input.Host}/{Input.Share}" : "Volume");
        Flash(L["Admin.Storage.Saved"]);
        return RedirectToPage();
    }

    /// <summary>Tries the settings in the form (not yet saved): connects, creates a small file and removes it.</summary>
    public async Task<IActionResult> OnPostTestAsync()
    {
        await PrepareAsync();
        if (string.IsNullOrWhiteSpace(Input.Host) || string.IsNullOrWhiteSpace(Input.Share))
        {
            ModelState.AddModelError("Input.Host", L["Admin.Storage.Test.Required"]);
            return Page();
        }

        var password = string.IsNullOrEmpty(Input.Password) ? secrets.Unprotect(config.Current.Storage.PasswordProtected) : Input.Password;
        var share = new SmbShare(new SmbOptions(Input.Host.Trim(), Input.Share.Trim(), Input.Path, Input.Domain, Input.UserName, password));
        var check = await share.CheckAsync(HttpContext.RequestAborted);
        TestOk = check.Ok;
        Test = check.Ok && check.FreeBytes is { } free ? $"{check.Message} {L["Admin.Storage.Free", Fmt.FileSize(free)].Value}" : check.Message;
        return Page();
    }

    /// <summary>Lists the shares of the server, so that the name does not have to be guessed.</summary>
    public async Task<IActionResult> OnPostSharesAsync()
    {
        await PrepareAsync();
        if (string.IsNullOrWhiteSpace(Input.Host))
        {
            ModelState.AddModelError("Input.Host", L["Admin.Storage.Shares.HostRequired"]);
            return Page();
        }

        try
        {
            var password = string.IsNullOrEmpty(Input.Password) ? secrets.Unprotect(config.Current.Storage.PasswordProtected) : Input.Password;
            Shares = await SmbShare.ListSharesAsync(Input.Host.Trim(), Input.Domain, Input.UserName, password, HttpContext.RequestAborted);
            Test = Shares.Count == 0 ? L["Admin.Storage.Shares.None"].Value : L["Admin.Storage.Shares.Found", string.Join(", ", Shares)].Value;
            TestOk = Shares.Count > 0;
        }
        catch (StorageException ex)
        {
            Test = ex.Message;
        }

        return Page();
    }

    public async Task<IActionResult> OnPostMoveAsync(string to)
    {
        var scheme = to == FileStoreService.SmbScheme ? FileStoreService.SmbScheme : FileStoreService.LocalScheme;
        try
        {
            var result = await files.MoveAllAsync(scheme, HttpContext.RequestAborted);
            await log.LogAsync(L["Admin.Storage.Log.Moved"], "Storage", null, L["Admin.Storage.Log.MovedSummary", result.Moved, scheme, result.Failed].Value);
            if (result.Failed == 0)
            {
                Flash(result.Moved switch
                {
                    0 => L["Admin.Storage.Moved.Nothing"],
                    1 => L["Admin.Storage.Moved.One", result.Moved],
                    _ => L["Admin.Storage.Moved.Many", result.Moved],
                });
            }
            else
            {
                FlashError(L["Admin.Storage.Moved.Partly", result.Moved, result.Failed, result.FirstError ?? ""]);
            }
        }
        catch (StorageException ex)
        {
            FlashError(ex.Message);
        }

        return RedirectToPage();
    }

    private async Task PrepareAsync()
    {
        Head(L["Admin.Storage.Title"], L["Admin.Storage.Sub"], (L["Admin.Settings"], null), (L["Admin.Storage.Title"], null));
        ViewData["Width"] = "narrow";
        var all = await db.StoredFile.AsNoTracking().Select(f => new { f.StorageKey, f.SizeBytes }).ToListAsync();
        var smb = all.Where(f => f.StorageKey.StartsWith(FileStoreService.SmbScheme + ":", StringComparison.Ordinal)).ToList();
        Stored = new Usage(all.Count - smb.Count, all.Sum(f => f.SizeBytes) - smb.Sum(f => f.SizeBytes), smb.Count, smb.Sum(f => f.SizeBytes));
    }
}
