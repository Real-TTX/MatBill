# The API: invoice requests

Other programs of the owner ask MatBill for invoices (the first: Matmon.Cloud, which bills its partners every month). MatBill stays the one place
where invoices live and where their numbers are given; the finished invoice is the usual ZUGFeRD PDF / XRechnung. There is no standard for
*requesting* an invoice, so this is MatBill's own small API.

## Clients and keys

*Einstellungen › API* (administrators): a **client** per program, with

- a **name** ("Matmon"): the invoices it requests show "von Matmon" in the list and on the invoice;
- a **key** `mbk_` + 43 characters, shown **once** after it was made. MatBill stores only its SHA-256 (and the first 12 characters, to find it and to
  show it in the list); a presented key is compared in constant time. *Neuer Schlüssel* replaces it at once, *Sperren* stops the client (its invoices
  stay and keep their origin; a new key switches it on again). The page shows when and from where it was used last;
- **rights**: *Darf Rechnungen anfordern*, *Darf Stand und Dokumente abrufen*, and **Darf direkt ausstellen** (off by default: every request becomes a
  **draft** that somebody checks and issues in MatBill);
- an **own number range** (optional): a pattern such as `RE-MAT-{YYYY}-{NNNN}` with its own counter (range `invoice:client:<id>`) and the next counter,
  with a live preview. The same placeholders and the same restart as every range: with `{YYYY}` the counter starts at 1 every year, with `{MM}` every
  month. Empty: the client's invoices take the normal invoice numbers. The pattern must differ from the normal one and from every other client's.
  The number is given when the invoice is **issued**, never to a draft - by the API or by a person in MatBill. A cancellation of such an invoice takes
  its number from the same range.

## Calls

Base address `https://<matbill>/api/v1`. The key goes into `Authorization: Bearer mbk_…` (or `X-Api-Key: mbk_…`). No cookie, no anti-forgery token,
JSON in and out (UTF-8), `Cache-Control: no-store`, at most **120 calls a minute per address** (then `429` with `Retry-After`). Errors are JSON:
`{ "error": "<code>", "message": "<sentence>", "problems": ["…"] }` (`problems` only for `invalid_request`). Messages are English.

| Call | Right | Answer |
|---|---|---|
| `POST /invoice-requests` | request | `201` new invoice, `200` the invoice the reference already stands for |
| `GET /invoice-requests?reference=<ref>` | read | the invoice of a reference |
| `GET /invoice-requests/{id}` | read | the invoice |
| `GET /invoices/{id}/pdf` | read | the stored PDF of an issued invoice (ZUGFeRD PDF/A-3 unless switched off), `409` while it is a draft |
| `GET /invoices/{id}/xml` | read | the CII XML (EN 16931); `?xrechnung=1` the XRechnung |

A client sees only the invoices it requested (and their cancellations); any other id is `404`.

### Request an invoice

```json
{
  "reference": "matmon:2026-10:org:4f1c…",
  "issue": false,
  "note": "Partnerabrechnung Oktober, 3 Instanzen",
  "title": "Rechnung",
  "buyerReference": "PO-4711",
  "paymentTermDays": 14,
  "servicePeriod": { "from": "2026-10-01", "to": "2026-10-31" },
  "customer": {
    "externalRef": "matmon:org:4f1c…",
    "name": "Beispiel GmbH",
    "contact": "Frau Keller",
    "addressLines": ["Hauptstr. 1"],
    "postcode": "12345",
    "city": "Berlin",
    "country": "DE",
    "vatId": "DE123456789",
    "email": "rechnung@example.com"
  },
  "lines": [
    { "text": "Matmon Business", "description": "Instanz nas-01", "quantity": 1, "unit": "Monat", "unitPrice": 29.00, "taxRate": 19,
      "servicePeriodFrom": "2026-10-01", "servicePeriodTo": "2026-10-31" }
  ]
}
```

Required: `reference` (≤ 200), `customer.externalRef` (≤ 200), `customer.name`, at least one line with `text` and `unitPrice` (net, per unit).
Defaults: `quantity` 1, `taxRate` the default rate of the settings, `title` "Rechnung", `paymentTermDays` the customer's or the default, `country`
"Deutschland" (`DE`/`Germany` are written as "Deutschland", anything else as sent). Dates are `yyyy-MM-dd`. At most 500 lines; the body at most 1 MB.

- **Idempotency.** The `reference` is the client's key of this invoice. A second request with the same reference (same client) creates nothing and
  answers `200` with the existing invoice *as it is now* - the body of the repeat is not compared, a draft is not issued by repeating it. A reference
  is free again once its draft was **deleted** in MatBill (the owner threw it away; asking again makes a new draft). Two identical requests at the
  same moment end up as one invoice (a unique index; the loser answers the winner's invoice).
- **Issue directly.** `"issue": true` is honoured only for a client that *may issue directly*: the invoice is created and issued in one transaction
  (number from the client's range, PDF stored, e-invoice inside). Without the right the request is **refused** with `403 issue_not_allowed` and
  nothing is created - a client that asked for an issued invoice must not believe it has one. Leave `issue` out to get a draft.
- **The customer** is found by `customer.externalRef` **per client** (another client's identical key is another customer); none yet: a new customer
  with the next customer number. What the request says about the customer **updates** it every time (name always; the other fields when sent) - the
  client keeps the master data of its customers. This never changes an issued invoice: an invoice copies the address when it is written. Customers
  that were entered by hand in MatBill are never matched by name (no risk of billing the wrong "Müller"); such a customer appears once more with
  the client's key.
- **Service period.** `servicePeriod` is the period of the invoice; without it the span of the line periods is used. A line's period is printed under
  the line ("Leistungszeitraum: 01.10.2026 – 31.10.2026").
- `note` is for whoever checks the draft in MatBill (shown on the invoice page, not printed).

### The answer

```json
{
  "id": 412,
  "reference": "matmon:2026-10:org:4f1c…",
  "kind": "invoice",
  "status": "open",
  "number": "RE-MAT-2026-0003",
  "invoiceDate": "2026-10-29",
  "dueDate": "2026-11-12",
  "servicePeriod": { "from": "2026-10-01", "to": "2026-10-31" },
  "issuedAt": "2026-10-29T20:51:13.42Z",
  "paidDate": null,
  "cancelledAt": null,
  "currency": "EUR",
  "netTotal": 29.00, "taxTotal": 5.51, "grossTotal": 34.51,
  "customer": { "id": 57, "number": "K-0057", "externalRef": "matmon:org:4f1c…" },
  "cancellation": null,
  "cancels": null,
  "links": {
    "self": "https://matbill.example.com/api/v1/invoice-requests/412",
    "pdf": "https://matbill.example.com/api/v1/invoices/412/pdf",
    "xml": "https://matbill.example.com/api/v1/invoices/412/xml",
    "xrechnung": "https://matbill.example.com/api/v1/invoices/412/xml?xrechnung=1"
  }
}
```

`status` is MatBill's own state: `draft` (no number, no date yet - `invoiceDate`/`dueDate` are null), `open` (issued, unpaid), `paid`, `cancelled`
(then `cancellation` names the cancellation invoice, which the client can read too: `kind` `cancellation`, `cancels`), or `deleted` (a draft that was
thrown away in MatBill). The links of the documents are there once the invoice is issued. The addresses are absolute, built from
*Server › PublicUrl* (else the address of the request).

### Codes

| Status | `error` | Meaning |
|---|---|---|
| 400 | `invalid_json`, `invalid_request`, `reference_required` | the body is no JSON / misses or breaks a field (`problems` lists them) |
| 401 | `invalid_key` | no key, an unknown key, or the client is revoked |
| 403 | `create_not_allowed`, `read_not_allowed`, `issue_not_allowed` | the client lacks the right |
| 404 | `not_found` | no such invoice for this client (or no such call) |
| 409 | `not_issued` | a document of a draft |
| 413 | `too_large` | the body is larger than 1 MB |
| 422 | `einvoice_failed` | the e-invoice of the invoice could not be made |
| 429 | `rate_limited` | more than 120 calls a minute |

## Examples

```bash
KEY=mbk_…
curl -X POST https://matbill.example.com/api/v1/invoice-requests \
  -H "Authorization: Bearer $KEY" -H "Content-Type: application/json" \
  -d '{ "reference": "matmon:2026-10:org:4f1c", "customer": { "externalRef": "matmon:org:4f1c", "name": "Beispiel GmbH",
        "addressLines": ["Hauptstr. 1"], "postcode": "12345", "city": "Berlin", "country": "DE" },
        "lines": [ { "text": "Matmon Business", "unit": "Monat", "unitPrice": 29.00, "taxRate": 19 } ] }'

curl -H "Authorization: Bearer $KEY" "https://matbill.example.com/api/v1/invoice-requests?reference=matmon:2026-10:org:4f1c"
curl -H "Authorization: Bearer $KEY" https://matbill.example.com/api/v1/invoice-requests/412
curl -H "Authorization: Bearer $KEY" -o RE-MAT-2026-0003.pdf https://matbill.example.com/api/v1/invoices/412/pdf
curl -H "Authorization: Bearer $KEY" -o RE-MAT-2026-0003.xml "https://matbill.example.com/api/v1/invoices/412/xml?xrechnung=1"
```

## Not there (yet)

- **Webhooks** (a call to the client when an invoice is issued or paid): a client asks for the state instead (`GET /invoice-requests?reference=…`).
- A list of a client's requests (`GET /invoice-requests` without a reference) and paging.
- Linking a customer entered by hand to a client's `externalRef` in the interface.
- The rate limit counts per address; behind a reverse proxy that address comes from `X-Forwarded-For`, which MatBill takes from any sender.
