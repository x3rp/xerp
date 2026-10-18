# Spec P05a — Price change, discounts on a line or the bill, void of a line or the bill with a reason

Status: ready for the tester and the builder.
Owner requirements of 2026-10-13 (`docs/pos/requirements.md`): price change "with the proper rights", void
with a reason, discounts (2); a record of discounts (5); authorisation and audit (7).
Branches: `tests/P05a-price-discount-void` (tester), `feat/P05a-price-discount-void` (builder).
Read first: spec **P05** (whole), **P02** §6.5 (`AuthorizeAsync`, R20–R22) and R11 (PIN counting),
`docs/pos/overview.md` §5, ADR-0022/2 and /6, architecture §6, §11.

**Inherited, not re-specified**: everything specs 001–P05 established — in particular P05's order of checks
(R2), idempotency (6.6), `version`, the change feed and `serverTime`. This spec states only what is new.

**Every act here is sensitive.** Each names a right of P02 (`changePrice`, `discount`, `voidLine`,
`voidBill`), is checked by P02's `AuthorizeAsync` — the session's operator has the right, or a second
operator approves with code and PIN — and records on the bill who did it, who approved it, when, and why.
Nothing is paid here; the tax breakdown over discounted lines is P07's, on the amounts fixed here.

**Seam.** Part A (price change and discounts, 4.A–4.B) and part B (voids, 4.C). If one loop turn is too short,
B follows as P05b with its criteria as numbered.

*(default)* marks an architect's decision the owner may change; all are collected in section 12.

## 1. Goal

A guest's latte was made with the wrong milk: Ana gives 50 % off the line; she has no `discount` right, so the
shift manager Iva types her code and PIN on Ana's tablet and the discount is recorded with both names. A
regular gets 10 % off the whole bill. A wine with no fixed price is sold at 0 and its price is set to 24.00.
A beer poured by mistake is voided with the reason "spilled" and stays on the bill as a voided line; a table
that left without ordering anything more has its bill voided with a reason.

## 2. Scope

In scope
- A. **Price change** of a line (its article's unit price; options keep their deltas). Right `changePrice`.
- A. **Line discount** and **bill discount**, each a percent or an amount, set or cleared. Right `discount`.
- B. **Void of a line**: it stays on the bill, `status: voided`, out of every total. Right `voidLine`.
- B. **Void of a bill with lines** (amends P05/R6). Right `voidBill`.
- The approver's code and PIN with every act (P02/R20); the record of each act on the bill.
- Four rules (6.1); new error codes `DISCOUNT_EXCEEDS_AMOUNT`, `LINE_REMOVAL_NOT_ALLOWED`; `BILL_NOT_EMPTY`
  is retired.
- 4 new MCP tools, one amended; `tools/list` returns 143 for a `full` key.

Out of scope
- Price lists, happy hour, automatic or coupon discounts, discount limits per role (overview §9 row 10).
- Discount and void reports (P08 lists them per shift); reason codes as a master (a reason is text).
- Anything after payment (refund is P07a).

## 3. Data

No new table. `PosBillLines` gains `Status` (`active` default), `OriginalUnitPrice` (= `UnitPrice` for
existing rows), `PriceChangedAt`, `PriceChangedByOperatorId`, `PriceChangeApprovedById`, `PriceChangeReason`;
`DiscountPercent` null, `DiscountAmount` null, `DiscountAt`, `DiscountByOperatorId`, `DiscountApprovedById`,
`DiscountReason`; `VoidedAt`, `VoidedByOperatorId`, `VoidApprovedById`, `VoidReason` — all null.
`PosBills` gains the same six discount columns and `VoidApprovedById`. Operator FKs restrict (P05/R22).
Computed amounts (`amount`, `billDiscountShare`, totals) may be stored or computed; they are as R30–R33.
One new migration.

## 4. Operations — HTTP

All under `/api/v1`, tenant key. Every write requires `Idempotency-Key` (P05 §6.6), takes `version`
(required) and `sessionId` (required, R1), may take `approverCode` + `approverPin` (both or neither), and
returns the whole Bill.

Act record (`ActRecord`): `{ "operator": summary, "approvedBy": summary|null, "at": "…Z", "reason": string|null }`.

Line, as P05, with these members added:

```json
{ "status": "active", "amount": 5.6, "originalUnitPrice": 2.5,
  "priceChange": null,
  "discount": { "percent": 50, "amount": 2.8, "operator": {…}, "approvedBy": {…}, "at": "…Z", "reason": "wrong milk" },
  "billDiscountShare": 0.28, "lineTotal": 2.52,
  "void": null }
```

`priceChange` and `void` are `ActRecord` or `null`; `discount` is `ActRecord` with `percent` (number or `null`)
and `amount` (the money taken off) or `null`. Bill, as P05, with `subtotal`, `discount` (same shape as a line
discount, or `null`), `discountTotal`, `total`, and `voidApprovedBy` (summary or `null`) added.

DiscountInput: `{ "percent": number }` or `{ "amount": number }` — exactly one member — or `null` (clear).

### 4.A Price change

| Operation | Request | Success | Errors |
|---|---|---|---|
| Change price | `POST /pos/bills/{id}/lines/{lineId}/price` body `{ version, sessionId, unitPrice, reason?, approverCode?, approverPin? }` | `200` Bill | 400, 404, 403 `OPERATOR_RIGHT_MISSING`, 409 `INVALID_STATE`, `BILL_CHANGED`, `REFERENCE_NOT_FOUND`, `SESSION_NOT_ACTIVE`, `OPERATOR_SIGN_IN_FAILED`, `DISCOUNT_EXCEEDS_AMOUNT`, `IDEMPOTENCY_KEY_REUSED` |

### 4.B Discounts

| Operation | Request | Success | Errors |
|---|---|---|---|
| Line discount | `POST /pos/bills/{id}/lines/{lineId}/discount` body `{ version, sessionId, discount: DiscountInput, reason?, approverCode?, approverPin? }` | `200` Bill | as change price |
| Bill discount | `POST /pos/bills/{id}/discount` body as line discount | `200` Bill | as change price, without the line `404` |

### 4.C Voids (B)

| Operation | Request | Success | Errors |
|---|---|---|---|
| Void line | `POST /pos/bills/{id}/lines/{lineId}/void` body `{ version, sessionId, reason?, approverCode?, approverPin? }` | `200` Bill | as change price |
| Void bill | `POST /pos/bills/{id}/void` body `{ version, sessionId?, reason?, approverCode?, approverPin? }` *(amends P05)* | `200` Bill, `status: voided` | as P05 without `BILL_NOT_EMPTY`, and 403 `OPERATOR_RIGHT_MISSING`, 409 `OPERATOR_SIGN_IN_FAILED` |

### 4.D Error codes (registry: architecture §6)

| HTTP | `code` | Meaning | `errors` keys |
|---|---|---|---|
| 409 | `DISCOUNT_EXCEEDS_AMOUNT` | A discount amount is larger than what it applies to — when set, or after a change that lowers that base (R33). | `discount`, or `quantity` / `unitPrice` of the change that lowered a line's own base |
| 409 | `LINE_REMOVAL_NOT_ALLOWED` | With `pos.lineRemovalAllowed` = `false`: a line cannot be removed, only voided. Always with `rules`. | `lineId` |

`BILL_NOT_EMPTY` (P05) is **retired**: no operation returns it after this spec. *(Extended)* `INVALID_STATE`:
any change of a voided line (replace, remove, price, discount, void). `OPERATOR_RIGHT_MISSING`,
`OPERATOR_SIGN_IN_FAILED`: as P02/R20, now returned by these acts.

## 5. Operations — MCP

Same fields, limits and errors (ADR-0009), each with `idempotencyKey`, covered by the parity test. The 139 of
P05 and these 4:

| Tool | Input | Annotations |
|---|---|---|
| `pos_bill_line_price_set` | `{ idempotencyKey, id, lineId, version, sessionId, unitPrice, reason?, approverCode?, approverPin? }` | destructive, idempotent |
| `pos_bill_line_discount_set` | `{ idempotencyKey, id, lineId, version, sessionId, discount, reason?, approverCode?, approverPin? }` | destructive, idempotent |
| `pos_bill_discount_set` | `{ idempotencyKey, id, version, sessionId, discount, reason?, approverCode?, approverPin? }` | destructive, idempotent |
| `pos_bill_line_void` | `{ idempotencyKey, id, lineId, version, sessionId, reason?, approverCode?, approverPin? }` | destructive, idempotent |

`pos_bill_void` gains `approverCode?`, `approverPin?`. Output: Bill. A PIN is an input and never in a result
(P02 §5). All are on the `pos`-key allow-list (P02/R32).

## 6. Rules

### 6.1 Rules this spec introduces

| Key | Name | Default | Switching it to the other value |
|---|---|---|---|
| `pos.voidReasonRequired` | A void of a line or of a bill with lines needs a reason | `true` | `false`: `reason` may be omitted. |
| `pos.priceChangeReasonRequired` | A price change needs a reason | `false` | `true`: `reason` omitted, `null` or empty -> `400 VALIDATION_FAILED`, key `reason`, with `rules`. |
| `pos.discountReasonRequired` | A discount needs a reason | `false` | `true`: as above, for setting a line or bill discount (not for clearing one). |
| `pos.lineRemovalAllowed` | A line may be removed from an open bill without a trace (P05 remove) | `true` | `false`: remove -> `409 LINE_REMOVAL_NOT_ALLOWED` with `rules`; a mistake is voided (with `voidLine` and a reason) and stays on record. |

With `pos.voidReasonRequired` = `true`, a missing reason -> `400 VALIDATION_FAILED`, key `reason`, with `rules`.
Group `pos`. The registry then has **70** rules. No stored bill is re-checked when a rule changes (012/R15).

### 6.2 Every other check is an invariant (`docs/rules.md` §2)

| Check | Invariant | Reason |
|---|---|---|
| A sensitive act names an active session of the calling key, whatever `pos.operatorSessionRequired` says | I2, I8 | A right belongs to a person; without an operator there is nobody to hold it. |
| Which right each act needs; approval by a second operator | ADR-0022/6, P02/R20 | Rights are role data; approval is P02's check, with its rule `pos.secondOperatorApprovalAllowed`. |
| `unitPrice`: a JSON number, `0 ≤ x ≤ 999999999.999999`, at most 6 decimals; with options the unit price stays `>= 0` | I8, I10 | Bounds of a unit price (P01); a sale line does not pay the guest (P05 §6.2). |
| `percent`: `0 < x ≤ 100`, at most 2 decimals; `amount`: `> 0`, at most 2 decimals, at most what it applies to | I8, I10 | A discount takes off something and not more than there is; amount precision. |
| `reason`: `null` or 1–2000 characters, multi-line text | I10 | Column length. |
| How amounts, discounts and the bill discount's distribution are computed (R30–R33) | I8 | Arithmetic and rounding (half away from zero, I8); P07 fixes taxes on these amounts. |
| A voided line or bill is never changed; a voided line is never deleted | I4, I8 | ADR-0022/2: a void is a record, not an erasure. |

**This spec adds no check that is not in one of the two tables.**

### 6.3 Every act (A, B)

- R1. Order of checks, extending P05/R2: validation (`sessionId` required here — `400 VALIDATION_FAILED`, key
  `sessionId`, **without** `rules`; `approverCode`/`approverPin` together, P02/R20 step 1; the reason rules;
  the act's own fields) -> bill and line exist (`404`) -> under the tenant lock: idempotency -> bill `open`
  (`INVALID_STATE`) -> `version` (`BILL_CHANGED`) -> session (P05/R5) -> line not voided (`INVALID_STATE`) ->
  the act's amount checks (`DISCOUNT_EXCEEDS_AMOUNT`) -> **authorize** (P02/R20 steps 4–5 with the act's
  right) -> effect. The amount checks come before authorize, so a request that cannot succeed does not
  spend an approver's PIN attempt.
- R2. Authorize refusals are as P02/R20: `403 OPERATOR_RIGHT_MISSING` (key `right` without approver,
  `approverCode` with one; with `rules` when `pos.secondOperatorApprovalAllowed` is `false`), `409
  OPERATOR_SIGN_IN_FAILED` (key `approverPin`). A wrong approver PIN is counted and committed although the act
  is refused (P02/R11); nothing else of the request is written, and no idempotency record is stored (P05/R61).
- R3. The act's record: `operator` = the session's operator, `approvedBy` = the approver when one was given
  and authorize answered with it, otherwise `null`; `at` = now; `reason` as sent (empty -> `null`). A later act
  of the same kind replaces the record (the bill keeps the latest; the full trail is the audit log, spec 020).
- R4. A replayed act (same key) is not authorized again (P05/R60 step 2).

### 6.4 Price change (A)

- R10. Sets the line's `unitPrice`; `originalUnitPrice` keeps the price copied at add (P05/R12) and never
  changes. Option `priceDelta`s are untouched. Setting the current price is allowed and recorded.
- R11. Right `changePrice`. The line's percent discount follows the new amount; an amount discount larger than
  the new amount -> `DISCOUNT_EXCEEDS_AMOUNT`, key `unitPrice` (R33).

### 6.5 Discounts (A)

- R20. Line discount: `percent` -> `discount.amount` = round2(`amount` × percent / 100), recomputed whenever the
  line's `amount` changes; `amount` -> fixed. Bill discount: the same on the bill's base (R31). Setting replaces
  an earlier discount of the same place. Right `discount`.
- R21. Clearing (`discount: null`) needs the session but **no right** and no reason: it only gives back money
  to the venue *(default)*. Clearing where there is no discount -> `200`, `version` + 1.
- R22. A line discount and the bill discount may be combined; the bill discount applies after line discounts.

### 6.6 Voids (B)

- R25. Void line: `status: voided`, `void` record; the line keeps all its data, is shown in place, and counts
  in no total and in no discount base. Right `voidLine`. The 200-line bound (P05/R10) counts voided lines.
- R26. Void bill (amends P05/R6): a bill with no **active** line (no lines, or only voided ones) is voided as in
  P05 — no right, no reason required, `voidApprovedBy` `null`. A bill with an active line needs the right
  `voidBill` (R1, R2) and, with `pos.voidReasonRequired`, a reason; its lines stay as they were. Here
  `sessionId` is required only when the bill has an active line; otherwise P05/R5 applies.
- R27. Remove (P05/R21) of a voided line -> `INVALID_STATE`; with `pos.lineRemovalAllowed` = `false`, remove
  of any line -> `LINE_REMOVAL_NOT_ALLOWED` (after the state checks of P05/R2, before anything else).
- R28. Merging (P05/R13) never merges into a line that is voided, has a discount, or has a price change.
  Replace (P05/R20) of a voided line -> `INVALID_STATE`; of a line with a discount or price change keeps them.

### 6.7 Amounts (A, B)

- R30. Per active line: `amount` = round2((`unitPrice` + Σ option `priceDelta`) × `quantity`) (P05's
  `lineTotal`); `lineTotal` = `amount` − line `discount.amount` − `billDiscountShare`. A voided line has
  `amount` as computed, `billDiscountShare` 0 and `lineTotal` 0.
- R31. Bill: `subtotal` = Σ `amount` of active lines; base of the bill discount = Σ (`amount` − line discount)
  of active lines; `discount.amount` = round2(base × percent / 100) or the fixed amount; `discountTotal` = Σ line
  discounts + bill discount; `total` = `subtotal` − `discountTotal` = Σ `lineTotal`.
- R32. The bill discount is distributed over active lines in proportion to their base (`amount` − line
  discount), each share rounded to 2 decimals half away from zero; the difference of the rounded shares to the
  bill discount goes to the line with the largest base (lowest `position` on a tie). Lines with base 0 get 0.
  Recomputed after every change of the bill (P07 computes taxes per line on `lineTotal`).
- R33. Any change (add is exempt — it only raises a base) after which a fixed discount exceeds its base is
  refused with `DISCOUNT_EXCEEDS_AMOUNT`: a line's own amount discount — key `quantity` (replace) or
  `unitPrice` (price change); the bill's fixed discount — key `discount` (for remove, void line, replace,
  price change and line discount alike). A percent discount never exceeds its base.

### 6.8 POS keys

- R40. All operations of this spec are on the `pos`-key allow-list (P02/R32) and allowed to `full` keys.

## 7. Edge cases

- E1. Ana (no `discount` right) sets 50 % on a 2.80 line without an approver -> `403`, key `right`; with Iva's
  code and PIN (Iva's role has `discount`) -> `200`, `discount.amount` 1.40, `approvedBy` Iva; with Iva's code
  and a wrong PIN -> `409 OPERATOR_SIGN_IN_FAILED`, key `approverPin`, Iva's failure count +1, no discount.
- E2. Iva approving her own act (Iva's session, approver Iva) -> `403`, key `approverCode` (P02/R20).
- E3. Bill discount 10 % on lines 2.80, 2.80, 4.40 (base 10.00) -> 1.00; shares 0.28, 0.28, 0.44. Bill
  discount 1.00 fixed on three lines of 1.00 -> each rounds to 0.33; the 0.01 difference goes to the largest
  base, a tie, so to position 1: shares 0.34, 0.33, 0.33. Totals equal Σ `lineTotal`.
- E4. A fixed bill discount of 5.00 on lines 4.00 and 3.00; voiding the 3.00 line -> `DISCOUNT_EXCEEDS_AMOUNT`,
  key `discount`; clearing the discount first, then voiding -> `200`.
- E5. A line of 3 × 2.00 with an amount discount of 5.00; replace to quantity 2 -> `DISCOUNT_EXCEEDS_AMOUNT`,
  key `quantity`; with a 50 % discount instead -> `200`, discount 2.00.
- E6. Unpriced wine sold at 0 (P05 `pos.unpricedArticleSellable` = `true`), price changed to 24.00 ->
  `unitPrice` 24, `originalUnitPrice` 0, `priceChange` with operator.
- E7. A voided line: replace, remove, price, discount, void -> `INVALID_STATE`; the next equal add makes a new
  line (no merge).
- E8. A bill whose every line is voided: void without a right -> `200`. A bill with one active line: void by an
  operator without `voidBill` and no approver -> `403`; without a reason (default rule) -> `400`, key `reason`,
  with `rules`.
- E9. `pos.operatorSessionRequired` = `false` and a discount without `sessionId` -> `400`, key `sessionId`, no
  `rules`.
- E10. `discount` `{}`, `{ "percent": 10, "amount": 1 }`, `{ "percent": 0 }`, `{ "percent": 100.001 }`,
  `{ "amount": 0.001 }` -> `400`, key `discount` or `discount.<member>`; `{ "percent": 100 }` -> `200`
  (`lineTotal` 0).

## 8. Tenant isolation

- T1. As P05/T1; an approver code is looked up in the tenant of the key only (another tenant's operator ->
  `OPERATOR_SIGN_IN_FAILED`). T2. Rules set in X change nothing in Y. T3. The same through the tools.
  No `IgnoreQueryFilters()`.

## 9. Security requirements

- S1. A PIN appears in no response, log line, stored idempotency response or error (P02 I2). The fingerprint of
  a request may cover the PIN only as part of a one-way hash of the body.
- S2. The approver's failed PIN count is committed in its own transaction and cannot be avoided by a refusal
  later in the request; a lock-out applies at once (P02/R11).
- S3. No input causes `500`; every write attributed (I3) to the key and recorded with the operator and approver.

## 10. Acceptance criteria

Conventions of 012 §10, P01 §10, P05 §10. One tenant per test; operators with roles prepared by `full` key.

Structure
- AC-01 *(manual)* Build and tests exit 0; earlier tests pass unweakened. The **only** earlier tests changed:
  (1) rules 66 -> 70; (2) tools 139 -> 143; (3) P05 tests expecting `BILL_NOT_EMPTY` now expect
  `OPERATOR_RIGHT_MISSING` (no right, no approver) or `400` key `reason` — whichever R26 gives first for that
  request; (4) P05 tests comparing a whole Bill or Line gain the members of §4; (5) a rule search whose result
  now matches a new key.
- AC-02 *(manual)* Every act calls P02's `AuthorizeAsync`; no second rights check exists; the four rules come
  from `IRules`.
- AC-03 *(builder, unit)* Pure functions: amounts and distribution (R30–R32, E3), the exceed check (R33),
  discount input shape (E10).

Registry and tools
- AC-10 New tenant: `total == 70`; the 66 of P05 and `pos.discountReasonRequired` `false`,
  `pos.lineRemovalAllowed` `true`, `pos.priceChangeReasonRequired` `false`, `pos.voidReasonRequired` `true`.
- AC-11 Each of the four accepts both values and resets.
- AC-12 `tools/list` returns exactly 143; each act through its tool equals HTTP (as P05 AC-12), including an
  authorize refusal's `code`, `errors` and `rules`.

A — price and discounts
- AC-20 Price (R10, R11, E6): with the right -> new `unitPrice`, `originalUnitPrice` kept, record with
  `approvedBy` `null`; a second change keeps `originalUnitPrice`; bounds -> `400`; options' deltas unchanged.
- AC-21 Rights and approval (R1, R2, E1, E2) for each of the four acts: no right -> `403` key `right`; approver
  with the right -> `200` with `approvedBy`; approver without -> `403` key `approverCode`; wrong approver PIN ->
  `409` key `approverPin`, the approver's count incremented, bill unchanged, `version` unchanged; approver code
  without PIN -> `400`; Set(`pos.secondOperatorApprovalAllowed`, `false`) -> `403` with `rules`.
- AC-22 Line discount (R20, E1, E10): percent and amount; recomputed after a quantity change; replace by another
  discount; clear without a right -> `200`.
- AC-23 Bill discount (R22, R31, R32, E3): both examples of E3 exactly; a line discount and a bill discount
  together; `total == Σ lineTotal == subtotal − discountTotal`.
- AC-24 Exceeding (R33, E4, E5): both cases as written; an add after a fixed bill discount -> `200`, shares
  redistributed.
- AC-25 Reason rules: Set(`pos.priceChangeReasonRequired`, `true`) and Set(`pos.discountReasonRequired`, `true`)
  -> missing reason refused by (`VALIDATION_FAILED`, rule, `true`), key `reason`; clearing a discount needs none.
- AC-26 Merge and replace (R28): no merge into a discounted or re-priced line; replace keeps them.

B — voids
- AC-30 Void line (R25, E7): `status: voided`, record, out of totals and discount base; every change of it ->
  `INVALID_STATE`; default rule without reason -> `400` with `rules`; Set(`pos.voidReasonRequired`, `false`) ->
  `200` without reason.
- AC-31 Void bill (R26, E8): every case of E8; a voided bill keeps its lines and amounts; the feed reports it.
- AC-32 Removal (R27): Set(`pos.lineRemovalAllowed`, `false`) -> remove refused by
  (`LINE_REMOVAL_NOT_ALLOWED`, rule, `false`), key `lineId`; void still works.
- AC-33 Session (R1, E9): every act without `sessionId` -> `400`, no `rules`, also with
  `pos.operatorSessionRequired` = `false`.

Idempotency, POS key, isolation
- AC-40 Each act replayed with its key -> same body, applied once, not authorized again (R4) — also after the
  approver's PIN changed. An act refused by authorize and repeated with the same key after the approver signs
  correctly -> `200` (R2: nothing was stored).
- AC-41 No response, tool result, problem document or stored replay contains `approverPin` or its value (S1).
- AC-60 A `pos` key performs every act over HTTP and tools (R40).
- AC-61 Y's operator code as approver in X -> `OPERATOR_SIGN_IN_FAILED`; Y cannot act on X's bill (`404`);
  rules set in X do not change Y.

## 11. Notes for the tester and the builder

Tester
- Prepare roles: `WAITER` (no rights), `MANAGER` (all four rights); operators Ana (waiter) and Iva (manager).
- Approved changes to earlier tests are exactly those of AC-01; anything else -> `docs/questions/P05a-q.md`.

Builder
- One Application step "authorize an act" wraps P02's `AuthorizeAsync` inside the bill's transaction; the
  approver's failed-PIN increment must survive the rollback of the act (S2) — commit it separately, as the
  sign-in does.
- Amounts in one Domain function (`BillAmounts`) used by every bill write and read; P07 reuses it.
- The stored idempotency fingerprint must not let a PIN be read back (S1): hash the canonical body.
- Anything unclear: `docs/questions/P05a-q.md`, then continue.

`xerp-pos`: a long-press on a line offers price, discount, void; on `OPERATOR_RIGHT_MISSING` show an approver
code + PIN sheet and resend the same request with `approverCode`, `approverPin` and a **new** idempotency key
(the refused one stored nothing, but a new body needs a new key, P05/R60). Draw voided lines struck through;
show `subtotal`, `discountTotal`, `total` from the server.

## 12. Decisions behind this spec

Open owner decisions of the overview §9 that touch it: **7** (an agent acts and approves with an operator's
code and PIN), **10** (no automatic discounts, vouchers or happy hour).

Architect's defaults, each until the owner says otherwise:

| # | Question | Default | Where |
|---|---|---|---|
| 1 | Discount as percent, amount, or both? | Both, one per line and one per bill | 4.B, R20 |
| 2 | Line and bill discount together? | Yes; the bill's after the lines' | R22 |
| 3 | How is a bill discount split for taxes? | In proportion to line bases, rounding difference to the largest line | R32 |
| 4 | Is a reason required? | For voids yes, for price changes and discounts no — each a switch | 6.1 |
| 5 | Does clearing a discount need a right? | No | R21 |
| 6 | Is a voided line kept on the bill? | Yes, shown and out of totals; plain removal stays possible unless `pos.lineRemovalAllowed` is off | R25, R27 |
| 7 | Void of a bill whose lines are all voided, or empty | No right needed; otherwise `voidBill` | R26 |
| 8 | Is every past act kept on the bill? | Only the latest per kind; the full trail comes with the audit log (020) | R3 |
| 9 | Discount limits per role (e.g. waiters up to 10 %) | Not in scope; a right is all or nothing | §2 |
