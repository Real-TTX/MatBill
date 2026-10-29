using System.ComponentModel.DataAnnotations;
using MatBill.Configuration;
using MatBill.Core.Billing;
using MatBill.Data;
using MatBill.Data.Entities;
using MatBill.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.EntityFrameworkCore;

namespace MatBill.Pages.Admin.Api;

/// <summary>
/// One API client: its name, what it may do (create requests, read them, issue directly), its own invoice number range with the next counter, and its key
/// (shown once after it was made; a new key replaces the old one at once; revoking stops the client, its invoices keep showing where they came from).
/// </summary>
public sealed class EditModel(MbDbContext db, ApiClientService clients, NumberService numbers, ConfigStore config, ActivityLogger log, TimeProvider clock) : PageModelBase
{
    public sealed class InputModel
    {
        [Required(ErrorMessage = "Api.Name.Required")]
        [StringLength(100, ErrorMessage = "Api.Name.Length")]
        public string Name { get; set; } = "";

        public bool CanCreate { get; set; } = true;

        public bool CanRead { get; set; } = true;

        public bool CanIssue { get; set; }

        public string? NumberPattern { get; set; }

        [Range(1, 999_999_999, ErrorMessage = "Admin.Invoicing.Next.Range")]
        public long Next { get; set; } = 1;
    }

    [BindProperty(SupportsGet = true)]
    public long? Id { get => _id; set => _id = value > 0 ? value : null; }

    private long? _id;

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public ApiClient? Client { get; private set; }

    /// <summary>The key, right after it was made: shown this once.</summary>
    public string? NewKey { get; private set; }

    public int InvoiceCount { get; private set; }

    public int CustomerCount { get; private set; }

    /// <summary>The number the next invoice of the client gets (its own range, or the normal one).</summary>
    public string NextNumber { get; private set; } = "";

    /// <summary>The pattern of the normal invoice range (the placeholder of the field).</summary>
    public string InvoicePattern => config.Current.Invoicing.InvoiceNumberPattern;

    /// <summary>The next number of the normal invoice range: what the preview shows while the field is empty.</summary>
    public string NormalNext { get; private set; } = "";

    public DateOnly Today => Fmt.Today(clock);

    public async Task<IActionResult> OnGetAsync()
    {
        if (!await LoadAsync())
        {
            return NotFound();
        }

        if (Client is { } c)
        {
            Input = new InputModel { Name = c.Name, CanCreate = c.CanCreate, CanRead = c.CanRead, CanIssue = c.CanIssue, NumberPattern = c.NumberPattern };
            Input.Next = c.NumberPattern is { Length: > 0 } p ? await numbers.NextCounterAsync(NumberService.ClientRange(c.Id), p, Today) : 1;
        }

        NewKey = TempData["ApiKey"] as string;
        await PrepareAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!await LoadAsync())
        {
            return NotFound();
        }

        var pattern = string.IsNullOrWhiteSpace(Input.NumberPattern) ? null : Input.NumberPattern.Trim();
        if (await clients.PatternProblemAsync(pattern, Id) is { } problem)
        {
            ModelState.AddModelError("Input.NumberPattern", L[problem]);
        }
        else if (pattern is not null && Client is not null && ModelState.GetFieldValidationState("Input.Next") != ModelValidationState.Invalid
            && !await numbers.CanContinueAtAsync(NumberService.ClientRange(Client.Id), pattern, Today, Input.Next))
        {
            ModelState.AddModelError("Input.Next", L["Admin.Invoicing.Next.Taken", await numbers.NextCounterAsync(NumberService.ClientRange(Client.Id), pattern, Today)]);
        }

        if (!ModelState.IsValid)
        {
            await PrepareAsync();
            return Page();
        }

        ApiClient client;
        if (Client is null)
        {
            (client, var key) = await clients.CreateAsync(Input.Name, Input.CanCreate, Input.CanRead, Input.CanIssue, pattern);
            TempData["ApiKey"] = key;
            await log.LogAsync(L["Api.Log.Created"], nameof(ApiClient), client.Id, client.Name);
            Flash(L["Api.Created"]);
        }
        else
        {
            client = await db.ApiClient.FirstAsync(c => c.Id == Client.Id);
            client.Name = Input.Name.Trim();
            client.CanCreate = Input.CanCreate;
            client.CanRead = Input.CanRead;
            client.CanIssue = Input.CanIssue;
            client.NumberPattern = pattern;
            await db.SaveChangesAsync();
            await log.LogAsync(L["Api.Log.Changed"], nameof(ApiClient), client.Id, client.Name);
            Flash(L["Api.Saved"]);
        }

        if (pattern is not null)
        {
            await numbers.SetNextCounterAsync(NumberService.ClientRange(client.Id), pattern, Today, Input.Next);
        }

        return RedirectToPage(new { id = client.Id });
    }

    /// <summary>A new key: the old one stops working at once (a revoked client works again with it).</summary>
    public async Task<IActionResult> OnPostRenewAsync()
    {
        var client = await db.ApiClient.FirstOrDefaultAsync(c => c.Id == Id);
        if (client is null)
        {
            return NotFound();
        }

        TempData["ApiKey"] = await clients.RenewKeyAsync(client);
        await log.LogAsync(L["Api.Log.Renewed"], nameof(ApiClient), client.Id, client.Name);
        Flash(L["Api.Renewed"]);
        return RedirectToPage(new { id = client.Id });
    }

    public async Task<IActionResult> OnPostRevokeAsync()
    {
        var client = await db.ApiClient.FirstOrDefaultAsync(c => c.Id == Id);
        if (client is null)
        {
            return RedirectToPage("/Admin/Api/Index");
        }

        await clients.RevokeAsync(client);
        await log.LogAsync(L["Api.Log.Revoked"], nameof(ApiClient), client.Id, client.Name);
        Flash(L["Api.Revoked"]);
        return RedirectToPage(new { id = client.Id });
    }

    private async Task<bool> LoadAsync()
    {
        if (Id is { } id)
        {
            Client = await db.ApiClient.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id);
            if (Client is null)
            {
                return false;
            }

            InvoiceCount = await db.Invoice.CountAsync(i => i.ApiClientId == id);
            CustomerCount = await db.Customer.CountAsync(c => c.ApiClientId == id);
        }

        var title = Client?.Name ?? L["Api.New"].Value;
        Head(title, null, (L["Admin.Settings"], null), (L["Api.Title"], "/Admin/Api"), (title, null));
        ViewData["Width"] = "narrow";
        ViewData["ActiveHref"] = "/Admin/Api";
        return true;
    }

    private async Task PrepareAsync()
    {
        var pattern = string.IsNullOrWhiteSpace(Input.NumberPattern) ? null : Input.NumberPattern.Trim();
        NormalNext = await numbers.PeekInvoiceNumberAsync(Today);
        if (pattern is null)
        {
            NextNumber = NormalNext;
        }
        else if (NumberPattern.Problem(pattern) is null)
        {
            NextNumber = Client is null
                ? NumberPattern.Format(pattern, Today, Math.Max(1, Input.Next))
                : await numbers.FirstFreeAsync(NumberService.ClientRange(Client.Id), pattern, Today, Math.Max(1, Input.Next));
        }
        else
        {
            NextNumber = "–";
        }
    }
}
