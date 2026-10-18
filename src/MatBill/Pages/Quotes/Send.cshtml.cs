using System.ComponentModel.DataAnnotations;
using MatBill.Data;
using MatBill.Data.Entities;
using MatBill.Services;
using MatBill.UiKit;
using Microsoft.AspNetCore.Mvc;

namespace MatBill.Pages.Quotes;

/// <summary>Sends an issued quote as a PDF by mail, to the address of the quote, with the text of the settings.</summary>
public sealed class SendModel(QuoteService quotes, MailService mail, ICurrentUser current) : PageModelBase
{
    public sealed class MailInput
    {
        [Required(ErrorMessage = "Quotes.Send.To.Required")]
        [Display(Name = "Quotes.Send.To")]
        public string To { get; set; } = "";

        [Display(Name = "Quotes.Send.Cc")]
        public string? Cc { get; set; }

        [Required(ErrorMessage = "Quotes.Send.Subject.Required")]
        [Display(Name = "Quotes.Send.Subject")]
        public string Subject { get; set; } = "";

        [Required(ErrorMessage = "Quotes.Send.Body.Required")]
        [Display(Name = "Quotes.Send.Body")]
        public string Body { get; set; } = "";
    }

    [BindProperty(SupportsGet = true)]
    public long Id { get; set; }

    [BindProperty]
    public MailInput Input { get; set; } = new();

    public Quote Quote { get; private set; } = null!;

    public bool Configured => mail.IsConfigured;

    public bool IsAdmin => current.IsAdmin;

    public async Task<IActionResult> OnGetAsync()
    {
        if (await LoadAsync() is { } problem)
        {
            return problem;
        }

        var m = mail.DefaultMail(Quote);
        Input = new MailInput { To = m.To, Cc = m.Cc, Subject = m.Subject, Body = m.Body };
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (await LoadAsync() is { } problem)
        {
            return problem;
        }

        if (!ModelState.IsValid)
        {
            return Page();
        }

        var error = await mail.SendAsync(Quote, new InvoiceMail(Input.To.Trim(), string.IsNullOrWhiteSpace(Input.Cc) ? null : Input.Cc.Trim(), Input.Subject.Trim(), Input.Body), await quotes.PdfAsync(Quote));
        if (error is not null)
        {
            ModelState.AddModelError(string.Empty, error);
            return Page();
        }

        Flash(L["Quotes.Send.Sent", Quote.Number!, Input.To]);
        return RedirectToPage("/Quotes/Edit", new { id = Quote.Id });
    }
    private async Task<IActionResult?> LoadAsync()
    {
        var quote = await quotes.FindAsync(Id);
        if (quote is null)
        {
            return NotFound();
        }

        if (quote.IsDraft)
        {
            FlashError(L["Quotes.Send.IssueFirst"]);
            return RedirectToPage("/Quotes/Edit", new { id = quote.Id });
        }

        Quote = quote;
        Head(L["Quotes.Send.Title", quote.Number!], $"{quote.RecipientName} · {Fmt.Money(quote.GrossTotal)}", (L["Quotes.Title"], "/Quotes"), (quote.Number!, $"/Quotes/Edit?id={quote.Id}"), (L["Quotes.Send.Crumb"], null));
        ViewData["Width"] = "narrow";
        ViewData["ActiveHref"] = "/Quotes";
        return null;
    }
}
