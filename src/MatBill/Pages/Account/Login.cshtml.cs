using System.ComponentModel.DataAnnotations;
using MatBill.Services;
using MatBill.Services.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MatBill.Pages.Account;

[AllowAnonymous]
public sealed class LoginModel(SignInService signIn, TwoFactorTicket ticket, ActivityLogger log) : PageModelBase
{
    public sealed class InputModel
    {
        [Required(ErrorMessage = "Account.Login.Email.Required")]
        [EmailAddress(ErrorMessage = "Account.Email.Invalid")]
        [Display(Name = "Account.Email")]
        public string Email { get; set; } = "";

        [Required(ErrorMessage = "Account.Login.Password.Required")]
        [Display(Name = "Account.Password")]
        public string Password { get; set; } = "";
    }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    public IActionResult OnGet()
    {
        if (User.Identity?.IsAuthenticated == true)
        {
            return LocalRedirect(Landing());
        }

        ViewData["Title"] = L["Account.Login.Title"].Value;
        ViewData["ClearKept"] = true;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        ViewData["Title"] = L["Account.Login.Title"].Value;
        ViewData["ClearKept"] = true;
        if (!ModelState.IsValid)
        {
            return Page();
        }

        var result = await signIn.CheckPasswordAsync(Input.Email, Input.Password, HttpContext.RequestAborted);
        switch (result.Outcome)
        {
            case SignInOutcome.SecondFactor:
                ticket.Issue(HttpContext, result.User!.Id, ReturnUrl);
                return RedirectToPage("/Account/TwoFactor");

            case SignInOutcome.Success:
                await signIn.StartSessionAsync(HttpContext, result.User!, HttpContext.RequestAborted);
                await log.LogAsync(L["Account.Log.SignIn"], "User", result.User!.Id, result.User.Email);
                return LocalRedirect(Landing());

            case SignInOutcome.LockedOut:
                ModelState.AddModelError(string.Empty, L["Account.LockedOut"]);
                return Page();

            case SignInOutcome.Inactive:
                ModelState.AddModelError(string.Empty, L["Account.Login.Inactive"]);
                return Page();

            default:
                ModelState.AddModelError(string.Empty, L["Account.Login.Wrong"]);
                return Page();
        }
    }

    private string Landing() => SafeReturn(ReturnUrl, "/");
}
