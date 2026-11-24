# Spec 017 — Number series per company

Status: ready for the tester and the builder after spec 015 is merged.
Owner goal of 2026-11-24: a production-ready ERP, everything except fiscalisation (`docs/roadmap.md`,
"Production plan", step 4). Owner rule: every business choice is the tenant's, with our default (`CLAUDE.md`).
Branches: `tests/017-number-series` (tester), `feat/017-number-series` (builder).
Read first: spec 005 R17 (numbers at posting, gapless, under the tenant lock), `DocumentSeries` and
`DocumentCounter` (`src/Xerp.Domain/Inventory`), spec I01 (invoice numbers `RN-`), spec 012 (rules).

**The sentence that governs everything below: a tenant that configures nothing gets exactly the numbers it
gets today** (`SR-000001`, `PO-000001`, `RN-000001`, …).

## 1. Goal

A company that moves to Xerp in the middle of a year must continue its own numbering ("2026-0147" follows
"2026-0146" of the old program), and many companies restart invoice numbers every year
(`1/2026`, `2/2026`). After this spec every document type has a **series** per tenant: a pattern, the number
of digits, whether it restarts every year, and the next number — all the tenant's, through HTTP and MCP.

## 2. Scope

In scope: one series per numbered document type; read and change it; numbering at posting/confirmation/issue
follows it; a collision is refused, never repaired silently.
Out of scope: several series per type (e.g. per shop or per cash register — that comes with fiscalisation,
whose number is `n/premises/device`), date-effective series lines, numbering of drafts, renumbering of
existing documents.

## 3. Data

`DocumentCounter` (today: tenant, document type, last number) becomes the **series**:

| Field | Type | Default | Meaning |
|---|---|---|---|
| `key` | text | — | The document type: `stockReceipt`, `stockIssue`, `stockTransfer`, `stockCount`, `purchaseOrder`, `salesOrder`, `workOrder`, `invoice` (the keys of `DocumentSeries`; existing counter rows keep theirs). |
| `pattern` | text, 1–40 | `SR-{n}` etc. (today's prefix + `-{n}`) | Tokens: `{n}` the sequence number (required, exactly once), `{yyyy}` and `{yy}` the year (R4). Any other character is literal; allowed: letters, digits, `-`, `/`, `.`, `_`. |
| `digits` | int 1–10 | 6 | `{n}` is padded with zeros to this many digits; never truncated. |
| `yearlyRestart` | bool | `false` | `true`: the sequence restarts at 1 every year (R4). |
| counters | (year or none) -> last number | — | One counter without a year while `yearlyRestart` is false; one per year while it is true. |

A migration creates the eight series for every tenant with the defaults and carries the existing last numbers
into the yearless counter. No document changes.

## 4. Operations

| Method and path | MCP tool | Meaning |
|---|---|---|
| `GET /number-series` | `number_series_list` | The eight series of the tenant, ordered by key, each as in R1. No paging (fixed size). |
| `GET /number-series/{key}` | `number_series_get` | One series. Unknown key -> `404 NOT_FOUND`. |
| `PUT /number-series/{key}` | `number_series_set` | Body `{ pattern, digits, yearlyRestart, nextNumber? }`. Returns the series. |

`tools/list` grows by 3. Annotations: list/get read-only; set not read-only, idempotent, not destructive.

- R1. A series reads `{ key, pattern, digits, yearlyRestart, nextNumber, preview, updatedAt, updatedBy }`.
  `nextNumber` is the number the next document would get — for a yearly series, in the current UTC year —
  and `preview` the formatted number it would get today.
- R2. **Validation** (`400 VALIDATION_FAILED`, all keys together): `pattern` as in §3, `{n}` exactly once, at
  most one year token; `digits` 1–10; `yearlyRestart: true` requires a year token in `pattern` (otherwise
  `1/2026` and `1/2027` would both read `1`) — key `pattern`; `nextNumber`, when given, an integer ≥ 1.
- R3. **nextNumber** may only be raised: a value below the current next number of the counter it addresses
  (the current year's for a yearly series) is `409 INVALID_STATE` ("numbers below N are or may be in use").
  Omitted: unchanged. This is how a company continues the numbering of its previous program.
- R4. **The year** of a number is the year of the document's own date — `documentDate` of a stock document,
  `orderDate` of an order, `issueDate` of an invoice, the completion date of a work order — not of the
  moment of posting. With `yearlyRestart` the counter of that year is used (created at 0 when first needed),
  so a document dated in December posted in January takes December's year.
- R5. **Changing the series** affects only numbers given afterwards; existing documents keep theirs.
  Switching `yearlyRestart` on starts the yearly counters at the current yearless counter's value for the
  current year (numbers continue, they do not restart in the middle of a year) and at 0 for other years;
  switching it off continues from the highest number of any counter. The change takes the tenant lock, like
  posting.
- R6. **Collision.** Numbers are unique per document type and tenant (today's unique indexes, compared
  case-insensitively). If the number a posting would get already exists (possible after a pattern change),
  the posting is refused with `409 NUMBER_TAKEN`, key `number`, detail naming the series and telling to change
  the pattern or raise `nextNumber`; the counter does not advance; nothing is posted. Never skip silently:
  a gap in invoice numbers is a legal problem.
- R7. Numbers stay gapless: they are given under the tenant lock in the posting transaction, as today
  (005/R17). A refused posting consumes nothing.
- R8. Formatted numbers are at most 40 characters; a pattern whose number would exceed it with the counter's
  value is refused at `PUT` (`400`, key `pattern` or `digits`).

## 5. Rules

None new. The series is tenant data with our defaults, not a yes/no rule of the registry — a pattern is not a
switch (architecture §11, "data of a series, not rules"; roadmap row 17). Who may change a series is a
permission (021, deferred by the owner): until then every key may.

## 6. Tenant isolation and security

- Series and counters are tenant-owned (`ITenantOwned`, the global filter); tenant Y never sees or changes
  X's series; a tenant's numbers never depend on another tenant's.
- `PUT` takes the tenant lock; a concurrent posting sees the series before or after the change, never a mix.
- No input causes `500`.

## 7. Acceptance criteria (selection; the tester completes them from R1–R8)

- AC-01 Defaults: in a new tenant the eight series read today's patterns, `digits` 6, `yearlyRestart`
  false, `nextNumber` 1; every earlier test passes unchanged (the only changes: tool count +3).
- AC-02 Existing tenants: after the migration a tenant with 5 posted receipts reads `nextNumber == 6` for
  `stockReceipt`, and the next receipt is `SR-000006`.
- AC-03 `PUT invoice {pattern: "{n}/{yyyy}", digits: 1, yearlyRestart: true}`; two invoices issued this year
  are `1/<year>`, `2/<year>`; an invoice dated last year is `1/<last year>`.
- AC-04 Migration from another program: `PUT invoice {…, nextNumber: 147}` -> next invoice `147/<year>`;
  then `nextNumber: 100` -> `409 INVALID_STATE`.
- AC-05 Collision: receipts `SR-000001..3` exist; `PUT stockReceipt {pattern: "SR-{n}", digits: 6,
  nextNumber: 3}` is refused by R3; set `pattern: "{n}"`, `digits: 1`, post, then back to `SR-{n}` with
  digits 6 and `nextNumber` left: the next posting that would produce an existing number answers
  `409 NUMBER_TAKEN`, the draft stays a draft and the counter did not advance.
- AC-06 Validation of R2 and R8, each key reported together.
- AC-07 Tenant isolation; MCP parity (the same errors and bodies as HTTP).
- AC-08 Concurrency: 20 parallel postings of receipts with a yearly pattern give 20 distinct consecutive
  numbers.

## 8. Decisions behind this spec

| # | Question | Default | Owner? |
|---|---|---|---|
| 1 | One series per document type, or several (per premises/device)? | One; several come with fiscalisation. | — |
| 2 | Year of the number: document date or posting moment? | Document date (R4). | may change |
| 3 | May `nextNumber` be lowered? | No (R3): lowering is how duplicates and gaps happen. | — |
| 4 | Collision: refuse or skip to the next free number? | Refuse (R6): a skipped invoice number is a gap. | — |
| 5 | Yearly restart as a registry rule or a series field? | A series field: it is per document type. | — |
