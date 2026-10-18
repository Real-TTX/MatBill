using System.ComponentModel.DataAnnotations;
using MatBill.Data;
using MatBill.Data.Entities;
using MatBill.Services;
using MatBill.Services.Auth;
using MatBill.UiKit;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;

namespace MatBill.Pages.Admin.Users;

/// <summary>One account: name, address, job, department, role, whether it may sign in, a new password, the second factor and the sessions.</summary>
public sealed class EditModel(MbDbContext db, UserService users, SignInService signIn, TwoFactorService twoFactor, SessionCache cache, ICurrentUser current, ActivityLogger log, TimeProvider clock) : PageModelBase
{
    public sealed class InputModel
    {
        [Required(ErrorMessage = "Admin.Users.Name.Required")]
        [Display(Name = "Common.Name")]
        public string DisplayName { get; set; } = "";

        [Required(ErrorMessage = "Admin.Users.Email.Required")]
        [EmailAddress(ErrorMessage = "Admin.Users.Email.Invalid")]
        [Display(Name = "Admin.Users.Email")]
        public string Email { get; set; } = "";

        [Display(Name = "Common.Phone")]
        public string? Phone { get; set; }

        [Display(Name = "Admin.Users.Role")]
        public UserRole Role { get; set; } = UserRole.User;

        public bool IsActive { get; set; } = true;

        [Display(Name = "Admin.Users.Password")]
        public string? Password { get; set; }
    }

    [BindProperty(SupportsGet = true)]
    public long? Id { get; set; }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public User? Account { get; private set; }

    public bool IsMe => Account is not null && Account.Id == current.UserId;

    public bool HasTotp => Account?.Totp is { IsConfirmed: true };

    public bool IsLocked => Account is { } a && a.LockoutEndDate > clock.GetUtcNow().UtcDateTime;

    public int Sessions { get; private set; }


    public async Task<IActionResult> OnGetAsync()
    {
        if (!await LoadAsync())
        {
            return NotFound();
        }

        if (Account is { } a)
        {
            Input = new InputModel { DisplayName = a.DisplayName, Email = a.Email, Phone = a.Phone, Role = a.Role, IsActive = a.IsActive };
        }

        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!await LoadAsync())
        {
            return NotFound();
        }

        var email = SignInService.NormalizeEmail(Input.Email);
        if (ModelState.IsValid && await users.EmailTakenAsync(email, Id))
        {
            ModelState.AddModelError("Input.Email", L["Admin.Users.Email.Taken"]);
        }

        if (!string.IsNullOrEmpty(Input.Password) && UserService.CheckPassword(Input.Password, email) is { } weak)
        {
            ModelState.AddModelError("Input.Password", weak);
        }

        if (Id is null && string.IsNullOrEmpty(Input.Password))
        {
            ModelState.AddModelError("Input.Password", L["Admin.Users.Password.Required"]);
        }

        if (Account is { Role: UserRole.Admin } && (Input.Role != UserRole.Admin || !Input.IsActive) && !await OtherAdminExistsAsync())
        {
            ModelState.AddModelError("Input.Role", L["Admin.Users.LastAdmin"]);
        }

        if (IsMe && (!Input.IsActive || Input.Role != UserRole.Admin))
        {
            ModelState.AddModelError("Input.Role", L["Admin.Users.NotYourself"]);
        }

        if (!ModelState.IsValid)
        {
            return Page();
        }

        if (Account is null)
        {
            var created = await users.CreateAsync(email, Input.DisplayName.Trim(), Input.Role, Input.Password!);
            created.Phone = string.IsNullOrWhiteSpace(Input.Phone) ? null : Input.Phone.Trim();
            created.IsActive = Input.IsActive;
            await db.SaveChangesAsync();
            await log.LogAsync(L["Admin.Users.Log.Created"], "User", created.Id, $"{created.Email} ({Labels.Of(created.Role)})");
            Flash(L["Admin.Users.Created"]);
            return RedirectToPage("/Admin/Users/Edit", new { id = created.Id });
        }

        var user = await db.User.FirstAsync(u => u.Id == Account.Id);
        var roleChanged = user.Role != Input.Role;
        user.DisplayName = Input.DisplayName.Trim();
        user.Email = email;
        user.Phone = string.IsNullOrWhiteSpace(Input.Phone) ? null : Input.Phone.Trim();
        user.Role = Input.Role;
        await db.SaveChangesAsync();
        if (user.IsActive != Input.IsActive)
        {
            await users.SetActiveAsync(user, Input.IsActive);
        }

        if (!string.IsNullOrEmpty(Input.Password))
        {
            await users.ChangePasswordAsync(user, Input.Password);
        }

        cache.InvalidateUser(user.Id);
        await log.LogAsync(L["Admin.Users.Log.Changed"], "User", user.Id, roleChanged ? L["Admin.Users.Log.NewRole", user.Email, Labels.Of(user.Role)].Value : user.Email);
        Flash(L["Admin.Users.Saved"]);
        return RedirectToPage("/Admin/Users/Edit", new { id = user.Id });
    }

    public async Task<IActionResult> OnPostResetTotpAsync()
    {
        var user = await db.User.Include(u => u.Totp).FirstOrDefaultAsync(u => u.Id == Id);
        if (user is null)
        {
            return NotFound();
        }

        await twoFactor.DisableAsync(user);
        await log.LogAsync(L["Admin.Users.Log.TotpReset"], "User", user.Id, user.Email);
        Flash(L["Admin.Users.TotpReset"]);
        return RedirectToPage(new { id = Id });
    }

    public async Task<IActionResult> OnPostEndSessionsAsync()
    {
        if (Id is not { } id)
        {
            return NotFound();
        }

        // An administrator who ends their own sessions would lock themselves out in the middle of the click; keep the one in use.
        var keep = id == current.UserId ? Guid.TryParse(User.FindFirst(AppClaims.Session)?.Value, out var token) ? token : (Guid?)null : null;
        await signIn.EndAllSessionsAsync(id, keep);
        await log.LogAsync(L["Admin.Users.Log.SessionsEnded"], "User", id);
        Flash(L["Admin.Users.SessionsEnded"]);
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostUnlockAsync()
    {
        var user = await db.User.FirstOrDefaultAsync(u => u.Id == Id);
        if (user is null)
        {
            return NotFound();
        }

        user.FailedLogins = 0;
        user.LockoutEndDate = null;
        await db.SaveChangesAsync();
        cache.InvalidateUser(user.Id);
        Flash(L["Admin.Users.Unlocked"]);
        return RedirectToPage(new { id = Id });
    }

    public async Task<IActionResult> OnPostDeleteAsync()
    {
        var user = await db.User.FirstOrDefaultAsync(u => u.Id == Id);
        if (user is null)
        {
            return RedirectToPage("/Admin/Users/Index");
        }

        if (user.Id == current.UserId)
        {
            FlashError(L["Admin.Users.CannotDeleteYourself"]);
            return RedirectToPage(new { id = Id });
        }

        if (user.Role == UserRole.Admin && user.IsActive && !await db.User.AnyAsync(u => u.Id != user.Id && u.Role == UserRole.Admin && u.IsActive))
        {
            FlashError(L["Admin.Users.OnlyAdmin"]);
            return RedirectToPage(new { id = Id });
        }

        await signIn.EndAllSessionsAsync(user.Id, null);
        db.User.Remove(user);
        await db.SaveChangesAsync();
        cache.InvalidateUser(user.Id);
        await log.LogAsync(L["Admin.Users.Log.Deleted"], "User", user.Id, user.Email);
        Flash(L["Admin.Users.Deleted"]);
        return RedirectToPage("/Admin/Users/Index");
    }

    private Task<bool> OtherAdminExistsAsync() => db.User.AnyAsync(u => u.Id != Id && u.Role == UserRole.Admin && u.IsActive);

    private async Task<bool> LoadAsync()
    {
        if (Id is { } id)
        {
            Account = await db.User.AsNoTracking().Include(u => u.Totp).FirstOrDefaultAsync(u => u.Id == id);
            if (Account is null)
            {
                return false;
            }

            var now = clock.GetUtcNow().UtcDateTime;
            Sessions = await db.UserSession.CountAsync(s => s.UserId == id && s.RevokedDate == null && s.ExpiresDate > now);
        }

        var title = Id is null ? L["Admin.Users.New"].Value : Account!.DisplayName;
        Head(title, null, (L["Admin.Settings"], null), (L["Admin.Users.Title"], "/Admin/Users"), (title, null));
        ViewData["Width"] = "narrow";
        ViewData["ActiveHref"] = "/Admin/Users";
        return true;
    }
}
