# Spec 014 — Rules, batch 3: documents

Status: ready for the tester and the builder, **after spec 013 is merged** (its registry inventory is the
starting point of this one).
Owner requirement of 2026-10-10: every validation and business rule is configurable per tenant; we ship the
default (`CLAUDE.md`, ADR-0020, architecture §11). Owner's order of work: 012, then the retrofit 013 and 014,
then valuation.
Branches: `tests/014-document-rules` (tester), `feat/014-document-rules` (builder).
Read first: **spec 012** (the mechanism; this spec adds rules to it and changes nothing of it), architecture
§11, `docs/rules.md` §3 (batch 014), specs **005**, **006**, **008** and **011a** (stock documents, reversal,
count, the partner of a document), 009 §6 (save and confirmation of an order), 011 (the default warehouse).
Why this spec and why this number: `docs/roadmap.md` section 3, row 14.

**Batch 014 of `docs/rules.md` is split in two specs.** This one has the rules of stock documents and the
rules that stock documents and orders share (dates, reference, repeated articles, the warehouse, inactive
masters) — 13 rules. Spec **014a** has the eleven rules that only orders have. A shared rule is introduced
whole, here, for documents and orders together: no key changes its meaning between the two specs.

**Inherited, not re-specified** (roadmap §4): everything specs 001–013 established. Numbers are local;
"006/R12" means rule R12 of spec 006. "Document" means a stock document of any type; "order" a purchase or
sales order; "linked" a receipt or issue created against an order.

**The one sentence that governs everything below: a tenant that sets no rule behaves exactly as specs
001–013 say.** Every default is today's behaviour.

**Decisions of the architect that the owner may change** are marked *(default)* and collected in section 12.

## 1. Goal

The business choices in saving, posting and reversing stock documents — and the ones orders share with
them — become switches: may a document be back-dated or post-dated, must it carry a reference, a partner, a
named warehouse, may an article repeat, may retired masters be used, may a reversal be dated before its
original, may an outdated count be posted. Among them is the owner's own example in its stock-document
form: *"Partner required on a receipt: True or False"* and the same for an issue.

## 2. Scope

In scope
1. 13 new entries in the rule registry (6.1); the registry then has 43.
2. The existing checks of 6.1 read their rule and name it in the error; four behaviours that did not exist
   are built as the other value of a switch: refusing a back-dated or post-dated document, refusing a
   reversal of a document with retired masters, posting an outdated count against current stock.
3. One new error code: `DATE_NOT_ALLOWED`.

Out of scope
- **No new operation and no new tool**; `tools/list` still returns 62. No request or response shape changes
  (architecture §11): `partnerId`, `reference` and `warehouseId` are already nullable or omittable.
- Rules that only orders have — the partner's role on an order, due dates, zero prices, the order's
  warehouse on a linked document, reopening, self-closing, confirming beyond availability: spec 014a.
- Everything `docs/rules.md` classifies as an invariant: a date is a real calendar date, a document has
  lines and a warehouse, a count names each article once, a transfer has two different warehouses, a linked
  document has its order's partner, the limits of quantities and lengths. No `rules` member, as before.
- A time zone per tenant (R13). A number of days of tolerance for dates: a rule is yes or no.
- Number series (017), permissions (021).

## 3. Data

No table and no column changes. No migration.

## 4. Operations — HTTP

No new route. The rules judge the existing operations of stock documents (`/stock-documents`: create,
replace, post, reverse), of orders (`/purchase-orders`, `/sales-orders`: create, replace, confirm) and — for
`references.inactiveAssignable` only — of articles and their units (create and replace of an article, set of
an article unit).

New error code (registry: architecture §6):

| HTTP | `code` | Meaning | `errors` keys |
|---|---|---|---|
| 409 | `DATE_NOT_ALLOWED` | With `document.pastDateAllowed` or `document.futureDateAllowed` = `false`: the document being posted, or the order being confirmed, is dated before or after today (R14). | `documentDate` (document) / `orderDate` (order) |

Existing refusals that now carry `rules` (R30): every `REFERENCE_INACTIVE`; `PARTNER_ROLE_MISSING` with key
`partnerId` (a stock document); `COUNT_OUTDATED`; the `400` of a reversal dated before its original.

## 5. Operations — MCP

No new tool and no schema change. A tool error carries `rules` as the HTTP problem does (012 §5).
Descriptions:
- `stock_document_create`, `stock_document_update`, `purchase_order_create`, `purchase_order_update`,
  `sales_order_create`, `sales_order_update` — say that this company's rules can require a reference, a
  named warehouse and (receipt, issue) a partner, and can forbid the same article on two lines
  (`rule_list` with `group` = `document` or `stock`); a refusal's `rules` names the rule.
- `stock_document_post`, `purchase_order_confirm`, `sales_order_confirm` — add `DATE_NOT_ALLOWED` to their
  error codes and name `document.pastDateAllowed`, `document.futureDateAllowed` and
  `posting.activeMastersRequired`; `stock_document_post` also names `stock.outdatedCountAllowed`.
- `stock_document_reverse` — adds `REFERENCE_INACTIVE` to its error codes and names
  `stock.reversalBeforeOriginalAllowed`, `stock.reversalActiveMastersRequired` and the two date rules.

## 6. Rules

### 6.1 Rules this spec introduces (architecture §11)

`true` means the statement in the name holds. For all of them: **no stored document, order, line, ledger
entry, balance or number is changed, re-checked or refused later for having been written under another
value** (012/R15); the last column says what that means for the rule.

| Key | Name | Default | Switching it to the other value | It does not rewrite |
|---|---|---|---|---|
| `stock.receiptPartnerRequired` | Partner required on a receipt that is not linked to an order | `false` | `true`: an unlinked receipt saved without a partner is refused. | Receipts without a partner, draft or posted, stay; a draft posts as it is; only its next replace must name one. |
| `stock.issuePartnerRequired` | Partner required on an issue that is not linked to an order | `false` | the same for an unlinked issue | the same |
| `stock.partnerRoleRequired` | The partner of a receipt must be a supplier, of an issue a customer | `true` | `false`: any partner may be named on an unlinked receipt or issue; `PARTNER_ROLE_MISSING` is not answered for it. | Documents keep their partner. After `true` again, a draft whose partner lacks the role is refused at its next save and at posting. |
| `document.pastDateAllowed` | A document or order may be dated before today | `true` | `false`: posting a document, confirming an order or reversing a document with a date before today is refused. | Dates of posted documents and confirmed orders; drafts keep their date and are judged when posted or confirmed. |
| `document.futureDateAllowed` | A document or order may be dated after today | `true` | `false`: the same for a date after today. | the same |
| `stock.reversalBeforeOriginalAllowed` | A reversal may be dated before its original | `false` | `true`: the reversal's date may be any date the two rules above allow. | Reversals already made. |
| `document.referenceRequired` | Reference required on a document or order | `false` | `true`: a document or order saved without a reference is refused. | Documents and orders without one stay, post and confirm; only the next replace of a draft must give one. |
| `document.repeatedArticleAllowed` | The same article may be on several lines | `true` | `false`: a receipt, issue, transfer or order saved with one article on two or more lines is refused. | Documents and orders with repeats stay, post and confirm; only the next replace of a draft is judged. |
| `document.warehouseRequired` | The warehouse must be named on every new document | `false` | `true`: a create without `warehouseId` is refused instead of taking the default (or the order's) warehouse. | The warehouse of every existing document, however it was chosen. |
| `references.inactiveAssignable` | An inactive record may be newly referenced | `false` | `true`: a save may newly name an inactive unit, article, partner or warehouse; `REFERENCE_INACTIVE` is not answered on create or replace. | References already stored. Posting and confirmation are judged by the next rule, not by this one. |
| `posting.activeMastersRequired` | Posting and confirmation need active warehouse, articles and partner | `true` | `false`: a document is posted and an order confirmed although a master it names is inactive. | Posted documents and confirmed orders. |
| `stock.reversalActiveMastersRequired` | A reversal needs active warehouse, articles and partner | `false` | `true`: a reversal is refused while a master of the original is inactive (`REFERENCE_INACTIVE`). | Reversals already made; the original stays `posted` and can be reversed once the master is active again. |
| `stock.outdatedCountAllowed` | A count whose book quantity is no longer current may be posted | `false` | `true`: the count is posted against the stock at the moment of posting; `COUNT_OUTDATED` is not answered. | Posted counts; a draft count's book quantity until it is posted. |

Fixed checks these replace: 011a/R2, R6, R12 (partner optional; its role); 005/R2 (any date); 006/R12
(reversal not before the original); 005/R7, 009/R4 (reference optional); 005/R4, 009/R5 (repeats allowed);
011/R8–R10 (omitted warehouse); 002/R9, 005/R3, R5, 007/R4, R12, 009/R6, R10, 011a/R5 (inactive not newly
assigned); 005/R13, 006/R5, 009/R12, 011a/R12 (active at posting and confirmation); 006/R17, 011a/R14
(reversal needs no active masters); 008/R10 (outdated count).

Checks this spec adds that are **invariants**: none. What it leaves fixed, as invariants and without
`rules`: a count names each article once (008/R3, I8) whatever `document.repeatedArticleAllowed` says; a
transfer names both warehouses (006/R2, I8) and a replace names the warehouse (011/R9, I9) whatever
`document.warehouseRequired` says; a linked document has its order's partner (011a/R8, I8).

### 6.2 What is judged, and when

- R1. The registry has exactly 43 rules: the 30 of specs 012–013 and the 13 of 6.1. Everything of 012
  §6.2–6.3 holds for them.
- R2. Which operation applies which rule:

  | Rule | Judges |
  |---|---|
  | `stock.receiptPartnerRequired`, `stock.issuePartnerRequired` | create and replace of an unlinked receipt / issue |
  | `stock.partnerRoleRequired` | create, replace and posting of an unlinked receipt or issue that has a partner |
  | `document.referenceRequired`, `document.repeatedArticleAllowed` | create and replace of a document and of an order |
  | `document.warehouseRequired` | create of a document and of an order |
  | `references.inactiveAssignable` | every create and replace that names another record: documents, orders, articles (`baseUnitId`), article units (`unitId`) |
  | `posting.activeMastersRequired` | posting of a document, confirmation of an order |
  | `document.pastDateAllowed`, `document.futureDateAllowed` | posting of a document, confirmation of an order, reversal (the reversal's date) |
  | `stock.reversalBeforeOriginalAllowed`, `stock.reversalActiveMastersRequired` | reversal |
  | `stock.outdatedCountAllowed` | posting of a count |

- R3. **A rule that judges saving does not judge the life of the draft** (012/R37): a draft saved while a
  looser value held is read, posted or confirmed as it is; its next replace is judged whole. *(default)*
- R4. Posting, confirmation and reversal read the rules under the per-tenant lock (012/R14). Create and
  replace read them once and are judged by that one set.
- R5. A reversing document is made by the system from its original: it is not judged by the rules of saving
  (reference, partner, repeats, warehouse, role), whatever their values.

### 6.3 Partner on a receipt and on an issue — `stock.receiptPartnerRequired`, `stock.issuePartnerRequired`

Stated for receipts; it holds mirrored for issues with `stock.issuePartnerRequired`. The two are separate.

- R6. With `true`, create and replace of an **unlinked** receipt with `partnerId` omitted (create) or `null`
  is refused: `400 VALIDATION_FAILED`, key `partnerId`, naming the rule. On create it is part of validation,
  reported with every other invalid field. On replace it is answered where 011a/R7 answers what only the
  stored type can decide (after the document is found), together with the other keys of that place.
- R7. With `false` (default) 011a/R2 holds as built. For either value: the shape of `partnerId` (a uuid or
  `null`; present on a replace of a receipt or issue; not on a transfer or count) is an invariant without
  `rules`; a partner that is named is checked as before (existence, activity, role).
- R8. **A linked receipt is never judged**: its partner is the order's (011a/R8), also when the order has
  none (012/R36). Whether an order needs a partner is `purchase.partnerRequired` / `sales.partnerRequired`.
- R9. Posting and reversal do not look at the rule (R3, R5).

### 6.4 `stock.partnerRoleRequired`

- R10. With `true` (default) 011a/R6 and R12 hold as built, and `PARTNER_ROLE_MISSING` with key `partnerId`
  names the rule. With `false` the role is not checked — on save and at posting — and a receipt may name a
  customer, an issue a supplier, or a partner with no role. Existence and activity are still checked.
  Linked documents are not concerned (011a/R10).

### 6.5 Dates — `document.pastDateAllowed`, `document.futureDateAllowed`

- R11. The date judged is `documentDate` of a stock document (all four types), `orderDate` of an order, and
  the `documentDate` a reversal is given. `expectedDate` and `requestedDate` are never judged by these two.
- R12. **A draft's date is not judged** *(default)*: create and replace accept any real date, as today. The
  rules judge the moment the date takes effect — posting, confirmation, reversal — because "today" moves
  between saving and posting.
- R13. **Today.** The system has no time zone per tenant. A date is *before today* when it is earlier than
  the date at UTC−12 at this moment, and *after today* when it is later than the date at UTC+14: a date
  that is today's date anywhere in the world is neither. The moment is the server's clock at the operation.
  *(default)*
- R14. With `document.pastDateAllowed` = `false`, posting a document or confirming an order whose date is
  before today is refused: `409 DATE_NOT_ALLOWED` with key `documentDate` / `orderDate`. With
  `document.futureDateAllowed` = `false` the same for a date after today. Nothing changes; no number is
  consumed. Place in the order of checks (005/R13, 009/R12): exists -> is a draft -> **date** -> masters
  active -> the rest.
- R15. **Reversal.** The date a reversal is given is judged by the same two rules, at the place and in the
  form of 006/R12: `400 VALIDATION_FAILED` with key `documentDate`. The original's date is not judged again.
- R16. With `true` (default, both) no date is refused for being past or future (005/R2).

### 6.6 `stock.reversalBeforeOriginalAllowed`

- R17. With `false` (default) 006/R12 holds as built and names the rule. With `true` a reversal may be
  dated before its original. Order of checks on reversal: form -> `404` -> `INVALID_STATE` -> **dates** ->
  masters (R25) -> stock -> reservation. The date stage answers once, `400` with key `documentDate`, with
  one `rules` element for every date rule that refused (this one, R15), each with `fields`
  `["documentDate"]`.

### 6.7 `document.referenceRequired`, `document.repeatedArticleAllowed`, `document.warehouseRequired`

- R18. `document.referenceRequired` = `true`: create and replace of a document (any type) or an order with
  `reference` omitted (create), `null` or empty after trimming -> `400 VALIDATION_FAILED`, key `reference`,
  naming the rule; part of validation. A replace body without the property is the invariant of 005/R7, and
  an over-long reference that of I10 — both without `rules`. `note` is never required.
- R19. `document.repeatedArticleAllowed` = `false`: create and replace of a receipt, issue, transfer or
  order in which two or more lines have the same `articleId` -> `400 VALIDATION_FAILED` with key
  `lines[i].articleId` for every line of a repeated article (the keys of 008/R3), naming the rule. On a
  replace of a document it is answered where 008/R3 is (the stored type decides). A linked document is
  judged like any other: two lines for one order line, or for two order lines with the same article, are
  repeats.
- R20. A count is refused for repeats whatever the rule's value, and its refusal has no `rules` (I8).
- R21. `document.warehouseRequired` = `true`: create of a receipt, issue or count — linked or not — or of
  an order with `warehouseId` omitted or `null` -> `400 VALIDATION_FAILED`, key `warehouseId`, naming the
  rule; part of validation. With `false` (default) 011/R8 and R10 hold: the default warehouse, or the
  order's. A transfer without `warehouseId`, any replace without it, and a malformed value are refused as
  before, without `rules`.

### 6.8 Inactive masters — `references.inactiveAssignable`, `posting.activeMastersRequired`, `stock.reversalActiveMastersRequired`

- R22. **Saving.** With `references.inactiveAssignable` = `false` (default) ADR-0008 holds as built: an
  inactive record that is newly assigned is `409 REFERENCE_INACTIVE` — and names the rule. With `true` no
  create or replace answers `REFERENCE_INACTIVE`: the base unit of an article, the unit of an article-unit
  conversion, the warehouse(s), partner, articles and units of a document, the partner, warehouse, articles
  and units of an order. Existence (`REFERENCE_NOT_FOUND`) and every other check of the reference are
  unchanged; the stages keep their order, the inactive stage simply never answers.
- R23. `set-default` of an inactive warehouse stays `DEFAULT_WAREHOUSE` (I11): it is not a reference of a
  record and no rule lifts it. An omitted warehouse still resolves to the default, which is always active.
- R24. **Posting and confirmation.** With `posting.activeMastersRequired` = `true` (default) 005/R13,
  006/R5, 009/R12 and 011a/R12 hold as built, and the `REFERENCE_INACTIVE` names the rule. With `false`
  that stage never answers: the document is posted, the order confirmed. The role stage that follows it
  (011a/R12, 009/R12) is another rule and is unchanged. The two rules are independent: with only
  `references.inactiveAssignable` = `true` a draft naming an inactive article is saved and its posting
  refused.
- R25. **Reversal.** With `stock.reversalActiveMastersRequired` = `false` (default) 006/R17 and 011a/R14
  hold as built. With `true` a reversal is refused with `409 REFERENCE_INACTIVE` when, at that moment, the
  original's warehouse (`warehouseId`), destination (`toWarehouseId`), partner — of an unlinked document
  only (`partnerId`) — or an article of a line (`lines[i].articleId`) is inactive: header keys together and
  alone first, then lines, as 005/R13. Nothing changes; the original stays `posted`.
- R26. So every `REFERENCE_INACTIVE` is a rule's refusal: on create and replace it names
  `references.inactiveAssignable` (`false`), at posting and confirmation `posting.activeMastersRequired`
  (`true`), at reversal `stock.reversalActiveMastersRequired` (`true`).

### 6.9 `stock.outdatedCountAllowed`

- R27. With `false` (default) 008/R10 holds as built and `COUNT_OUTDATED` names the rule.
- R28. With `true` a count is **posted against the stock at the moment of posting**: for every line,
  `bookQuantity` becomes the stock on hand of the pair at that moment and `differenceQuantity` the counted
  base quantity minus it; the entries are those of 008/R11 with these differences; 008/R12 holds — after
  posting, stock on hand of every counted article equals the counted quantity. The posted document shows
  the book quantity and difference it was posted with, for good (008/R14). `COUNT_OUTDATED` is not answered.
- R29. A draft count is not rewritten by the rule or by its change: its `bookQuantity` is the one taken at
  its last save (008/R6) until it is posted.

### 6.10 Errors name the rule

- R30. Every refusal by a rule of this spec carries `rules` (012 §4): the rule's key, the value in force
  and, as `fields`, exactly the `errors` keys that rule produced.

  | Refusal | `rules[].key` | `value` |
  |---|---|---|
  | `VALIDATION_FAILED`, an unlinked receipt / issue without a partner | `stock.receiptPartnerRequired` / `stock.issuePartnerRequired` | `true` |
  | `PARTNER_ROLE_MISSING`, key `partnerId` | `stock.partnerRoleRequired` | `true` |
  | `DATE_NOT_ALLOWED`; `VALIDATION_FAILED` for a reversal's date before / after today | `document.pastDateAllowed` / `document.futureDateAllowed` | `false` |
  | `VALIDATION_FAILED`, a reversal dated before its original | `stock.reversalBeforeOriginalAllowed` | `false` |
  | `VALIDATION_FAILED`, no reference | `document.referenceRequired` | `true` |
  | `VALIDATION_FAILED`, a repeated article (not on a count) | `document.repeatedArticleAllowed` | `false` |
  | `VALIDATION_FAILED`, no warehouse on a create | `document.warehouseRequired` | `true` |
  | `REFERENCE_INACTIVE` on create or replace | `references.inactiveAssignable` | `false` |
  | `REFERENCE_INACTIVE` at posting or confirmation | `posting.activeMastersRequired` | `true` |
  | `REFERENCE_INACTIVE` at reversal | `stock.reversalActiveMastersRequired` | `true` |
  | `COUNT_OUTDATED` | `stock.outdatedCountAllowed` | `false` |

- R31. A `VALIDATION_FAILED` reports all invalid fields together; `rules` has one element per rule that
  contributed; fields an invariant refused are in no element's `fields` (012/R39).
- R32. `PARTNER_ROLE_MISSING` with key `supplierId` / `customerId` (an order) is not changed by this spec:
  it stays without `rules` until spec 014a. Error codes, statuses and `errors` keys of specs 001–013 are
  unchanged.

## 7. Edge cases

- E1. `stock.receiptPartnerRequired` = `true`: an unlinked receipt without `partnerId` -> refused; an issue
  without one -> saved (the other rule); a transfer and a count -> saved (they have no partner); a receipt
  linked to an order without a supplier (012) -> saved with `partner == null` (R8).
- E2. `true`, a draft receipt without a partner saved earlier: it posts; `PUT` with its own body -> refused;
  with a supplier -> `200`.
- E3. `stock.partnerRoleRequired` = `false`: a receipt naming a partner that is only a customer is saved and
  posted. Set back to `true`: the posted receipt keeps the partner; a draft like it is refused at posting
  (`PARTNER_ROLE_MISSING`).
- E4. `document.pastDateAllowed` = `false`: a draft dated 3 days ago is created (`201`) and replaced
  (`200`); posting it -> `DATE_NOT_ALLOWED`; after a replace with today's date it posts. A draft dated today
  posts. A draft dated 3 days ahead posts (the other rule).
- E5. Both date rules `false`: only a document dated today can be posted; its reversal must be dated today.
- E6. `document.pastDateAllowed` = `false`, a document posted 3 days ago and dated then: reversing it with
  its own date -> `400`, key `documentDate`, naming `document.pastDateAllowed`; with today's date -> `201`.
- E7. `stock.reversalBeforeOriginalAllowed` = `false` (default) and `document.pastDateAllowed` = `false`,
  an original dated today, a reversal dated 3 days ago -> one `400` with key `documentDate` and two `rules`
  elements.
- E8. `document.repeatedArticleAllowed` = `false`: an issue with lines A, B, A -> `400` with keys
  `lines[0].articleId` and `lines[2].articleId`; a count with A twice -> `400` with the same form of keys
  and **no** `rules`.
- E9. `document.warehouseRequired` = `true`: a receipt linked to an order, created without `warehouseId` ->
  refused; with the order's warehouse -> `201`.
- E10. `references.inactiveAssignable` = `true`, `posting.activeMastersRequired` = `true` (default): a
  receipt naming an inactive article is saved; posting -> `REFERENCE_INACTIVE` naming
  `posting.activeMastersRequired`. With that one `false` too, it posts and stock rises.
- E11. `stock.reversalActiveMastersRequired` = `true`: a posted transfer whose destination was deactivated
  -> reversal refused with key `toWarehouseId`; after reactivation -> `201`.
- E12. `stock.outdatedCountAllowed` = `true`: a count of A saved at book 10, counted 7; then 4 are issued
  (stock 6); posting -> `200`, the line shows `bookQuantity == 6`, `differenceQuantity == 1`, stock is 7.

## 8. Tenant isolation

- T1. No new table. A value set by tenant X changes nothing for tenant Y.
- T2. "Today" (R13) is the same for every tenant; it is not tenant data.
- T3. The same through the tools. No `IgnoreQueryFilters()` is added.

## 9. Security requirements

- S1. Authentication and permissions as before (012/S1).
- S2. The date rules use the server's clock only: no header, parameter or body field moves "today".
- S3. No input and no combination of rule values may cause `500` (001/R16) — in particular not a document
  that names an inactive or role-less master and is saved, posted and reversed with the three rules of 6.8
  and `stock.partnerRoleRequired` at any values.
- S4. With `posting.activeMastersRequired` = `false`, with negative stock allowed and with outdated counts
  allowed, the invariants of `docs/rules.md` I5 hold: stock on hand == sum of the ledger == stored balance;
  verify is empty.

## 10. Acceptance criteria

Conventions of specs 012 §10 and 013 §10: the standard setup (unit `pcs`, stock articles `A` and `B`,
warehouses `W1` and `W2`, partners `SUP` and `CUS`), "Set(key, v)", "refused by (CODE, key, v)", "Receive n
of A into W1", "Balanced". In addition:
- **Today** is the current UTC date when the test runs; **Past** is Today − 3 days; **Future** is
  Today + 3 days. No criterion uses yesterday or tomorrow (R13).
- "Deactivate X" means a replace of the master with `isActive: false`.
Each test uses its own tenant. Unmarked criteria are black-box (tester).

Structure
- AC-01 *(manual)* Build and tests exit 0; earlier tests pass unweakened. **No test of specs 001–013 that
  asserts a status, a code, an `errors` key, a quantity, a number or a state changes** — they run in tenants
  that set no rule (AC-20). The **only** earlier tests changed:
  1. tests that pin the number of rules or their literal list: 30 -> 43, with the list of AC-10;
  2. tests of the rule list's filters whose expected result now has more keys: `group=stock` (one key ->
     seven), and `search=partnerRequired` — the search is case-insensitive (001/R9), so it now also finds
     `stock.receiptPartnerRequired` and `stock.issuePartnerRequired`;
  3. tests asserting the **exact member set** of a problem document or tool error for `REFERENCE_INACTIVE`,
     for `PARTNER_ROLE_MISSING` with key `partnerId`, for `COUNT_OUTDATED`, or for the `400` of a reversal
     dated before its original (they gain `rules`);
  4. tests asserting that such a response has **no** `rules` member — spec 012 listed `COUNT_OUTDATED`
     (012 AC-43) and the `REFERENCE_INACTIVE` of an inactive supplier (012 AC-82) among the refusals no rule
     caused: they now expect the rule of R30. `PARTNER_ROLE_MISSING` on an **order** stays without `rules`
     in this spec (R32);
  5. tests pinning the error-code list in the description of `stock_document_post`,
     `stock_document_reverse`, `purchase_order_confirm` or `sales_order_confirm` (section 5).
  No migration.
- AC-02 *(manual)* The 13 decisions are made by booleans obtained through `IRules` and passed to Domain
  functions; `Api` reads no rule; no check of 6.1 remains as a constant (architecture §11).
- AC-03 *(builder, unit)* R13 as a pure function of a date, a moment and the two booleans — including a
  moment at which UTC−12 and UTC+14 are on three different dates; the reversal's date stage (R17) as a pure
  function; the count's differences of R28.

The registry
- AC-10 In a new tenant `GET /rules?limit=500` -> `total == 43`: the 30 keys of 013 AC-10 and, each at its
  place in ordinal order, these with these defaults (literal): `document.futureDateAllowed` `true`,
  `document.pastDateAllowed` `true`, `document.referenceRequired` `false`,
  `document.repeatedArticleAllowed` `true`, `document.warehouseRequired` `false`,
  `posting.activeMastersRequired` `true`, `references.inactiveAssignable` `false`,
  `stock.issuePartnerRequired` `false`, `stock.outdatedCountAllowed` `false`, `stock.partnerRoleRequired`
  `true`, `stock.receiptPartnerRequired` `false`, `stock.reversalActiveMastersRequired` `false`,
  `stock.reversalBeforeOriginalAllowed` `false`.
- AC-11 `group=document` -> five; `group=posting` -> one; `group=references` -> one; `group=stock` -> seven.
- AC-12 Every one of the 13 accepts both values and resets.
- AC-13 `tools/list` still returns exactly 62 names.

Defaults reproduce specs 001–013
- AC-20 In a tenant that sets nothing: an unlinked receipt and an unlinked issue without a partner are
  saved and posted; a receipt naming `CUS` -> `PARTNER_ROLE_MISSING`; documents and orders dated Past and
  Future are posted and confirmed; a reversal dated before its original -> `400`, key `documentDate`; a
  document and an order without a reference are saved; an issue and an order with A on two lines are saved;
  a receipt and an order without `warehouseId` are created in the default warehouse; a draft newly naming
  an inactive article -> `REFERENCE_INACTIVE`; posting a draft whose warehouse was deactivated ->
  `REFERENCE_INACTIVE`; reversing a posted receipt whose article was deactivated -> `201`; posting a count
  after stock moved -> `COUNT_OUTDATED`.
- AC-21 The same in a tenant in which every one of the 13 rules was Set to its other value and then reset.
- AC-22 **The default refusals name their rule**: the five refusals of AC-20 are refused by
  (`PARTNER_ROLE_MISSING`, `stock.partnerRoleRequired`, `true`), (`VALIDATION_FAILED`,
  `stock.reversalBeforeOriginalAllowed`, `false`), (`REFERENCE_INACTIVE`, `references.inactiveAssignable`,
  `false`), (`REFERENCE_INACTIVE`, `posting.activeMastersRequired`, `true`), (`COUNT_OUTDATED`,
  `stock.outdatedCountAllowed`, `false`).
- AC-23 A refusal no rule caused has no `rules` member, in a default tenant and with every rule of this
  spec at its other value: an invalid `documentDate` (`"2026-02-30"`), a replace body without `reference`,
  a reference of 101 characters, a count with an article twice, a transfer without `warehouseId`, a replace
  without `warehouseId`, `partnerId: "abc"`, `partnerId` on a transfer, `REFERENCE_NOT_FOUND`,
  `ORDER_MISMATCH` for another partner on a linked document, `INVALID_STATE`.

Partner on a receipt and on an issue
- AC-30 Set(`stock.receiptPartnerRequired`, `true`). Create an unlinked receipt without `partnerId`, and
  with `"partnerId": null` -> each refused by (`VALIDATION_FAILED`, `stock.receiptPartnerRequired`, `true`)
  with key `partnerId`; nothing created. With `SUP` -> `201`; `PUT` of it with `"partnerId": null` ->
  refused likewise, the draft unchanged; a `PUT` without the property `partnerId` -> `400` with key
  `partnerId`, without `rules`.
- AC-31 Separate and typed (E1): with only that rule `true`, an unlinked issue without a partner, a
  transfer and a count are created. Mirrored: with only `stock.issuePartnerRequired` = `true`, an issue
  without a partner is refused by (`VALIDATION_FAILED`, `stock.issuePartnerRequired`, `true`) and a receipt
  without one is created.
- AC-32 Linked (R8): Set(`purchase.partnerRequired`, `false`) and Set(`stock.receiptPartnerRequired`,
  `true`); a confirmed purchase order without a supplier; a receipt linked to it without `partnerId` ->
  `201` with `partner == null`, and it posts.
- AC-33 The rule judges saving (E2): a draft receipt without a partner exists; Set to `true`; `GET` of it
  is JSON-equal to before; posting it -> `200`; another such draft: `PUT` with its own body -> refused.
  Reversing the posted one -> `201`, the reversing document's `partner == null`.
- AC-34 Mixed: rule `true`, a receipt body without `partnerId`, with an invalid `documentDate` and a line
  `quantity: -1` -> `400` with the three keys; `rules` has one element whose `fields` is `["partnerId"]`.

`stock.partnerRoleRequired`
- AC-40 Set to `false`: a receipt naming `CUS`, an issue naming `SUP`, and (with `partner.roleRequired` =
  `false`) a receipt naming a partner with no role -> created and posted. A random uuid as `partnerId` ->
  `REFERENCE_NOT_FOUND`.
- AC-41 Back to `true` (E3): the posted receipt reads unchanged; a draft receipt naming `CUS` -> posting
  refused by (`PARTNER_ROLE_MISSING`, `stock.partnerRoleRequired`, `true`); `PUT` with its own body ->
  refused likewise.

Dates
- AC-50 Set(`document.pastDateAllowed`, `false`). A receipt dated Past: create -> `201`; replace -> `200`;
  post -> refused by (`DATE_NOT_ALLOWED`, `document.pastDateAllowed`, `false`) with key `documentDate`,
  status `409`; the draft is unchanged and no number was consumed (the next posted receipt is
  `SR-000001`). The same draft replaced with Today -> posts. A receipt dated Future -> posts.
- AC-51 The same for an issue, a transfer and a count dated Past (one each): post -> `DATE_NOT_ALLOWED`.
- AC-52 Set(`document.futureDateAllowed`, `false`): a receipt dated Future -> post refused by
  (`DATE_NOT_ALLOWED`, `document.futureDateAllowed`, `false`); dated Past -> posts; dated Today -> posts.
- AC-53 Orders: with `document.pastDateAllowed` = `false`, a purchase order and a sales order with
  `orderDate` Past are created; confirm -> refused by (`DATE_NOT_ALLOWED`, `document.pastDateAllowed`,
  `false`) with key `orderDate`; the order stays `draft` with `number == null`; replaced with Today ->
  confirms as `PO-000001` / `SO-000001`. An order dated Today with `expectedDate` Future confirms with
  `document.futureDateAllowed` = `false` (R11).
- AC-54 Reversal (E6): a receipt dated Past is posted while the rule is `true`; Set(`document.pastDateAllowed`,
  `false`); reversing it with date Past -> refused by (`VALIDATION_FAILED`, `document.pastDateAllowed`,
  `false`) with key `documentDate`, status `400`; with Today -> `201`. With `document.futureDateAllowed` =
  `false`, reversing a receipt dated Today with date Future -> refused by (`VALIDATION_FAILED`,
  `document.futureDateAllowed`, `false`).
- AC-55 Existing data: with both rules `false`, a document posted with date Past and a confirmed order
  dated Future, both made before the Set, read JSON-equal to before; the order is received against (the
  receipt dated Today) and closed.
- AC-56 A linked receipt dated Past against an order dated Today, `document.pastDateAllowed` = `false` ->
  posting refused with key `documentDate`.

`stock.reversalBeforeOriginalAllowed`
- AC-60 Set to `true`: a receipt dated Today is posted and reversed with date Past -> `201`; the reversing
  document's `documentDate` is Past; Balanced.
- AC-61 Two rules at once (E7): default, plus `document.pastDateAllowed` = `false`; original dated Today,
  reversal dated Past -> `400` with the single key `documentDate`; `rules` has exactly two elements,
  `stock.reversalBeforeOriginalAllowed` (`false`) and `document.pastDateAllowed` (`false`), each with
  `fields` `["documentDate"]`.

Reference, repeated articles, warehouse
- AC-70 Set(`document.referenceRequired`, `true`). Create of a receipt, an issue, a transfer, a count, a
  purchase order and a sales order without `reference`, with `null` and with `"  "` -> each refused by
  (`VALIDATION_FAILED`, `document.referenceRequired`, `true`) with key `reference`; with `"DN-1"` -> `201`.
  `PUT` of one of them with `"reference": null` -> refused, unchanged.
- AC-71 A draft without a reference made before the Set posts (a document) and confirms (an order); a
  reversal of a posted document without a reference -> `201` (R5).
- AC-72 Set(`document.repeatedArticleAllowed`, `false`). Create of a receipt, an issue, a transfer, a
  purchase order and a sales order with lines A, B, A -> each refused by (`VALIDATION_FAILED`,
  `document.repeatedArticleAllowed`, `false`) with keys `lines[0].articleId` and `lines[2].articleId`. With
  lines A, B -> `201`; `PUT` of a draft receipt with A twice -> refused, unchanged. A count with A twice ->
  `400` **without** `rules` (E8).
- AC-73 A draft issue with A on two lines made before the Set posts, and both lines write their entries.
- AC-74 Set(`document.warehouseRequired`, `true`). Create of a receipt, an issue, a count, a purchase order
  and a sales order without `warehouseId`, and with `null` -> each refused by (`VALIDATION_FAILED`,
  `document.warehouseRequired`, `true`) with key `warehouseId`; with `W1` -> `201`. A receipt linked to a
  confirmed order without `warehouseId` -> refused likewise; with the order's warehouse -> `201` (E9).
  A transfer without `warehouseId` -> `400` without `rules`.
- AC-75 After reset, a receipt without `warehouseId` is created in the default warehouse.

Inactive masters
- AC-80 Set(`references.inactiveAssignable`, `true`). With an inactive warehouse, article, supplier and
  unit: a receipt naming the inactive warehouse, article and supplier -> `201`; a purchase order naming
  them -> `201`; an article whose `baseUnitId` is the inactive unit -> `201`; an article unit with the
  inactive unit -> saved. A random uuid in each of these places -> `REFERENCE_NOT_FOUND` as before.
- AC-81 Independent (E10): after AC-80 the receipt's posting is refused by (`REFERENCE_INACTIVE`,
  `posting.activeMastersRequired`, `true`) with the header keys `warehouseId` and `partnerId` together
  (005/R13); the order's confirmation is refused by the same rule with `supplierId` and `warehouseId`.
- AC-82 Set(`posting.activeMastersRequired`, `false`) as well: the receipt posts, Stock(A, inactive
  warehouse) rises, Balanced; the order confirms and gets its number. With only this rule `false`
  (`references.inactiveAssignable` at default): a draft whose article was deactivated after saving posts.
- AC-83 `posting.activeMastersRequired` = `false` does not lift the role (R24): a draft receipt whose
  partner lost `isSupplier` after saving -> posting `PARTNER_ROLE_MISSING`.
- AC-84 Set(`stock.reversalActiveMastersRequired`, `true`). A posted receipt; Deactivate A; reverse ->
  refused by (`REFERENCE_INACTIVE`, `stock.reversalActiveMastersRequired`, `true`) with key
  `lines[0].articleId`; the original stays `posted`, no number consumed. Reactivate A -> `201`.
- AC-85 Header first (R25, E11): a posted transfer; deactivate the destination and an article; reverse ->
  `REFERENCE_INACTIVE` with exactly the key `toWarehouseId`. An unlinked receipt with partner `SUP`;
  deactivate `SUP` -> reverse refused with key `partnerId`. A **linked** receipt whose order's supplier was
  deactivated -> reverse `201`.
- AC-86 `set-default` of an inactive warehouse with `references.inactiveAssignable` = `true` ->
  `DEFAULT_WAREHOUSE`, without `rules` (R23).

`stock.outdatedCountAllowed`
- AC-90 Set to `true` (E12). Receive 10 of A into W1; a draft count of A in W1 with counted 7 (book 10,
  difference −3); issue 4 of A from W1. Post the count -> `200`; the line has `bookQuantity == 6`,
  `differenceQuantity == 1`; Stock(A, W1) == 7; one ledger entry of `+1`; Balanced; verify empty.
- AC-91 `true`: before posting, `GET` of the draft count after the issue still shows `bookQuantity == 10`
  (R29). A count that is not outdated posts as before.
- AC-92 Back to `false`: the count posted in AC-90 reads unchanged; a new outdated draft count -> refused
  by (`COUNT_OUTDATED`, `stock.outdatedCountAllowed`, `false`).

Existing data and timing
- AC-95 For one Set of each of the 13 rules: a posted document, a draft document, a confirmed order, the
  ledger and stock on hand read before the Set are JSON-equal to the same reads after it.
- AC-96 Read-your-writes: 10 times in a row — Set(`document.pastDateAllowed`, `false`), post a receipt
  dated Past (expect `DATE_NOT_ALLOWED`), Set to `true`, post it (expect `200`). No iteration differs.

Tenant isolation
- AC-100 Tenant X sets all 13 rules to the value that is not the default. In tenant Y everything of AC-20
  and AC-22 still holds, and `GET /rules` shows 43 defaults.

MCP
- AC-110 Through tools only: `rule_set` `{ key: "stock.receiptPartnerRequired", value: true }`;
  `stock_document_create` of an unlinked receipt without `partnerId` -> tool error `VALIDATION_FAILED` with
  key `partnerId` and `rules` JSON-equal to the HTTP response's for the same request; with a supplier ->
  the document.
- AC-111 `stock_document_post` of a document dated Past with `document.pastDateAllowed` = `false` -> tool
  error `DATE_NOT_ALLOWED` with the `errors` keys and `rules` of HTTP; `stock_document_reverse` refused by
  `stock.reversalActiveMastersRequired` likewise.
- AC-112 The input schemas of `stock_document_create`, `stock_document_update`, `purchase_order_create`
  and `sales_order_create` are identical before and after every rule of this spec is set to its other
  value.

Unchanged
- AC-120 After the criteria above, in every tenant used: Balanced, and verify is empty.

## 11. Notes for the tester and the builder

Tester
- **Reversing a linked receipt or delivery.** A receipt or delivery created by the order helper
  (`OrderApi.Document`) is dated `Stock.NextDay` — one day later than `Stock.Date`, the default date of
  `Stock.ReverseAsync`. A reversal dated before its original is refused (006/R12), so reversing such a
  document needs `Stock.NextDay` (or later) passed as the date. This mistake reached review in 009, 010 and
  012. In this spec it matters twice over, because the reversal's date is itself under test.
- **Dates in this spec's own tests are computed, not literal.** `Stock.Date` and `Stock.NextDay` are fixed
  calendar days: fine while both date rules are `true`, wrong in any test that sets one to `false`. There,
  build every document with Today, Past or Future as section 10 defines them, and never with yesterday or
  tomorrow — within a day of the date line the outcome depends on the hour (R13).
- One tenant per test, always.
- The weight is on: the defaults change nothing and name their rule (AC-20–AC-23); each rule at its other
  value; a rule that judges saving leaves drafts alone (AC-33, AC-71, AC-73); the three rules of inactive
  masters are independent (AC-80–AC-85).
- The approved changes to earlier tests are exactly those of AC-01 — mind item 4, which reverses two
  assertions of spec 012. Anything else: `docs/questions/014-q.md` first.

Builder
- A new rule is one `RuleDefinition` plus the check that receives its boolean. Nothing of the mechanism
  changes.
- `REFERENCE_INACTIVE` is produced in a few shared places (`ReferenceCheck`, the header references, the
  line kinds, the posting check): each takes the boolean and names the rule of its stage (R26). When the
  rule lifts the stage, skip it — do not turn inactive into "not found".
- Dates: one Domain function of (date, now, pastAllowed, futureAllowed); `now` from `IClock`. Posting,
  confirmation and reversal call it; create and replace do not (R12).
- Reversal's date stage answers once for all date rules (R17): build the `400` and call `RefusedBy` per
  rule, each with `["documentDate"]`.
- The partner and repeat checks of a replace sit where the stored type is known (`OfType`); the count's
  repeat check stays unconditional.
- R28: at posting, with the rule `true`, take the book quantities from the stock read under the lock and
  post the count with them; with `false` compare as today.
- Anything unclear or contradictory: `docs/questions/014-q.md`, then continue with the rest.

## 12. Decisions behind this spec

Owner's decisions (2026-10-10; ADR-0020): a rule is a boolean; "partner required on a receipt / an issue"
follows in batch 014, default *no* (`docs/rules.md` §5).

**For the owner** — two defaults of the architect that he may want to decide himself:

| # | Question | Default taken | Rule |
|---|---|---|---|
| 1 | What is "today" when the system has no time zone per company? | A date that is today anywhere in the world is never refused as past or future. A time zone per company would make it exact; it is not built. | R13 |
| 2 | "Back-dating not allowed": is a draft with an old date refused when it is saved, or when it is posted? | When it is posted (an order: confirmed) and when a reversal is dated. A draft may carry any date. | R12 |

Architect's defaults — each stands until the owner says otherwise.

| # | Question | Default | Rule |
|---|---|---|---|
| 3 | A draft saved under a looser rule, after the rule is tightened | It posts or confirms as it is; its next replace is judged | R3 |
| 4 | Partner required on a receipt — and a receipt linked to an order without a supplier | Not judged: it has the order's partner, which may be none | R8 |
| 5 | Is a system-made reversing document judged by the rules of saving? | No | R5 |
| 6 | May one switch allow inactive masters on saving, posting and reversal together? | No: three switches, independent | 6.8 |
| 7 | An outdated count that may be posted | It is posted against the stock of that moment, and shows the book quantity it was posted with | R28 |
| 8 | `document.warehouseRequired` and a linked receipt | It must name the warehouse too | R21 |
| 9 | Repeated articles forbidden — and a stock count | A count never has repeats, whatever the switch (invariant) | R20 |
