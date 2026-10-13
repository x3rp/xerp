# Spec 015 — Stock valuation: purchase cost on receipts, value of stock per warehouse

Status: ready for the tester and the builder, **after spec 014 is merged** (its registry inventory is the
starting point of this one).
Owner's order of work (2026-10-10, `docs/rules.md` §5): 012, 013, 014, then valuation.
Branches: `tests/015-stock-valuation` (tester), `feat/015-stock-valuation` (builder).
Read first: **ADR-0021** (the method and why), ADR-0018 and spec **011** §3, §4.3–§4.5, §6 (the stored
balance, verify and rebuild — this spec adds value to it by the same pattern), specs **005** §3–§4, **006**
(transfer, reversal), **008** (count), **009** §3, §4.2 (order price, linked receipt), 007 (units and
factors), **012** (the rule mechanism; this spec adds rules and changes nothing of it), architecture §11.
Why this spec and why this number: `docs/roadmap.md` section 3, row 15.

**Inherited, not re-specified** (roadmap §4): everything specs 001–014 established. Numbers are local;
"006/R12" means rule R12 of spec 006. "Linked" means a receipt created against a purchase order.

**The sentence that governs the retrofit: a tenant that gives no cost and sets no rule sees every quantity,
status, code and error of specs 001–014 unchanged.** Valuation adds numbers next to the quantities; it
refuses nothing by default.

**Decisions of the architect that the owner may change** are marked *(default)* and collected in section 12.

## 1. Goal

Stock has a quantity; after this spec it also has a **value**. A receipt line may carry the purchase cost of
the goods; a receipt against a purchase order takes it from the order's price. Posting writes the value of
every movement to the stock ledger next to its quantity, by the **moving average** per article and warehouse
(ADR-0021). The value of stock is the sum of the ledger, stored on the balance and rebuildable from the
ledger. A tenant, or its agent, can ask what an article is worth in a warehouse and what each warehouse is
worth.

## 2. Scope

In:
- `unitCost` on receipt lines; cost of a linked receipt from the order line's price.
- `value` on every ledger entry; `value` and `unitCost` on the stored balance, on stock on hand and on the
  stock list of a warehouse; `value` on posted document lines.
- Valuation of every movement that exists: receipt, issue, transfer, count, reversal — including stock
  below zero (`stock.negativeStockAllowed`, owner's decision 5).
- Value of stock per warehouse (one new read operation and MCP tool).
- Verify and rebuild cover value.
- Three rules on the registry (6.1).

Out:
- Any other method (FIFO, standard cost) and one average per article across warehouses (ADR-0021).
- Revaluation, landed costs (freight, duty), cost corrections after posting, repricing of earlier issues.
- Stock value as of a past date; value by article group.
- Cost on a count line or on a manual inbound other than a receipt.
- General ledger postings of value and of cost of goods sold (016, 018); margins on sales.
- Currencies: every cost and value is in the tenant's one currency, without tax (architecture §5).

## 3. Data

- `StockDocumentLine` gains `UnitCost` numeric(18,6), nullable — the cost per the line's unit.
- `StockLedgerEntry` gains `Value` numeric(18,2), signed, not null.
- `StockBalance` gains `Value` numeric(18,2), signed, not null, and `UnitCost` numeric(18,6), not null — the
  current cost of the pair (R12). Both are **derived** from the ledger, as `Quantity` is (ADR-0018).

One migration; earlier migrations are not edited. Existing rows: `UnitCost` of a line `null`; `Value` of every
existing ledger entry and balance `0`; `UnitCost` of every existing balance `0` *(default: stock posted before
this spec is worth zero; section 12, #5)*. No new table. No check constraint on `Value` (it may be negative
while stock is below zero).

## 4. Operations — HTTP

### 4.1 Stock documents

| Operation | Change |
|---|---|
| Create, Replace | A line gains `"unitCost"?` — a number or `null`; omitted and `null` are equal ("no cost given"). Allowed only when the document's `type` is `"receipt"` (R2). |
| Post, Reverse | No change in request or errors. Posting values the movement (6.3–6.6). |

Representation: every line of every stock document gains two properties, always present:
```json
{ "…": "as spec 008 §4", "unitCost": 12.5, "value": 50.00 }
```
- `unitCost` — per the line's unit. On a draft: the cost given, or `null`. On a posted receipt: the cost
  given, else the cost taken from the order line (R6), else `null`. On every other type: `null`.
- `value` — `null` on a draft. On a posted document: the `value` of the line's ledger entry (a transfer: of
  the entry in the source warehouse), signed as that entry is (a receipt `> 0` or `0`, an issue `<= 0`).

### 4.2 Ledger entry, stock on hand, stock list of a warehouse

- A ledger entry (`GET /stock-ledger-entries`) gains `"value"`: the value the entry adds to (positive) or
  takes from (negative) the stock of its article in its warehouse.
- A stock-on-hand item (`GET /stock-on-hand`) and an item of the stock list of a warehouse
  (`GET /warehouses/{id}/stock`) gain `"value"` (2 decimals) and `"unitCost"` (6 decimals; R12). A pair that
  never moved has `0` and `0`. Which pairs are listed, their order and their other properties are unchanged.

### 4.3 Value of stock per warehouse

`GET /stock-value?limit=&offset=` -> `200` list envelope of
```json
{ "warehouse": { "id": "uuid", "code": "CENTRAL", "name": "Central warehouse" },
  "value": 12450.00, "articleCount": 37 }
```
One item per **warehouse of the tenant**, active or not, whether or not it holds stock. `value` is the sum
of the stored values of the warehouse; `articleCount` the number of articles whose quantity there is not
zero. Ordered by warehouse code (case-insensitive). Paging and query-string rules as every list (001/R15).
Errors: `400`.

### 4.4 Verify and rebuild (011 §4.5)

A difference gains `"storedValue"`, `"ledgerValue"` and `"differenceValue"` (stored − ledger). A pair is a
difference when its stored quantity **or** its stored value differs from the ledger's. Rebuild recomputes
quantity, value and cost from the ledger; `corrected` counts the pairs in which any of the three changed.

No new error code.

## 5. Operations — MCP

| Tool | Arguments | Success | HTTP operation | Error codes |
|---|---|---|---|---|
| `stock_value_list` | `{ limit?, offset? }` | list envelope | Value per warehouse | `VALIDATION_FAILED` |

- Annotations: read-only, `openWorldHint: false`.
- Changed schemas: a line of `stock_document_create` and `stock_document_update` gains `unitCost?` (number
  or `null`); the output schemas of every tool that returns a stock document, a ledger entry, a
  stock-on-hand item, a warehouse stock item or a balance difference gain the properties of section 4.
- Descriptions say:
  - `stock_document_create`, `stock_document_update` — `unitCost` is the purchase cost per the line's unit,
    in the company's currency, without tax; only on a receipt; on a receipt against a purchase order it may
    be omitted and the order's price is taken; this company's rules can require it and can forbid a cost
    other than the order's (`rule_list` with `group` = `valuation`).
  - `stock_on_hand_list`, `warehouse_stock_list` — `value` is what the stock cost (moving average per
    article and warehouse); `unitCost` is the current cost per base unit.
  - `stock_value_list` — the value of stock in each warehouse; the company's total is the sum of the items;
    for the articles behind a number use `warehouse_stock_list`.
  - `stock_ledger_entry_list` — `value` is the value each movement added or took.
- `tools/list` returns exactly 63 tools: the 62 before this spec and this one.

## 6. Rules

### 6.1 Rules this spec introduces (architecture §11)

`true` means the statement in the name holds. Group `valuation`. For all of them 012/R15 holds: no stored
line, ledger entry, value or balance is changed, re-checked or refused later for having been written under
another value.

| Key | Name | Default | Switching it to the other value | It does not rewrite |
|---|---|---|---|---|
| `valuation.receiptCostRequired` | Purchase cost required on a receipt that is not linked to an order | `false` | `true`: an unlinked receipt saved with a line without `unitCost` is refused. | Receipts without a cost, draft or posted, stay; a draft posts as it is (R8); only its next replace must give the cost. |
| `valuation.receiptCostMayDifferFromOrder` | The cost on a receipt may differ from the price of its purchase order | `true` | `false`: a linked receipt saved with a `unitCost` that is not the order line's cost (R6) is refused; omitting it is always accepted. | Costs of saved and posted receipts; a draft posts with the cost it has. |
| `valuation.uncostedReceiptAtCurrentCost` | A receipt without a cost is valued at the current cost of the stock | `true` | `false`: it is valued at zero. | Values already posted. |

Fixed checks these replace: none — receipts had no cost (005 §2). Defaults reproduce today's requests: a
receipt without a cost is accepted.

**Invariants this spec adds** (definitions and integrity; without `rules`; `docs/rules.md` §2, additions to
I4, I5, I8, I10 — for the owner's approval, section 12, #1):
- a cost is `>= 0` and has at most 6 decimals; a line's amount (quantity × cost) is at most 9999999999.99;
- only a receipt line has a cost;
- the ledger value sums to the stored value, per article and warehouse; a quantity of zero has a value of
  zero; a ledger entry's value never changes;
- the method: moving average per article and warehouse, in the order of posting (6.3–6.6).

Not a switch, a **feature**: the valuation method (another method does not exist yet; and a method is a
choice of several, not a yes/no).

### 6.2 Cost on a receipt line

- R1. `unitCost`, when given, is a number `>= 0` with at most 6 decimals, and `quantity × unitCost` rounded
  to 2 decimals (half away from zero) is at most 9999999999.99. Otherwise `400 VALIDATION_FAILED`, key
  `lines[i].unitCost`, no `rules`. `0` is a valid cost (goods free of charge) and is **not** "no cost".
- R2. A non-null `unitCost` on a line of an issue, a transfer or a count is `400`, key `lines[i].unitCost`,
  no `rules`. On replace the type is the stored one (008 §4): judged with the other type-dependent rules.
- R3. `valuation.receiptCostRequired` = `true`: create or replace of an **unlinked** receipt with a line
  without `unitCost` -> `400 VALIDATION_FAILED`, key `lines[i].unitCost` for every such line, naming the rule
  (value `true`). A linked receipt is not judged by it (it has the order's price).
- R4. `valuation.receiptCostMayDifferFromOrder` = `false`: create or replace of a **linked** receipt with a
  line whose given `unitCost` is not equal to the order line's cost (R6) -> `400 VALIDATION_FAILED`, key
  `lines[i].unitCost`, naming the rule (value `false`). A line without `unitCost`, and a line that gives
  exactly the order line's cost, are accepted. A line that names no existing order line is refused as today
  and not by this rule.
- R5. R1–R4 are part of validation: reported together with every other invalid field (012/R39); `rules` has
  one element per rule that contributed. They judge saving only: posting never refuses for a cost (014/R3).
- R6. **The order line's cost** for a linked receipt line is the order line's unit price converted to the
  receipt line's unit: `unitPrice × lineFactor / orderLineFactor`, rounded to 6 decimals, half away from
  zero (same unit: the price itself). A linked line without a given cost takes it **at posting**; the posted
  line shows it as `unitCost`.
- R7. **The amount of a receipt line** = `quantity × unitCost`, rounded to 2 decimals, half away from zero
  (the formula of 009/R9), with the cost given or taken by R6.
- R8. An unlinked receipt line without a cost is posted. Its amount is: with
  `valuation.uncostedReceiptAtCurrentCost` = `true`, `baseQuantity ×` the pair's current cost (R12) at that
  moment, rounded to 2 decimals; with `false`, `0`. Its `unitCost` stays `null`. The rule is read at posting,
  under the lock (012/R14).

### 6.3 The balance and the order of valuation

- R9. Valuation is per pair (article, warehouse). The pair has a quantity `Q` (011), a value `V` and a cost
  `C`; all three start at `0`.
- R10. Every ledger entry gets its `value` when it is written — in the posting transaction, under the
  per-tenant lock, in the order of posting; a document's lines in `lineNo` order, each seeing the result of
  the previous. `documentDate` plays no part: a back-dated document is valued as of the moment it is posted
  and reprices nothing.
- R11. `V` after an entry = `V` before + the entry's `value`. So at every moment, per pair: stored `Value` ==
  sum of the ledger's `value`.
- R12. **Current cost.** After an entry, if the new quantity is not zero, `C` = new `V` / new `Q`, rounded to
  6 decimals; if it is zero, `C` keeps its value (the last known cost). `C` is per base unit and is what
  `unitCost` shows on stock on hand and the stock list.
- R13. A quantity of zero has a value of zero (it follows from R14 and R15; stated because tests check it).

### 6.4 Movements out

An entry that lowers the quantity by `q > 0`: an issue line, the source side of a transfer, a count's
shortage, **and a reversal of an entry that raised it** (a reversed receipt, a reversed count surplus, the
destination side of a reversed transfer).

- R14. It takes value `v`; the entry's `value` is `−v`:
  - `q == Q`: `v = V` (the last unit takes the remainder, so no cent is left behind);
  - `0 < q < Q`: `v = q × V / Q`, rounded to 2 decimals;
  - `Q > 0` and `q > Q` (it goes below zero): `v = V + (q − Q) × C`, the second term rounded to 2 decimals;
  - `Q <= 0` (it is already at or below zero): `v = q × C`, rounded to 2 decimals.
  **Stock below zero costs the last known cost of the pair** — zero, if the pair never had one.

### 6.5 Movements in

An entry that raises the quantity by `r > 0`. Its **incoming amount** `w` is:

| Entry | `w` |
|---|---|
| Receipt line | its amount (R7, R8) |
| Destination side of a transfer | the `v` the same line took from the source (R14) |
| Count surplus | `r × C`, rounded to 2 decimals |
| Reversal of an entry that lowered the quantity (a reversed issue, a reversed count shortage) | what the original entry took: `−`(its `value`) |
| Source side of a reversed transfer | the `v` the reversal took from the destination |

- R15. The entry's `value`:
  - `Q >= 0`: `value = w`;
  - `Q < 0` (goods arrive into stock that is below zero): the stock that remains is valued at the cost of
    what arrived — new `V` = `(Q + r) × w / r`, rounded to 2 decimals (so `0` when it lands on zero), and
    `value` = new `V` − old `V`. The entry's value then differs from the line's amount by what the earlier
    issues below zero were costed too low or too high; it may be negative (ADR-0021).

### 6.6 Transfer, count, reversal — consequences

- R16. A transfer line writes two entries: the source by R14, the destination by R15 with that `v`. While
  the destination is not below zero the two values sum to zero: a transfer changes no article's total value.
- R17. A count line with no difference writes no entry and changes no value. A count never carries a cost.
- R18. A reversal is valued by R14/R15 as the table and list above say, **not** by negating the original's
  value: a reversed receipt leaves at the current cost. Quantities of original and reversal sum to zero
  (I4); values need not. The reversing document's lines show `unitCost == null` and their own `value`.
- R19. No operation changes the `value` of a written ledger entry. There is no operation that sets a
  balance's value or cost.

### 6.7 Reads, verify, rebuild

- R20. `value` and `unitCost` of stock on hand and of the stock list are the stored `Value` and `UnitCost`
  of the pair; a pair without a balance row has `0` and `0`.
- R21. `GET /stock-value`: `value` = sum of stored `Value` over the warehouse's pairs; `articleCount` = pairs
  with `Quantity != 0`; a warehouse without stock has `0` and `0`. The sum of all items' `value` equals the
  sum of all ledger entries' `value` of the tenant.
- R22. Verify reports a pair whose stored quantity or stored value differs from the ledger's sum. Rebuild
  sets, per pair, `Quantity` and `Value` to the ledger's sums and `UnitCost` to what R12 gives when the
  pair's entries are replayed in ledger order (005 §4.3); it changes no document and no ledger entry; safe
  to repeat.
- R23. Nothing else of specs 001–014 changes: no status, code, `errors` key, quantity, number or state.

## 7. Edge cases

- E1. `unitCost: 0` on a receipt with `valuation.receiptCostRequired` = `true` -> accepted; the line is
  valued at 0, not at the current cost.
- E2. A receipt of 3 at `3.333333`: amount `10.00`. Three issues of 1: `−3.33`, `−3.34`, `−3.33`; value 0.
- E3. Receive 10 at 10, receive 10 at 20, issue 10 (`−150.00`). Reverse the second receipt: its original
  entry is `+200.00`, the reversal's `−150.00`; quantity 0, value 0, `unitCost` stays `15`.
- E4. Receive 10 at 10, issue 4 (`−40.00`), receive 6 at 20. Reverse the issue: `+40.00`; quantity 16, value
  `220.00`, `unitCost` `13.75`.
- E5. Negative stock allowed. Receive 10 at 10, issue 15: `−150.00`; quantity −5, value `−50.00`, cost 10.
  Receive 10 at 20: the entry's `value` is `150.00` (the line's amount is 200); quantity 5, value `100.00`,
  cost 20.
- E6. Negative stock allowed, an article that was never received: issue 5 -> `value` `0`; quantity −5,
  value 0. Receive 3 at 20: the entry's `value` is `−40.00`; quantity −2, value `−40.00`, cost 20. Receive 2
  at 20: `+40.00`; quantity 0, value 0.
- E7. A count of an article that never had a cost, counted 5: `+0.00`; quantity 5, value 0, cost 0.
- E8. A transfer of the whole stock of a pair: the source ends at quantity 0, value 0 and keeps its cost.
- E9. A linked receipt whose order line has price 0: cost 0, value 0 — not "no cost".
- E10. A linked receipt in another unit than its order line: order `box` (factor 12) at 24, receipt 6 `pcs`
  -> `unitCost` 2, value `12.00`.
- E11. `valuation.receiptCostMayDifferFromOrder` = `false`, a linked draft saved earlier with another cost:
  it posts with that cost; `PUT` with its own body -> refused.
- E12. A transfer line and an issue line with `unitCost: null` -> accepted (null is "not given").
- E13. One receipt with article A on two lines at different costs: two entries; the second sees the first.
- E14. A receipt back-dated before earlier issues: those issues keep their values (R10).

## 8. Tenant isolation

- T1. No new table; the new columns are on tenant-owned rows. Every sum (`stock-value`, verify, rebuild)
  is over the caller's tenant only. Costs, values and rules of tenant X are invisible to and change nothing
  for tenant Y — two tenants with the same article and warehouse codes have independent costs.
- T2. The same through the tools. No `IgnoreQueryFilters()` is added.

## 9. Security requirements

- S1. Authentication and permissions as before: every tenant key reads costs and values (who may see
  purchase costs is spec 021).
- S2. Values are computed by the server only: no request field sets a ledger entry's or a balance's value,
  a posted line's `value`, or the current cost. Unknown properties stay refused (I9).
- S3. No input and no combination of rule values may cause `500` (001/R16) — in particular not a division
  by a zero or negative quantity, an amount beyond the column, or any sequence of postings and reversals
  with negative stock allowed.
- S4. Exact decimals throughout, never floating point (I10).
- S5. After any sequence of operations, with any rule values: verify is empty (quantity and value) and R13
  holds.

## 10. Acceptance criteria

Conventions of specs 012 §10 and 014 §10: the standard setup (unit `pcs`, stock articles `A` and `B`,
warehouses `W1` and `W2`, partners `SUP` and `CUS`), "Set(key, v)", "refused by (CODE, key, v)", "Receive n
of A into W1", "Balanced". In addition:
- "Receive n of A into W1 **at c**" — an unlinked receipt with one line, `unitCost` c, created and posted.
  Without "at c" the line has no `unitCost`.
- **Stock(A, W) = (q, v, c)** — the stock-on-hand item and the warehouse stock-list item of the pair show
  `quantity` q, `value` v, `unitCost` c.
- **Valued** — Balanced, verify is empty, and for every pair the sum of its ledger entries' `value` equals
  its `value` on stock on hand.
Each test uses its own tenant. Unmarked criteria are black-box (tester).

Structure
- AC-01 *(manual)* Build and tests exit 0; earlier tests pass unweakened. **No test of specs 001–014 that
  asserts a status, a code, an `errors` key, a quantity, a number or a state changes.** The only earlier
  tests changed: (1) tests that pin the number of rules or their literal list (+3, the keys of 6.1);
  (2) tests that pin the number or the list of tools (62 -> 63, `stock_value_list`); (3) tests that pin
  the exact property set or schema of a document line, a ledger entry, a stock-on-hand or stock-list item
  or a balance difference (the properties of section 4 are added); (4) a test that sends `unitCost` as an
  example of an unknown line property, if one exists (it takes another name).
- AC-02 *(unit, builder)* The valuation of one movement is one pure Domain function of (direction, q or r,
  w, Q, V, C); tests cover every branch of R14 and R15 and R12, including each example of section 7.
- AC-03 `GET /rules?group=valuation` returns exactly the three rules of 6.1 with their names and defaults;
  the registry has 3 more rules than before this spec.
- AC-04 `tools/list` has 63 tools; `stock_value_list` exists with the annotations of section 5; the line
  schema of `stock_document_create` and `stock_document_update` has `unitCost`.
- AC-05 *(manual)* One migration; existing ledger entries and balances get value 0.

Cost on a line
- AC-10 A draft receipt created with `unitCost: 12.5` returns `unitCost == 12.5`, `value == null`; get and
  list agree; a replace without `unitCost` returns `null`.
- AC-11 `unitCost` of `-1`, of `1.1234567`, a string, and a cost whose amount exceeds 9999999999.99 -> `400`,
  key `lines[0].unitCost`, no `rules`.
- AC-12 `unitCost: 5` on an issue, on a transfer, on a count (create and replace) -> `400`, key
  `lines[0].unitCost`, no `rules`; `unitCost: null` on each -> accepted (E12).
- AC-13 Defaults: a receipt without `unitCost` is created, replaced and posted (as spec 005).
- AC-14 Set(`valuation.receiptCostRequired`, true): an unlinked receipt with two lines, the second without
  cost -> refused by (`VALIDATION_FAILED`, the rule, `true`) with key `lines[1].unitCost` only; with both
  costs -> `201`; with `unitCost: 0` -> `201` (E1). An issue, a transfer and a count are created without it.
- AC-15 The same rule: a draft receipt without cost saved before the switch posts; its `PUT` with its own
  body is refused; a linked receipt without `unitCost` -> `201`.
- AC-16 The same through `stock_document_create`: the tool error carries the same `rules`.

Receipts and the moving average
- AC-20 Receive 10 of A into W1 at 10: the posted line has `unitCost == 10`, `value == 100`; its ledger
  entry `value == 100`; Stock(A, W1) = (10, 100, 10).
- AC-21 Then receive 10 at 20: Stock = (20, 300, 15). Issue 5: the line and the entry have `value == -75`;
  Stock = (15, 225, 15). Issue 15: `value == -225`; Stock = (0, 0, 15). Valued.
- AC-22 E2 (rounding; the last unit takes the remainder).
- AC-23 E13 (one article on two lines of a receipt): 4 at 10 and 6 at 20 -> Stock = (10, 160, 16).
- AC-24 Receive 10 at 10, then receive 5 without cost: its entry `value == 50`, the line `unitCost == null`;
  Stock = (15, 150, 10). With Set(`valuation.uncostedReceiptAtCurrentCost`, false) instead: `value == 0`;
  Stock = (15, 100, 6.666667).
- AC-25 A first receipt without cost: `value == 0`; Stock = (n, 0, 0) under either value of the rule.
- AC-26 A receipt at `unitCost: 0` after 10 at 10: `value == 0`, Stock = (20, 100, 5) under either value.
- AC-27 A receipt in an alternative unit (`box`, factor 12), 2 boxes at 24: `value == 48`;
  Stock = (24, 48, 2).
- AC-28 The cost is per pair: receive A into W1 at 10 and into W2 at 30 -> `unitCost` 10 and 30.
- AC-29 E14: a receipt dated in the past, posted after an issue, does not change that issue's `value`.

Linked receipts
- AC-30 PO: 10 of A at 12.5, confirmed. Receive (1, 4) without `unitCost`: the draft line shows `null`; the
  posted line `unitCost == 12.5`, `value == 50`; Stock = (4, 50, 12.5).
- AC-31 The same with `unitCost: 13` -> posted, `value == 52`.
- AC-32 Set(`valuation.receiptCostMayDifferFromOrder`, false): the receipt of AC-31 is refused by
  (`VALIDATION_FAILED`, the rule, `false`), key `lines[0].unitCost`; with `unitCost: 12.5` and without
  `unitCost` -> `201`. An unlinked receipt at any cost -> `201`.
- AC-33 E11.
- AC-34 E10 (order in `box`, receipt in `pcs`). AC-35 E9 (price 0).
- AC-36 `valuation.receiptCostRequired` = `true` and `valuation.receiptCostMayDifferFromOrder` = `false`
  together, one unlinked receipt without cost -> one `rules` element; a linked receipt without cost -> `201`.

Transfer, count, reversal
- AC-40 Stock(A, W1) = (20, 300, 15). Transfer 8 to W2: the line `value == -120`; entries `-120` in W1 and
  `+120` in W2; Stock(A, W1) = (12, 180, 15), Stock(A, W2) = (8, 120, 15). Reverse it: `-120` in W2, `+120`
  in W1; Stock(A, W1) = (20, 300, 15), Stock(A, W2) = (0, 0, 15). Valued.
- AC-41 A transfer into a warehouse that holds the article at another cost averages: W2 has (10, 100, 10),
  transfer 10 from W1 at cost 20 -> Stock(A, W2) = (20, 300, 15).
- AC-42 E8.
- AC-43 Stock = (10, 100, 10). A count of 12: line `value == 20`; Stock = (12, 120, 10). A count of 7 from
  (10, 100, 10): `value == -30`; Stock = (7, 70, 10). A count equal to the book quantity: no entry, value
  unchanged, the line's `value == 0`.
- AC-44 E7. Reversing that count: `value == 0`; Stock = (0, 0, 0).
- AC-45 E3 (a reversed receipt leaves at the current cost). AC-46 E4 (a reversed issue returns its value).
- AC-47 Reversing a count shortage returns exactly what it took; reversing a surplus leaves at the current
  cost. Valued after each.
- AC-48 A reversing document's lines have `unitCost == null` and `value` equal to their ledger entries'.

Stock below zero (Set(`stock.negativeStockAllowed`, true))
- AC-50 E5, step by step, with Stock after each step: (−5, −50, 10), then (5, 100, 20). Valued.
- AC-51 E6: (−5, 0, 0), then (−2, −40, 20), then (0, 0, 20). Valued.
- AC-52 From (−5, −50, 10): issue 2 more -> `value == -20`; Stock = (−7, −70, 10).
- AC-53 From (−5, −50, 10): reverse the issue that went below zero (15, `−150`) -> the entry's `value` is
  `+150`, Stock = (10, 100, 10).
- AC-54 A transfer of 5 into a warehouse at (−5, −50, 10) from a source at cost 20: source `−100`,
  destination `+50`; destination Stock = (0, 0, 10).
- AC-55 A count of 3 on a pair at (−5, −50, 10): surplus 8 at cost 10, `w == 80`; new value `3 × 10 = 30`,
  entry `value == 80`; Stock = (3, 30, 10).

Reads, verify, rebuild
- AC-60 `GET /stock-value` in a new tenant: one item, the default warehouse, `value == 0`,
  `articleCount == 0`. After receipts of A (100) and B (50) into W1 and A (30) into W2: W1 `150` and 2, W2
  `30` and 1, the default warehouse `0` and 0; ordered by warehouse code; an inactive warehouse is listed.
- AC-61 The sum of the items of `GET /stock-value` equals the sum of `value` over all ledger entries, after
  a mixed sequence (receipts, issue, transfer, count, reversals).
- AC-62 `GET /stock-value?limit=0`, `?limit=501`, `?foo=1` -> `400`; paging works.
- AC-63 `GET /warehouses/{id}/stock`: an article that never moved has `value == 0`, `unitCost == 0`; an
  article at quantity 0 after movements shows its last cost.
- AC-64 Ledger entries filtered by `documentId` show the values of that document's lines.
- AC-65 `stock_value_list`, `stock_on_hand_list`, `warehouse_stock_list` and `stock_ledger_entry_list`
  return the same numbers as HTTP.
- AC-66 *(integration, builder)* A stored `Value` changed directly in the database: verify reports the pair
  with `storedValue`, `ledgerValue`, `differenceValue` and equal quantities; rebuild returns `corrected == 1`
  and restores value and cost; a second rebuild returns `corrected == 0`.
- AC-67 *(integration, builder)* Rebuild after a sequence that ends at quantity 0 restores the last known
  cost (R22).
- AC-68 Verify is empty after every scenario of this section.

Isolation, security, concurrency
- AC-70 Two tenants with the same codes: receipts at different costs give independent `unitCost`, `value`
  and `stock-value`; a rule set in one changes nothing in the other.
- AC-71 A body with `value` on a line, or `unitCost` on the header -> `400` (unknown property).
- AC-72 *(integration, builder)* Twenty receipts and issues of one pair posted in parallel: Valued, and R13
  holds; no `500`.
- AC-73 No `500` for: an issue of 0.000001 from a pair with value 0.01; a receipt of 0.000001 at
  999999999; a reversal that lands a pair exactly on zero from below.

## 11. Notes for the tester and the builder

Tester
- Numbers in section 10 are exact; assert them as decimals, not with a tolerance. Each "Stock = (q, v, c)"
  is checked on both reads.
- Do not assert that a reversal's values negate the original's (R18) — only AC-46, AC-47 (shortage) and
  the transfer of AC-40 return the same amount, and for the reasons stated there.
- The reversal-date trap of 014 §11 still applies to linked receipts.
- The approved changes to earlier tests are exactly those of AC-01. Anything else: `docs/questions/015-q.md`.

Builder
- One pure Domain function values a movement (AC-02); posting calls it per entry, in line order, on the
  balance row it already locks and updates for the quantity (011/R29–R31). No second pass, no job.
- Decide the direction by the sign of the entry's quantity; the incoming amount comes from the table of
  6.5. A reversal needs its original's entries only for "what the original took".
- Divide in `decimal`, round once, at the end of each formula, half away from zero
  (`MidpointRounding.AwayFromZero`).
- The cost of a linked line is resolved at posting (R6) and written to the line then; the draft keeps
  `null`.
- A new rule is one `RuleDefinition`; group `valuation`.
- Rebuild: quantity and value are sums; the cost needs the running sums in ledger order (a window function
  or a replay) — keep it in one statement per tenant if you can, but correctness first.
- Anything unclear or contradictory: `docs/questions/015-q.md`, then continue with the rest.

## 12. Decisions behind this spec

Owner's decisions: valuation follows 014 (2026-10-10); negative stock is a tenant's switch and valuation
must say what stock below zero costs (decision 5); a rule is a boolean.

**For the owner** — decisions of the architect he may want to make himself:

| # | Question | Default taken | Where |
|---|---|---|---|
| 1 | Is the method itself an invariant? | Moving average per article **and warehouse**, fixed; additions to invariants I4, I5, I8, I10 in `docs/rules.md` await approval. Another method (FIFO, standard) or one average per article for the whole company is a later feature, not a switch. | ADR-0021 |
| 2 | What does stock below zero cost? | The last known cost of that article in that warehouse (zero if none). The next arrival sets the cost of what remains, and its ledger value absorbs the correction. | R14, R15 |
| 3 | What does a reversed receipt take out? | The current average, not the receipt's own cost — value never stays behind on an empty shelf. | R18 |
| 4 | A receipt without a cost | Accepted (rule, default *not required*); valued at the current cost of the stock (rule, default *yes*). | 6.1 |
| 5 | Stock that exists before this spec | Worth zero until the next receipts; there is no revaluation or opening-value operation yet. | §3 |
| 6 | Where does a count's surplus get its cost? | The current cost of the pair; a count line carries no cost. Opening stock with a value is entered as a receipt. | 6.5 |
| 7 | Who may see purchase costs? | Every key of the tenant, until permissions (021). | S1 |

Architect's defaults — each stands until the owner says otherwise.

| # | Question | Default | Rule |
|---|---|---|---|
| 8 | When is a linked receipt's cost fixed? | At posting, from the order line | R6 |
| 9 | Do the cost rules judge posting? | No, saving only; a draft posts as it is | R5 |
| 10 | Does a back-dated document reprice later ones? | No; order of posting | R10 |
| 11 | Is a zero cost "no cost"? | No: zero is a cost | R1 |
