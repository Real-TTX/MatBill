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
