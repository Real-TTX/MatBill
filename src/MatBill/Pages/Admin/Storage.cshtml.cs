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
        [Display(Name = "Speicherort")]
        public StorageMode Mode { get; set; } = StorageMode.Local;

        [Display(Name = "Server")]
        public string Host { get; set; } = "";

        [Display(Name = "Freigabe")]
        public string Share { get; set; } = "";

        [Display(Name = "Ordner in der Freigabe")]
        public string Path { get; set; } = "MatBill";

        [Display(Name = "Domäne (optional)")]
        public string? Domain { get; set; }

        [Display(Name = "Benutzername")]
        public string? UserName { get; set; }

        [Display(Name = "Passwort")]
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
                ModelState.AddModelError("Input.Host", "Bitte gib den Server (Namen oder Adresse) des NAS an.");
            }

            if (string.IsNullOrWhiteSpace(Input.Share))
            {
                ModelState.AddModelError("Input.Share", "Bitte gib den Namen der Freigabe an.");
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
        await log.LogAsync("Speicher geändert", "Storage", null, Input.Mode == StorageMode.Smb ? $"NAS {Input.Host}/{Input.Share}" : "Volume");
        Flash("Der Speicher ist eingestellt. Neue Dateien werden dort abgelegt.");
        return RedirectToPage();
    }

    /// <summary>Tries the settings in the form (not yet saved): connects, creates a small file and removes it.</summary>
    public async Task<IActionResult> OnPostTestAsync()
    {
        await PrepareAsync();
        if (string.IsNullOrWhiteSpace(Input.Host) || string.IsNullOrWhiteSpace(Input.Share))
        {
            ModelState.AddModelError("Input.Host", "Bitte gib Server und Freigabe an, damit die Verbindung geprüft werden kann.");
            return Page();
        }

        var password = string.IsNullOrEmpty(Input.Password) ? secrets.Unprotect(config.Current.Storage.PasswordProtected) : Input.Password;
        var share = new SmbShare(new SmbOptions(Input.Host.Trim(), Input.Share.Trim(), Input.Path, Input.Domain, Input.UserName, password));
        var check = await share.CheckAsync(HttpContext.RequestAborted);
        TestOk = check.Ok;
        Test = check.Ok && check.FreeBytes is { } free ? $"{check.Message} Frei: {Fmt.FileSize(free)}." : check.Message;
        return Page();
    }

    /// <summary>Lists the shares of the server, so that the name does not have to be guessed.</summary>
    public async Task<IActionResult> OnPostSharesAsync()
    {
        await PrepareAsync();
        if (string.IsNullOrWhiteSpace(Input.Host))
        {
            ModelState.AddModelError("Input.Host", "Bitte gib zuerst den Server an.");
            return Page();
        }

        try
        {
            var password = string.IsNullOrEmpty(Input.Password) ? secrets.Unprotect(config.Current.Storage.PasswordProtected) : Input.Password;
            Shares = await SmbShare.ListSharesAsync(Input.Host.Trim(), Input.Domain, Input.UserName, password, HttpContext.RequestAborted);
            Test = Shares.Count == 0 ? "Der Server zeigt keine Freigaben." : "Freigaben auf dem Server: " + string.Join(", ", Shares);
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
            await log.LogAsync("Dateien verschoben", "Storage", null, $"{result.Moved} nach {scheme}, {result.Failed} Fehler");
            if (result.Failed == 0)
            {
                Flash(result.Moved == 0 ? "Es gab nichts zu verschieben." : $"{Ui.Plural(result.Moved, "Datei ist", "Dateien sind")} verschoben.");
            }
            else
            {
                FlashError($"{result.Moved} verschoben, {result.Failed} nicht: {result.FirstError}");
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
        Head("Speicher (NAS)", "Wo die PDFs der ausgestellten Rechnungen und das Logo liegen.", ("Einstellungen", null), ("Speicher (NAS)", null));
        ViewData["Width"] = "narrow";
        var all = await db.StoredFile.AsNoTracking().Select(f => new { f.StorageKey, f.SizeBytes }).ToListAsync();
        var smb = all.Where(f => f.StorageKey.StartsWith(FileStoreService.SmbScheme + ":", StringComparison.Ordinal)).ToList();
        Stored = new Usage(all.Count - smb.Count, all.Sum(f => f.SizeBytes) - smb.Sum(f => f.SizeBytes), smb.Count, smb.Sum(f => f.SizeBytes));
    }
}
