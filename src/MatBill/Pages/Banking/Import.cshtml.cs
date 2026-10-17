using MatBill.Configuration;
using MatBill.Core.Banking;
using MatBill.Data;
using MatBill.Data.Entities;
using MatBill.Services;
using MatBill.UiKit;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MatBill.Pages.Banking;

/// <summary>
/// Imports a statement file in two steps: the file is read and shown (format, bookings, for a CSV the columns as they were recognized, changeable), then imported.
/// The file waits in the scratch folder of the data volume between the steps, named by a random token.
/// </summary>
public sealed class ImportModel(MbDbContext db, BankingService banking, ConfigStore config, NavCounts nav) : PageModelBase
{
    public sealed class MappingInput
    {
        public int? BookingDate { get; set; }

        public int? ValueDate { get; set; }

        public int? Amount { get; set; }

        public int? Debit { get; set; }

        public int? Credit { get; set; }

        public int? DebitCreditMark { get; set; }

        public int? Name { get; set; }

        public int? Iban { get; set; }

        public List<int> Purpose { get; set; } = [];

        public int? Currency { get; set; }

        public int? Reference { get; set; }

        public bool InvertSign { get; set; }

        public CsvMapping ToMapping() => new()
        {
            BookingDate = BookingDate, ValueDate = ValueDate, Amount = Amount, Debit = Debit, Credit = Credit, DebitCreditMark = DebitCreditMark, Name = Name, Iban = Iban,
            Purpose = Purpose, Currency = Currency, Reference = Reference, InvertSign = InvertSign,
        };

        public static MappingInput From(CsvMapping m) => new()
        {
            BookingDate = m.BookingDate, ValueDate = m.ValueDate, Amount = m.Amount, Debit = m.Debit, Credit = m.Credit, DebitCreditMark = m.DebitCreditMark, Name = m.Name, Iban = m.Iban,
            Purpose = m.Purpose, Currency = m.Currency, Reference = m.Reference, InvertSign = m.InvertSign,
        };
    }

    [BindProperty(SupportsGet = true, Name = "account")]
    public long? AccountId { get; set; }

    [BindProperty]
    public IFormFile? Statement { get; set; }

    [BindProperty]
    public string? Token { get; set; }

    [BindProperty]
    public string? FileName { get; set; }

    [BindProperty]
    public MappingInput Mapping { get; set; } = new();

    public List<BankAccount> Accounts { get; private set; } = [];

    public ImportPreview? Preview { get; private set; }

    public void OnGet() => Prepare();

    private void Prepare()
    {
        Head("Kontoauszug importieren", "CSV jeder Bank, CAMT.052/053 (XML) oder MT940. Doppelte Umsätze werden erkannt.", ("Banking", "/Banking"), ("Import", null));
        ViewData["ActiveHref"] = "/Banking";
        ViewData["Width"] = "wide";
        Accounts = [.. db.BankAccount.AsNoTracking().Where(a => a.IsActive).OrderBy(a => a.Name)];
    }

    public async Task<IActionResult> OnPostPreviewAsync()
    {
        Prepare();
        var account = await AccountAsync();
        if (account is null)
        {
            ModelState.AddModelError(nameof(AccountId), "Wähle das Konto (oder lege zuerst eines an).");
        }

        byte[]? bytes = null;
        if (Statement is { Length: > 0 and <= 20 * 1024 * 1024 })
        {
            using var buffer = new MemoryStream();
            await Statement.CopyToAsync(buffer);
            bytes = buffer.ToArray();
            Token = Guid.NewGuid().ToString("N");
            FileName = Path.GetFileName(Statement.FileName);
            await System.IO.File.WriteAllBytesAsync(PathOf(Token), bytes);
        }
        else if (!string.IsNullOrEmpty(Token) && Guid.TryParseExact(Token, "N", out _) && System.IO.File.Exists(PathOf(Token)))
        {
            bytes = await System.IO.File.ReadAllBytesAsync(PathOf(Token));
        }
        else
        {
            ModelState.AddModelError(nameof(Statement), "Wähle die Datei des Kontoauszugs (höchstens 20 MB).");
        }

        if (!ModelState.IsValid || bytes is null)
        {
            return Page();
        }

        // A second round keeps the columns the person chose; the first one takes the saved or guessed ones.
        Preview = banking.Read(bytes, account, Request.Form.ContainsKey("Mapping.BookingDate") ? Mapping.ToMapping() : null);
        if (Preview.Mapping is not null)
        {
            Mapping = MappingInput.From(Preview.Mapping);
        }

        return Page();
    }

    public async Task<IActionResult> OnPostImportAsync()
    {
        Prepare();
        var account = await AccountAsync();
        if (account is null || string.IsNullOrEmpty(Token) || !Guid.TryParseExact(Token, "N", out _) || !System.IO.File.Exists(PathOf(Token)))
        {
            FlashError("Die Datei ist nicht mehr da. Bitte wähle sie noch einmal.");
            return RedirectToPage(new { account = AccountId });
        }

        var bytes = await System.IO.File.ReadAllBytesAsync(PathOf(Token));
        var preview = banking.Read(bytes, account, Request.Form.ContainsKey("Mapping.BookingDate") ? Mapping.ToMapping() : null);
        if (preview.Lines.Count == 0)
        {
            Preview = preview;
            ModelState.AddModelError(string.Empty, "Die Datei enthält mit dieser Zuordnung keine Umsätze.");
            return Page();
        }

        var import = await banking.ImportAsync(account, preview.Lines, preview.Format.ToString().ToUpperInvariant(), FileName, preview.Info, preview.Table, preview.Mapping);
        System.IO.File.Delete(PathOf(Token));
        nav.Invalidate();
        Flash($"{import.Added} neue Umsätze importiert{(import.Duplicates > 0 ? $", {import.Duplicates} waren schon da" : "")}, {import.Matched} automatisch zugeordnet.");
        return RedirectToPage("/Banking/Index", new { konto = account.Id });
    }

    private async Task<BankAccount?> AccountAsync() => AccountId is { } id ? await db.BankAccount.FirstOrDefaultAsync(a => a.Id == id) : null;

    private string PathOf(string token)
    {
        var folder = Path.Combine(config.DataDir, "tmp");
        Directory.CreateDirectory(folder);
        return Path.Combine(folder, $"import-{token}.bin");
    }

    public string FormatName => Preview?.Format switch
    {
        StatementFormat.Camt => "CAMT (ISO 20022, XML)",
        StatementFormat.Mt940 => "MT940 (SWIFT)",
        _ => "CSV",
    };
}
