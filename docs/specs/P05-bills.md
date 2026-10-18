# Spec P05 — The bill: open, lines, quantities, notes, modifiers, keep open, resume; version; idempotency; change feed

Status: ready for the tester and the builder.
Owner requirements of 2026-10-13 (`docs/pos/requirements.md`): open a bill, add and remove articles, notes,
save as an open order, resume (2); no duplicates, safe retry after a lost connection (7, 8).
Branches: `tests/P05-bills` (tester), `feat/P05-bills` (builder).
Read first: `docs/pos/overview.md` §5–§7, ADR-0022/2 and /4, spec **P01** (sales data, tax rates), **P02**
(sessions R13–R14, `pos`-key allow-list R32), **P03** (modifier groups and options, R4, R6, R7), **P04**
(stations, the menu read R20, `serverTime`), architecture §5, §6, §11.

**Inherited, not re-specified**: everything specs 001–P04 established. This spec states only what is new.

**Nothing in this spec takes money or moves stock.** A bill here is `open` (or `voided` while empty). Price
change, discount and void of lines with a reason are P05a; payment, issue and the tax breakdown are P07;
tables and transfer to another waiter are P09; sending to preparation is P10.

**Seam.** Part A (bills and lines, 4.A–4.B) and part B (the change feed 4.C, and the idempotency mechanism
section 6.6 as a reusable piece). B's idempotency is needed by A's acceptance criteria; if one loop turn is
too short, the feed (4.C, AC-50..AC-53) follows as P05b with its criteria as numbered.

*(default)* marks an architect's decision the owner may change; all are collected in section 12.

## 1. Goal

Two tablets and a register in one bar. Ana opens a bill on `BAR-1` with two espressos and a latte with oat
milk ("no sugar"), keeps it open and serves another guest; Marko on `BAR-2` sees the bill appear within two
seconds, adds a beer, and Ana resumes it. A tablet that lost its connection after tapping "add" repeats the
request with the same idempotency key and gets the same answer — no second beer. Two devices changing one
line at once cannot both win: the second is told the bill changed.

`xerp-pos` today keeps bills in the browser with lines `{ articleId, qty, options, note }`; this API replaces
that store one-for-one (section 11).

## 2. Scope

In scope
- A. **Bills** `/pos/bills`: open (optionally with lines), get, list, change the label, void an **empty** bill.
- A. **Lines**: add (one or more), replace (quantity, options, note), remove.
- A. Price, taxes, option names and price deltas **copied onto the line** when it is added (overview §5).
- A. `version` on every bill; `BILL_CHANGED` on a stale change.
- B. **Idempotency keys** on every write of this spec (ADR-0022/4), as a reusable Application mechanism.
- B. **Change feed** `GET /pos/bills/changes` with a cursor; `serverTime` on every bill and on the feed.
- Five rules (6.1); new error codes `IDEMPOTENCY_KEY_REUSED`, `BILL_CHANGED`, `BILL_NOT_EMPTY`,
  `ARTICLE_NOT_SALEABLE`, `MODIFIERS_INVALID`.
- 9 MCP tools; `tools/list` returns 139 for a `full` key.

Out of scope
- Price change, discounts, void of a line or of a non-empty bill, reasons, rights (P05a). Removing a line here
  needs no right: nothing has been sent or paid (P10 makes a sent line unremovable).
- Payment, receipt number, tax breakdown, customer on a bill (P07). Tables, waiter transfer, split, merge
  (P09). Preparation (P10). Server push (polling the feed is enough, overview §7).
- Idempotency on operations of other specs: the header is **ignored** outside this spec's operations until a
  spec opts in (6.6, R61).
- Who may change another operator's bill (P09, with transfer).

## 3. Data

New tenant-owned tables, audit columns and composite foreign keys as architecture §8 / ADR-0002.

| Table | Columns besides `Id`, `TenantId`, audit | Keys |
|---|---|---|
| `PosBills` | `StationId`, `WarehouseId`, `Status`, `Label` null, `OperatorId` null, `Version` int, `ChangeSeq` bigint, `OpenedAt`, `VoidedAt` null, `VoidedByOperatorId` null, `VoidReason` null | index `(TenantId, ChangeSeq)`, `(TenantId, Status, OpenedAt)`; FK to `PosStations`, `Warehouses`, `PosOperators` (restrict) |
| `PosBillLines` | `BillId`, `Position` int, `ArticleId`, `Name`, `Quantity` numeric(18,6), `UnitPrice` numeric(18,6), `Note` null, `OperatorId` null, `AddedAt`, `UpdatedAt` | unique `(TenantId, BillId, Position)`; FK to `PosBills` (cascade), `Articles`, `PosOperators` (restrict) |
| `PosBillLineTaxes` | `LineId`, `TaxRateId`, `Code`, `Name`, `Rate`; no audit | FK to `PosBillLines` (cascade), `TaxRates` (restrict) |
| `PosBillLineOptions` | `LineId`, `OptionId`, `GroupId`, `Code`, `Name`, `PriceDelta` numeric(18,6), `Position` int; no audit | unique `(TenantId, LineId, OptionId)`; FK to `PosBillLines` (cascade), `ModifierOptions` (restrict) |
| `IdempotencyRecords` | `Key`, `Operation`, `Fingerprint`, `StatusCode`, `Response` (text/jsonb), `CreatedAt`; no audit | unique `(TenantId, Key)` |

`ChangeSeq` comes from a per-tenant counter incremented **under the tenant lock** by every bill write (R50).
Where the counter lives is the builder's choice. One new migration; no existing table changes.

## 4. Operations — HTTP

All under `/api/v1`, tenant key. Every write below **requires** the header `Idempotency-Key` (6.6) and
returns the whole Bill.

### 4.A Bills — `/pos/bills`

Bill:

```json
{ "id": "uuid", "status": "open", "version": 4,
  "station": { "id": "uuid", "code": "BAR-1", "name": "Bar tablet 1" },
  "warehouse": { "id": "uuid", "code": "BAR", "name": "Bar" },
  "label": "Terasa — Marko", "operator": { "id": "uuid", "code": "ANA", "name": "Ana" },
  "lines": [ Line ], "total": 7.6,
  "openedAt": "…Z", "voidedAt": null, "voidedBy": null, "voidReason": null,
  "createdAt": "…Z", "updatedAt": "…Z", "createdBy": "uuid", "updatedBy": "uuid",
  "serverTime": "…Z" }
```

Line:

```json
{ "id": "uuid", "position": 2, "article": { "id": "uuid", "code": "LATTE", "name": "Latte" },
  "name": "Latte", "quantity": 1, "unitPrice": 2.5,
  "taxRates": [ { "id": "uuid", "code": "VAT25", "name": "VAT 25 %", "rate": 25 } ],
  "options": [ { "id": "uuid", "groupId": "uuid", "code": "OAT", "name": "Oat milk", "priceDelta": 0.3 } ],
  "note": "no sugar", "lineTotal": 2.8,
  "operator": { "id": "uuid", "code": "ANA", "name": "Ana" }, "addedAt": "…Z", "updatedAt": "…Z" }
```

`operator` (bill) is who opened it; `operator` (line) who added it; both `null` when written without a
session (rule `pos.operatorSessionRequired` = `false`). `name`, `unitPrice`, `taxRates` and the option
`code`, `name`, `priceDelta` are **copies** taken when the line or option was added (R12); `article` is the
live summary (ADR-0008). Lines are ordered by `position`.

LineInput: `{ "articleId": uuid, "quantity"?: number, "optionIds"?: [uuid], "note"?: string|null }` —
`quantity` omitted = `1`, `optionIds` omitted = `[]`, `note` omitted = `null`.

| Operation | Request | Success | Errors |
|---|---|---|---|
| Open | `POST /pos/bills` body `{ stationId, sessionId?, label?, lines?: [LineInput] }` | `201` Bill + `Location` | 400, 409 `REFERENCE_NOT_FOUND`, `REFERENCE_INACTIVE`, `SESSION_NOT_ACTIVE`, `ARTICLE_NOT_SALEABLE`, `MODIFIERS_INVALID`, `IDEMPOTENCY_KEY_REUSED` |
| Get | `GET /pos/bills/{id}` | `200` | 404 |
| List | `GET /pos/bills?status=&stationId=&operatorId=&limit=&offset=` | `200` list envelope of Bill, newest `openedAt` first, then `id` | 400 |
| Change label | `PUT /pos/bills/{id}` body `{ version, sessionId?, label }` | `200` | 400, 404, 409 `INVALID_STATE`, `BILL_CHANGED`, `REFERENCE_NOT_FOUND`, `SESSION_NOT_ACTIVE`, `IDEMPOTENCY_KEY_REUSED` |
| Void (empty) | `POST /pos/bills/{id}/void` body `{ version, sessionId?, reason? }` | `200`, `status: voided` | 400, 404, 409 `INVALID_STATE`, `BILL_CHANGED`, `BILL_NOT_EMPTY`, `REFERENCE_NOT_FOUND`, `SESSION_NOT_ACTIVE`, `IDEMPOTENCY_KEY_REUSED` |

`status` filter: `open` or `voided` (`issued` arrives with P07); an unknown value -> `400`, key `status`.

### 4.B Lines — `/pos/bills/{id}/lines`

| Operation | Request | Success | Errors |
|---|---|---|---|
| Add | `POST /pos/bills/{id}/lines` body `{ version?, sessionId?, lines: [LineInput] }` (1–200) | `200` Bill | 400, 404, 409 `INVALID_STATE`, `BILL_CHANGED`, `REFERENCE_NOT_FOUND`, `REFERENCE_INACTIVE`, `SESSION_NOT_ACTIVE`, `ARTICLE_NOT_SALEABLE`, `MODIFIERS_INVALID`, `IDEMPOTENCY_KEY_REUSED` |
| Replace | `PUT /pos/bills/{id}/lines/{lineId}` body `{ version, sessionId?, quantity, optionIds, note }` | `200` Bill | as Add, and 404 for the line |
| Remove | `POST /pos/bills/{id}/lines/{lineId}/remove` body `{ version, sessionId? }` | `200` Bill | 400, 404, 409 `INVALID_STATE`, `BILL_CHANGED`, `REFERENCE_NOT_FOUND`, `SESSION_NOT_ACTIVE`, `IDEMPOTENCY_KEY_REUSED` |

A line of another bill is `404`. Replace carries every member (`note` may be `null`); the article of a line
never changes (remove it and add another).

### 4.C Change feed (B)

`GET /pos/bills/changes?since=&stationId=&limit=` ->

```json
{ "items": [ Bill ], "cursor": "opaque string", "hasMore": false, "serverTime": "…Z" }
```

`limit` 1–500, default 50. Without `since`: the **open** bills, in change order — a snapshot to start from.
With `since` (a `cursor` from an earlier response): every bill, of any status, changed after it, in change
order. A client polls with the last `cursor` (R50–R53). Errors: 400 (malformed `since`, key `since`).

### 4.D Error codes (registry: architecture §6)

| HTTP | `code` | Meaning | `errors` keys |
|---|---|---|---|
| 409 | `IDEMPOTENCY_KEY_REUSED` | The key was already used in this tenant for another operation or another request body. | `idempotencyKey` |
| 409 | `BILL_CHANGED` | The `version` sent is not the bill's current version: another device changed it. Re-read and retry. | `version` |
| 409 | `BILL_NOT_EMPTY` | Void of a bill that has lines (P05a voids such bills, with a right and a reason). | `lines` |
| 409 | `ARTICLE_NOT_SALEABLE` | The article cannot be sold on this bill: it has no sales price (`pos.unpricedArticleSellable` = `false`) or is not on the station's menu (`pos.saleOutsideMenuAllowed` = `false`). Always with `rules`. | `lines[i].articleId` |
| 409 | `MODIFIERS_INVALID` | The chosen options do not fit the article's modifier groups (R14). | `lines[i].optionIds` or `lines[i].optionIds[j]`; in replace `optionIds…` |

*(Extended)* `INVALID_STATE`: a change of a bill that is not `open`; an add that would give a bill more than
200 lines (key `lines`). `IN_USE`: delete of a station, warehouse, article, tax rate, modifier option or
operator that a bill or a bill line names. `VALIDATION_FAILED`: a missing or malformed idempotency key, key
`idempotencyKey` (also over HTTP).

## 5. Operations — MCP

One tool per operation, same fields, limits and errors (ADR-0009), covered by the parity test. Every write
tool takes a **required** `idempotencyKey` argument (the header's value). `tools/list` for a `full` key
returns the 130 of P04 and these 9:

| Tool | Input | Output | Annotations |
|---|---|---|---|
| `pos_bill_open` | `{ idempotencyKey, stationId, sessionId?, label?, lines? }` | Bill | not read-only, not destructive, idempotent |
| `pos_bill_get` | `{ id }` | Bill | read-only |
| `pos_bill_list` | `{ status?, stationId?, operatorId?, limit?, offset? }` | list envelope | read-only |
| `pos_bill_changes` | `{ since?, stationId?, limit? }` | the feed | read-only |
| `pos_bill_update` | `{ idempotencyKey, id, version, sessionId?, label }` | Bill | destructive, idempotent |
| `pos_bill_void` | `{ idempotencyKey, id, version, sessionId?, reason? }` | Bill | destructive, idempotent |
| `pos_bill_line_add` | `{ idempotencyKey, id, version?, sessionId?, lines }` | Bill | not destructive, idempotent |
| `pos_bill_line_update` | `{ idempotencyKey, id, lineId, version, sessionId?, quantity, optionIds, note }` | Bill | destructive, idempotent |
| `pos_bill_line_remove` | `{ idempotencyKey, id, lineId, version, sessionId? }` | Bill | destructive, idempotent |

"Idempotent" means: with the same `idempotencyKey` (R60). A replayed tool call returns the stored result
unchanged. All nine are on the `pos`-key allow-list (P02/R32: routes under `/api/v1/pos/`).

## 6. Rules

### 6.1 Rules this spec introduces

| Key | Name | Default | Switching it to the other value |
|---|---|---|---|
| `pos.operatorSessionRequired` | Every bill write names a signed-in operator | `true` | `false`: `sessionId` may be omitted or `null`; the bill or line then has `operator: null`. A given `sessionId` is still checked. |
| `pos.unpricedArticleSellable` | An article without a sales price may be put on a bill | `false` | `true`: the line gets `unitPrice` `0` (a price is set later, P05a). |
| `pos.saleOutsideMenuAllowed` | An article that is not on the station's menu may be put on a bill | `true` | `false`: such an article -> `409 ARTICLE_NOT_SALEABLE` with `rules`. |
| `pos.sameLinesMerged` | Adding an article that is already on the bill with the same options adds to that line | `true` | `false`: every add makes a new line. |
| `posBill.labelRequired` | A bill must have a label | `false` | `true`: `label` omitted, `null` or empty on open or change label -> `400 VALIDATION_FAILED`, key `label`, with `rules`. |

Groups `pos`, `posBill`. The registry then has **66** rules. No stored bill is re-checked when a rule changes
(012/R15): a bill opened without a session stays valid after `pos.operatorSessionRequired` becomes `true`,
but its next change must name one.

### 6.2 Every other check is an invariant (`docs/rules.md` §2)

| Check | Invariant | Reason |
|---|---|---|
| `quantity`: a JSON number `> 0`, at most 6 decimals, at most 999999999.999999 | I8, I10 | A line sells something; storage bounds. Zero is "remove". |
| `label`: `null` or 1–100 characters, single line; `note`, `reason`: `null` or 1–2000, multi-line text | I9, I10 | Column lengths (a label is a reference, 100). |
| At most 200 lines on a bill; `optionIds` at most 20 distinct UUIDs per line | I10 | Bound of a document (I10) and of the article's groups (P03). |
| Options fit the article's groups (R14) | I8 | `isRequired` and `allowsMultiple` are data of a group (P03 §4.A); this is what they mean. |
| A line's unit price with its options (`unitPrice + Σ priceDelta`) is `>= 0`; `lineTotal` at most 9999999999.99 | I8, I10 | A sale line does not pay the guest (refunds are P07a); amount bound. |
| Prices and taxes are copied onto the line at add | I8 | Overview §5: a later price change does not touch an open bill. |
| `version`, `BILL_CHANGED`; a change of a bill that is not `open` | I8 | Two devices cannot both change one state; an ended document is not changed. |
| Idempotency (6.6) | I8 | Exactly-once effect of a repeated request (ADR-0022/4). |
| A new bill names an active station; references exist; inactive is not newly referenced; a referenced record is not deleted | I1, I6 | ADR-0008. |
| A session is the calling key's and active | I2 | P02/R13–R14. |
| The warehouse is the station's at open and stays | I11 | Every sale issues from one warehouse. |

**This spec adds no check that is not in one of the two tables.**

### 6.3 Bills and lines (A)

- R1. Open: validation (`idempotencyKey`; `stationId` a UUID; `sessionId` a UUID or `null`, required by
  `pos.operatorSessionRequired`; `label` with `posBill.labelRequired`; each LineInput; all reported together)
  -> under the tenant lock: idempotency (R60) -> `stationId` (`REFERENCE_NOT_FOUND`; inactive ->
  `REFERENCE_INACTIVE`) -> session (R5) -> lines (R10). Success: `status: open`, `version` 1, `warehouse` =
  the station's warehouse, `openedAt` = now, `operator` = the session's operator or `null`; `201`.
- R2. Every change (change label, void, add, replace, remove), order of checks: validation -> bill exists
  (`404`; for replace and remove also the line, `404`) -> under the tenant lock: idempotency (R60) -> bill is
  `open` (`409 INVALID_STATE`) -> `version` equals the bill's (`409 BILL_CHANGED`, key `version`; on add only
  when `version` is given, R4) -> session (R5) -> the operation's own checks. Success: `version` + 1,
  `updatedAt`, `updatedBy`, a new `ChangeSeq` (R50).
- R3. A refusal changes nothing — no line, no `version`, no `ChangeSeq`, no idempotency record.
- R4. `version` is **required** on change label, void, replace and remove, and **optional** on add: adding
  lines does not depend on what another device just did, and two waiters adding to one bill must not refuse
  each other *(default)*. A given `version` on add is checked.
- R5. Session: a given `sessionId` must be a session of the calling key (`409 REFERENCE_NOT_FOUND`, key
  `sessionId`, as P02/R20 step 2) and active (`409 SESSION_NOT_ACTIVE`). Omitted or `null` with
  `pos.operatorSessionRequired` = `true` -> `400 VALIDATION_FAILED`, key `sessionId`, with `rules`.
- R6. Change label sets `label` (with `posBill.labelRequired`). Void: the bill has no lines, else `409
  BILL_NOT_EMPTY`; success: `status: voided`, `voidedAt` = now, `voidedBy` = the session's operator or
  `null`, `voidReason` = `reason`. A voided bill can be read and is never changed again.
- R7. A bill stays open until voided (later: issued). Nothing closes it by time, by the end of a session, or
  by deactivation of its station or operator; it may be resumed from any device of the tenant.
- R8. List: `status`, `stationId`, `operatorId` filter (AND); a well-formed id naming nothing -> empty;
  malformed -> `400`.

### 6.4 Adding lines (A)

- R10. Each LineInput, in order: `articleId` exists (`REFERENCE_NOT_FOUND`) and is active (`REFERENCE_INACTIVE`)
  -> saleable (R11) -> each `optionIds[j]` exists (`REFERENCE_NOT_FOUND`, key `lines[i].optionIds[j]`) ->
  options fit (R14). The first failing line answers alone, with its first failing check. Then the bound:
  existing + new lines (after merging, R13) `> 200` -> `409 INVALID_STATE`, key `lines`.
- R11. Saleable: with `pos.unpricedArticleSellable` = `false`, an article whose sales data has
  `salesPrice` `null` (or no sales data) -> `ARTICLE_NOT_SALEABLE` with that rule; with
  `pos.saleOutsideMenuAllowed` = `false`, an article not in the station's menu read (P04/R20, the station's
  **current** menu) -> `ARTICLE_NOT_SALEABLE` with that rule; both at once -> one error, two elements in
  `rules`. Any article type may be sold (`stock` or `service`).
- R12. Copies, at the moment of the add: `name` = the article's name; `unitPrice` = its `salesPrice` (or `0`
  by R11); `taxRates` = its sales data's tax rates (id, code, name, rate), in their order (possibly empty);
  each option's `code`, `name`, `priceDelta`, `groupId`, in the order of `optionIds`. `operator` = the
  session's operator; `addedAt` = `updatedAt` = now; `position` = the bill's highest position ever used + 1
  (positions are never reused within a bill).
- R13. With `pos.sameLinesMerged` = `true`, a LineInput whose `note` is `null` is **merged** into an existing
  line of the bill (or an earlier input of the same request) that has the same article, the same set of
  option ids, `note` `null`, and the same copied `unitPrice` and option `priceDelta`s as a new copy would
  have: that line's `quantity` grows, its `updatedAt` = now; it keeps its `id`, `position` and `operator`. A
  line with a note is never merged into, and a LineInput with a note always makes a new line *(default; as
  `xerp-pos` does)*.
- R14. Options fit the article when: every option belongs to one of the article's modifier groups (P03 §4.A)
  that is **active**, and is itself active — except an option the line already has (replace, ADR-0008); no
  option twice (`400`, key `optionIds[j]`); a group with `allowsMultiple` `false` has at most one; a group
  with `isRequired` `true` that has at least one active option has at least one chosen (a required group
  with no active option asks nothing, P03/R7). Failures -> `409 MODIFIERS_INVALID`, key `lines[i].optionIds[j]`
  for a foreign or inactive or second-of-a-single option, `lines[i].optionIds` for a missing required group.
- R15. `lineTotal` = (`unitPrice` + Σ option `priceDelta`) × `quantity`, rounded to 2 decimals half away
  from zero (I8, I10); `total` = Σ `lineTotal`. A negative unit price with options, or a `lineTotal` above the
  bound -> `400 VALIDATION_FAILED`, key `lines[i].quantity` (in replace `quantity`). The tax breakdown is not
  part of a bill until P07.

### 6.5 Replacing and removing lines (A)

- R20. Replace sets `quantity`, `optionIds`, `note` (all required; `optionIds` may be `[]`, `note` `null`).
  Options checked as R14 with key `optionIds[j]` / `optionIds`. Options the line already had keep their
  copied values; newly chosen ones are copied now. `unitPrice`, `name`, `taxRates` and `operator` of the line
  do not change; `updatedAt` = now. Replace never merges lines.
- R21. Remove deletes the line. Any line of an open bill can be removed in this spec (P10 forbids it for a
  sent line; P05a adds void with a reason). Removing the last line leaves an open, empty bill.
- R22. Delete of a station, warehouse, article, tax rate, modifier option or operator that any bill or bill
  line (any status) names -> `409 IN_USE` (amends P01, P02/R7, P03/R6, P04 §4.A). Deactivating them is allowed
  and changes no bill.

### 6.6 Idempotency (B)

- R60. Every write of this spec requires a key: HTTP header `Idempotency-Key`, MCP argument
  `idempotencyKey`; 1–100 printable ASCII characters (`!`–`~`, no space). Missing or malformed -> `400
  VALIDATION_FAILED`, key `idempotencyKey`, reported with the other validation errors. Under the tenant lock,
  before any state check:
  1. no record with this key in the tenant -> run the operation; on **success** store `(key, operation,
     fingerprint, status, response)` **in the same transaction** as the effect;
  2. a record with the same operation and fingerprint -> return the stored status and body unchanged
     (including its `version` and `serverTime`), HTTP header `Idempotency-Replayed: true`; nothing is done and
     nothing is checked again (an ended session or a changed bill does not matter);
  3. a record with another operation or fingerprint -> `409 IDEMPOTENCY_KEY_REUSED`.
  The fingerprint covers the operation, the path ids and the request body as parsed JSON (member order and
  whitespace do not matter). Keys are per **tenant**, shared by its API keys.
- R61. Only successes are stored; a refusal (`400`, `404`, `409`) stores nothing, so the same key may be sent
  again after the cause is removed. Records are kept at least 7 days, then may be pruned (I10). The header is
  ignored by every operation outside this spec until its spec opts in; reads ignore it.
- R62. Two requests with the same key at the same time: the second waits on the tenant lock and then replays
  (step 2). Exactly one effect.
- R63. The mechanism is one Application piece (a key, an operation name, a fingerprint, a callback that
  produces the effect and the response inside the transaction), reused by P05a–P12.

### 6.7 Change feed and server time (B)

- R50. Every successful bill write (open, change label, void, add, replace, remove) gives the bill a
  `ChangeSeq` from the tenant's counter, assigned under the tenant lock, so the order of `ChangeSeq` is the
  order of commits. A refusal or a replay gives none.
- R51. Without `since`: the bills with `status: open` (and `stationId` if given), ordered by `ChangeSeq`, at
  most `limit`. With `since`: every bill (any status) with `ChangeSeq` greater than the cursor's, same order.
  A bill appears once, in its latest state, however often it changed.
- R52. `cursor`: with `hasMore` `true`, the position of the last item; with `hasMore` `false`, the tenant's
  current position (not lower than `since`). Polling with an unchanged state returns `items: []` and the same
  cursor. `stationId` filters items, not the cursor. The cursor is opaque; a malformed one -> `400`.
- R53. A client keeps a set of open bills: it starts with the snapshot, then for each item replaces its copy,
  and drops it when the status is not `open`. A void on another device thus reaches every device.
- R54. `serverTime` is the server's clock when the response was produced, on every Bill and on the feed
  (overview §7). A replay returns the stored one.

### 6.8 POS keys

- R70. All operations of this spec are on the `pos`-key allow-list (P02/R32, routes under `/api/v1/pos/`) and
  are allowed to `full` keys.

## 7. Edge cases

- E1. Add espresso, add espresso -> one line, `quantity` 2 (`pos.sameLinesMerged` `true`); with a note on the
  second -> two lines; with `false` -> two lines.
- E2. Price of espresso changed between two adds -> two lines with their own `unitPrice` (no merge across
  prices); the bill's first line keeps the old price.
- E3. A latte with `MILK` required and `optionIds: []` -> `MODIFIERS_INVALID`, key `lines[0].optionIds`;
  two options of `MILK` (single) -> key of the second; an option of a group the article does not have -> key
  of that option; an unknown option id -> `REFERENCE_NOT_FOUND`.
- E4. An option deactivated after it was put on a line: replace with the same `optionIds` -> `200`; a new add
  with it -> `MODIFIERS_INVALID`.
- E5. Tablet A and B read version 3; A replaces line 1 -> version 4; B removes line 2 with `version` 3 ->
  `BILL_CHANGED`; B re-reads and removes with 4 -> `200`. A and B both **add** without `version` -> both
  `200`, version 5 then 6.
- E6. Add with key `k1` -> `200`, version 2; the same request again -> the same body, version 2,
  `Idempotency-Replayed: true`, still one line; the same key with `quantity` 2 -> `IDEMPOTENCY_KEY_REUSED`;
  the same key on remove -> `IDEMPOTENCY_KEY_REUSED`.
- E7. A remove refused with `BILL_CHANGED` under `k2`; re-read; the same key `k2` with the new version ->
  `200` (nothing was stored for the refusal; the body differs but no record exists).
- E8. Open with `k3` returns `201`; the response is lost; the session ends; the same request -> `201` with the
  same bill id (replay before the session check).
- E9. Void of an empty bill -> `voided`; any change after it -> `INVALID_STATE`; the feed reports it once
  with `status: voided`; a void of a bill with a line -> `BILL_NOT_EMPTY`.
- E10. A station deactivated while a bill is open on it: the bill can still be changed; a new bill on it ->
  `REFERENCE_INACTIVE`, key `stationId`.
- E11. `quantity` `0`, `-1`, `1.0000001`, `"1"` -> `400`; `0.5` -> `200` (a half litre).
- E12. 200 lines and one more distinct add -> `INVALID_STATE`, key `lines`; one more add merged into an
  existing line -> `200`.

## 8. Tenant isolation

- T1. All tables tenant-owned and filtered; another tenant's bill or line is `404`; another tenant's station,
  article, option or session in a body is `REFERENCE_NOT_FOUND`.
- T2. Idempotency keys are per tenant: X and Y may use the same key for different requests and each gets its
  own effect; Y never receives X's stored response.
- T3. The feed and its cursor see only the tenant's bills; Y using X's cursor sees only Y's changes.
- T4. Rules set in X change nothing in Y. T5. The same through the tools. No `IgnoreQueryFilters()`.

## 9. Security requirements

- S1. Authentication and the `pos`-key limit as P02, with R70.
- S2. No input causes `500` — in particular not two parallel requests with one key, an add racing an article
  delete, or a replace racing a remove of the same line.
- S3. Every write is attributed (I3) to the key, and to the operator where a session is named.
- S4. A stored response is returned only to a request of the same tenant with the same key and fingerprint.
- S5. A bill read and the feed are computed in a fixed number of queries, not one per line.

## 10. Acceptance criteria

Conventions of 012 §10, 013 §10, P01 §10. One tenant per test. "Write" = a request with a fresh
idempotency key unless the criterion says otherwise.

Structure
- AC-01 *(manual)* Build and tests exit 0; earlier tests pass unweakened. The **only** earlier tests changed:
  (1) the number or literal list of rules: 61 -> 66; (2) the number or literal list of tools: 130 -> 139;
  (3) delete tests of stations, warehouses, articles, tax rates, options and operators gain the `IN_USE` case
  of R22 only if they assert "always allowed"; (4) a rule search whose expected result now also matches a new
  key or name.
- AC-02 *(manual)* The five decisions are booleans from `IRules` passed to Domain functions; idempotency is one
  Application piece (R63) used by all six writes; `ChangeSeq` is assigned in one place under the lock.
- AC-03 *(builder, unit)* Pure functions: option fit (R14), merge decision (R13), line total and rounding
  (R15), saleable (R11) at both rule values, fingerprint stability (member order and whitespace irrelevant).

Registry and tools
- AC-10 New tenant `GET /rules?limit=500` -> `total == 66`: the 61 of P04 AC-10 and
  `pos.operatorSessionRequired` `true`, `pos.saleOutsideMenuAllowed` `true`, `pos.sameLinesMerged` `true`,
  `pos.unpricedArticleSellable` `false`, `posBill.labelRequired` `false`.
- AC-11 Each of the five accepts both values and resets.
- AC-12 `tools/list` (full key) returns exactly 139 names; for open, add, replace, remove and get the tool
  result is JSON-equal to HTTP apart from `serverTime`, `id`s and audit times; each write tool without
  `idempotencyKey` -> tool error `VALIDATION_FAILED`, key `idempotencyKey`.

A — bills and lines
- AC-20 Open (R1): with a session and two LineInputs -> `201`, `Location`, `version` 1, `status` open,
  `warehouse` the station's, `operator` the session's, lines with positions 1, 2, copied `name`, `unitPrice`,
  `taxRates`, options and `lineTotal`, `total`; without lines -> empty bill. Get returns the same.
- AC-21 Open refusals: unknown station -> `REFERENCE_NOT_FOUND` `stationId`; inactive -> `REFERENCE_INACTIVE`
  (E10); another key's session -> `REFERENCE_NOT_FOUND` `sessionId`; an ended session ->
  `SESSION_NOT_ACTIVE`; no session -> refused by (`VALIDATION_FAILED`, `pos.operatorSessionRequired`, `true`),
  key `sessionId`; Set it `false` -> `201` with `operator: null`.
- AC-22 Copies (R12, E2): change the article's price and an option's `priceDelta` and name after the add ->
  the line keeps the old values, `article.name` shows the new name; a new add gets the new price on a new line.
- AC-23 Merge (R13, E1) at both values of `pos.sameLinesMerged`, also two equal inputs in one request.
- AC-24 Options (R14, E3, E4): every case of E3 and E4 with the stated keys; a required group with no active
  option asks nothing; an option given twice -> `400`.
- AC-25 Saleable (R11): an unpriced article -> refused by (`ARTICLE_NOT_SALEABLE`, `pos.unpricedArticleSellable`,
  `false`); Set `true` -> line with `unitPrice` 0; Set(`pos.saleOutsideMenuAllowed`, `false`) and an article off
  the station's menu -> refused by (`ARTICLE_NOT_SALEABLE`, that rule, `false`); both -> two `rules`; an
  inactive article -> `REFERENCE_INACTIVE`, key `lines[i].articleId`; the first failing line answers alone.
- AC-26 Quantity and totals (R15, E11): the cases of E11; `lineTotal` of 3 × (1.15 + 0.3) = 4.35; a total over
  several lines; a negative unit price with options -> `400`.
- AC-27 Replace (R20): quantity, options and note change; kept options keep copied values, new ones are copied
  now; missing member -> `400`; another bill's line -> `404`; replace does not merge.
- AC-28 Remove (R21): a line is removed, positions of others unchanged, the next add gets a new position; the
  last line removed -> open empty bill.
- AC-29 Label and void (R6, E9): change label; Set(`posBill.labelRequired`, `true`) -> open without label
  refused by (`VALIDATION_FAILED`, rule, `true`), key `label`; void of an empty bill -> `voided` with
  `voidedBy`, `voidReason`; void with a line -> `BILL_NOT_EMPTY`; every change of a voided bill ->
  `INVALID_STATE`.
- AC-30 Version (R2, R4, E5): every change increments by 1; E5 as written; a refusal does not increment (R3).
- AC-31 List (R8): by `status`, `stationId`, `operatorId`, order newest first; unknown `status` -> `400`.
- AC-32 Bound (E12) and `IN_USE` (R22) for each of the six masters; after deactivation the bill still reads.

B — idempotency, feed, server time
- AC-40 Replay (R60, E6): each of the six writes repeated with its key -> the same status and body and
  `Idempotency-Replayed: true`; the effect happened once (line count, `version`, feed).
- AC-41 Reuse (E6): the same key with another body or on another operation -> `IDEMPOTENCY_KEY_REUSED`, key
  `idempotencyKey`, nothing changed; member order and whitespace of the same body -> replay.
- AC-42 Refusals not stored (R61, E7, E8): E7 and E8 as written; header missing, empty, 101 characters, with a
  space -> `400`, key `idempotencyKey`.
- AC-43 Parallel (R62): ten parallel identical adds with one key -> exactly one line added, ten equal bodies.
  Ten parallel adds with ten keys and no `version` -> ten effects, `version` 11.
- AC-44 Scope (R61): `Idempotency-Key` on a P04 write or a stock document create is ignored (two requests,
  two effects or the usual refusal).
- AC-50 Snapshot and polling (R51, R52): a first call returns the open bills and a cursor; a call with it and
  no change -> `items: []`, the same cursor; after an add on one bill and a void of another -> exactly those
  two, latest state, once each; `hasMore` paging with `limit` 1 visits each changed bill once.
- AC-51 Refusals and replays move no cursor (R50); `stationId` filters; malformed `since` -> `400`.
- AC-52 `serverTime` on a Bill and on the feed is within a few seconds of the test's clock; a replay returns
  the stored one.
- AC-53 MCP `pos_bill_changes` equals the HTTP feed.

POS key, isolation
- AC-60 R70: a `pos` key performs every operation of this spec over HTTP and tools.
- AC-61 Isolation (T1–T3): Y cannot read or change X's bill, cannot reference X's station or session; the
  same idempotency key in X and Y gives two effects; Y's feed never shows X's bill.
- AC-62 X sets the five rules to the other value; in Y the defaults of AC-21, AC-23, AC-25 and AC-29 hold.

## 11. Notes for the tester and the builder, and for `xerp-pos`

Tester
- Do not pin the form of the cursor, nor of `serverTime` beyond ISO-8601 UTC. One tenant per test. Approved
  changes to earlier tests are exactly those of AC-01; anything else -> `docs/questions/P05-q.md`.

Builder
- Every write runs inside `SerializedPerTenantAsync`: the idempotency lookup, the checks, the effect, the
  `ChangeSeq` and the stored response are one transaction (R60, R50).
- The Application operations take `idempotencyKey` and return the response the API will send; the HTTP layer
  maps the header and `Idempotency-Replayed`; the MCP layer maps the argument. The stored response is the
  serialized result, so a replay is byte-for-byte the first answer apart from the header.
- The P02 allow-list needs no change if it already admits every `/api/v1/pos/` route (R70).
- Anything unclear: `docs/questions/P05-q.md`, then continue.

`xerp-pos` (`public/app.js`) — what replaces the browser store

| Today | With P05 |
|---|---|
| `newBill()` + first `addArticle` | `POST /pos/bills` with `stationId`, `sessionId` and the first line (a bill is created only when it gets its first line, as today). |
| `addArticle(article, options, note)` and its merge of equal lines | `POST /pos/bills/{id}/lines` `{ lines: [{ articleId, optionIds: options.map(o => o.id), note }] }`; the server merges (R13) as the client does today. |
| `changeQty`, `sheetNote` | `PUT …/lines/{lineId}` with the line's `quantity`, `optionIds`, `note` and the bill's `version`. |
| `removeLine` | `POST …/lines/{lineId}/remove` with `version`. |
| `openBills()`, `sheetBills()` | The set kept from `GET /pos/bills/changes` (snapshot, then polling every 1–2 s with `cursor`). |
| line `qty`, `unitPrice`, `options[].priceDelta`, `taxRates`, `name` | `quantity` (renamed), the rest the same; `lineTotal` and `total` come from the server. |
| `bill.label` "Račun N" | `label` is optional server data; the client may keep showing its own counter when `label` is `null`. |
| Lost connection | Keep each write with its idempotency key (e.g. `crypto.randomUUID()`) in a local queue and resend it unchanged until an answer arrives; on `BILL_CHANGED` re-read and rebuild the request with a **new** key. |

## 12. Decisions behind this spec

Open owner decisions of the overview §9 that touch it: **4** (one tax-inclusive price, copied onto the line),
**7** (an agent acts with an operator's session, like a person), **10** (no split by seat, no tips).

Architect's defaults, each until the owner says otherwise:

| # | Question | Default | Where |
|---|---|---|---|
| 1 | Must every bill write name a signed-in operator? | Yes (`pos.operatorSessionRequired`); a tenant without operators switches it off | 6.1 |
| 2 | Can an unpriced article be sold? | No (`pos.unpricedArticleSellable`); yes means price `0`, changed in P05a | 6.1 |
| 3 | Can the till sell an article that is not on its menu (barcode, agent)? | Yes (`pos.saleOutsideMenuAllowed`) | 6.1 |
| 4 | Is a repeated tap one line with a higher quantity? | Yes, unless a note makes the line individual (`pos.sameLinesMerged`) | R13 |
| 5 | Does an add need the bill's `version`? | No, optional; every other change needs it (refines ADR-0022/4) | R4 |
| 6 | May a line be removed without a right? | Yes, in P05 (nothing is sent or paid); P10 and P05a restrict it | R21 |
| 7 | What becomes of an abandoned empty bill? | It can be voided without a right; it stays as a record so that the feed reports it | R6 |
| 8 | Change feed by timestamp (`updatedSince`) or by cursor? | By an opaque cursor over a commit-ordered sequence; a timestamp misses commits that land out of order | R50–R52 |
| 9 | Idempotency on the operations of earlier specs? | Not yet; each later spec opts in (refines ADR-0022/4 "generic on any POST") | R61 |
| 10 | May a waiter change another waiter's bill? | Yes, until P09 brings the waiter of a bill and transfer | §2 |
