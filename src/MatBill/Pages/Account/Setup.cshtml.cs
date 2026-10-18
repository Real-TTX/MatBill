using System.ComponentModel.DataAnnotations;
using MatBill.Data;
using MatBill.Data.Entities;
using MatBill.Services;
using MatBill.Services.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MatBill.Pages.Account;

/// <summary>The first start: creates the first administrator. The page exists only while there is none.</summary>
[AllowAnonymous]
public sealed class SetupModel(MbDbContext db, UserService users, SignInService signIn, SetupState setup, ActivityLogger log) : PageModelBase
{
    public sealed class InputModel
    {
        [Required(ErrorMessage = "Account.Setup.Name.Required")]
        [Display(Name = "Account.Setup.Name")]
        public string DisplayName { get; set; } = "";

        [Required(ErrorMessage = "Account.Setup.Email.Required")]
        [EmailAddress(ErrorMessage = "Account.Email.Invalid")]
        [Display(Name = "Account.Email")]
        public string Email { get; set; } = "";

        [Required(ErrorMessage = "Account.Setup.Password.Required")]
        [Display(Name = "Account.Password")]
        public string Password { get; set; } = "";

        [Required(ErrorMessage = "Account.Setup.Repeat.Required")]
        [Display(Name = "Account.Setup.Repeat")]
        public string PasswordRepeat { get; set; } = "";
    }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public async Task<IActionResult> OnGetAsync()
    {
        ViewData["Title"] = L["Account.Setup.Title"].Value;
        return await db.User.AnyAsync(u => u.Role == UserRole.Admin) ? RedirectToPage("/Account/Login") : Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        ViewData["Title"] = L["Account.Setup.Title"].Value;
        if (await db.User.AnyAsync(u => u.Role == UserRole.Admin))
        {
            return RedirectToPage("/Account/Login");
        }

        if (ModelState.IsValid)
        {
            if (CheckedPassword() is { } problem)
            {
                ModelState.AddModelError("Input.Password", problem);
            }
            else if (Input.Password != Input.PasswordRepeat)
            {
                ModelState.AddModelError("Input.PasswordRepeat", L["Account.Setup.Mismatch"]);
            }
        }

        if (!ModelState.IsValid)
        {
            return Page();
        }

        var admin = await users.CreateAsync(Input.Email, Input.DisplayName, UserRole.Admin, Input.Password);
        setup.MarkDone();
        await signIn.StartSessionAsync(HttpContext, admin);
        await log.LogAsync(L["Account.Setup.Title"], "User", admin.Id, L["Account.Setup.Log.Detail"]);
        Flash(L["Account.Setup.Welcome"]);
        return RedirectToPage("/Branding/Index");
    }

    private string? CheckedPassword() => UserService.CheckPassword(Input.Password, Input.Email);
}
