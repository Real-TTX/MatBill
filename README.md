# MatBill

Rechnungen schreiben, versenden und wiederholen: ein minimalistischer, dokumentbasierter Rechnungseditor auf einer DIN-A4-Vorschau, wiederkehrende
Rechnungen mit automatischer Erstellung und Versand, Angebote, Ausgaben mit Belegen, Banking mit automatischer Zuordnung der Zahlungen, E-Rechnung
(ZUGFeRD/XRechnung) und Export für Finanzamt oder Steuerberater, als installierbare Web-App (PWA). ASP.NET Core 10 Razor Pages, PostgreSQL 17, ein Container
(Port **2505**) plus Datenbank. Das Aussehen folgt MatDesign (MatUi).

![Übersicht](docs/images/dashboard.png)

## Was MatBill kann

**Das Dokument ist die Oberfläche.** Eine Rechnung wird direkt an ihrer Stelle auf dem Papier bearbeitet: Empfänger per Autocomplete aus den Kunden
(oder einfach einen neuen Namen tippen, er wird als Kunde angelegt), Positionen per Produktauswahl, Texte durch Anklicken. Summen und Umsatzsteuer
(je Steuersatz) rechnen sich beim Tippen. Ein Schalter wechselt zur **normalen Formularansicht** mit denselben Feldern; die Wahl wird pro Person gemerkt.

| Dokument | Formular |
|---|---|
| ![Editor als Dokument](docs/images/editor-document.png) | ![Editor als Formular](docs/images/editor-form.png) |

- **Entwurf → Ausstellen:** Ein Entwurf hat keine Nummer und ist frei änderbar. Beim Ausstellen bekommt er die nächste Nummer (lückenloser
  Nummernkreis, z. B. `RE-{JJJJ}-{NNNN}`), die PDF wird gespeichert und die Rechnung ist danach unveränderlich (bezahlt, storniert, dupliziert).
- **PDF und Versand:** A4-PDF im Layout des Editors (Logo, Absender, Fensteranschrift nach DIN 5008, Bank und Steuernummer im Fuß), Versand per SMTP
  mit Protokoll jeder Zustellung, erneut senden bei Fehlern.
- **Freigabelinks (Rolle „Anonym“):** Ein Link zeigt dem Kunden die Rechnung samt PDF, zeitlich begrenzt und optional mit Passwort.
- **Kunden, Produkte, Branding:** Stammdaten als Liste plus Bearbeitungsseite; Branding mit Logo (SVG/PNG/JPEG), Farbe des Dokuments, Gruß und Live-Vorschau.
  Kleinunternehmer-Regelung (§ 19 UStG) auf Knopfdruck.

### Wiederkehrende Rechnungen

![Serie](docs/images/series.png)

- Aus jeder Rechnung mit **„Wiederholung einrichten“**: wöchentlich, monatlich, quartalsweise, halbjährlich oder jährlich; Startdatum, Enddatum oder unbegrenzt.
- Eine Serie ist eine **Vorlage**. Zu jedem Termin entsteht daraus eine eigenständige Rechnung mit eigener Nummer, Rechnungsdatum, Leistungszeitraum und
  Zahlungsziel; die Anschrift kommt aktuell aus den Kundendaten. Änderungen an der Vorlage gelten nur für künftige Rechnungen.
- **Automatischer Versand** als PDF an die E-Mail-Adresse des Kunden (optional).
- **Steuerung:** pausieren, fortsetzen, bearbeiten, beenden, „Jetzt ausführen“. Termine zählen vom Start aus (ein Start am 31. läuft am Monatsletzten).
- **Fehlerbehandlung:** Eine fehlgeschlagene Erstellung oder ein fehlgeschlagener Versand steht im Protokoll, wird automatisch mit wachsendem Abstand erneut
  versucht und kann jederzeit von Hand wiederholt werden. Verpasste Termine (Server aus) werden nachgeholt, kein Termin wird doppelt berechnet.
- **Benachrichtigungen** (Glocke oben rechts): anstehende Rechnungen, erstellte und versendete Rechnungen, Probleme.

### Storno und E-Rechnung

![Stornorechnung](docs/images/storno.png)

- **Stornieren** legt eine **Stornorechnung** mit eigener Nummer und negativen Beträgen an, die auf die Originalrechnung verweist; das Original wird
  als storniert markiert und bleibt unverändert (GoBD). Danach wird die Rechnung bei Bedarf neu geschrieben (Duplizieren).
- **ZUGFeRD 2.3 (Profil EN 16931):** Jede ausgestellte Rechnung ist eine PDF/A-3 mit eingebetteter `factur-x.xml`. Die XML gibt es auch einzeln,
  ebenso als **XRechnung** für öffentliche Auftraggeber (Leitweg-ID beim Kunden als „Käuferreferenz“).

### Angebote

![Angebot](docs/images/quote.png)

- Derselbe A4-Editor wie für Rechnungen, mit eigenem Nummernkreis (`AN-{JJJJ}-{NNNN}`), Gültigkeit und Angebotsbedingungen.
- Ausstellen, als PDF versenden, als angenommen oder abgelehnt markieren, neue Version anlegen und mit einem Klick **in eine Rechnung umwandeln**.

### Ausgaben

![Ausgabe](docs/images/expense.png)

- **Belege per Drag & Drop** (PDF, Bilder, XML) in die Liste ziehen: Je Datei entsteht eine Ausgabe mit dem Beleg. E-Rechnungen (ZUGFeRD, XRechnung)
  werden ausgelesen (Lieferant, Nummer, Datum, Beträge); andere Belege sind als „zu prüfen“ markiert und werden nacheinander vervollständigt.
- Kategorie, netto/brutto mit Steuersatz, Fälligkeit, bezahlt am; der Beleg wird neben dem Formular angezeigt.

### Banking

| Umsätze | Zuordnung |
|---|---|
| ![Banking](docs/images/banking.png) | ![Umsatz](docs/images/bank-transaction.png) |

- **Universeller CSV-Import** jeder Bank: Spalten, Trennzeichen, Kodierung und Kopfzeile werden erkannt, die Zuordnung der Spalten lässt sich anpassen
  und wird je Konto gemerkt. Außerdem **CAMT.052/053/054** (XML) und **MT940**. Doppelte Umsätze werden beim erneuten Import übersprungen.
- **Online-Anbindung** über [Enable Banking](https://enablebanking.com) (PSD2, praktisch alle Banken in Europa): Bank wählen, beim Online-Banking
  zustimmen, die Umsätze werden danach regelmäßig abgerufen. Dafür braucht es ein eigenes Konto bei Enable Banking (*Einstellungen › Banking*).
- **Automatische Zuordnung:** Rechnungsnummer im Verwendungszweck, Betrag (auch mit Skonto), Name oder IBAN ergeben eine Punktzahl; eindeutige Treffer
  werden automatisch zugeordnet und die Rechnung bzw. Ausgabe als bezahlt markiert. Für den Rest gibt es Vorschläge, eine Suche von Hand, „Ausgabe anlegen“
  und „Ignorieren“ (Umbuchungen, Steuern, Privates).

### Auswertung und Export

![Auswertung](docs/images/report.png)

- Einnahmen, Ausgaben, Umsatzsteuer, Vorsteuer und Zahllast je Monat, nach Belegdatum (Soll) oder Zahlung (Ist).
- **Export als ZIP** für einen Zeitraum: alle Rechnungen und Belege als Dateien (sortierbar benannt `Datum_Nummer_Partner`), dazu `Rechnungen.csv`,
  `Ausgaben.csv`, `Uebersicht.csv` und optional `Kontoumsaetze.csv` (Excel-tauglich).

### Installierbar und mobil

| Übersicht | Editor | Rechnung |
|---|---|---|
| ![](docs/images/mobile-dashboard.png) | ![](docs/images/mobile-editor.png) | ![](docs/images/mobile-invoice.png) |

- Web App Manifest, Service Worker, Icons (auch maskable und Apple Touch Icon), Shortcuts „Neue Rechnung“, „Rechnungen“, „Wiederkehrend“.
- iOS: Safe Areas (Notch, Home Indicator), Statusleiste in der Farbe der Kopfleiste (hell und dunkel, folgt auch dem in der App gewählten Thema),
  Eingabefelder mit 16 px (kein ungewolltes Zoomen), Zoomen bleibt für die Barrierefreiheit erlaubt.
- **Offline:** Bereits geöffnete Seiten bleiben lesbar (als „Stand von …“ gekennzeichnet), sonst erklärt eine Offline-Seite die Lage. Automatischer
  Reconnect mit wachsendem Abstand und ein Knopf „Erneut verbinden“; nach der Rückkehr wird eine Offline-Kopie sofort durch die aktuelle Seite ersetzt.
  Formulare werden ohne Verbindung nicht abgeschickt (keine verlorenen Eingaben), und der Editor sichert ungespeicherte Eingaben im Browser zum Wiederherstellen.
  Beim Abmelden werden die zwischengespeicherten Seiten gelöscht.

### Betrieb

- **Anmeldung** lokal mit Passwort und optional **TOTP** (Authenticator-App, Wiederherstellungscodes); Rollen Administrator, Benutzer, Anonym (Freigabelink).
- **Sitzungen** liegen in der Datenbank, die Schlüssel im Volume: Ein Neustart des Containers meldet niemanden ab.
- **Backup** mit Zeitplänen (stündlich, täglich, wöchentlich, monatlich) direkt auf ein NAS (SMB) oder in einen Ordner, mit Retention
  (letzte N, täglich, wöchentlich, monatlich), optional verschlüsselt, und Wiederherstellung mit Prüfung und Sicherheitskopie.

Details: [docs/OPERATIONS.md](docs/OPERATIONS.md).

## Start

```bash
docker compose up -d          # Release: ghcr.io/real-ttx/matbill:latest + PostgreSQL
```

Dann `http://localhost:2505` öffnen: Der erste Besuch legt den Administrator an, danach im **Branding** Absender, Steuernummer und Bankverbindung eintragen.
Zum Ausprobieren legt *Einstellungen › System › Beispieldaten anlegen* die Firma „MatBill Demo“ mit Logo, Kunden, Produkte, Angebote, Rechnungen
(auch eine Stornierte), zwei Serien, Ausgaben und ein Geschäftskonto mit zugeordneten Umsätzen an.

Hinter einem Reverse Proxy mit HTTPS: `MATBILL__Server__PublicUrl` (für Freigabelinks) und `MATBILL__Server__ForceSecureCookie=true` setzen.

## Entwicklung

```powershell
./scripts/redeploy.ps1            # Container neu bauen und Dev-Stack neu deployen (Web 2505, pgAdmin 2506, Postgres 12505)
./scripts/redeploy.ps1 -Fresh     # dasselbe mit leeren Volumes
dotnet build MatBill.slnx
$env:MATBILL_TEST_DB="Host=localhost;Port=12505;Username=postgres;Password=matbill"; dotnet test MatBill.slnx
cd tests/e2e; npm install; node flow.js http://localhost:2505      # Browser-Prüfung mit Edge (SHOTS=<Ordner> für Screenshots)
```

Versionen: Release `<major>.<minor>.<build>-<datum>` (Branch `main`), Entwicklung `nightly-<build>-<datum>` (Branch `dev`), lokal `local-<datum>`.
Die GitHub Action baut, testet (mit PostgreSQL und Samba) und veröffentlicht das Image unter `ghcr.io/real-ttx/matbill`.
