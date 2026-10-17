using System.ComponentModel.DataAnnotations;
using MatBill.Configuration;
using MatBill.Core.Billing;
using MatBill.Data;
using MatBill.Data.Entities;
using MatBill.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MatBill.Pages.Products;

/// <summary>A product or a service. A change of the price only shows on positions chosen later: an invoice keeps the price it was written with.</summary>
public sealed class EditModel(MbDbContext db, ConfigStore config, ActivityLogger log) : PageModelBase
{
    public sealed class InputModel
    {
        public ProductKind Kind { get; set; } = ProductKind.Service;

        [Required(ErrorMessage = "Bitte gib einen Namen ein."), StringLength(300)]
        public string Name { get; set; } = "";

        [StringLength(2000)]
        public string? Description { get; set; }

        [StringLength(30)]
        public string? Unit { get; set; }

        [Required(ErrorMessage = "Bitte gib einen Preis ein.")]
        public string UnitPrice { get; set; } = "";

        public string TaxRate { get; set; } = "19";

        [StringLength(50)]
        public string? Sku { get; set; }

        public bool IsActive { get; set; } = true;
    }

    [BindProperty(SupportsGet = true)]
    public long? Id { get; set; }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public Product? Product { get; private set; }

    public int UsedIn { get; private set; }

    public async Task<IActionResult> OnGetAsync()
    {
        if (Id is { } id)
        {
            var p = await db.Product.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
            if (p is null)
            {
                return NotFound();
            }

            Product = p;
            Input = new InputModel
            {
                Kind = p.Kind, Name = p.Name, Description = p.Description, Unit = p.Unit, UnitPrice = Amount.Edit(p.UnitPrice, p.UnitPrice == Math.Round(p.UnitPrice, 2) ? 2 : 4),
                TaxRate = Amount.Quantity(p.TaxRate), Sku = p.Sku, IsActive = p.IsActive,
            };
        }
        else
        {
            Input.TaxRate = Amount.Quantity(config.Current.Invoicing.DefaultTaxRate);
            Input.Unit = "Stunde";
        }

        await LoadAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var product = Id is { } id ? await db.Product.FirstOrDefaultAsync(x => x.Id == id) : null;
        if (Id is not null && product is null)
        {
            return NotFound();
        }

        Product = product;
        if (!Amount.TryParse(Input.UnitPrice, out var price) || price is < -99_999_999 or > 99_999_999)
        {
            ModelState.AddModelError("Input.UnitPrice", "Das ist kein Betrag.");
        }

        if (!Amount.TryParse(Input.TaxRate, out var rate) || rate is < 0 or > 100)
        {
            ModelState.AddModelError("Input.TaxRate", "Der Steuersatz liegt zwischen 0 und 100 %.");
        }

        if (!ModelState.IsValid)
        {
            await LoadAsync();
            return Page();
        }

        if (product is null)
        {
            product = new Product();
            db.Product.Add(product);
        }

        product.Kind = Input.Kind;
        product.Name = Input.Name.Trim();
        product.Description = string.IsNullOrWhiteSpace(Input.Description) ? null : Input.Description.Trim();
        product.Unit = string.IsNullOrWhiteSpace(Input.Unit) ? null : Input.Unit.Trim();
        product.UnitPrice = Math.Round(price, 4, MidpointRounding.AwayFromZero);
        product.TaxRate = rate;
        product.Sku = string.IsNullOrWhiteSpace(Input.Sku) ? null : Input.Sku.Trim();
        product.IsActive = Input.IsActive;
        await db.SaveChangesAsync();
        await log.LogAsync(Id is null ? "Produkt angelegt" : "Produkt geändert", nameof(Product), product.Id, product.Name);
        Flash(Id is null ? $"Produkt {product.Name} angelegt." : "Produkt gespeichert.");
        return RedirectToPage("/Products/Index");
    }

    public async Task<IActionResult> OnPostDeleteAsync()
    {
        var product = Id is { } id ? await db.Product.FirstOrDefaultAsync(x => x.Id == id) : null;
        if (product is not null)
        {
            db.Product.Remove(product);
            await db.SaveChangesAsync();
            await log.LogAsync("Produkt gelöscht", nameof(Product), product.Id, product.Name);
            Flash("Produkt gelöscht. Rechnungen, die es enthalten, bleiben unverändert.");
        }

        return RedirectToPage("/Products/Index");
    }

    private async Task LoadAsync()
    {
        var title = Product?.Name ?? "Neues Produkt";
        Head(title, null, ("Produkte", "/Products"), (title, null));
        ViewData["ActiveHref"] = "/Products";
        ViewData["Width"] = "narrow";
        if (Product is not null)
        {
            UsedIn = await db.InvoiceLine.CountAsync(l => l.ProductId == Product.Id);
        }
    }
}
