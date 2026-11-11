using System.IO.Compression;
using System.Security.Cryptography;
using MatBill.Configuration;
using MatBill.Core.Billing;
using MatBill.Core.Import;
using MatBill.Data;
using MatBill.Data.Entities;
using MatBill.Localization;
using MatBill.Storage;
using Microsoft.EntityFrameworkCore;

namespace MatBill.Services;

/// <summary>What an import found (a check) or did (a run): counts, the sums of the export by year, the problems that stop it and what it noticed.</summary>
public sealed class LegacyImportResult
{
    public bool Applied { get; set; }

    public int CustomersNew { get; set; }

    public int CustomersKnown { get; set; }

    public int ProductsNew { get; set; }

    public int ProductsKnown { get; set; }

    public int InvoicesNew { get; set; }

    public int InvoicesKnown { get; set; }

    public int DraftsSkipped { get; set; }

    public int ExpensesNew { get; set; }

    public int ExpensesKnown { get; set; }

    public int ExpensesToReview { get; set; }

    public List<string> CategoriesAdded { get; } = [];

    /// <summary>Why nothing was (or would be) imported, in the language of the person.</summary>
    public List<string> Problems { get; } = [];

    /// <summary>What the import noticed and marked (an invoice whose date does not fit, receipts to review), in the language of the person.</summary>
    public List<string> Notes { get; } = [];

    /// <summary>Per year: the paid invoices (gross, by invoice date) and the expenses (gross, by receipt date) of the export, to compare with the old program.</summary>
    public SortedDictionary<int, (decimal Invoices, decimal Expenses)> Years { get; } = [];

    /// <summary>The pattern and the next number of the invoice range after the import (or as it would be).</summary>
    public string? NumberPattern { get; set; }

    public string? NextInvoiceNumber { get; set; }
}

/// <summary>
/// Takes over the data of an earlier invoice program (for now the export of Invoiz, <see cref="InvoizExport"/>), so that every year can be exported from MatBill
/// with all its invoices and receipts. The rules, as the tax office wants them (GoBD):
/// <list type="bullet">
/// <item>Invoices come as they were issued: the original number, dates, positions and payments, and the <b>original PDF</b> stored byte for byte (its SHA-256
/// in <see cref="StoredFile"/>). MatBill draws no PDF of its own for them and gives no number. A draft of the old program is not an invoice and stays out.</item>
/// <item>Each record says where it comes from (<c>ImportSource</c>, <c>ImportRef</c>), every invoice and receipt is written to the activity log with its origin
/// and the hash of its file. An import run twice adds nothing (customers by number or name, products by item number or name, invoices and receipts by their id
/// in the old program).</item>
/// <item>All or nothing: a check reads everything first; one problem (a missing PDF, totals that do not add up, a number that is taken) stops the whole import.</item>
/// <item>What looks wrong is taken over as it is and marked (<see cref="Invoice.ImportNote"/>, <see cref="Expense.NeedsReview"/>), never corrected.</item>
/// </list>
/// </summary>
public sealed class LegacyImportService(MbDbContext db, ConfigStore config, NumberService numbers, FileStoreService files, ActivityLogger activity,
    TimeProvider clock)
{
    public const long MaxZipBytes = 100L * 1024 * 1024;

    private const long MaxUnpackedBytes = 500L * 1024 * 1024;

    private const int MaxEntries = 5000;

    /// <summary>An invoice dated more than this before one with a lower number is marked.</summary>
    private const int OutOfOrderDays = 60;

    /// <summary>Reads the ZIP of an export and checks it; with <paramref name="apply"/> and no problem it imports it. <paramref name="continueNumbers"/>: the invoice range
    /// goes on after the highest number taken over, in its form ("{NNNN}" after "0069").</summary>
    public async Task<LegacyImportResult> RunAsync(Stream zip, bool apply, bool continueNumbers, CancellationToken ct = default)
    {
        var result = new LegacyImportResult();
        var (entries, zipHash) = await ReadZipAsync(zip, result, ct);
        if (entries is null)
        {
            return result;
        }

        var data = InvoizExport.Read(entries);
        result.Problems.AddRange(data.Problems.Select(T));
        if (data.Customers.Count + data.Products.Count + data.Invoices.Count + data.Expenses.Count == 0 && result.Problems.Count == 0)
        {
            result.Problems.Add(Text.T("Import.Problem.Empty"));
        }

        foreach (var i in data.Invoices.Where(i => !i.IsDraft && i.Open == 0))
        {
            var y = result.Years.GetValueOrDefault(i.Date.Year);
            result.Years[i.Date.Year] = (y.Invoices + i.Gross, y.Expenses);
        }

        foreach (var e in data.Expenses)
        {
            var y = result.Years.GetValueOrDefault(e.Date.Year);
            result.Years[e.Date.Year] = (y.Invoices, y.Expenses + e.Gross);
        }

        var plan = await PlanAsync(data, result, ct);
        var form = InvoizExport.NumberForm(data.Invoices.Where(i => !i.IsDraft).Select(i => i.Number));
        var pattern = config.Current.Invoicing.InvoiceNumberPattern;
        var today = Fmt.Today(clock);
        if (continueNumbers && data.Invoices.Any(i => !i.IsDraft))
        {
            if (form is null)
            {
                result.Notes.Add(Text.T("Import.Note.NumbersNoForm"));
            }
            else
            {
                pattern = form.Value.Pattern;
            }
        }

        if (!apply || result.Problems.Count > 0)
        {
            result.NumberPattern = pattern;
            result.NextInvoiceNumber = form is { } f && continueNumbers && pattern == f.Pattern
                ? NumberPattern.Format(pattern, today, Math.Max(f.Last + 1, await numbers.NextCounterAsync(NumberService.InvoiceRange, pattern, today, ct)))
                : await numbers.PeekAsync(NumberService.InvoiceRange, pattern, today, ct);
            return result;
        }

        await ApplyAsync(plan, ct);
        if (result.CategoriesAdded.Count > 0)
        {
            config.Update(c =>
            {
                var list = c.Invoicing.ExpenseCategories;
                // the new ones go before "Sonstiges" when that closes the list
                var at = list.Count > 0 && list[^1] == "Sonstiges" ? list.Count - 1 : list.Count;
                list.InsertRange(at, result.CategoriesAdded);
            });
        }

        if (continueNumbers && form is { } found && pattern == found.Pattern)
        {
            if (config.Current.Invoicing.InvoiceNumberPattern != pattern)
            {
                var before = config.Current.Invoicing.InvoiceNumberPattern;
                config.Update(c => c.Invoicing.InvoiceNumberPattern = pattern);
                result.Notes.Add(Text.T("Import.Note.PatternChanged", before, pattern));
            }

            var next = found.Last + 1;
            if (await numbers.NextCounterAsync(NumberService.InvoiceRange, pattern, today, ct) < next)
            {
                await numbers.SetNextCounterAsync(NumberService.InvoiceRange, pattern, today, next, ct);
            }
        }

        result.NumberPattern = config.Current.Invoicing.InvoiceNumberPattern;
        result.NextInvoiceNumber = await numbers.PeekInvoiceNumberAsync(today, ct);
        result.Applied = true;
        await activity.LogAsync(Text.T("Import.Activity.Done"), null, null, Text.T("Import.Activity.DoneSummary", InvoizExport.Source, result.CustomersNew, result.ProductsNew,
            result.InvoicesNew, result.ExpensesNew, zipHash));
        return result;
    }

    private static string T(ImportProblem p) => Text.T(p.Key, p.Args);

    /// <summary>The files of the ZIP (with limits against a ZIP bomb) and its SHA-256.</summary>
    private static async Task<(Dictionary<string, byte[]>? Entries, string Hash)> ReadZipAsync(Stream zip, LegacyImportResult result, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        await zip.CopyToAsync(buffer, ct);
        var hash = Convert.ToHexString(SHA256.HashData(buffer.ToArray())).ToLowerInvariant();
        buffer.Position = 0;
        var entries = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var archive = new ZipArchive(buffer, ZipArchiveMode.Read);
            if (archive.Entries.Count > MaxEntries)
            {
                result.Problems.Add(Text.T("Import.Problem.TooLarge"));
                return (null, hash);
            }

            long total = 0;
            foreach (var entry in archive.Entries.Where(e => !e.FullName.EndsWith('/') && e.Length > 0))
            {
                total += entry.Length;
                if (total > MaxUnpackedBytes)
                {
                    result.Problems.Add(Text.T("Import.Problem.TooLarge"));
                    return (null, hash);
                }

                await using var stream = await entry.OpenAsync(ct);
                using var bytes = new MemoryStream((int)entry.Length);
                await stream.CopyToAsync(bytes, ct);
                entries[entry.FullName.Replace('\\', '/')] = bytes.ToArray();
            }
        }
        catch (InvalidDataException)
        {
            result.Problems.Add(Text.T("Import.Problem.NotAZip"));
            return (null, hash);
        }

        return (entries, hash);
    }

    private sealed class Plan
    {
        public Dictionary<string, Customer> CustomerBySource { get; } = [];

        public List<Customer> NewCustomers { get; } = [];

        public Dictionary<string, Product> ProductByNumber { get; } = new(StringComparer.OrdinalIgnoreCase);

        public List<Product> NewProducts { get; } = [];

        public List<(LegacyInvoice Source, Invoice Invoice, string PdfPath, byte[] Pdf)> NewInvoices { get; } = [];

        public List<(LegacyExpense Source, Expense Expense, byte[] Receipt)> NewExpenses { get; } = [];
    }

    /// <summary>Checks everything and builds what would be saved (nothing is added to the context yet).</summary>
    private async Task<Plan> PlanAsync(LegacyData data, LegacyImportResult result, CancellationToken ct)
    {
        var plan = new Plan();
        var small = config.Current.Company.SmallBusiness;

        // customers: by number, else by name; a number held by someone else stops the import
        var customers = await db.Customer.ToListAsync(ct);
        var takenNumbers = (await db.Customer.IgnoreQueryFilters().Select(c => c.Number).ToListAsync(ct)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var c in data.Customers)
        {
            var known = customers.FirstOrDefault(x => string.Equals(x.Number, c.Number, StringComparison.OrdinalIgnoreCase));
            if (known is not null && !SameName(known.Name, c.Name))
            {
                result.Problems.Add(Text.T("Import.Problem.CustomerNumberTaken", c.Number, c.Name, known.Name));
                continue;
            }

            known ??= customers.FirstOrDefault(x => SameName(x.Name, c.Name));
            if (known is not null)
            {
                result.CustomersKnown++;
                plan.CustomerBySource[c.SourceId] = known;
                continue;
            }

            if (takenNumbers.Contains(c.Number))
            {
                result.Problems.Add(Text.T("Import.Problem.CustomerNumberTaken", c.Number, c.Name, "—"));
                continue;
            }

            var customer = new Customer
            {
                Number = c.Number,
                Kind = c.IsPerson ? CustomerKind.Person : CustomerKind.Company,
                Name = c.Name,
                Email = c.Email,
                Phone = c.Phone,
                Street = c.Street,
                PostalCode = c.PostalCode,
                City = c.City,
                Country = c.Country,
                Note = c.Website is null ? null : "Website: " + c.Website,
            };
            takenNumbers.Add(c.Number);
            plan.NewCustomers.Add(customer);
            plan.CustomerBySource[c.SourceId] = customer;
            result.CustomersNew++;
        }

        // products: by item number, else by name
        var products = await db.Product.ToListAsync(ct);
        foreach (var p in data.Products)
        {
            var known = (p.Number.Length > 0 ? products.FirstOrDefault(x => string.Equals(x.Sku, p.Number, StringComparison.OrdinalIgnoreCase)) : null)
                ?? products.FirstOrDefault(x => SameName(x.Name, p.Name));
            if (known is not null)
            {
                result.ProductsKnown++;
            }
            else
            {
                known = new Product
                {
                    Name = p.Name,
                    Sku = p.Number.Length > 0 ? p.Number : null,
                    UnitPrice = p.UnitPrice,
                    TaxRate = small ? 0m : config.Current.Invoicing.DefaultTaxRate,
                    Kind = ProductKind.Service,
                };
                products.Add(known);
                plan.NewProducts.Add(known);
                result.ProductsNew++;
            }

            if (p.Number.Length > 0)
            {
                plan.ProductByNumber.TryAdd(p.Number, known);
            }
        }

        // invoices: by their id in the old program; the number must be free
        var issued = data.Invoices.Where(i => !i.IsDraft).ToList();
        result.DraftsSkipped = data.Invoices.Count - issued.Count;
        var knownRefs = (await db.Invoice.Where(i => i.ImportSource == InvoizExport.Source && i.ImportRef != null).Select(i => i.ImportRef!).ToListAsync(ct)).ToHashSet();
        var sourceNumbers = issued.Select(i => i.Number).ToList();
        var usedNumbers = (await db.Invoice.IgnoreQueryFilters().Where(i => i.Number != null && sourceNumbers.Contains(i.Number)).Select(i => i.Number!).ToListAsync(ct)).ToHashSet();
        var outOfOrder = InvoizExport.OutOfOrder(issued, OutOfOrderDays).ToDictionary(x => x.Invoice.SourceId, x => x.Before);
        foreach (var dup in issued.GroupBy(i => i.Number).Where(g => g.Count() > 1))
        {
            result.Problems.Add(Text.T("Import.Problem.InvoiceNumberTwice", dup.Key));
        }

        foreach (var source in issued.OrderBy(i => i.Number.Length).ThenBy(i => i.Number, StringComparer.Ordinal))
        {
            if (knownRefs.Contains(source.SourceId))
            {
                result.InvoicesKnown++;
                continue;
            }

            var problems = result.Problems.Count;
            if (usedNumbers.Contains(source.Number))
            {
                result.Problems.Add(Text.T("Import.Problem.InvoiceNumberTaken", source.Number));
            }

            var pdf = data.InvoicePdf(source.Number);
            if (pdf is null)
            {
                result.Problems.Add(Text.T("Import.Problem.InvoicePdfMissing", source.Number));
            }

            if (!plan.CustomerBySource.TryGetValue(source.CustomerSourceId, out var customer))
            {
                result.Problems.Add(Text.T("Import.Problem.InvoiceCustomerMissing", source.Number, source.CustomerName));
            }

            if (source.Lines.Count == 0)
            {
                result.Problems.Add(Text.T("Import.Problem.InvoiceNoLines", source.Number));
            }

            if (source.Paid + source.Open != source.Gross)
            {
                result.Problems.Add(Text.T("Import.Problem.InvoicePayments", source.Number, Fmt.Money(source.Gross), Fmt.Money(source.Paid), Fmt.Money(source.Open)));
            }

            if (result.Problems.Count > problems)
            {
                continue;
            }

            var invoice = Build(source, customer!, plan);
            if (invoice.NetTotal != source.Net || invoice.GrossTotal != source.Gross)
            {
                result.Problems.Add(Text.T("Import.Problem.InvoiceTotals", source.Number, Fmt.Money(source.Gross), Fmt.Money(invoice.GrossTotal)));
                continue;
            }

            if (outOfOrder.TryGetValue(source.SourceId, out var before))
            {
                invoice.ImportNote = Text.T("Import.Note.DateOutOfOrder", Fmt.Date(source.Date), before.Number, Fmt.Date(before.Date));
                result.Notes.Add(Text.T("Import.Note.Invoice", source.Number, invoice.ImportNote));
            }

            plan.NewInvoices.Add((source, invoice, pdf!.Value.Path, pdf.Value.Bytes));
            result.InvoicesNew++;
        }

        // expenses: by their id in the old program (or the hash of the receipt); the category must exist or is added
        var knownExpenses = (await db.Expense.Where(e => e.ImportSource == InvoizExport.Source && e.ImportRef != null).Select(e => e.ImportRef!).ToListAsync(ct)).ToHashSet();
        var expenseNumbers = (await db.Expense.IgnoreQueryFilters().Select(e => e.Number).ToListAsync(ct)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var categories = config.Current.Invoicing.ExpenseCategories;
        foreach (var source in data.Expenses.OrderBy(e => e.Date).ThenBy(e => e.Row))
        {
            var receipt = data.Receipt(source.File);
            var reference = source.SourceId ?? (receipt is null ? null : "sha256:" + Convert.ToHexString(SHA256.HashData(receipt)).ToLowerInvariant());
            if (reference is not null && knownExpenses.Contains(reference))
            {
                result.ExpensesKnown++;
                continue;
            }

            if (receipt is null)
            {
                result.Problems.Add(Text.T("Import.Problem.ReceiptMissing", source.Row, source.File));
                continue;
            }

            if (source.Number is { } number && expenseNumbers.Contains(number))
            {
                result.Problems.Add(Text.T("Import.Problem.ExpenseNumberTaken", source.Row, number));
                continue;
            }

            if (source.Net + source.Tax != source.Gross)
            {
                result.Problems.Add(Text.T("Import.Problem.ExpenseAmounts", source.Row, Fmt.Money(source.Net), Fmt.Money(source.Tax), Fmt.Money(source.Gross)));
                continue;
            }

            if (!categories.Contains(source.Category) && !result.CategoriesAdded.Contains(source.Category))
            {
                result.CategoriesAdded.Add(source.Category);
            }

            var expense = new Expense
            {
                Number = source.Number ?? "",
                ExpenseDate = source.Date,
                Supplier = source.Supplier,
                SupplierInvoiceNumber = source.SupplierInvoiceNumber,
                Category = source.Category,
                Description = source.Description,
                NetAmount = source.Net,
                TaxRate = source.TaxRate,
                TaxAmount = source.Tax,
                GrossAmount = source.Gross,
                Status = source.Paid ? ExpenseStatus.Paid : ExpenseStatus.Open,
                PaidDate = source.Paid ? source.PaidDate : null,
                FromEInvoice = source.FromEInvoice,
                NeedsReview = source.NeedsReview,
                Note = source.Note,
                ImportSource = InvoizExport.Source,
                ImportRef = reference,
            };
            if (expense.NeedsReview)
            {
                result.ExpensesToReview++;
                result.Notes.Add(Text.T("Import.Note.ExpenseReview", Fmt.Date(source.Date), source.Supplier, Fmt.Money(source.Gross), source.Note ?? ""));
            }

            plan.NewExpenses.Add((source, expense, receipt));
            result.ExpensesNew++;
        }

        return plan;
    }

    private static bool SameName(string a, string b) => string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>The invoice as it was issued: positions as they stood (a discount is in the price), the address of its customer, paid or open as the payments say.</summary>
    private Invoice Build(LegacyInvoice source, Customer customer, Plan plan)
    {
        var exempt = source.Net == source.Gross;
        var rate = exempt || source.Net == 0 ? 0m : Math.Round((source.Gross - source.Net) / source.Net * 100m, 0, MidpointRounding.AwayFromZero);
        var invoice = new Invoice
        {
            Kind = InvoiceKind.Invoice,
            Number = source.Number,
            Title = source.Title,
            InvoiceDate = source.Date,
            ServiceFrom = source.ServiceFrom ?? source.Date,
            ServiceTo = source.ServiceTo,
            DueDate = source.DueDate ?? source.Date,
            PaymentTermDays = Math.Clamp((source.DueDate ?? source.Date).DayNumber - source.Date.DayNumber, 0, 365),
            TaxExempt = exempt,
            IssuedDate = source.Date.ToDateTime(new TimeOnly(12, 0), DateTimeKind.Utc),
            ImportSource = InvoizExport.Source,
            ImportRef = source.SourceId,
        };
        InvoiceService.FillRecipient(invoice, customer);
        if (customer.Id == 0)
        {
            invoice.CustomerId = null;
        }

        var position = 1;
        foreach (var l in source.Lines)
        {
            var price = InvoiceMath.LineNet(l.Quantity, l.UnitPrice) == l.Net ? l.UnitPrice : l.Quantity == 0 ? l.UnitPrice : Math.Round(l.Net / l.Quantity, 4, MidpointRounding.AwayFromZero);
            var description = l.DiscountPercent == 0 ? l.Description
                : string.Join("\n", new[] { l.Description, $"Rabatt {Amount.Quantity(l.DiscountPercent)} % auf {DocFmt.Money(l.UnitPrice)}" }.Where(s => !string.IsNullOrWhiteSpace(s)));
            invoice.Lines.Add(new InvoiceLine
            {
                Position = position++,
                Name = l.Name,
                Description = description,
                Quantity = l.Quantity,
                Unit = l.Unit,
                UnitPrice = price,
                TaxRate = rate,
                ProductId = l.ProductNumber is { } n && plan.ProductByNumber.TryGetValue(n, out var p) && p.Id > 0 ? p.Id : null,
            });
        }

        InvoiceService.Recalculate(invoice);
        if (source.Open == 0 && source.Gross != 0 || source.Gross == 0)
        {
            invoice.Status = InvoiceStatus.Paid;
            invoice.PaidDate = source.PaidDate ?? source.Date;
        }
        else
        {
            invoice.Status = InvoiceStatus.Open;
        }

        return invoice;
    }

    /// <summary>Saves the plan in one transaction (the files are stored first; a failure leaves files without rows, never rows without files) and logs every record.</summary>
    private async Task ApplyAsync(Plan plan, CancellationToken ct)
    {
        await using (var transaction = await db.Database.BeginTransactionAsync(ct))
        {
            db.Customer.AddRange(plan.NewCustomers);
            db.Product.AddRange(plan.NewProducts);
            await db.SaveChangesAsync(ct);

            foreach (var (source, invoice, path, pdf) in plan.NewInvoices)
            {
                // the customer and the products of this import have their ids now
                var customer = plan.CustomerBySource[source.CustomerSourceId];
                invoice.CustomerId = customer.Id;
                invoice.Customer = customer;
                for (var k = 0; k < source.Lines.Count; k++)
                {
                    if (source.Lines[k].ProductNumber is { } n && plan.ProductByNumber.TryGetValue(n, out var p))
                    {
                        invoice.Lines[k].ProductId = p.Id;
                    }
                }

                invoice.PdfFile = await files.SaveAsync(pdf, Path.GetFileName(path), "application/pdf", ct);
                db.Invoice.Add(invoice);
            }

            await db.SaveChangesAsync(ct);

            foreach (var (source, expense, receipt) in plan.NewExpenses)
            {
                if (expense.Number.Length == 0)
                {
                    expense.Number = await numbers.NextExpenseNumberAsync(expense.ExpenseDate, ct);
                }

                expense.ReceiptFile = await files.SaveAsync(receipt, Path.GetFileName(source.File), ExpenseService.ContentType(source.File), ct);
                db.Expense.Add(expense);
                await db.SaveChangesAsync(ct);
            }

            await transaction.CommitAsync(ct);
        }

        foreach (var (source, invoice, path, _) in plan.NewInvoices)
        {
            await activity.LogAsync(Text.T("Import.Activity.Invoice"), nameof(Invoice), invoice.Id, Text.T("Import.Activity.InvoiceSummary",
                invoice.Number ?? "", invoice.RecipientName ?? "", Fmt.Money(invoice.GrossTotal), InvoizExport.Source, source.SourceId, path, invoice.PdfFile?.Sha256 ?? ""));
        }

        foreach (var (source, expense, _) in plan.NewExpenses)
        {
            await activity.LogAsync(Text.T("Import.Activity.Expense"), nameof(Expense), expense.Id, Text.T("Import.Activity.ExpenseSummary",
                expense.Number, expense.Supplier, Fmt.Money(expense.GrossAmount), InvoizExport.Source, source.SourceId ?? "—", source.File, expense.ReceiptFile?.Sha256 ?? ""));
        }

        if (plan.NewCustomers.Count + plan.NewProducts.Count > 0)
        {
            await activity.LogAsync(Text.T("Import.Activity.Masters"), null, null, Text.T("Import.Activity.MastersSummary", plan.NewCustomers.Count, plan.NewProducts.Count, InvoizExport.Source,
                string.Join(", ", plan.NewCustomers.Select(c => $"{c.Number} {c.Name}"))));
        }
    }
}
