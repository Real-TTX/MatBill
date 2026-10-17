using System.ComponentModel.DataAnnotations;
using MatBill.Data;
using MatBill.Data.Entities;
using MatBill.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MatBill.Pages.Banking.Accounts;

/// <summary>A bank account: name, IBAN, the bank; how its bookings come in (files or online) and what was imported.</summary>
public sealed class EditModel(MbDbContext db, ActivityLogger log) : PageModelBase
{
    public sealed class InputModel
    {
        [Required(ErrorMessage = "Bitte gib dem Konto einen Namen."), StringLength(100)]
        public string Name { get; set; } = "";

        [StringLength(40)]
        public string? Iban { get; set; }

        [StringLength(15)]
        public string? Bic { get; set; }

        [StringLength(100)]
        public string? BankName { get; set; }

        public bool IsActive { get; set; } = true;
    }

    private long? _id;

    [BindProperty(SupportsGet = true)]
    public long? Id { get => _id; set => _id = value > 0 ? value : null; }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public BankAccount? Account { get; private set; }

    public List<BankImport> Imports { get; private set; } = [];

    public int Transactions { get; private set; }

    public async Task<IActionResult> OnGetAsync()
    {
        if (Id is { } id)
        {
            var a = await db.BankAccount.AsNoTracking().Include(x => x.BankConnection).FirstOrDefaultAsync(x => x.Id == id);
            if (a is null)
            {
                return NotFound();
            }

            Account = a;
            Input = new InputModel { Name = a.Name, Iban = a.Iban, Bic = a.Bic, BankName = a.BankName, IsActive = a.IsActive };
        }

        await LoadAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var account = Id is { } id ? await db.BankAccount.FirstOrDefaultAsync(x => x.Id == id) : new BankAccount();
        if (account is null)
        {
            return NotFound();
        }

        var iban = Input.Iban?.Replace(" ", "", StringComparison.Ordinal).ToUpperInvariant();
        if (!string.IsNullOrEmpty(iban) && !IbanCheck.IsValid(iban))
        {
            ModelState.AddModelError("Input.Iban", "Die IBAN stimmt nicht (Prüfziffer).");
        }

        if (!ModelState.IsValid)
        {
            Account = account.Id == 0 ? null : account;
            await LoadAsync();
            return Page();
        }

        account.Name = Input.Name.Trim();
        account.Iban = string.IsNullOrEmpty(iban) ? null : iban;
        account.Bic = string.IsNullOrWhiteSpace(Input.Bic) ? null : Input.Bic.Trim().ToUpperInvariant();
        account.BankName = string.IsNullOrWhiteSpace(Input.BankName) ? null : Input.BankName.Trim();
        account.IsActive = Input.IsActive;
        if (account.Id == 0)
        {
            db.BankAccount.Add(account);
        }

        await db.SaveChangesAsync();
        await log.LogAsync(Id is null ? "Bankkonto angelegt" : "Bankkonto geändert", nameof(BankAccount), account.Id, account.Name);
        Flash(Id is null ? "Konto angelegt. Importiere jetzt einen Kontoauszug." : "Konto gespeichert.");
        return Id is null ? RedirectToPage("/Banking/Import", new { account = account.Id }) : RedirectToPage("/Banking/Index");
    }

    public async Task<IActionResult> OnPostDeleteAsync()
    {
        var account = Id is { } id ? await db.BankAccount.FirstOrDefaultAsync(x => x.Id == id) : null;
        if (account is not null)
        {
            db.BankAccount.Remove(account);
            await db.SaveChangesAsync();
            await log.LogAsync("Bankkonto gelöscht", nameof(BankAccount), account.Id, account.Name);
            Flash("Konto entfernt. Seine Umsätze bleiben gespeichert, das Konto erscheint nicht mehr.");
        }

        return RedirectToPage("/Banking/Index");
    }

    private async Task LoadAsync()
    {
        var title = Account?.Name ?? "Neues Bankkonto";
        Head(title, Account?.Iban, ("Banking", "/Banking"), (title, null));
        ViewData["ActiveHref"] = "/Banking";
        ViewData["Width"] = "narrow";
        if (Account is not null)
        {
            Imports = await db.BankImport.AsNoTracking().Where(i => i.BankAccountId == Account.Id).OrderByDescending(i => i.ImportDate).Take(15).ToListAsync();
            Transactions = await db.BankTransaction.CountAsync(t => t.BankAccountId == Account.Id);
        }
    }
}

/// <summary>The check digits of an IBAN (ISO 13616, mod 97).</summary>
public static class IbanCheck
{
    public static bool IsValid(string iban)
    {
        if (iban.Length is < 15 or > 34 || !iban.All(char.IsLetterOrDigit))
        {
            return false;
        }

        var rearranged = iban[4..] + iban[..4];
        var remainder = 0;
        foreach (var c in rearranged)
        {
            var value = char.IsDigit(c) ? c - '0' : char.ToUpperInvariant(c) - 'A' + 10;
            remainder = value > 9 ? (remainder * 100 + value) % 97 : (remainder * 10 + value) % 97;
        }

        return remainder == 1;
    }
}
