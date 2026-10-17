using System.ComponentModel.DataAnnotations;
using MatBill.Configuration;
using MatBill.Core.Billing;
using MatBill.Services;
using Microsoft.AspNetCore.Mvc;

namespace MatBill.Pages.Branding;

/// <summary>
/// The look of the invoices: the company as sender, bank and tax numbers for the footer, the logo and the colour of the document. A preview of a sample invoice
/// stands beside the form. Issued invoices keep their look (their PDF is stored): a change here shows on what comes next.
/// </summary>
public sealed class IndexModel(ConfigStore config, BrandingService branding, ActivityLogger log) : PageModelBase
{
    public sealed class InputModel
    {
        [Required(ErrorMessage = "Bitte gib den Namen des Unternehmens ein."), StringLength(200)]
        public string Name { get; set; } = "";

        [StringLength(200)]
        public string? Manager { get; set; }

        [StringLength(200)]
        public string? Street { get; set; }

        [StringLength(20)]
        public string? PostalCode { get; set; }

        [StringLength(100)]
        public string? City { get; set; }

        [StringLength(100)]
        public string? Country { get; set; }

        [EmailAddress(ErrorMessage = "Das ist keine E-Mail-Adresse."), StringLength(200)]
        public string? Email { get; set; }

        [StringLength(50)]
        public string? Phone { get; set; }

        [StringLength(200)]
        public string? Website { get; set; }

        [StringLength(50)]
        public string? TaxNumber { get; set; }

        [StringLength(30)]
        public string? VatId { get; set; }

        [StringLength(200)]
        public string? Register { get; set; }

        [StringLength(100)]
        public string? BankName { get; set; }

        [StringLength(40)]
        public string? Iban { get; set; }

        [StringLength(15)]
        public string? Bic { get; set; }

        [RegularExpression("^#[0-9a-fA-F]{6}$", ErrorMessage = "Eine Farbe wie #1d4ed8.")]
        public string AccentColor { get; set; } = "#1d4ed8";

        [StringLength(40)]
        public string? Signature { get; set; }

        public bool SmallBusiness { get; set; }
    }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    [BindProperty]
    public IFormFile? Logo { get; set; }

    public string? LogoUrl => MatBill.Pages.Brand.LogoUrl(config.Current.Company);

    public DocumentViewModel Preview { get; private set; } = null!;

    public void OnGet()
    {
        var c = config.Current.Company;
        Input = new InputModel
        {
            Name = c.Name, Manager = c.Manager, Street = c.Street, PostalCode = c.PostalCode, City = c.City, Country = c.Country, Email = c.Email, Phone = c.Phone, Website = c.Website,
            TaxNumber = c.TaxNumber, VatId = c.VatId, Register = c.Register, BankName = c.BankName, Iban = c.Iban, Bic = c.Bic, AccentColor = c.AccentColor, Signature = c.Signature,
            SmallBusiness = c.SmallBusiness,
        };
        Prepare();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (string.IsNullOrWhiteSpace(Input.TaxNumber) && string.IsNullOrWhiteSpace(Input.VatId))
        {
            ModelState.AddModelError("Input.TaxNumber", "Eine Rechnung braucht die Steuernummer oder die USt-IdNr.");
        }

        if (Logo is { Length: > 0 })
        {
            var problem = await branding.SaveLogoAsync(Logo);
            if (problem is not null)
            {
                ModelState.AddModelError(nameof(Logo), problem);
            }
        }

        if (!ModelState.IsValid)
        {
            Prepare();
            return Page();
        }

        static string? Clean(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();
        config.Update(c =>
        {
            c.Company.Name = Input.Name.Trim();
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
            c.Company.AccentColor = Input.AccentColor.ToLowerInvariant();
            c.Company.Signature = Clean(Input.Signature);
            c.Company.SmallBusiness = Input.SmallBusiness;
        });
        await log.LogAsync("Branding geändert", "Company");
        Flash("Gespeichert. Neue Rechnungen sehen so aus, ausgestellte bleiben, wie sie sind.");
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRemoveLogoAsync()
    {
        branding.RemoveLogo();
        await log.LogAsync("Logo entfernt", "Company");
        Flash("Das Logo ist entfernt; auf neuen Rechnungen steht der Name.");
        return RedirectToPage();
    }

    private void Prepare()
    {
        Head("Branding", "So sehen deine Rechnungen aus: Absender, Logo, Farbe, Bank und Steuernummer.", ("Branding", null));
        ViewData["Width"] = "wide";
        var company = config.Current.Company;
        var today = DateOnly.FromDateTime(DateTime.Today);
        var lines = new List<DocumentLine>
        {
            new(1, "Webdesign", "Konzeption & Gestaltung", 1, "Pauschal", 1200m, 19m, 1200m),
            new(2, "Beratung", "Beratungsleistung pro Stunde", 2, "Stunde", 150m, 19m, 300m),
        };
        Preview = new DocumentViewModel(new InvoiceDocument
        {
            Number = "RE-" + today.Year + "-0042",
            CustomerNumber = "K-0001",
            InvoiceDate = today,
            DueDate = today.AddDays(14),
            PaymentTermDays = 14,
            ServiceFrom = today,
            RecipientLines = ["Studio Nord GmbH", "z. Hd. Frau Keller", "Hafenstraße 12", "68159 Mannheim"],
            IntroText = config.Current.Invoicing.IntroText,
            PaymentTerms = Texts.Expand(config.Current.Invoicing.PaymentTerms, 14, today.AddDays(14)),
            ClosingText = config.Current.Invoicing.ClosingText,
            Lines = lines,
            Totals = InvoiceMath.Totals(lines.Select(l => new LineAmounts(l.Quantity, l.UnitPrice, l.TaxRate)), company.SmallBusiness),
            TaxExempt = company.SmallBusiness,
            TaxExemptNote = config.Current.Invoicing.SmallBusinessNote,
        }, company, LogoUrl);
    }
}
