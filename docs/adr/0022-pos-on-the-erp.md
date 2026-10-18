# ADR-0022: The POS is a module of the ERP — bill, stock posting, idempotency, operators

Status: proposed by the architect, 2026-10-13. Decisions 2, 4, 5 and 8 contain defaults the owner may change
(`docs/pos/overview.md`, section 9).
Context documents: `docs/pos/requirements.md` (owner, 2026-10-13), `docs/pos/overview.md`.

## Context

The owner wants a hospitality POS (tables, kitchen, cash drawer, fiscal receipts) built on this ERP, with its
own frontend (`xerp-pos`). The requirements say: the POS uses the ERP's articles, prices, taxes and units;
a confirmed sale is recorded in the common sales module; stock moves through the existing stock logic, also
by recipe; ERP reports include POS turnover without an import; nothing is charged or recorded twice.

Four things have to be decided before the first POS spec, because every later spec rests on them.

## Decision

### 1. One backend, one more module

The POS is not a service and has no database of its own. It is a set of Domain types and Application
operations in the same solution (`Pos/` in each layer), exposed like everything else: HTTP under
`/api/v1/pos/…` and one MCP tool per operation (`pos_…`). `xerp-pos` is a thin HTTP client with a tenant API
key, like `xerp-web` and `xerp-cli` (architecture §7). Tenant scoping, the error model, the rule registry,
attribution and the per-tenant lock are the existing ones. What the POS needs from the ERP that is not
POS-specific (categories, barcodes, prices, tax rates, payment methods) is built as ERP master data, not
inside `Pos/` (spec P01).

### 2. A POS bill is its own document type, not a sales order

`PosBill`: a header (station, table, operator, optional customer, totals) and lines (article, quantity,
unit price, discount, tax, note, modifiers). States: `open` -> `issued` (paid; numbered; immutable), or
`open` -> `voided` (abandoned before payment, with a reason).

- It follows the header-and-lines model (architecture §5) with **one stated deviation**: an open bill is
  changed by line operations (add, change quantity, void a line), not by replacing the whole document.
  Several devices work on one bill during an evening and lines already sent to the kitchen must keep their
  identity; a whole-document replace would lose both.
- A line that was sent to preparation is never deleted; it is voided, with a reason and an operator.
- **An issued bill is the ERP's record of the sale** — the retail kind of what spec 018 will call a sales
  invoice. No sales order and no delivery is created for it. ERP turnover reports read issued bills; spec
  018 builds invoices on the same tax rates and payment methods, and reports both together.
- Correction after issue is a new document: a **refund bill** that names the original (`refundOf`), with
  negative amounts, its own number and its own payments. Nothing of an issued bill changes (I4).

### 3. A paid bill posts stock through an ordinary stock document

When a bill is issued, in the same transaction and under the tenant lock, the server creates and posts one
stock document of type `issue` in the bill's warehouse, with the bill as its source document (`posBill` on
the header, like `salesOrder`). It is posted by the existing posting code: ledger, stored balance, numbering,
`stock.negativeStockAllowed`.

- Its lines are derived, never sent by the client: a `stock` article issues itself; an article with a
  recipe (spec P03) issues its ingredients (recipe quantity × sold quantity, in base units), also those of
  chosen modifiers; a `service` article without a recipe issues nothing. Quantities are summed per article.
  A bill that issues nothing has no stock document.
- A refund bill posts a `receipt` linked to it, only for the lines the operator marks as returned to stock
  (a returned bottle, not a returned meal). A whole-document reversal (006) is not used: refunds are often
  partial.
- The stock document is read-only for callers: it cannot be created, replaced or reversed by hand while it
  names a bill.

### 4. Idempotency: a key per request, kept with the effect

Every POS operation that creates or moves something (open a bill, add a line, send to preparation, pay,
issue, refund, cash movement, open or close a shift) **requires** an idempotency key: HTTP header
`Idempotency-Key` (1–100 printable characters), MCP argument `idempotencyKey`.

- The server stores, per tenant, the key, the operation, a fingerprint of the request and the response —
  **in the same transaction as the effect**. A repeated request with the same key returns the stored
  response (HTTP header `Idempotency-Replayed: true`) and does nothing. The same key with another operation
  or body is `409 IDEMPOTENCY_KEY_REUSED`. A duplicate that arrives while the first is running waits on the
  tenant lock and then replays.
- Refusals that depend on state (`409`) are not stored: the same key may be retried after the cause is
  removed. Keys are kept for at least 7 days (a platform bound, I10).
- Independently, an open bill has a `version` that every change increments; a change names the version it
  saw and is refused with `409 BILL_CHANGED` if another device changed the bill in between.
- State is the last guard: a bill is issued once, a shift closed once, a payment completed once.
- The mechanism is generic (`Idempotency-Key` on any `POST`), delivered with spec P05 and required only by
  POS operations. Existing ERP operations are unchanged.
- *Refined by spec P05 (2026-10-18):* only successes are stored; the mechanism is one reusable Application
  piece, honoured by the operations of P05 and of each later spec that opts in (other operations ignore the
  header); `version` is optional on adding lines (adds commute) and required on every other bill change; the
  change feed uses an opaque, commit-ordered cursor instead of a timestamp.

### 5. Payments are records with an outcome; issue happens with the covering payment

A payment has a method (spec P01), an amount and a status `pending` -> `completed` | `failed`. The backend
never talks to a card terminal: the client (or its device bridge) reports the outcome. A cash payment is
completed when recorded; change is computed by the server. The bill is issued in the same transaction as the
completed payment that covers its total — there is no moment in which a bill is paid and not issued. Failed
payments stay on record.

### 6. Operators are not API keys

A device has one API key; many waiters use it. `Operator` is a tenant master with a PIN (stored hashed).
Signing in with code and PIN gives a short-lived operator session bound to the key; POS operations name it.
Every POS record carries both the key (`createdBy`, I3) and the operator. What an operator may do
(change a price, discount, void, refund, cash movement, close a shift) is a set of booleans on the
operator's **role** — tenant data, with our defaults — and a sensitive act may carry the approval of a
second operator who has the right. This is not spec 021 (permissions per key), which stays as planned.

### 7. Rules

Every POS check that can refuse an operation is a boolean rule on the existing registry (group `pos`, or the
group of the master it judges) or an invariant in `docs/rules.md` (ADR-0020). Operator rights are data
(decision 6), not rules: they differ per person, not per tenant.

### 8. What is not in the backend

Printing, the touch UI, the bar-code reader, the card terminal's protocol and offline operation are the
client's. The backend gives the data they need: a printable representation of a receipt and of a
preparation ticket, payment and fiscal status, and idempotency so that a queue of offline operations can be
replayed safely. Fiscalisation is the backend's (the receipt is its record): it happens **after** the
transaction that issues the bill, from an outbox, with a status on the bill (`pending`, `done`, `failed`)
and retries — a failed or slow tax service never undoes a payment (spec P11; which party signs is an owner
decision).

## Alternatives

- **A bill is a sales order plus a delivery.** Rejected: an order is immutable once confirmed, needs a
  customer by default, has no tax, discount or payment, and means "to be delivered later". A café would
  create two documents per coffee and still need a third for the payment. The requirement "recorded in the
  common sales module" is met by decision 2: the bill is a sales document of the ERP.
- **Stock issued once per shift** (one summary issue at close). Rejected as the default: stock is wrong
  during the shift, a refund has nothing to point at, and a failed posting is discovered hours later.
  Offered to the owner as an option if the number of stock documents becomes a concern.
- **Ingredients deducted by a production document** (a prepared article is produced, then issued).
  Rejected: nobody records production of a coffee; the recipe is applied at the sale.
- **Client-generated ids instead of idempotency keys.** Covers "create", not "pay" or "send".
- **Operators as API keys.** A key per waiter on a shared tablet cannot be switched in a second, and a key
  can do everything in the ERP (until 021).
- **A separate POS backend that syncs to the ERP.** It is the import the owner excluded.

## Consequences

- New source document on stock documents (`posBill`); stock documents created by a bill are not editable.
- `stock.negativeStockAllowed` = `false` can refuse a payment at the till (the goods are already on the
  table). Default: the rule is obeyed and a tenant that runs a POS is advised to set it to `true`; the
  alternative is an owner decision (overview §9).
- A table of idempotency records, tenant-owned, pruned by age.
- Many small `issue` documents (one per bill that moves stock) and gapless `SI-` numbers shared with manual
  issues, until number series (017).
- Valuation (015, deferred) will have to cost recipe ingredients at the sale; nothing here prevents it.
- The API contract is fixed by specs only (OpenAPI is spec 022, deferred); `xerp-pos` is built by the
  orchestrator against the specs.
