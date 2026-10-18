<div align="center">

<img src="src/MatBill/wwwroot/img/icon-192.png" width="96" alt="MatBill" />

# MatBill

**Rechnungen und Finanzen für kleine Firmen – im Browser, auf dem eigenen Server.**

Rechnungen direkt auf dem A4-Papier schreiben, wiederkehrend verschicken, Angebote, Ausgaben mit Belegen,
Banking mit automatischer Zuordnung, E-Rechnung (ZUGFeRD/XRechnung) und Export für das Finanzamt.
Ein Container plus PostgreSQL, keine Cloud, installierbar als App.

</div>

![Die Übersicht mit offenen Beträgen, Umsatz, Ergebnis und Aufgaben](docs/images/dashboard.png)

---

## Worum es geht

Rechnungsprogramme sind entweder Formularwüsten oder Cloud-Abos, die die eigenen Zahlen auf fremden Servern halten.
MatBill macht das Dokument selbst zur Oberfläche: Man schreibt die Rechnung dort, wo sie auf dem Papier steht, und sieht dabei
genau das, was der Kunde bekommt. Drumherum steht, was eine kleine Firma sonst noch braucht – Angebote, Serien, Belege, Kontoumsätze
und ein Export für Steuerberater oder Finanzamt – auf einem Server im eigenen Netz.

## Auf einen Blick

**Schreiben**
- **Dokument-Editor auf A4:** Empfänger per Autocomplete aus den Kunden, Positionen per Produktsuche, Texte durch Anklicken
- Summen und Umsatzsteuer **je Steuersatz** rechnen sich beim Tippen; Kleinunternehmer-Regelung (§ 19 UStG) auf Knopfdruck
- Umschalten auf eine **normale Formularansicht** mit denselben Feldern – die Wahl bleibt pro Person gemerkt
- **Entwurf → Ausstellen:** lückenloser Nummernkreis (`RE-{JJJJ}-{NNNN}`), danach ist die Rechnung unveränderlich (GoBD)
- **Storno** als Stornorechnung mit eigener Nummer und negativen Beträgen; das Original bleibt erhalten
- **Angebote** im selben Editor, mit Gültigkeit, Annahme/Ablehnung und Umwandlung in eine Rechnung

**Versenden und wiederholen**
- PDF im Layout des Editors (Fensteranschrift nach DIN 5008), Versand per SMTP mit Protokoll jeder Zustellung
- **E-Rechnung:** jede Rechnung ist ein ZUGFeRD-PDF (2.3, Profil EN 16931, PDF/A-3); die XML gibt es einzeln und als **XRechnung**
- **Wiederkehrende Rechnungen:** wöchentlich bis jährlich, mit Start, Ende oder unbegrenzt, automatischer Erstellung und Versand
- Pausieren, fortsetzen, bearbeiten, beenden; Fehler werden erkannt, erneut versucht und gemeldet
- **Freigabelinks** für Kunden, zeitlich begrenzt und optional mit Passwort

**Finanzen**
- **Ausgaben:** Belege per **Drag & Drop** hochladen – E-Rechnungen von Lieferanten füllen sich selbst aus
- **Banking:** universeller **CSV-Import** jeder Bank, dazu **CAMT** und **MT940**; online über **Enable Banking** (PSD2)
- **Automatische Zuordnung** von Zahlungen zu Rechnungen und Ausgaben über Nummer, Betrag (auch mit Skonto), Name und IBAN
- **Auswertung** je Monat nach Soll oder Ist: Einnahmen, Ausgaben, Umsatzsteuer, Vorsteuer, Zahllast
- **Export als ZIP** mit allen Rechnungen, Belegen und CSV-Listen für Finanzamt oder Steuerberater

**Betrieb**
- Lokale Anmeldung mit **TOTP** (Authenticator-App), Rollen Administrator, Benutzer und Anonym (Freigabelink)
- Sitzungen überstehen einen Neustart des Containers
- **Backup** nach Zeitplan direkt auf ein **NAS** (SMB) oder in einen Ordner, mit Retention, optional verschlüsselt
- **Installierbar (PWA)**, für das Handy gemacht, mit Offline-Ansicht der zuletzt geöffneten Seiten; hell und dunkel

## Screenshots

### Die Rechnung ist die Oberfläche

| Dokument | Formular |
|---|---|
| ![Der Editor als A4-Dokument](docs/images/editor-document.png) | ![Derselbe Editor als Formular](docs/images/editor-form.png) |

Jeder Wert wird dort bearbeitet, wo er auf dem Papier steht; die Felder sehen aus wie Text, bis man sie anklickt.
Wer lieber ein Formular ausfüllt, schaltet um – es sind dieselben Felder.

### Ausgestellt, storniert, als E-Rechnung

| Ausgestellte Rechnung | Stornorechnung |
|---|---|
| ![Eine ausgestellte Rechnung](docs/images/invoice-issued.png) | ![Eine Stornorechnung mit negativen Beträgen](docs/images/storno.png) |

Eine ausgestellte Rechnung ändert sich nie mehr. Ein Fehler wird storniert: Es entsteht eine Stornorechnung mit eigener Nummer,
die auf das Original verweist. ZUGFeRD-XML und XRechnung liegen direkt an der Rechnung.

### Wiederkehrende Rechnungen

| Serie | Übersicht |
|---|---|
| ![Eine Serie mit den nächsten Terminen](docs/images/series.png) | ![Die Liste der Serien](docs/images/recurring.png) |

„Wiederholung einrichten“ macht aus einer Rechnung eine Vorlage. Zu jedem Termin entsteht eine eigenständige Rechnung mit
eigener Nummer, Datum und Zahlungsziel; Änderungen an der Vorlage gelten nur für künftige Rechnungen.

### Angebote

| Angebot | Liste |
|---|---|
| ![Ein ausgestelltes Angebot](docs/images/quote.png) | ![Die Liste der Angebote](docs/images/quotes.png) |

Derselbe Editor mit eigenem Nummernkreis und Gültigkeit. Ein angenommenes Angebot wird mit einem Klick zur Rechnung.

### Ausgaben per Drag & Drop

| Liste mit Ablage | Beleg prüfen |
|---|---|
| ![Ausgaben mit der Ablage für Belege](docs/images/expenses.png) | ![Eine Ausgabe neben ihrem Beleg](docs/images/expense.png) |

Belege auf die Liste ziehen, einfügen oder auswählen: Jede Datei wird zu einer Ausgabe. E-Rechnungen werden vollständig ausgelesen,
alles andere ist „zu prüfen“ und wird nacheinander vervollständigt – der Beleg steht daneben.

### Banking mit automatischer Zuordnung

| Konten und Umsätze | Ein Umsatz | CSV-Import |
|---|---|---|
| ![Konten und Umsätze](docs/images/banking.png) | ![Vorschläge für einen Umsatz](docs/images/bank-transaction.png) | ![Spalten eines CSV-Imports](docs/images/bank-import.png) |

Kontoauszüge kommen als CSV (jede Bank, die Spalten werden erkannt und pro Konto gemerkt), CAMT oder MT940 – oder online über
Enable Banking. Eindeutige Zahlungen werden automatisch zugeordnet und die Rechnung als bezahlt markiert; für den Rest gibt es Vorschläge.

### Auswertung und Export

![Einnahmen, Ausgaben und Umsatzsteuer je Monat](docs/images/report.png)

Ein Jahr auf einen Blick, nach Belegdatum (Soll) oder Zahlung (Ist). Der Export packt alle Rechnungen und Belege eines Zeitraums
mit `Rechnungen.csv`, `Ausgaben.csv`, `Uebersicht.csv` und `Kontoumsaetze.csv` in ein ZIP.

### Stammdaten, Branding, Einstellungen

| Kunden | Branding | Backup |
|---|---|---|
| ![Kunden](docs/images/customers.png) | ![Branding mit Vorschau](docs/images/branding.png) | ![Backup](docs/images/backup.png) |

### Dunkel und auf dem Handy

| Dunkel | Editor dunkel |
|---|---|
| ![Die Übersicht im dunklen Modus](docs/images/dark-dashboard.png) | ![Der Editor im dunklen Modus](docs/images/dark-editor.png) |

| Übersicht | Editor | Rechnung | Banking | Ausgaben |
|---|---|---|---|---|
| ![](docs/images/mobile-dashboard.png) | ![](docs/images/mobile-editor.png) | ![](docs/images/mobile-invoice.png) | ![](docs/images/mobile-banking.png) | ![](docs/images/mobile-expenses.png) |

Installierbar auf iOS und Android, mit Safe Areas, Statusleiste in der Farbe der App und Offline-Ansicht der zuletzt geöffneten Seiten.

## Schnellstart

Fertige Images liegen in der GitHub Container Registry:

| Tag | Gebaut aus | Wofür |
|---|---|---|
| `ghcr.io/real-ttx/matbill:latest` | `main` | Releases |
| `ghcr.io/real-ttx/matbill:nightly` | `dev` | die neuesten Funktionen |

### 1. Einfach starten

Das hier als `docker-compose.yml` speichern und starten – mehr braucht es nicht:

```yaml
name: matbill

services:
  matbill:
    image: ghcr.io/real-ttx/matbill:latest
    restart: unless-stopped
    depends_on:
      db:
        condition: service_healthy
    ports:
      - "2505:2505"
    environment:
      TZ: Europe/Berlin
      MATBILL__Database__Password: ${MATBILL_DB_PASSWORD:-matbill}
    volumes:
      - matbill-data:/data

  db:
    image: postgres:17
    restart: unless-stopped
    environment:
      POSTGRES_PASSWORD: ${MATBILL_DB_PASSWORD:-matbill}
      POSTGRES_DB: matbill
    healthcheck:
      test: ["CMD-SHELL", "pg_isready -U postgres -d matbill"]
      interval: 5s
      timeout: 5s
      retries: 20
    volumes:
      - matbill-db:/var/lib/postgresql/data

volumes:
  matbill-data:
  matbill-db:
```

```bash
echo "MATBILL_DB_PASSWORD=$(openssl rand -hex 24)" > .env   # eigenes Datenbank-Passwort, vor dem ersten Start
docker compose up -d
```

**http://localhost:2505** öffnen: Der erste Besuch legt den **Administrator** an (es gibt kein Standard-Passwort), danach führt
MatBill ins **Branding** für Firma, Logo, Steuernummer und Bankverbindung. Zum Ausprobieren legt
*Einstellungen › System › Beispieldaten anlegen* die Firma „MatBill Demo“ mit Logo, Kunden, Angeboten, Rechnungen, Serien,
Ausgaben und einem Geschäftskonto an.

Health-Check: `/health`. Ein Update ist `docker compose pull && docker compose up -d`; die Migrationen laufen beim Start.

### 2. Hinter einem Reverse Proxy, mit Backup aufs NAS

MatBill spricht HTTP auf Port 2505. Für den Betrieb gehört ein Reverse Proxy mit TLS davor (Caddy, Traefik, nginx, der Proxy der
Synology). Dann zwei Variablen setzen – die öffentliche Adresse wird für Freigabelinks, Mails und die Rückkehr vom Online-Banking gebraucht:

```yaml
    environment:
      TZ: Europe/Berlin
      MATBILL__Database__Password: ${MATBILL_DB_PASSWORD:-matbill}
      MATBILL__Server__PublicUrl: https://rechnungen.example.de
      MATBILL__Server__ForceSecureCookie: "true"
    volumes:
      - matbill-data:/data
      # optional: ein Ordner des NAS als Backup-Ziel (sonst direkt per SMB aus der App)
      - /volume1/backup/matbill:/backups
```

Backups laufen aus der App: *Einstellungen › Backup* – ein **Ziel** (Ordner wie `/backups` oder eine SMB-Freigabe des NAS mit
Verbindungstest), ein **Plan** (stündlich bis monatlich) und die **Aufbewahrung** (letzte N, täglich, wöchentlich, monatlich).

### 3. Aus dem Quellcode

```bash
docker compose -f docker-compose.dev.yml up -d --build    # Entwicklungs-Stack: Web 2505, pgAdmin 2506, Postgres 12505
```

### Einstellungen, die zählen

Fast alles wird in der App eingestellt und landet in `/data/config/app.json`. Umgebungsvariablen gewinnen und werden nie zurückgeschrieben:

| Variable | Standard | Bedeutung |
|---|---|---|
| `MATBILL__Database__Host` | `db` | Host der PostgreSQL |
| `MATBILL__Database__Port` | `5432` | Port |
| `MATBILL__Database__Database` | `matbill` | Datenbank |
| `MATBILL__Database__Username` | `postgres` | Benutzer |
| `MATBILL__Database__Password` | `matbill` | Passwort – im Compose aus `MATBILL_DB_PASSWORD` |
| `MATBILL__Server__PublicUrl` | – | die Adresse, unter der MatBill erreichbar ist (Links, Mails, Online-Banking) |
| `MATBILL__Server__TimeZone` | `Europe/Berlin` | Zeitzone für Daten und Zeitpläne |
| `MATBILL__Server__ForceSecureCookie` | `false` | `true` hinter HTTPS |
| `MATBILL_PORT` | `2505` | Port im Container |
| `MATBILL_DATA` | `/data` | Datenverzeichnis |
| `TZ` | `Europe/Berlin` | Zeitzone des Containers |

### Nach der ersten Anmeldung

- **Branding:** Firma, Anschrift, Logo (SVG/PNG/JPEG), Farbe des Dokuments, Steuernummer/USt-IdNr., Bankverbindung – alles, was auf
  Rechnung und E-Rechnung gehört
- *Einstellungen › Rechnungen:* Nummernkreise, Zahlungsziel, Texte, Kategorien der Ausgaben, ZUGFeRD an/aus
- *Einstellungen › E-Mail:* SMTP-Server, Absender, Texte für Rechnung und Angebot, Testmail
- *Einstellungen › Banking:* Enable Banking (eigene App-ID und Schlüssel), automatische Zuordnung, Abrufintervall
- *Mein Konto › Sicherheit:* zweiter Faktor (TOTP); *Einstellungen › Sicherheit* kann ihn für Administratoren verlangen

### Das Volume `/data`

```
/data
├─ config/    app.json – die Einstellungen
├─ keys/      Schlüssel für Cookies und gespeicherte Passwörter (Sitzungen überstehen Neustarts)
├─ files/     PDFs der Rechnungen und Angebote, Belege, Logo (leer, wenn sie auf dem NAS liegen)
├─ backups/   lokale Backup-Ziele und Sicherheitskopien vor einer Wiederherstellung
└─ tmp/       Zwischenablage für Backups und Importe
```

Die Daten selbst stehen in der PostgreSQL (`matbill-db`). **Geht `/data/keys` verloren**, muss sich jeder neu anmelden und
gespeicherte Passwörter (NAS, Mailserver, Banking-Schlüssel) müssen neu eingetragen werden – deshalb gehört es ins Backup.

Mehr zum Betrieb: [docs/OPERATIONS.md](docs/OPERATIONS.md) (englisch).

## Entwicklung

Getestet wird über den Container – nach jeder Änderung neu bauen und den Stack neu deployen:

```powershell
./scripts/redeploy.ps1            # Image neu bauen, Dev-Stack neu deployen
./scripts/redeploy.ps1 -Fresh     # dasselbe mit leeren Volumes
```

Tests:

```powershell
dotnet build MatBill.slnx
$env:MATBILL_TEST_DB="Host=localhost;Port=12505;Username=postgres;Password=matbill"; dotnet test MatBill.slnx
cd tests/e2e; npm install; node flow.js http://localhost:2505    # Browser-Durchlauf mit Edge, SHOTS=<Ordner> für Screenshots
```

Die Unit-Tests brauchen nichts, die Integrationstests die PostgreSQL des Dev-Stacks, die NAS-Tests einen Samba-Server
(`MATBILL_TEST_SMB`, die CI startet einen). Der Browser-Durchlauf schreibt eine Rechnung, ein Angebot, storniert, lädt einen Beleg hoch,
importiert Umsätze, prüft Handy, PWA und Offline – und erzeugt dabei die Screenshots dieser Seite.

## Wie es gebaut ist

- **ASP.NET Core 10 Razor Pages**, **EF Core 10** auf **PostgreSQL 17**
- Oberfläche aus **MatUi** (MatDesign), schlichtes JavaScript ohne Framework und ohne Build-Schritt, strikte CSP ohne Inline-Skripte
- PDFs mit **QuestPDF** (Community-Lizenz: frei unter 1 Mio. USD Jahresumsatz), E-Rechnung mit **ZUGFeRD-csharp**, PDF lesen mit **PdfPig**
- Kontoauszüge: eigene Parser für CSV, CAMT.052/053/054 und MT940; online über die API von **Enable Banking**
- Mail mit **MailKit**, Backup auf SMB mit **SMBLibrary**, Geheimnisse verschlüsselt über **DataProtection**

## Branches und Versionen

| Branch | Zweck | Version |
|---|---|---|
| `main` | Release | `<major>.<minor>.<build>-<yyyyMMdd>` |
| `dev` | Entwicklung | `nightly-<build>-<yyyyMMdd>` |
| lokal | – | `local-<yyyyMMdd>` |

`Major`/`Minor` stehen in [`Directory.Build.props`](Directory.Build.props), die Build-Nummer kommt aus der GitHub Action
(`github.run_number`). Die Action baut, testet (mit PostgreSQL und Samba) und veröffentlicht das Image unter `ghcr.io/real-ttx/matbill`.
