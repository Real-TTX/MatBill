using MatBill.Data;
using MatBill.Data.Entities;
using MatBill.Services;
using MatBill.UiKit;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MatBill.Pages.Admin.TestInvoices;

/// <summary>
/// Deleting a test invoice (<see cref="TestInvoiceService"/>): the issued invoices that could be tests with what stands in the way of each, the form that deletes
/// one (the number typed to confirm), and the permanent record of what was deleted. Administrators only (the folder /Admin); the rules are the service's.
/// </summary>
public sealed class IndexModel(MbDbContext db, InvoiceService invoices, TestInvoiceService tests) : PageModelBase
{
    /// <summary>How many issued invoices the list shows (the newest): a test is among the first, and any invoice is reached with its id.</summary>
    private const int Shown = 30;

    private long? _id;

    [BindProperty(SupportsGet = true)]
    public long? Id { get => _id; set => _id = value > 0 ? value : null; }

    public sealed class InputModel
    {
        public string? Reason { get; set; }

        public string? ConfirmNumber { get; set; }

        public bool ResetCounter { get; set; } = true;
    }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    /// <summary>The invoice of the form; null shows the list.</summary>
    public Invoice? Selected { get; private set; }

    public List<string> Problems { get; private set; } = [];

    /// <summary>The number of the cancellation invoice that goes with the selected one.</summary>
    public string? CancellationNumber { get; private set; }

    public TableModel Table { get; private set; } = null!;

    public List<DeletedDocument> Record { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync()
    {
        if (Id is { } id)
        {
            if (await invoices.FindAsync(id, HttpContext.RequestAborted) is not { } invoice)
            {
                return NotFound();
            }

            await SelectAsync(invoice);
        }

        await BuildAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (Id is not { } id || await invoices.FindAsync(id, HttpContext.RequestAborted) is not { } invoice)
        {
            return NotFound();
        }

        // the number is typed: a slip of the finger on a button deletes nothing
        if (!string.Equals(Input.ConfirmNumber?.Trim(), invoice.Number, StringComparison.OrdinalIgnoreCase))
        {
            ModelState.AddModelError("Input.ConfirmNumber", L["Admin.TestInvoices.ConfirmNumber.Wrong"]);
            await SelectAsync(invoice);
            await BuildAsync();
            return Page();
        }

        try
        {
            var result = await tests.DeleteAsync(invoice, Input.Reason, Input.ResetCounter, HttpContext.RequestAborted);
            var key = result.Counter switch
            {
                CounterOutcome.Reset => "Admin.TestInvoices.Done.Reset",
                CounterOutcome.Blocked => "Admin.TestInvoices.Done.Blocked",
                CounterOutcome.Unknown => "Admin.TestInvoices.Done.Unknown",
                _ => "Admin.TestInvoices.Done.NotAsked",
            };
            Flash(L[key, string.Join(", ", result.Numbers), result.NextNumber]);
        }
        catch (InvoiceException ex)
        {
            FlashError(ex.Message);
        }

        return RedirectToPage();
    }

    private async Task SelectAsync(Invoice invoice)
    {
        Selected = invoice;
        Problems = await tests.ProblemsAsync(invoice, HttpContext.RequestAborted);
        if (invoice.CancellationInvoiceId is { } cid)
        {
            CancellationNumber = await db.Invoice.AsNoTracking().Where(i => i.Id == cid).Select(i => i.Number).FirstOrDefaultAsync(HttpContext.RequestAborted);
        }
    }

    private async Task BuildAsync()
    {
        var title = Selected is null ? L["Admin.TestInvoices.Title"].Value : Problems.Count > 0 ? L["Admin.TestInvoices.Blocked.Title"].Value : L["Admin.TestInvoices.Confirm.Title", Selected.Number ?? ""].Value;
        Head(title, Selected is null ? L["Admin.TestInvoices.Sub"] : null, (L["Admin.Settings"], null), (L["Admin.TestInvoices.Title"], Selected is null ? null : "/Admin/TestInvoices"));
        ViewData["ActiveHref"] = "/Admin/Invoicing";

        var candidates = (await tests.CandidatesAsync(HttpContext.RequestAborted)).Take(Shown).ToList();
        var problems = new Dictionary<long, List<string>>();
        foreach (var candidate in candidates)
        {
            problems[candidate.Id] = await tests.ProblemsAsync(candidate, HttpContext.RequestAborted);
        }

        string yes = L["Admin.TestInvoices.Pill.Yes"], no = L["Admin.TestInvoices.Pill.No"], delete = L["Admin.TestInvoices.Delete"];
        Table = TableModel.Of("test-invoice-table", L["Admin.TestInvoices.Title"], candidates,
        [
            (new TableColumn(L["Admin.TestInvoices.Col.Number"]), i => Ui.NameWithSub(i.Number ?? "", $"/Admin/TestInvoices?id={i.Id}", problems[i.Id].FirstOrDefault())),
            (new TableColumn(L["Admin.TestInvoices.Col.Recipient"], HideOnPhone: true), i => Ui.Text(i.RecipientName)),
            (new TableColumn(L["Common.Date"], HideOnPhone: true), i => Ui.Text(Fmt.Date(i.InvoiceDate))),
            (new TableColumn(L["Common.Amount"], Numeric: true), i => Ui.Money(i.GrossTotal)),
            (new TableColumn(L["Admin.TestInvoices.Col.Deletable"]), i => problems[i.Id].Count == 0 ? Ui.Pill(yes, "ok") : Ui.Pill(no, "plain")),
        ],
        i => problems[i.Id].Count == 0 ? [new RowAction("trash", delete, $"/Admin/TestInvoices?id={i.Id}")] : [])
        with
        {
            EmptyTitle = L["Admin.TestInvoices.Empty.Title"],
            EmptyText = L["Admin.TestInvoices.Empty.Text"],
            EmptyIcon = "file-text",
        };

        Record = await db.DeletedDocument.AsNoTracking().OrderByDescending(d => d.DeletedDate).ThenByDescending(d => d.Id).Take(100).ToListAsync(HttpContext.RequestAborted);
    }
}
