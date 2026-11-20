# I01 — Outgoing invoices, payments and the company profile

Owner's request, 2026-11-20: "napravi da postoje računi" — the ERP must be able to invoice.

## Model

- **Invoice**: `draft` → `issued` (number from the gapless series `RN-000001`, final) → `cancelled` (a cancelling
  invoice was issued for it). Header: customer (a partner with `isCustomer`), issue / delivery / due date, payment
  method, warehouse (for stock), reference, note. Lines: article (stock or service), description (default the
  article's name), quantity (base unit), unit price **without tax** (default: the article's sales price, which
  includes tax, less the line's tax), discount %, tax rate (default: the article's single tax rate; its percentage is
  copied onto the line).
- **Amounts**: line net = qty × price × (1 − discount), rounded to cents; tax per rate on the sum of its lines
  (recapitulation), rounded to cents; total = net + tax.
- **Cancellation**: an issued invoice is never changed or deleted; `cancel` issues a new invoice with the same lines and
  negated quantities (`cancellationOf`), the original becomes `cancelled` (`cancelledBy`).
- **Payments**: amount, date, payment method, note on an issued, non-cancelling invoice; `paymentStatus`
  `unpaid` / `partial` / `paid` (`none` for drafts, cancelled and cancelling invoices).
- **Stock** (rule `invoice.issueMovesStock`): the issue posts a stock issue (`SI-…`, reference = invoice number,
  partner = customer) of the stock articles; the cancellation reverses it. That stock document cannot be reversed alone.
- **Company profile**: legal name, OIB, address, IBAN, e-mail, phone, web, invoice footer — printed on invoices.

## HTTP and MCP

`/api/v1/invoices` (list with `status`, `paymentStatus`, `customerId`, `search`; `/{id}`, `/by-number/{n}`, POST, PUT,
DELETE, `/{id}/issue`, `/{id}/cancel`, `/{id}/payments`, `/{id}/payments/{paymentId}` DELETE) and
`/api/v1/company` (GET, PUT). MCP: `invoice_list`, `invoice_get`, `invoice_create`, `invoice_update`, `invoice_delete`,
`invoice_issue`, `invoice_cancel`, `invoice_payment_add`, `invoice_payment_delete`, `company_get`, `company_set` (123 tools).

## Rules

| Key | Default | Meaning |
|---|---|---|
| `invoice.customerRequired` | `true` | an invoice is issued only with a customer |
| `invoice.backdatingAllowed` | `false` | an invoice may not be dated before the latest issued one |
| `invoice.issueMovesStock` | `false` | issuing posts a stock issue of the goods; cancelling reverses it |
| `invoice.overpaymentAllowed` | `false` | payments may not exceed the total |

## Not in I01

Fiscalisation (cash and card payments in Croatia need a JIR from the Tax Administration) and e-invoicing (Fiskalizacija
2.0 for B2B) are not done: use these invoices for bank-transfer payments, or add fiscalisation before taking cash.
Incoming (supplier) invoices, currencies, advance invoices, PDF export (the web client prints).
