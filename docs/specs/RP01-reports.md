# RP01 — Reports an agent can run

Owner, 2026-11-24: the company's agent must be able to **generate their reports** simply. Companion of A01.
Branches: `tests/RP01-reports`, `feat/RP01-reports`. Right after A01.

## 1. Design

One catalogue, two tools — not one tool per report, so that the tool list stays small and a new report never
needs a new tool:

- `report_list` (`GET /reports`): every report with `key`, `name`, `description` (what question it answers,
  for an agent), its parameters (name, type, required, default, allowed values) and its columns.
- `report_run { key, parameters, format? }` (`GET /reports/{key}?…`): runs it. Result:
  ```json
  { "report": "sales-by-period", "parameters": {…}, "generatedAt": "…Z",
    "columns": [{ "key": "period", "label": "Period", "type": "date|text|number|money|quantity|percent" }],
    "rows": [ {…} ], "totals": {…} | null, "rowCount": 12, "truncated": false, "notes": ["…"] }
  ```
  `format: "csv"` returns the same as CSV text (HTTP: `Accept: text/csv` or `?format=csv`, UTF-8 with BOM,
  `;` separator — Croatian Excel opens it directly). At most 5000 rows; beyond that `truncated: true` and a
  note telling which parameter narrows it.
- Reports are read-only, tenant-scoped, run in one read snapshot (R01 §6), and never answer `500` for any
  parameter combination: invalid parameters are `400 VALIDATION_FAILED` keyed by parameter name.
- Each report is a class in `src/Xerp.Application/Reports/` (a query + its definition). A test runs every
  report of the catalogue with its defaults in an empty tenant and in a seeded tenant.
- The agent renders, charts and explains; the server returns numbers. `columns[].type` lets it format money
  and quantities correctly.

Common parameters: `from`, `to` (dates, default: the current month), `groupBy` (`day|week|month|quarter|year`
where time is grouped), `warehouseId`, `partnerId`, `articleId`, `categoryId` — each only where it applies.

## 2. First catalogue (what exists in the data today)

| Key | Answers | Source |
|---|---|---|
| `sales-by-period` | How much did we invoice, per period: net, tax, total, number of invoices | issued invoices (I01), cancellations net out |
| `sales-by-customer` | Who are our best customers in a period | invoices |
| `sales-by-article` | What sells: quantity, net revenue per article (and category) | invoice lines |
| `vat-output` | Output VAT per rate per period (the outgoing half of the VAT return) | invoices' recapitulation |
| `payments-received` | Payments received per period and payment method | invoice payments |
| `unpaid-invoices` | Issued invoices not fully paid, with days overdue | invoices + payments |
| `stock-on-hand` | Quantity per article per warehouse (as of now) | stock balance |
| `stock-movements` | Per article: opening, in, out, closing quantity in a period | stock ledger |
| `slow-movers` | Articles with stock but no outgoing movement for N days (parameter `days`, default 90) | ledger + balance |
| `open-sales-orders` | What is promised and not delivered | sales orders |
| `open-purchase-orders` | What is ordered and not received | purchase orders |
| `purchases-by-supplier` | Received goods per supplier (quantity; value once 015 exists) | receipts |
| `production-output` | Completed work orders: product, quantity, period | work orders |

Every later spec adds its reports in the same catalogue — this becomes a standing rule in
`docs/architecture.md` (like configurable rules): **a spec that adds business data adds the reports that
answer the obvious questions about it.** Known follow-ups: 015 -> `stock-value`, `gross-margin` (sales minus
cost of goods), `stock-value-by-warehouse`; I02 -> `purchases-by-period`, `vat-input`, `payables-aging`,
`receivables-aging`; X01 -> the accountant's books (IRA/URA) are reports with a fixed CSV layout.

## 3. Rules

No business rules: a report decides nothing. Report definitions are not tenant-configurable in RP01;
saved report presets per tenant are a possible later step (`report_preset_*`) once agents show they need them.

## 4. Acceptance criteria (selection)

- `report_list` lists the 13 reports with parameters and columns; `tools/list` grows by 2.
- Each report on a seeded tenant returns hand-computed values (the tester builds the seed: 2 customers,
  2 suppliers, 3 articles, orders, receipts, deliveries, 4 invoices of which 1 cancelled, 3 payments, a
  work order); totals equal the sum of rows.
- Cancelled invoices and their cancelling invoices net to zero in every sales report.
- `format=csv` matches the JSON rows; Croatian characters survive (BOM, UTF-8).
- Invalid `from > to`, unknown `groupBy`, unknown `key` -> `400` / `404` with the parameter as key.
- Tenant isolation: a report never includes another tenant's data (seed two tenants).
- MCP parity: `report_run` returns the same body as HTTP JSON.
