# MatBill: notes for working in this repository

Invoices in a document editor, recurring invoices with automatic creation and sending, an installable PWA: ASP.NET Core 10 Razor Pages, EF Core 10 on
PostgreSQL 17, one container (port **2505**) plus a database. The interface speaks **English and German** (see "Languages"); documents (invoice, quote, PDF, e-invoice,
mails to customers, export) are **German**. Code, identifiers, URLs, comments and docs (except the README) are English.
The infrastructure (setup, sign-in, TOTP, sessions, backup to a NAS, list controls) came from `../NextProject`; the look is MatDesign (`../MatDesign`,
MatUi copied into `src/MatBill/wwwroot/ui`). Before changing UI read MatDesign's `CLAUDE.md` and `docs/ADOPTING.md`.

## Commands

```powershell
./scripts/redeploy.ps1            # always: rebuild the image and redeploy the dev stack (web 2505, pgAdmin 2506, Postgres 12505)
./scripts/redeploy.ps1 -Fresh     # the same with empty volumes (setup page again)
dotnet build MatBill.slnx         # build locally BEFORE a redeploy: a compile error otherwise shows up as "docker compose failed"
$env:MATBILL_TEST_DB="Host=localhost;Port=12505;Username=postgres;Password=matbill"; dotnet test MatBill.slnx
cd tests/e2e; npm install; node flow.js http://localhost:2505    # the way through the app in Edge; SHOTS=<dir> keeps screenshots
NODE_PATH=tests/e2e/node_modules node tools/icons.js              # PNG icons of the manifest from the SVGs
dotnet ef migrations add <Name> -p src/MatBill -s src/MatBill -o Data/Migrations
```

Test changes by **rebuilding the container and redeploying the stack**. Wait for `http://localhost:2505/health`. The NAS tests need Samba
(`MATBILL_TEST_SMB`, the CI starts one). The app shell scrolls inside `.mat-main`: a full-page screenshot needs a viewport as tall as the content.

## Layout

- `src/MatBill/Localization` (TextCatalog, Languages, ClientTexts) + `Resources/Texts/*.json`: the two languages of the interface.
- `src/MatBill.Core`: no web types. `Billing/` (InvoiceMath: tax once per rate; Recurrence: dates counted from the anchor; NumberPattern: `{JJJJ}{JJ}{MM}{NNNN}`; Amount: German input), `Security/`,
  `Banking/` (StatementLine + dedup keys, SepaPurpose, CsvStatement: read/guess/map any bank CSV, CamtStatement, Mt940Statement, StatementFormats.Detect, PaymentMatcher).
- `src/MatBill`: `Pages/` (Invoices, Quotes, Recurring, Expenses, Banking, Export, Customers (tabs), Products, Notifications, Admin (incl. Company and Branding), Account, Share, Offline), `Services/` (InvoiceService, QuoteService,
  RecurringService + BillingScheduler, ExpenseService, BankingService + MatchingService, EnableBanking (client, BankSyncService, BankingScheduler), ExportService, EInvoice (ZUGFeRD),
  MailService, NumberService, InvoicePdf (QuestPDF), BrandingService, NotificationService, ShareService, DemoData, Backup/), `Data/` (MbDbContext, entities incl. `Finance.cs`, migrations),
  `UiKit/` + `Controls/` (shell, list, table, toolbar, pager, `mb-*` tag helpers), `wwwroot/` (app.css, app.js, sw.js, manifest, icons, `ui/` = MatUi).
- The editor: `Pages/Shared/_DocumentEditor.cshtml` (+ `_DocumentLine`) is the editable paper for invoices, quotes and series templates (`DocumentEditorModel`, `EditorMode`);
  `_DocumentView` is the read-only paper (issued invoices, share links, branding preview), drawn from `InvoiceDocument`, the same model as the PDF.
  The pickers never get all customers/products: `GET /api/customers` and `/api/products` (`Services/Lookup.cs`: every word in a field, paged) answer the suggestions
  and the detail search (`#mb-lookup`, a `mat-dialog--end`). The side column of the editor depends on the width of the editor (`@container`), not of the window.
- Number ranges: `NumberService.CanContinueAtAsync` / `SetNextCounterAsync` set the next counter of the current period (forward always, back only over unused numbers).
- `tests/MatBill.Tests` (Core unit tests, Integration against PostgreSQL), `tests/e2e/flow.js` (Playwright).

## Rules of the domain

- **An issued invoice never changes.** Only drafts are edited (`InvoiceService.Apply` throws otherwise). Issuing takes the number in the same transaction
  (`NumberService`: `INSERT ... ON CONFLICT DO UPDATE ... RETURNING`), fixes `TaxExempt`, stores the PDF (`PdfFileId`); `PdfAsync` serves the stored one.
  A recipient that was typed becomes a customer on save (`LinkCustomerAsync`); the invoice keeps its own copy of the address.
- **A series is a template.** `RecurringService.RunAsync` builds the invoice with `RecurringPreview.Of` (the same code as the preview), issues it and steps the series in one
  transaction; `(RecurringInvoiceId, RecurringDate)` is unique. Failures: `FailedAttempts`, `RetryAfterDate` (15 min doubling, 5 attempts), a `RecurringRun`, a notification.
  `RescheduleAsync` keeps a failed due date; `ResumeAsync` continues from today. Mails of series: `SendState` Pending/Failed, `MailService.SendPendingAsync`.
- **Cancelling** (`InvoiceService.CancelAsync`) issues a second invoice, `Kind = Cancellation`, own number, negated quantities, `CancelsInvoiceId`/`CancelsNumber`;
  the original gets `Status = Cancelled` and `CancellationInvoiceId`. A cancellation cannot be cancelled. `InvoiceDocument.Kind` (Invoice/Cancellation/Quote) drives labels in PDF and view.
- **E-invoice:** `IssuedPdfAsync` renders PDF/A-3B and embeds the CII XML (`EInvoice.Xml`, ZUGFeRD-csharp, Comfort = EN 16931, or XRechnung) as `factur-x.xml`;
  on failure it falls back to a plain PDF and logs. `EInvoice.Read` reads an XML or a PDF with embedded XML (PdfPig) back into an expense draft.
- **Quotes** have their own number range (`quote`) and become an invoice draft (`QuoteService.ToInvoiceAsync`, once). **Expenses**: one per uploaded file
  (`ExpenseService.CreateFromFilesAsync`), `NeedsReview` unless read from an e-invoice; amounts via `ExpenseService.Amounts(net, gross, rate)`.
- **Banking:** a transaction's `Amount` is signed (credit > 0). `ImportKey` (`ref:` bank reference, else `fp:` sha256 fingerprint + occurrence) is unique per account, so a
  statement imported twice adds nothing. `PaymentMatcher` scores candidates (number in purpose 60, exact amount 40, unique amount 20, skonto ≤ 3 % 15, name/IBAN 25,
  paid before the document −30); `Auto` assigns at ≥ 80 with a lead of 20. Assigning marks the invoice/expense paid, unassigning reopens it. No partial payments.
  Online: Enable Banking (JWT RS256 signed with the app's key, `kid` = app id; the key is stored protected in `BankingConfig`), sync every `SyncHours` by `BankingScheduler`.
- **Small business** (`CompanyConfig.SmallBusiness`, § 19 UStG; `PageModelBase.SmallBusiness` in pages): documents get `TaxExempt` (no tax rows, no subtotal, the note,
  category E in the e-invoice), the editor hides the tax column (`is-exempt`), products and settings leave out tax rates, expenses count **gross** with no input tax
  (dashboard, expenses, `ExportService.MonthsAsync`, `Uebersicht.csv`). Tax number and VAT id are optional (the company page warns); the dashboard only asks for name and address.
- Money: `numeric(18,2)` totals, unit price `numeric(18,4)`, quantity `numeric(18,3)`; positions rounded to cents, tax per rate on the sum (`InvoiceMath`); the editor's
  script repeats the same arithmetic. Amounts in forms are strings in German format, read with `Amount.TryParse`.
- Database: tables PascalCase, `Id` bigint, audit columns filled by `MbDbContext`, `UpdateState` soft delete (`ISoftDeletable`: User, Customer, Product, Invoice,
  RecurringInvoice), enums as text, `Token` uuid for secrets (`UserSession`, `ShareLink`, `StoredFile`, `BankConnection`). Configuration (company, invoicing, mail, banking, storage, security,
  backup) is JSON in `/data/config/app.json` (`ConfigStore`), logic in the database. No automatic EF retry.

## UI conventions

- MatUi only, flat look (MatDesign "A flat look"); app classes `mb-*` in `app.css`; no inline styles or scripts (CSP `default-src 'self'`): data becomes a custom property in
  `app.js`; the editor's data is a `<script type="application/json" id="mb-editor-data">` block (never executed). Icons from the sprite (`Ui.Icon`; own icons in `ui/mb-icons.svg`).
- Lists: toolbar above (search, filter, sort), table, pager directly below, actions below on the left. A list never scrolls sideways: `app.js` hides `mat-hide-sm` columns, the last first, while a table is wider than its frame; tooltips of row buttons open to the left. CRUD = list + separate edit page. Buttons in one row from positive
  to negative, delete spaced apart (`mb-buttons`, `mat-btn-row`). Dependent fields with `show-if` (MatUi `data-show-if`). Several controls of one kind per page: ids derive from the control id.
- Mobile: check 390 px; editor fields are 16 px on touch screens; safe areas come from MatUi tokens.
- The accent is blue `#1d4ed8` (`[data-app="matbill"]` in `app.css`; theme-color metas follow `--mat-glass`). The document colour of a company is `CompanyConfig.AccentColor`.
- **WYSIWYG:** the paper (`.mb-paper` in `app.css`, block "The paper is the PDF") uses the font (Lato, `wwwroot/fonts`, OFL) and the sizes (pt) and distances (mm) of `InvoicePdf`; change both together.
  The head has three layouts (`CompanyConfig.Layout`, `DocumentLayouts`), drawn the same in `InvoicePdf.Head` and the `mb-doc__head--*` classes. `InvoicePdf.RenderImages` gives the pages as PNG for comparing.
- PWA: `sw.js` is registered as `/sw.js?v=<version>-<sprite hash>` (a build is a new worker). Static files cache-first, pages network-first with a kept copy marked
  `data-offline-copy`; never cached: POST, `/api`, `/Account`, `/s/`, PDFs. Logout and the sign-in page clear the kept pages.
- Formatting: request culture invariant; text a person reads goes through `Fmt` (the UI language: de-DE or en-GB), documents through `DocFmt` (always de-DE).
  Razor encodes umlauts as entities: tests compare decoded text.

## Languages

- **No interface text in code.** Every text a person reads is a key in `src/MatBill/Resources/Texts/<Area>.json` (embedded): `{ "Area.Key": { "en": "…", "de": "…" } }`.
  The part before the first dot names the file; a key lives in one file; placeholders `{0}` are the same in both languages. Shared words: `Common.json`.
- Use: Razor `@L["Key"]` (injected in `_ViewImports`), page models `L["Key", arg]` (`PageModelBase`), elsewhere `Text.T("Key", args)`; data annotations take keys
  (`[Display(Name = "Area.Key")]`, `ErrorMessage = "Area.Key"`); `app.js` uses `t('Js.Key')` (keys `Js.*` are written into the page as JSON by `ClientTexts`).
  Core (no web types) returns keys (`NumberPattern.Problem`, `MatchReason`, `StatementProblem`) and the pages translate them.
- The language: the account (`User.Language`, claim `mb:lang`) → the cookie of the sign-in page (`.MatBill.Language`) → Accept-Language → English
  (`Localization/Languages.cs`, `AccountLanguageProvider`, after the authentication). `POST /language` switches it; *My account* has the select.
  Only the UI culture changes; the request culture stays invariant (forms and query strings are read the same way in both languages).
- **Documents stay German**: the paper in `_DocumentView`/`_DocumentEditor`/`_DocumentLine`, `InvoicePdf`, `EInvoice`, mail templates, `ExportService`, `DemoData`.
  What only the writer sees on the paper (placeholders, hints) is interface and translates.
- Stored texts keep the language they were written in: notifications are written per recipient in their language (`NotifyAllAsync(tone, () => (Text.T(…), …))`),
  activity entries in the language of whoever acted, scheduler texts in the default language (English).
- Tests: `TextsTests` checks both languages, the file of each key, the placeholders, that every key in the code exists, and that no German (umlauts) is left
  outside the allowed document files. Integration tests read German (`AppFixture`: German browser, German accounts); `LanguageTests` cover English and the switch;
  the e2e has an English pass that looks for German interface words.

## Gotchas

- Shell strings with backticks, `$`, apostrophes get mangled by the Bash tool: write files with the editor tools.
- A `<partial model="...">` attribute cannot hold a C# object initialiser with quotes: build the value in a code block.
- An editor page binds `Id` with `SupportsGet`; a new form posts `id=0`, which the property turns into null.
- `MatUi` transitions border colours: wait a moment before a screenshot after switching the editor view.
- The dev compose has an explicit subnet (`10.253.55.0/24`).
- Don't insert `\n` with sed into C# files: it becomes a real line break inside string literals.
- A PageModel property named `File` hides `PageModel.File(...)`; the import page calls its upload `Statement`.
- Fold names before comparing (`PaymentMatcher.Fold`: ä→ae, ß→ss): banks write "Mueller" for "Müller".

## Git

Branches: `main` (release), `dev` (development). Commit and push **under the user's name** only: no `Co-Authored-By` trailer, no "Generated with" line.
Versions: release `<major>.<minor>.<build>-<date>`, dev `nightly-<build>-<date>`, local `local-<date>` (`Directory.Build.props`, set by the GitHub action).
