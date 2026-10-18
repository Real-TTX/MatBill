using System.ComponentModel.DataAnnotations;
using MatBill.Data;
using MatBill.Data.Entities;
using MatBill.Services;
using MatBill.Services.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MatBill.Pages.Account;

/// <summary>The second step: the code of the authenticator app (or a recovery code). Only here, and only with the ticket of the first step, a session is opened.</summary>
[AllowAnonymous]
public sealed class TwoFactorModel(MbDbContext db, TwoFactorService twoFactor, SignInService signIn, TwoFactorTicket ticket, ActivityLogger log) : PageModelBase
{
    [BindProperty]
    [Required(ErrorMessage = "Account.TwoFactor.Code.Required")]
    [Display(Name = "Account.TwoFactor.Code")]
    public string Code { get; set; } = "";

    public IActionResult OnGet()
    {
        ViewData["Title"] = L["Account.TwoFactor.Title"].Value;
        return ticket.Read(HttpContext) is null ? RedirectToPage("/Account/Login") : Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        ViewData["Title"] = L["Account.TwoFactor.Title"].Value;
        if (ticket.Read(HttpContext) is not var (userId, returnUrl))
        {
            return RedirectToPage("/Account/Login");
        }

        if (!ModelState.IsValid)
        {
            return Page();
        }

        var user = await db.User.Include(u => u.Totp).FirstOrDefaultAsync(u => u.Id == userId && u.IsActive);
        if (user is null)
        {
            return RedirectToPage("/Account/Login");
        }

        if (!await twoFactor.VerifyAsync(user, Code, HttpContext.RequestAborted))
        {
            ModelState.AddModelError(nameof(Code), signIn.IsLockedOut(user) ? L["Account.LockedOut"] : L["Account.WrongCode"]);
            return Page();
        }

        ticket.Clear(HttpContext);
        await signIn.StartSessionAsync(HttpContext, user, HttpContext.RequestAborted);
        await log.LogAsync(L["Account.Log.SignInSecondFactor"], "User", user.Id, user.Email);
        return LocalRedirect(SafeReturn(returnUrl, "/"));
    }
}
