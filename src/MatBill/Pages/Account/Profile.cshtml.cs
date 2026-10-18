using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using MatBill.Core.Security;
using MatBill.Data;
using MatBill.Data.Entities;
using MatBill.Localization;
using MatBill.Services;
using MatBill.Services.Auth;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MatBill.Pages.Account;

public sealed class ProfileModel(MbDbContext db, ICurrentUser current, UserService users, SignInService signIn, ActivityLogger log) : PageModelBase
{
    public sealed class ProfileInput
    {
        [Required(ErrorMessage = "Profile.Name.Required")]
        [Display(Name = "Profile.Name")]
        public string DisplayName { get; set; } = "";

        [Display(Name = "Common.Phone")]
        public string? Phone { get; set; }

        [Display(Name = "Profile.Theme")]
        public string Theme { get; set; } = "system";

        [Display(Name = "Common.Language")]
        public string Language { get; set; } = Languages.Default;
    }

    public sealed class PasswordInput
    {
        [Display(Name = "Profile.Password.Current")]
        public string? Current { get; set; }

        [Required(ErrorMessage = "Profile.Password.New.Required")]
        [Display(Name = "Profile.Password.New")]
        public string New { get; set; } = "";

        [Required(ErrorMessage = "Profile.Password.Repeat.Required")]
        [Display(Name = "Profile.Password.Repeat")]
        public string Repeat { get; set; } = "";
    }

    [BindProperty]
    public ProfileInput Profile { get; set; } = new();

    [BindProperty]
    public PasswordInput Password { get; set; } = new();

    public string Email { get; private set; } = "";

    public bool HasPassword { get; private set; }

    public async Task OnGetAsync()
    {
        var user = await LoadAsync();
        Profile = new ProfileInput { DisplayName = user.DisplayName, Phone = user.Phone, Theme = user.Theme, Language = Languages.Normalize(user.Language) };
        Prepare(user);
    }

    public async Task<IActionResult> OnPostProfileAsync()
    {
        var user = await LoadAsync();
        Only("Profile.");
        if (!ModelState.IsValid)
        {
            Prepare(user);
            return Page();
        }

        user.DisplayName = Profile.DisplayName.Trim();
        user.Phone = string.IsNullOrWhiteSpace(Profile.Phone) ? null : Profile.Phone.Trim();
        user.Theme = Profile.Theme is "light" or "dark" ? Profile.Theme : "system";
        user.Language = Languages.Normalize(Profile.Language);
        Languages.SetCookie(Response, user.Language, Request.IsHttps);
        await db.SaveChangesAsync();
        await signIn.RefreshAsync(HttpContext, user);
        Flash(Languages.In(user.Language, () => L["Profile.Saved"].Value));
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostPasswordAsync()
    {
        var user = await LoadAsync();
        Only("Password.");
        if (user.PasswordHash is not null && !PasswordHasher.Verify(Password.Current ?? string.Empty, user.PasswordHash))
        {
            ModelState.AddModelError("Password.Current", L["Profile.Password.Wrong"]);
        }

        if (UserService.CheckPassword(Password.New, user.Email) is { } problem)
        {
            ModelState.AddModelError("Password.New", problem);
        }
        else if (Password.New != Password.Repeat)
        {
            ModelState.AddModelError("Password.Repeat", L["Profile.Password.Mismatch"]);
        }

        if (!ModelState.IsValid)
        {
            Profile = new ProfileInput { DisplayName = user.DisplayName, Phone = user.Phone, Theme = user.Theme, Language = Languages.Normalize(user.Language) };
            Prepare(user);
            return Page();
        }

        var keep = Guid.TryParse(User.FindFirstValue(AppClaims.Session), out var token) ? token : (Guid?)null;
        await users.ChangePasswordAsync(user, Password.New, keep);
        await log.LogAsync(L["Profile.Log.PasswordChanged"], "User", user.Id);
        Flash(L["Profile.Password.Changed"]);
        return RedirectToPage();
    }

    // Each form posts only its own fields; the other one is bound from nothing (even without its prefix), so only the errors of this form count.
    private void Only(string prefix)
    {
        foreach (var key in ModelState.Keys.Where(k => !k.StartsWith(prefix, StringComparison.Ordinal)).ToList())
        {
            ModelState.Remove(key);
        }
    }

    private async Task<User> LoadAsync()
        => await db.User.Include(u => u.Totp).FirstAsync(u => u.Id == current.UserId);

    private void Prepare(User user)
    {
        Email = user.Email;
        HasPassword = user.PasswordHash is not null;
        Head(L["Profile.Title"], L["Profile.Sub"], (L["Profile.Title"], null));
    }
}
