using MatBill.Services;
using MatBill.Services.Auth;
using Microsoft.AspNetCore.Mvc;

namespace MatBill.Pages.Account;

public sealed class LogoutModel(SignInService signIn, ActivityLogger log) : PageModelBase
{
    /// <summary>Signing out changes state, so it is a POST (with the anti-forgery token of the menu): a link from another site cannot do it.</summary>
    public IActionResult OnGet() => RedirectToPage("/Index");

    public async Task<IActionResult> OnPostAsync()
    {
        await log.LogAsync(L["Account.Log.SignOut"]);
        await signIn.EndSessionAsync(HttpContext);
        return RedirectToPage("/Account/Login");
    }
}
