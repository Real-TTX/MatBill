# Operation

## Ports and volumes

| | Release (`docker-compose.yml`) | Development (`docker-compose.dev.yml`) |
|---|---|---|
| Web | 2505 | 2505 |
| pgAdmin | – | 2506 (`admin@matbill.example.com` / `matbill`, add the server `db`) |
| PostgreSQL | not published | 12505 |
| Volumes | `matbill-data` (`/data`), `matbill-db` | the same, plus `matbill-pgadmin` |

The services are `matbill` and `db` (PostgreSQL 17). The database and its user are made on the first start; the migrations run at start-up.

## Configuration

The settings live in `/data/config/app.json` and are edited on the pages under *Einstellungen*. How the machine is deployed can be set by
environment variables, which win over the file and are never written back:

| Variable | Meaning |
|---|---|
| `MATBILL__Database__Host` / `__Port` / `__Database` / `__Username` / `__Password` | the PostgreSQL (default `db`, 5432, `matbill`, `postgres`, `matbill`) |
| `MATBILL__Server__PublicUrl` | the address people use (links that are handed on) |
| `MATBILL__Server__TimeZone` | default `Europe/Berlin` |
| `MATBILL__Server__ForceSecureCookie` | `true` behind HTTPS |
| `MATBILL_DATA`, `MATBILL_PORT` | the data folder (`/data`) and the port (2505) |
| `MATBILL_DB_PASSWORD` (compose only) | one password for both services of `docker-compose.yml` (in `.env`); set it before the first start, Postgres keeps it in its volume |

## Sign-in, sessions, second factor

- Accounts are local (the e-mail address is the sign-in name). The first administrator is made on the first visit.
- A session is a row in the database; the cookie only names it. The keys that protect the cookie are in `/data/keys`, so a restart of the container keeps everybody signed in.
  **Lose `/data/keys` and everybody has to sign in again; saved passwords (NAS, mail server) have to be entered again.**
- TOTP (RFC 6238): *Mein Konto › Sicherheit › Einrichten*, QR code, ten recovery codes. An administrator can reset it. *Einstellungen › Sicherheit* can require it for administrators.
- Five wrong passwords lock an account for 15 minutes.

## Backup

*Einstellungen › Backup*: a **target** (a folder, or a share of a NAS over SMB on port 445), a **plan** (what, where, when) and the retention.

- *What*: *Nur das System* (the database and the settings) or *System und Dateien* (plus the stored PDFs of the issued invoices and the logo). The file is a ZIP; with a passphrase it is encrypted (AES-GCM, PBKDF2).
- *When*: hourly (every N hours), daily, weekly (weekday), monthly (day of the month), at a time of day in the time zone of the installation.
  A plan that was missed (the container was off) runs at the next start. A failed run is shown on the overview and tried again (three times, at growing intervals).
- *Retention*: keep the last N, plus one per day, week and month for a number of them. What is neither is deleted from the target after a successful run.
- *Restore*: *Wiederherstellung*: upload or pick a file, the software checks it (a newer version or a damaged file is refused, nothing is touched),
  makes a safety copy of the present state and replaces the database (and, if asked, the files and the settings). The sequences of the ids are set again.
- The database password is **not** part of a backup.
- The NAS: the account needs write access to the share; SMB 2.0.2 to 3.0.2 over TCP 445 (SMBLibrary, there is no port setting).
  *Einstellungen › Speicher (NAS)* can also keep the PDFs of the invoices and the logo on a NAS and move them over and back.

## Updating

`docker compose pull && docker compose up -d`. The migrations run at start-up; a backup from a newer version than the running one is refused.
Versions: releases `<major>.<minor>.<build>-<date>` (from `main`), `nightly-<build>-<date>` (from `dev`), `local-<date>` for a local build.

## Invoices, series and mail

- *Einstellungen › E-Mail*: the SMTP server (STARTTLS, SSL/TLS or none), sender, an optional blind copy, subject and text of an invoice mail, and a test mail.
  Without a server MatBill sends nothing; invoices can still be downloaded as PDF.
- A series runs in the background every minute (`BillingScheduler`): each due date becomes an issued invoice (next number, PDF stored). A series that missed dates
  (the container was off) catches up, one invoice per date. A failed run is written to the protocol of the series, everybody gets a notification, and it is tried
  again after 15 min, 30 min, 1 h, 2 h, 4 h; then the series waits for *Erneut ausführen*. A date is never invoiced twice (unique index per series and date).
- Automatic sending: a mail that fails is tried again after 10 min, 1 h and 6 h, then waits for *Erneut senden* on the invoice.
- Issued invoices are never changed: their PDF is kept as it was issued (GoBD). A mistake is cancelled (*Stornieren*) and written anew.
- PDFs are drawn with QuestPDF (community licence: free for companies below 1 million USD of annual revenue).
- Cancelling an issued invoice writes a cancellation invoice (own number, negative amounts) that refers to the original; send it to the customer like an invoice.

## E-invoice (ZUGFeRD, XRechnung)

- *Einstellungen › Rechnungen › ZUGFeRD-PDF* (on by default): every issued invoice is a PDF/A-3 with the CII XML embedded as `factur-x.xml` (ZUGFeRD 2.3,
  profile EN 16931). The XML alone: `/invoices/{id}/xml`, as XRechnung `/invoices/{id}/xml?xrechnung=1` (links on the invoice page).
- For a correct e-invoice the company needs name, address, VAT id or tax number, and IBAN (*Branding*); public customers need their Leitweg-ID (*Kunde › Käuferreferenz*).
- The files are written with ZUGFeRD-csharp but have not been checked against an official validator (Mustang, KoSIT). Check a sample with the validator of your
  recipient or tax advisor before relying on it. If building the e-invoice fails, the invoice is still issued with a plain PDF and the error is logged.

## Quotes and expenses

- Number ranges: *Einstellungen › Rechnungen* (`AN-{JJJJ}-{NNNN}`, `BE-{JJJJ}-{NNNN}`), validity in days, intro text and terms of a quote (`{Gueltig}` = valid until),
  the list of expense categories. *Einstellungen › E-Mail* holds subject and text of a quote mail.
- Receipts dropped on *Ausgaben* are stored like the PDFs of invoices (data volume or NAS, part of a backup "System und Dateien"). E-invoices are read automatically;
  everything else is marked *zu prüfen*.

## Banking

- **File import** (*Banking › Import*): CSV of any bank (columns, delimiter, encoding UTF-8/Windows-1252 and header row are detected; the mapping is kept per account),
  CAMT.052/053/054 and MT940. Bookings already imported are skipped (bank reference, else a fingerprint of date, amount, name and purpose).
- **Online** via [Enable Banking](https://enablebanking.com) (PSD2 aggregator for most banks in Europe). Register an application in the Enable Banking control panel,
  set its redirect URL to `<PublicUrl>/Banking/Callback`, then enter the application id and paste the private key (PEM) under *Einstellungen › Banking*.
  `MATBILL__Server__PublicUrl` must be set to the address people use. *Banking › Bank verbinden*: choose the bank, consent at the bank; the consent is requested for
  90 days (the bank may shorten it); MatBill notifies a week before it expires and when it has expired. Bookings are fetched every *SyncHours* hours (default 6) and on *Abrufen*.
  The key is stored encrypted with the data protection keys (`/data/keys`).
- **Assignment**: with *Automatisch zuordnen* on, a booking that clearly names an open invoice or expense (number in the purpose, amount, name/IBAN) is assigned and the
  document marked paid. Everything else waits under *Banking › offen* with suggestions. Partial payments are not split: assign the booking and mark the rest by hand.

## Report and export

- *Auswertung*: income, expenses, VAT and input tax per month, by document date (Soll) or by payment (Ist). It is an overview, not a tax return.
- *Export*: a ZIP for a period with the PDFs of the invoices, the receipts of the expenses, `Rechnungen.csv`, `Ausgaben.csv`, `Uebersicht.csv`, optionally
  `Kontoumsaetze.csv` and a `LIESMICH.txt`. CSV files are UTF-8 with BOM and semicolons (Excel in German).
