using MatBill.Configuration;
using MatBill.Core.Billing;
using MatBill.Data;
using MatBill.Data.Entities;
using MatBill.Storage;
using Microsoft.EntityFrameworkCore;

namespace MatBill.Services;

/// <summary>
/// Expenses and their receipts. Receipts can be dropped by the dozen: each file becomes an expense that is marked "zu prüfen"; an e-invoice of a supplier
/// (ZUGFeRD PDF, XRechnung XML) fills supplier, number, date and amounts by itself, an ordinary PDF gets a guess from its text.
/// </summary>
public sealed class ExpenseService(MbDbContext db, ConfigStore config, NumberService numbers, FileStoreService files, ActivityLogger activity, TimeProvider clock)
{
    public const long MaxReceiptBytes = 20 * 1024 * 1024;

    private static readonly string[] Allowed = [".pdf", ".png", ".jpg", ".jpeg", ".xml", ".heic", ".webp"];

    public DateOnly Today => Fmt.Today(clock);

    public IReadOnlyList<string> Categories => config.Current.Invoicing.ExpenseCategories;

    public Expense New()
        => new() { Number = "", ExpenseDate = Today, TaxRate = config.Current.Invoicing.DefaultTaxRate, Category = Categories.LastOrDefault() ?? "Sonstiges" };

    /// <summary>Net, tax and gross that fit each other: from the gross when it is given (receipts state the gross), else from the net.</summary>
    public static (decimal Net, decimal Tax, decimal Gross) Amounts(decimal? net, decimal? gross, decimal rate)
    {
        if (gross is { } g)
        {
            var n = InvoiceMath.Round(g / (1 + rate / 100m));
            return (n, g - n, g);
        }

        var nn = net ?? 0m;
        var tax = InvoiceMath.Round(nn * rate / 100m);
        return (nn, tax, nn + tax);
    }

    public async Task SaveAsync(Expense expense, CancellationToken ct = default)
    {
        if (expense.Id == 0)
        {
            expense.Number = await numbers.NextExpenseNumberAsync(expense.ExpenseDate, ct);
            db.Expense.Add(expense);
        }

        expense.NeedsReview = false;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Stores a receipt file for an expense (the old one stays in the store, as every stored file does).</summary>
    public async Task<string?> AttachAsync(Expense expense, IFormFile upload, CancellationToken ct = default)
    {
        var (bytes, problem) = await ReadAsync(upload, ct);
        if (bytes is null)
        {
            return problem;
        }

        expense.ReceiptFile = await files.SaveAsync(bytes, upload.FileName, ContentType(upload.FileName), ct);
        return null;
    }

    public sealed record UploadResult(int Created, List<string> Problems, List<long> Ids);

    /// <summary>One expense per dropped file, read as well as it goes, marked for review.</summary>
    public async Task<UploadResult> CreateFromFilesAsync(IReadOnlyList<IFormFile> uploads, CancellationToken ct = default)
    {
        var problems = new List<string>();
        var ids = new List<long>();
        foreach (var upload in uploads)
        {
            var (bytes, problem) = await ReadAsync(upload, ct);
            if (bytes is null)
            {
                problems.Add($"{upload.FileName}: {problem}");
                continue;
            }

            var data = EInvoice.Read(bytes, upload.FileName);
            var expense = New();
            expense.NeedsReview = true;
            expense.ExpenseDate = data?.Date ?? Today;
            expense.Supplier = data?.Supplier ?? Path.GetFileNameWithoutExtension(upload.FileName);
            expense.SupplierIban = data?.SupplierIban;
            expense.SupplierInvoiceNumber = data?.Number;
            expense.DueDate = data?.DueDate;
            expense.FromEInvoice = data?.FromEInvoice == true;
            if (data?.TaxRate is { } rate)
            {
                expense.TaxRate = rate;
            }

            if (data?.Gross is { } gross)
            {
                expense.GrossAmount = gross;
                expense.NetAmount = data.Net ?? Amounts(null, gross, expense.TaxRate).Net;
                expense.TaxAmount = data.Tax ?? gross - expense.NetAmount;
            }

            expense.Description = data?.FromEInvoice == true ? "aus E-Rechnung übernommen" : null;
            expense.Number = await numbers.NextExpenseNumberAsync(expense.ExpenseDate, ct);
            expense.ReceiptFile = await files.SaveAsync(bytes, upload.FileName, ContentType(upload.FileName), ct);
            db.Expense.Add(expense);
            await db.SaveChangesAsync(ct);
            ids.Add(expense.Id);
        }

        if (ids.Count > 0)
        {
            await activity.LogAsync("Belege hochgeladen", nameof(Expense), null, $"{ids.Count} Belege");
        }

        return new UploadResult(ids.Count, problems, ids);
    }

    private static async Task<(byte[]? Bytes, string? Problem)> ReadAsync(IFormFile upload, CancellationToken ct)
    {
        if (upload.Length == 0 || upload.Length > MaxReceiptBytes)
        {
            return (null, "Ein Beleg darf höchstens 20 MB groß sein.");
        }

        if (!Allowed.Contains(Path.GetExtension(upload.FileName).ToLowerInvariant()))
        {
            return (null, "Erlaubt sind PDF, Bilder (PNG, JPEG, HEIC, WebP) und E-Rechnungen (XML).");
        }

        using var buffer = new MemoryStream();
        await upload.CopyToAsync(buffer, ct);
        return (buffer.ToArray(), null);
    }

    public static string ContentType(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".pdf" => "application/pdf",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".heic" => "image/heic",
        ".webp" => "image/webp",
        ".xml" => "application/xml",
        _ => "application/octet-stream",
    };

    public async Task MarkPaidAsync(Expense expense, DateOnly paidOn, CancellationToken ct = default)
    {
        expense.Status = ExpenseStatus.Paid;
        expense.PaidDate = paidOn;
        await db.SaveChangesAsync(ct);
    }

    public Task<Expense?> FindAsync(long id, CancellationToken ct = default)
        => db.Expense.Include(e => e.ReceiptFile).FirstOrDefaultAsync(e => e.Id == id, ct);

    /// <summary>The suppliers written so far, for the suggestions of the field.</summary>
    public async Task<List<string>> SuppliersAsync(CancellationToken ct = default)
        => await db.Expense.AsNoTracking().GroupBy(e => e.Supplier).OrderByDescending(g => g.Count()).Select(g => g.Key).Take(300).ToListAsync(ct);
}
