using System.ComponentModel.DataAnnotations;
using MatBill.Data;
using MatBill.Data.Entities;
using MatBill.Services;
using MatBill.UiKit;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MatBill.Pages.Customers;

/// <summary>A customer: address, contact, terms; its invoices and series below. A change here only shows on invoices written later.</summary>
public sealed class EditModel(MbDbContext db, NumberService numbers, ActivityLogger log, TimeProvider clock) : PageModelBase
{
    public sealed class InputModel
    {
        public CustomerKind Kind { get; set; } = CustomerKind.Company;

        [Required(ErrorMessage = "Customers.Name.Required"), StringLength(200)]
        [Display(Name = "Common.Name")]
        public string Name { get; set; } = "";

        [StringLength(200)]
        [Display(Name = "Customers.Contact")]
        public string? ContactName { get; set; }

        [EmailAddress(ErrorMessage = "Customers.Email.Invalid"), StringLength(200)]
        [Display(Name = "Customers.Email")]
        public string? Email { get; set; }

        [StringLength(50)]
        public string? Phone { get; set; }

        [StringLength(200)]
        public string? Street { get; set; }

        [StringLength(20)]
        public string? PostalCode { get; set; }

        [StringLength(100)]
        public string? City { get; set; }

        [StringLength(100)]
        public string? Country { get; set; } = "Deutschland"; // part of the address on the (German) invoice

        [StringLength(30)]
        public string? VatId { get; set; }

        [StringLength(100)]
        public string? BuyerReference { get; set; }

        [Range(0, 365, ErrorMessage = "Customers.PaymentTerm.Range")]
        [Display(Name = "Customers.PaymentTerm")]
        public int? PaymentTermDays { get; set; }

        [StringLength(4000)]
        public string? Note { get; set; }
    }

    [BindProperty(SupportsGet = true)]
    public long? Id { get; set; }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public Customer? Customer { get; private set; }

    public List<Invoice> Invoices { get; private set; } = [];

    public List<RecurringInvoice> Series { get; private set; } = [];

    public decimal OpenSum { get; private set; }

    public decimal YearSum { get; private set; }

    public DateOnly Today => Fmt.Today(clock);

    public async Task<IActionResult> OnGetAsync()
    {
        if (Id is { } id)
        {
            var c = await db.Customer.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
            if (c is null)
            {
                return NotFound();
            }

            Customer = c;
            Input = new InputModel
            {
                Kind = c.Kind, Name = c.Name, ContactName = c.ContactName, Email = c.Email, Phone = c.Phone, Street = c.Street, PostalCode = c.PostalCode, City = c.City,
                Country = c.Country, VatId = c.VatId, BuyerReference = c.BuyerReference, PaymentTermDays = c.PaymentTermDays, Note = c.Note,
            };
        }

        await LoadAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var customer = Id is { } id ? await db.Customer.FirstOrDefaultAsync(x => x.Id == id) : null;
        if (Id is not null && customer is null)
        {
            return NotFound();
        }

        Customer = customer;
        if (!ModelState.IsValid)
        {
            await LoadAsync();
            return Page();
        }

        if (customer is null)
        {
            customer = new Customer { Number = await numbers.NextCustomerNumberAsync(Today) };
            db.Customer.Add(customer);
        }

        customer.Kind = Input.Kind;
        customer.Name = Input.Name.Trim();
        customer.ContactName = Clean(Input.ContactName);
        customer.Email = Clean(Input.Email);
        customer.Phone = Clean(Input.Phone);
        customer.Street = Clean(Input.Street);
        customer.PostalCode = Clean(Input.PostalCode);
        customer.City = Clean(Input.City);
        customer.Country = Clean(Input.Country) ?? "Deutschland";
        customer.VatId = Clean(Input.VatId);
        customer.BuyerReference = Clean(Input.BuyerReference);
        customer.PaymentTermDays = Input.PaymentTermDays;
        customer.Note = Clean(Input.Note);
        await db.SaveChangesAsync();
        await log.LogAsync(Id is null ? L["Customers.Log.Created"] : L["Customers.Log.Changed"], nameof(Customer), customer.Id, customer.Name);
        Flash(Id is null ? L["Customers.Created", customer.Name, customer.Number] : L["Customers.Saved"]);
        return RedirectToPage(new { id = customer.Id });
    }

    public async Task<IActionResult> OnPostDeleteAsync()
    {
        var customer = Id is { } id ? await db.Customer.FirstOrDefaultAsync(x => x.Id == id) : null;
        if (customer is null)
        {
            return RedirectToPage("/Customers/Index");
        }

        if (await db.RecurringInvoice.AnyAsync(s => s.CustomerId == customer.Id && s.Status != RecurringStatus.Ended))
        {
            FlashError(L["Customers.SeriesRunning"]);
            return RedirectToPage(new { id = customer.Id });
        }

        // The customer goes from the lists; its invoices keep their address (they hold their own copy).
        db.Customer.Remove(customer);
        await db.SaveChangesAsync();
        await log.LogAsync(L["Customers.Log.Deleted"], nameof(Customer), customer.Id, customer.Name);
        Flash(L["Customers.Deleted"]);
        return RedirectToPage("/Customers/Index");
    }

    private async Task LoadAsync()
    {
        var title = Customer?.Name ?? L["Customers.New"];
        Head(title, Customer is null ? null : L["Customers.NumberSub", Customer.Number], (L["Customers.Title"], "/Customers"), (title, null));
        ViewData["ActiveHref"] = "/Customers";
        if (Customer is null)
        {
            return;
        }

        Invoices = await db.Invoice.AsNoTracking().Where(i => i.CustomerId == Customer.Id).OrderByDescending(i => i.InvoiceDate).ThenByDescending(i => i.Id).Take(20).ToListAsync();
        Series = await db.RecurringInvoice.AsNoTracking().Where(s => s.CustomerId == Customer.Id).OrderBy(s => s.Name).ToListAsync();
        OpenSum = await db.Invoice.Where(i => i.CustomerId == Customer.Id && i.Status == InvoiceStatus.Open).SumAsync(i => i.GrossTotal);
        YearSum = await db.Invoice.Where(i => i.CustomerId == Customer.Id && i.Status != InvoiceStatus.Draft && i.InvoiceDate.Year == Today.Year).SumAsync(i => i.GrossTotal);
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
