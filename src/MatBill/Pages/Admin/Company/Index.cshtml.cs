using System.ComponentModel.DataAnnotations;
using MatBill.Configuration;
using MatBill.Services;
using Microsoft.AspNetCore.Mvc;

namespace MatBill.Pages.Admin.Company;

/// <summary>
/// The company that writes the invoices: name and address of the sender, tax number, VAT id and register for the footer, small business, bank details.
/// How the documents look (logo, colour, greeting) is the branding. A preview of a sample invoice stands beside the form; issued invoices keep what they had.
/// </summary>
public sealed class IndexModel(ConfigStore config, ActivityLogger log) : PageModelBase
{
    public sealed class InputModel
    {
        [Required(ErrorMessage = "Company.Name.Required"), StringLength(200)]
        [Display(Name = "Company.Name")]
        public string Name { get; set; } = "";

        [StringLength(200)]
        [Display(Name = "Company.NameLine2")]
        public string? NameLine2 { get; set; }

        [StringLength(200)]
        [Display(Name = "Company.Manager")]
        public string? Manager { get; set; }

        [StringLength(200)]
        [Display(Name = "Company.Street")]
        public string? Street { get; set; }

        [StringLength(20)]
        [Display(Name = "Company.PostalCode")]
        public string? PostalCode { get; set; }

        [StringLength(100)]
        [Display(Name = "Company.City")]
        public string? City { get; set; }

        [StringLength(100)]
        [Display(Name = "Company.Country")]
        public string? Country { get; set; }

        [EmailAddress(ErrorMessage = "Company.Email.Invalid"), StringLength(200)]
        [Display(Name = "Common.Email")]
        public string? Email { get; set; }

        [StringLength(50)]
        [Display(Name = "Common.Phone")]
        public string? Phone { get; set; }

        [StringLength(200)]
        [Display(Name = "Company.Website")]
        public string? Website { get; set; }

        [StringLength(50)]
        [Display(Name = "Company.TaxNumber")]
        public string? TaxNumber { get; set; }

        [StringLength(30)]
        [Display(Name = "Company.VatId")]
        public string? VatId { get; set; }

        [StringLength(200)]
        [Display(Name = "Company.Register")]
        public string? Register { get; set; }

        [StringLength(100)]
        [Display(Name = "Company.BankName")]
        public string? BankName { get; set; }

        [StringLength(40)]
        public string? Iban { get; set; }

        [StringLength(15)]
        public string? Bic { get; set; }

        [StringLength(100)]
        [Display(Name = "Company.Bank2Name")]
        public string? Bank2Name { get; set; }

        [StringLength(40)]
        public string? Bank2Iban { get; set; }

        [StringLength(15)]
        public string? Bank2Bic { get; set; }

        public bool SmallBusiness { get; set; }
    }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    /// <summary>Changes with every save, so the browser loads the preview again after one.</summary>
    public string PreviewVersion => System.Text.Json.JsonSerializer.Serialize(config.Current.Company).GetHashCode().ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Neither tax number nor VAT id: allowed (a new business may not have one yet), but an invoice over 250 euros needs one (§ 14 (4) UStG).</summary>
    public bool MissingTaxNumber => string.IsNullOrWhiteSpace(config.Current.Company.TaxNumber) && string.IsNullOrWhiteSpace(config.Current.Company.VatId);

    public void OnGet()
    {
        var c = config.Current.Company;
        Input = new InputModel
        {
            Name = c.Name, NameLine2 = c.NameLine2, Manager = c.Manager, Street = c.Street, PostalCode = c.PostalCode, City = c.City, Country = c.Country, Email = c.Email, Phone = c.Phone,
            Website = c.Website, TaxNumber = c.TaxNumber, VatId = c.VatId, Register = c.Register, BankName = c.BankName, Iban = c.Iban, Bic = c.Bic, Bank2Name = c.Bank2Name, Bank2Iban = c.Bank2Iban, Bank2Bic = c.Bank2Bic, SmallBusiness = c.SmallBusiness,
        };
        Prepare();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid)
        {
            Prepare();
            return Page();
        }

        static string? Clean(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();
        config.Update(c =>
        {
            c.Company.Name = Input.Name.Trim();
            c.Company.NameLine2 = Clean(Input.NameLine2);
            c.Company.Manager = Clean(Input.Manager);
            c.Company.Street = Clean(Input.Street);
            c.Company.PostalCode = Clean(Input.PostalCode);
            c.Company.City = Clean(Input.City);
            c.Company.Country = Clean(Input.Country) ?? "Deutschland";
            c.Company.Email = Clean(Input.Email);
            c.Company.Phone = Clean(Input.Phone);
            c.Company.Website = Clean(Input.Website);
            c.Company.TaxNumber = Clean(Input.TaxNumber);
            c.Company.VatId = Clean(Input.VatId);
            c.Company.Register = Clean(Input.Register);
            c.Company.BankName = Clean(Input.BankName);
            c.Company.Iban = Clean(Input.Iban)?.ToUpperInvariant();
            c.Company.Bic = Clean(Input.Bic)?.ToUpperInvariant();
            c.Company.Bank2Name = Clean(Input.Bank2Name);
            c.Company.Bank2Iban = Clean(Input.Bank2Iban)?.ToUpperInvariant();
            c.Company.Bank2Bic = Clean(Input.Bank2Bic)?.ToUpperInvariant();
            c.Company.SmallBusiness = Input.SmallBusiness;
        });
        await log.LogAsync(L["Company.Log.Changed"], "Company");
        Flash(L["Company.Saved"]);
        return RedirectToPage();
    }

    private void Prepare()
    {
        Head(L["Company.Title"], L["Company.Sub"], (L["Admin.Settings"], null), (L["Company.Title"], null));
        ViewData["Width"] = "wide";
    }
}
