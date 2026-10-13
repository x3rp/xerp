# POS module — overview

Owner of this file: the architect. Written 2026-10-13 from `docs/pos/requirements.md` (the owner's list).
Decisions: ADR-0022. First spec: `docs/specs/P01-sales-master-data.md`.

The POS is a hospitality point of sale — tables, kitchen and bar, cash drawer, fiscal receipts — built **on**
the ERP. It has a separate frontend, `xerp-pos` (its own repository, built by the orchestrator), which is a
thin client of the same HTTP API. Nothing is imported or synchronised between "POS" and "ERP": they are one
backend and one database.

## 1. How it sits on the existing layers

```
 xerp-pos (touch UI, printers, scanner, card terminal, offline queue) ──HTTP──┐
 xerp-web, xerp-cli ──HTTP──┐                                                 │
 agents ──MCP───────────────┴──► Xerp.Api ──► Xerp.Application ──► Xerp.Domain ◄── Pos/ in each layer
                                                                        │
                                                              Xerp.Infrastructure ──► PostgreSQL
```

- **Domain**: `Pos/` holds the bill, payments, shifts, tables, preparation tickets, and their checks as pure
  functions that take rule values as arguments (architecture §11).
- **Application**: one operation per use ("add a line", "pay"), plain inputs, no ASP.NET types. POS
  operations call the existing stock posting; they do not write the ledger themselves.
- **Api**: routes under `/api/v1/pos/…` and MCP tools `pos_…`, one per operation, same fields and errors
  (ADR-0009). An agent can do whatever the frontend can; the frontend has no private endpoint.
- **Infrastructure**: new tables, all tenant-owned with the tenant query filter and the composite foreign
  keys of ADR-0002 / architecture §8.

## 2. What is reused, unchanged

| Existing thing | How the POS uses it |
|---|---|
| Tenant scoping (ADR-0002) | Every POS table is tenant-owned; a bill, table or operator of another tenant is `404`. |
| API keys and actors (ADR-0003, I3) | A POS device authenticates with a tenant key; every record keeps `createdBy` / `updatedBy`. The operator (waiter) is recorded in addition (ADR-0022/6). |
| Articles, units, conversions (002, 007) | What is sold and what is consumed. A bill line names an article; recipes are in base units. No second catalogue. |
| Warehouses, default warehouse (004, 011) | A POS station sells from one warehouse (the bar, the kitchen store); omitted = the default. |
| Stock documents, ledger, stored balance (005, 006, 011) | A paid bill posts an `issue` linked to it; a refund posts a `receipt`. Stock on hand, the stock list and the ledger show POS consumption with no extra code. |
| Partners (004) | Optional customer on a bill (a receipt for a company). |
| Rule registry (012, 013) | Every POS business check is a boolean rule with a default, set with `rule_set`, named in the error's `rules`. |
| Error model (ADR-0004), order of checks, list envelope | Unchanged; POS adds error codes to the same registry. |
| Per-tenant lock | Issuing a bill, numbering and stock posting are one serialized transaction. |
| Document model: header and lines | Bills follow it, with the deviation of ADR-0022/2 (line operations on an open bill). |

## 3. What the ERP lacks today and must gain first

| Missing | Why the POS cannot start without it | Spec |
|---|---|---|
| **Article categories** | Menu by category; reports by category. | P01 |
| **Barcodes** | Search and scan. | P01 |
| **Sales prices** | Orders carry a typed-in price; an article has none. | P01 |
| **Tax rates** | No tax exists anywhere yet (018 was to bring it). A receipt must show tax per rate. | P01 |
| **Payment methods** | Nothing is paid in the ERP yet (019). | P01 |
| **Operators and what they may do** | Only API keys exist; every key may do everything (021 deferred). | P02 |
| **Recipes (normatives) and modifiers** | A sold coffee must consume coffee and milk. | P03 |
| **Idempotent writes** | No operation is safe to repeat after a lost response. | P05 |
| **A source document other than an order on a stock document** | The issue must point at its bill. | P07 |
| **Number series per premises and device** | A fiscal receipt number is not `SI-000123`. | P07 (fixed form), 017 later |
| **Turnover reports** | The ERP has stock reports only. | P08 |

P01 is deliberately ERP master data with no POS in it: invoices (018) will use the same tax rates, prices and
payment methods.

## 4. Specs and the requirements they cover

The split of `PLAN.md` (P01–P10) is kept in substance. Three changes, each for a reason:

1. **Shifts are split and the first half moves before payment.** A payment must land in an open shift,
   otherwise the expected cash of a shift cannot be computed and the payment spec would be retrofitted.
2. **The bill and the payment are each cut in two** (P05 / P05a, P07 / P07a). Each was the size of specs 009
   and 010 together; the second halves (price change, discount and void; refund) are exactly the parts that
   need operator rights and can follow.
3. **Fiscalisation is separated from the device topics.** It is backend work with a legal deadline; the
   rest of old P10 is mostly the frontend's.

| Spec | Content | Was | Requirements (section of `requirements.md`) |
|---|---|---|---|
| **P01** | Sales master data: article categories, tax rates, payment methods; per article — category, tax rates, sales price, barcodes | P01 | 1: prices, taxes, payment methods through master data; search by barcode (the data). 7: POS uses existing articles, prices, taxes, units |
| **P02** | Operators, roles (what an operator may do), PIN sign-in and operator session; approval by a second operator | P02 | 2: "with the proper rights"; 3: waiter responsible; 5: refund with rights; 7: authorisation and audit |
| **P03** | Modifier groups and options on articles; recipes (ingredients per article and per option) | P03 | 1: options and add-ons; normatives and ingredients |
| **P04** | POS station (warehouse, premises and device code), the station's menu: which articles, category pages, button layout; one read that gives the frontend the whole menu; lookup by name and barcode | P04 | 1: articles shown on the POS, quick view by category, button layout, search |
| **P05** | The bill: open, add and remove lines, quantity, note per line, modifiers on a line, keep open, list and resume; `version`; **idempotency keys** | P05 (first half) | 2: open, add/remove, notes, save as open order, resume. 7 and 8: no duplicates, safe retry |
| **P05a** | On a bill, with rights: change a price, discount on a line or the bill, void a line or the whole bill with a reason | P05 (second half) | 2: price change, void with reason, discounts. 5: record of discounts |
| **P06** | Shift, part 1: open with a starting float, cash paid in and out, hand-over to another operator | P07 (first half) | 6: open, pay-ins and pay-outs, hand-over |
| **P07** | Payment and issue: cash, card, mixed; completed and failed payments; change; issuing the receipt (number, tax per rate); posting the stock issue, also by recipe; the receipt as data for print and reprint | P06 (first half) | 5: cash, card, combined, payment records, change, issue and copy, record of tax and method. 7: sale recorded, stock updated, ingredients by recipe, no double charge |
| **P07a** | After payment: refund bill (whole or part), reason, rights, return to stock or not | P06 (second half) | 5: reversal or refund after payment. 7: reversals correct the records |
| **P08** | Shift, part 2: close, expected cash, counted cash, difference; turnover by operator, shift, payment method, tax rate, category | P07 (second half) | 6: close, expected, counted, difference, report. 7: ERP reports include POS turnover |
| **P09** | Zones and tables (with a position for the floor plan), table status, bill on a table, waiter on a bill, move to another table, transfer to another waiter, split, merge | P08 | 3: all. 2: split, merge, move |
| **P10** | Preparation: preparation stations, routing of articles, send (also later additions only), ticket and line statuses with times, the feed for a kitchen screen or ticket printer | P09 | 4: all |
| **P11** | Fiscalisation (Croatia): outbox, status on the receipt, retries, what the receipt must show | P10 (part) | 5: fiscalisation. 8: clear message on failure, safe continuation |
| **P12** | Device results and offline: card-terminal outcome as an operation, print-job status if the owner wants it recorded, replay of an offline queue (bounded by what idempotency allows) | P10 (rest) | 5: card terminal. 8: interrupted connection, unfinished operations, optional offline |

Order of work: P01 -> P02 -> P03 -> P04 -> P05 -> P05a -> P06 -> P07 -> P07a -> P08 -> P09 -> P10 -> P11 ->
P12. A usable bar without tables exists after P08; `xerp-pos` can start after P04 (menu) and show something
after P05.

Not covered by any spec because it is not backend work: requirement 8, first four points (touch screen and
screen sizes, bar-code reader, receipt printer, order printers) — section 7.

## 5. The sale, end to end (target after P07)

1. An operator signs in on a station (P02) with an open shift (P06).
2. Opens a bill (P05), optionally on a table (P09). Lines are added from the menu (P04); price and tax are
   **copied onto the line** from the article's sales data (P01) at that moment — a later price change does
   not touch an open bill, and an issued bill never depends on master data.
3. Lines are sent to preparation (P10); the bill stays open.
4. Payment (P07): one or more payments. With the payment that covers the total, in one transaction under the
   tenant lock: the bill becomes `issued`, gets its number, its tax breakdown is fixed, the stock `issue` is
   created and posted, the payments are attached to the shift, the idempotency record is written.
5. After the commit: fiscalisation from the outbox (P11); the client prints.
6. A mistake after that is a refund bill (P07a).

## 6. Idempotency for payment and posting

ADR-0022/4 and /5. In short:

- Every POS write carries `Idempotency-Key`; the key and the response are stored in the same transaction as
  the effect, so "the response was lost" and "the request was lost" are indistinguishable to the client and
  both are answered by repeating the same request.
- A bill has a `version`; two devices cannot both change the same state of it.
- Issue, stock posting and numbering are one transaction: there is no state "paid but not posted" to repair.
- External effects are outside that transaction and have their own status and retry: card terminal (before
  — its outcome is an input), fiscalisation and printing (after).
- A card payment that the terminal approved but the backend never heard of is the one case software cannot
  close alone: the client keeps the terminal's outcome locally and reports it with the same key until it
  gets an answer; the shift report (P08) lists `pending` payments so that a person resolves the rest.

## 7. What is not in the backend, and what the frontend needs from the API

Out of the backend (the work of `xerp-pos`):

| Topic | Who | The backend's part |
|---|---|---|
| Touch UI, screen sizes, floor-plan drawing | frontend | Tables have a zone and a position (P09); the menu has pages and button positions (P04). |
| Bar-code reader | frontend (keyboard wedge) | Lookup by barcode (P01, P04). |
| Receipt and ticket printing | frontend or a local print bridge | The receipt and the preparation ticket as complete JSON documents (P07, P10); "copy" is a re-read, marked as a copy. No printer protocol. |
| Card terminal | frontend or a local bridge | Payment `pending` -> `completed` / `failed` with the terminal's reference (P07, P12). |
| Offline | frontend: a local queue of operations with their idempotency keys | Safe replay. What cannot be done offline is a legal and a numbering question (section 9, decision 8). |

What the frontend needs from the API, and where it comes from:

- **One call for the menu** of a station — articles with name, price, tax, category, modifiers, button
  layout — with a version so it can be cached (P04).
- **Change feeds** it can poll cheaply: open bills, table statuses, preparation tickets, each with
  `updatedSince` and a cursor (P05, P09, P10). Server push (SSE) is an optimisation for later; polling at
  one or two seconds is enough for one venue.
- **Idempotency keys and `version`** on every write (P05).
- **Errors it can show**: stable `code`, `errors` per field, `rules` naming the switch — as today.
- **Operator session**: sign-in by PIN, fast switch of operator on one device (P02).
- **Server time** in responses (preparation timers must not depend on a tablet's clock) (P10).
- **A description of the contract.** OpenAPI is spec 022 and deferred; until then the specs are the
  contract and `xerp-pos` is built from them. Recommended: bring 022 forward after P05.

## 8. Standing constraints for every POS spec

- Tenant-scoped queries only; no `IgnoreQueryFilters()`.
- Every business check is a boolean rule with a default, or an invariant with a reason in `docs/rules.md`.
  Operator rights are data on a role (ADR-0022/6).
- One MCP tool per operation.
- A spec is small enough for one loop turn; where it is not, it is cut at a seam named in the spec.
- Amounts: the tenant's one currency, 2 decimals, half away from zero (I8, I10). Prices on the POS are
  **tax-inclusive** (P01).

## 9. Open owner decisions

Each has a default the specs use until the owner says otherwise.

| # | Question | Default | Affects |
|---|---|---|---|
| 1 | Does "recorded in the common sales module" mean the issued bill **is** the sales record (no sales order, no delivery)? | Yes (ADR-0022/2). | P07, 018 |
| 2 | Stock at the till: if stock on hand is short, is the payment refused? | The tenant's `stock.negativeStockAllowed` decides, as for every issue; a venue sets it to `true`. Alternative: a POS sale always posts, whatever that rule says. | P07 |
| 3 | One stock issue per bill, or one per shift? | Per bill, in the payment's transaction. | P07 |
| 4 | Sales prices include tax? | Yes: one price per article, tax-inclusive. Price lists (second price level, happy hour, net prices for companies) later. | P01, P04 |
| 5 | Several taxes on one article (VAT and the local consumption tax on drinks)? | Yes: an article has zero to four tax rates, all computed on the same net base. | P01, P07, P11 |
| 6 | Is a device's API key limited to POS operations? Today every key can do everything, including changing rules and prices (021 deferred). | Not limited. **Recommended: a "POS only" key in P02.** | P02 |
| 7 | May an agent (MCP) act as an operator? | Yes, with an operator's code and PIN, like a person. | P02 |
| 8 | Fiscalisation: who signs and sends — the backend (the tenant's certificate is stored on the server) or the device? Which rules apply at go-live (the current regime or the 2026 one), and is offline issue allowed at all? | Backend, from an outbox, after the payment commits. **The legal details are not verified by the architect and must be confirmed with an accountant before P11.** | P07 (receipt number: premises / device / sequence), P11, P12 |
| 9 | Receipt for a company (buyer's tax id on the receipt)? | Optional partner on the bill; no e-invoice. | P07 |
| 10 | Tips, service charge, vouchers, loyalty, happy hour, split by guest seat | Not in scope; none is in the requirements. | — |
| 11 | Categories: one level or a tree? | One level; the menu pages of P04 do the grouping on screen. | P01, P04 |
| 12 | Kitchen screen: polling or push? | Polling. | P10 |
| 13 | Specs 014 and 015 are deferred. Valuation (015) decides what a sold coffee cost; until then POS turnover has no margin. | Deferred as the owner said. | P08 |
