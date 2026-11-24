# I02 — Incoming (supplier) invoices, payables and open items

Owner goal of 2026-11-24: a production-ready ERP, everything except fiscalisation (`docs/roadmap.md`,
"Production plan", step 5). I01 made outgoing invoices; a company also has to record what it owes, pay it, and
know who owes it what. After 017 is merged (the series `incomingInvoice` is added to it).
Branches: `tests/I02-incoming-invoices`, `feat/I02-incoming-invoices`.

**A tenant that records no incoming invoice sees nothing of specs 001–017 change.**

## Model

- **Incoming invoice**: `draft` → `booked` (gets our internal number from the series `incomingInvoice`, default
  `UR-{n}`, 017) → `cancelled` (a cancelling incoming invoice was booked for it, as I01). Never changed or deleted
  once booked.
  Header: supplier (a partner with `isSupplier`), **supplier's invoice number** (text 1–60, as printed by the
  supplier, required), issue date (the supplier's), receipt date (when we got it; default today; this is the
  date of the VAT period), due date, payment method, reference, note, optional `purchaseOrderId` and
  `receiptIds` (stock receipts this invoice bills; informational links, R5).
  Lines: optional article, description (required; default the article's name), quantity, unit price without
  tax, discount %, tax rate (an existing tax rate of P01; its percentage copied onto the line), and
  `deductible` (bool, default `true`: the input VAT may be deducted — false e.g. for representation costs).
- **Amounts** exactly as I01 (line net rounded to cents, tax per rate on the sum of its lines, total). The
  recapitulation per rate also splits deductible and non-deductible tax — the accountant export (X01) needs it.
  `supplierTotal` (optional, the total printed on the paper invoice): when given, booking compares it with the
  computed total (rule below).
- **Payments**: amount, date, payment method, note — on a booked, non-cancelling incoming invoice;
  `paymentStatus` `unpaid` / `partial` / `paid` as I01.
- **Open items** (both sides, read-only views over I01 and I02):
  - `GET /open-items?side=receivable|payable&partnerId&asOf` — every issued (resp. booked), not cancelled,
    not fully paid invoice with total, paid, open amount, due date and `daysOverdue` as of `asOf` (default
    today, UTC).
  - `GET /partners/{id}/balance?asOf` — receivable open, payable open, net, and aging buckets
    (not due, 1–30, 31–60, 61–90, > 90 days) per side.
  - `GET /aging?side=…&asOf` — the same buckets per partner, sorted by total open, descending; paged.

## HTTP and MCP

`/api/v1/incoming-invoices` (list with `status`, `paymentStatus`, `supplierId`, `search` over our number and
the supplier's number, `fromDate`/`toDate` on receipt date; `/{id}`, `/by-number/{n}`, POST, PUT, DELETE,
`/{id}/book`, `/{id}/cancel`, `/{id}/payments`, `/{id}/payments/{paymentId}` DELETE), `/api/v1/open-items`,
`/api/v1/aging`, `/api/v1/partners/{id}/balance`.
MCP: `incoming_invoice_list`, `_get`, `_create`, `_update`, `_delete`, `_book`, `_cancel`, `_payment_add`,
`_payment_delete`, `open_item_list`, `aging_list`, `partner_balance_get` (12 tools).

## Rules (registry, group `purchaseInvoice`)

| Key | Default | Meaning |
|---|---|---|
| `purchaseInvoice.supplierNumberUnique` | `true` | booking is refused (`DUPLICATE_SUPPLIER_INVOICE`, key `supplierInvoiceNumber`) when a booked, not cancelled invoice of the same supplier has the same supplier number (trimmed, case-insensitive) — the classic double entry of one paper invoice |
| `purchaseInvoice.totalMustMatch` | `true` | when `supplierTotal` is given, booking is refused (`VALIDATION_FAILED`, key `supplierTotal`) unless it equals the computed total; false: booked with the computed total and the difference visible |
| `purchaseInvoice.overpaymentAllowed` | `false` | as `invoice.overpaymentAllowed` |
| `purchaseInvoice.backdatingAllowed` | `true` | false: the receipt date may not be before the latest booked one (incoming invoices arrive late, so the default is permissive, unlike I01) |
| `purchaseInvoice.receiptRequired` | `false` | true: an invoice with a stock article must name at least one posted receipt of the same supplier |

All judge booking (and the payment rule payments); drafts may be saved freely. Every refusal names its rule
(012). Defaults are written so that a company can start booking paper invoices on day one.

## Further rules of behaviour

- R1. Booking needs: supplier exists, active, with `isSupplier`; at least one line; every tax rate active. A
  cancelled supplier or tax rate on a draft is `REFERENCE_INACTIVE` at booking (like posting; 014's
  `posting.activeMastersRequired` applies, its default `true`).
- R2. Cancelling books a cancelling incoming invoice with negated quantities and the same supplier number
  prefixed `STORNO ` (it is ours, not the supplier's); payments must be deleted first (`INVALID_STATE`
  otherwise), as I01.
- R3. Incoming invoices **never move stock**: goods come in through receipts (009). A link to receipts is
  information only, for the person checking the invoice; there is no quantity matching (3-way match) in I02.
- R4. Valuation (015) is not changed by an incoming invoice: the cost of the goods is the receipt's.
  Price differences between invoice and receipt are visible to the user, not posted.
- R5. `receiptIds` must be posted receipts (not reversed) of the same supplier, else `VALIDATION_FAILED`, key
  `receiptIds[i]`. A receipt may be named by several invoices (partial billing).
- R6. Open items and aging read only issued/booked invoices and their payments; cancelled invoices and their
  cancelling invoices are excluded on both sides; amounts in the tenant's single currency.
- R7. Tenant-scoped like everything; `partners/{id}/balance` of another tenant's partner is `404`.

## Acceptance criteria (the tester completes them from the above)

- Book a draft with two rates and one non-deductible line: number `UR-000001`, recapitulation per rate with
  deductible/non-deductible tax, total = net + tax.
- Duplicate supplier number refused, naming the rule; allowed with the rule false; allowed for another
  supplier; allowed after the first is cancelled.
- `supplierTotal` mismatch refused / accepted per rule.
- Payments: partial, paid, overpayment per rule; delete payment.
- Cancel: cancelling invoice, original `cancelled`, both excluded from open items.
- Open items and aging: two customers and two suppliers with invoices due in different buckets; `asOf` in the
  past shows what was open then (payments dated after `asOf` do not count).
- `receiptRequired` and `receiptIds` validation.
- Tenant isolation and MCP parity; tool count +12.

## Not in I02

E-invoices (receiving UBL from Fiskalizacija 2.0 — later, the model leaves room for storing the XML), 3-way
matching, advance payments, currencies, bank statement import, cost centres, a general ledger (owner: the
accountant gets an export, X01).
