# Spec P01 — Sales master data: categories, tax rates, payment methods, sales prices, barcodes

Status: ready for the tester and the builder.
Owner requirement of 2026-10-13: a POS module on top of the ERP (`docs/pos/requirements.md`); "prices, taxes
and payment methods are defined through the existing master data".
Branches: `tests/P01-sales-master-data` (tester), `feat/P01-sales-master-data` (builder).
Read first: `docs/pos/overview.md` §3 (why this comes first), ADR-0022, spec **001** (a master: unit of
measure — the three new masters are copies of it), spec **002** (articles, references), spec **007**
(a sub-resource of an article), spec **013** (the master rules `codeChangeable` and `nameUnique`, and how a
refusal names its rule), architecture §5, §6, §11.

**Inherited, not re-specified**: everything specs 001–013 established — authentication, tenant scoping, the
error document, the order of checks, the list envelope and paging, `search` (001/R9), optional text, the
reference rules of ADR-0008, the rule mechanism. This spec states only what is new. Numbers are local;
"013/R12" means rule R12 of spec 013.

**There is no POS in this spec.** It is ERP master data, usable by any client and by later invoices (018).
Nothing here sells, computes a tax amount or takes a payment.

**The spec has a seam.** Part A (three masters: sections marked A) and part B (per article: sales data and
barcodes) share nothing but the references from B to A. If one loop turn is too short, the orchestrator cuts
there: A is built and merged first as P01, B follows as P01a, with the acceptance criteria as numbered.

**Decisions of the architect that the owner may change** are marked *(default)* and collected in section 12.

## 1. Goal

A tenant can say, for every article: which category it belongs to, which taxes apply to it, what it sells
for, and which barcodes identify it — and can keep the lists of categories, tax rates and payment methods
those answers come from.

## 2. Scope

In scope
- A. Three masters with the full set of operations of a unit of measure (list, get, get by code, create,
  replace, delete): **article categories**, **tax rates**, **payment methods**.
- B. **Sales data of an article** (category, tax rates, sales price): get, set, list.
- B. **Barcodes of articles**: list (the lookup by barcode), get, create, delete.
- Ten rules on the registry (6.1); one new error code, `BARCODE_TAKEN`.
- 22 MCP tools; `tools/list` returns 84.

Out of scope
- **The article itself does not change**: no new field in the `Article` representation, in `POST /articles`
  or in `PUT /articles/{id}`; every test of spec 002 stands. Sales data is a sub-resource, as conversions
  are (007).
- Price lists, prices per unit, per customer, per period or per POS; net prices; currencies.
- Computing tax; tax accounts; tax on orders (010 stays "prices without tax").
- A tree of categories *(default: one level)*.
- A unit on a barcode (a barcode names the article, in its base unit); check digits; barcode types.
- Seeding: a new tenant has no category, tax rate or payment method. Existing tenants get none.
- Payments. A payment method is only a name and a kind here.

## 3. Data

New tenant-owned tables, each with the audit columns and actor foreign keys of architecture §8 unless said
otherwise, and every foreign key composite with `TenantId` (ADR-0002).

| Table | Columns besides `Id`, `TenantId`, audit | Keys |
|---|---|---|
| `ArticleCategories` | `Code`, `Name`, `IsActive` | unique `(TenantId, lower(Code))` |
| `TaxRates` | `Code`, `Name`, `Rate` numeric(7,4), `IsActive` | unique `(TenantId, lower(Code))` |
| `PaymentMethods` | `Code`, `Name`, `Kind`, `IsActive` | unique `(TenantId, lower(Code))` |
| `ArticleSalesData` | `ArticleId`, `CategoryId` null, `SalesPrice` numeric null; `UpdatedAt`, `UpdatedBy` only | at most one row per article: unique `(TenantId, ArticleId)`; FK to `Articles`, `ArticleCategories` (restrict) |
| `ArticleTaxRates` | `ArticleId`, `TaxRateId`; no audit columns (part of the sales data) | unique `(TenantId, ArticleId, TaxRateId)`; FK to `Articles`, `TaxRates` (restrict) |
| `ArticleBarcodes` | `ArticleId`, `Barcode`; `CreatedAt`, `CreatedBy` only | unique `(TenantId, ArticleId, Barcode)`; a **non-unique** index `(TenantId, Barcode)`; FK to `Articles` |

No unique index on `(TenantId, Barcode)`, and none on a name: whether they are unique is a tenant's rule
(013 §3). An article without a row in `ArticleSalesData` has empty sales data; the builder may instead
create the row with the article — it is storage, not contract.

One new migration; no existing table changes.

## 4. Operations — HTTP

All under `/api/v1`, tenant key.

### 4.A Article categories, tax rates, payment methods

For each of `/article-categories`, `/tax-rates`, `/payment-methods` — the six operations of
`/units-of-measure` (001 §4.3), with the same statuses and errors, and with the refusals of 013 for a code
change and a taken name:

| Operation | Request | Success | Errors |
|---|---|---|---|
| List | `GET /<r>?search=&isActive=&limit=&offset=` (+ `kind=` for payment methods) | `200` list envelope, ordered by `code` | 400 |
| Get | `GET /<r>/{id}` | `200` | 404 |
| Get by code | `GET /<r>/by-code/{code}` | `200` | 404 |
| Create | `POST /<r>` | `201` + `Location` | 400, 409 `CODE_TAKEN`, 409 `NAME_TAKEN` |
| Replace | `PUT /<r>/{id}` (every field) | `200` | 400, 404, 409 `CODE_CHANGE_NOT_ALLOWED`, 409 `CODE_TAKEN`, 409 `NAME_TAKEN` |
| Delete | `DELETE /<r>/{id}` | `204` | 404, 409 `IN_USE` |

Representations (audit members `createdAt`, `updatedAt`, `createdBy`, `updatedBy` on each):

```json
{ "id": "uuid", "code": "DRINKS", "name": "Drinks", "isActive": true }                       // ArticleCategory
{ "id": "uuid", "code": "VAT25", "name": "VAT 25 %", "rate": 25, "isActive": true }          // TaxRate
{ "id": "uuid", "code": "CASH", "name": "Cash", "kind": "cash", "isActive": true }           // PaymentMethod
```

Bodies: create `{ "code", "name", "isActive"? }` plus `"rate"` (tax rate, required) or `"kind"` (payment
method, required); replace carries every field including `isActive`.

### 4.B Sales data of an article

Representation (`ArticleSalesData`):

```json
{ "article": { "id": "uuid", "code": "ESP", "name": "Espresso" },
  "category": { "id": "uuid", "code": "DRINKS", "name": "Drinks" },
  "taxRates": [ { "id": "uuid", "code": "VAT25", "name": "VAT 25 %", "rate": 25 } ],
  "salesPrice": 1.8,
  "updatedAt": "…Z", "updatedBy": "uuid" }
```

`category` and `salesPrice` are `null` when not set; `taxRates` is `[]` when none, ordered by `code`;
`updatedAt` and `updatedBy` are `null` for an article whose sales data was never set.

| Operation | Request | Success | Errors |
|---|---|---|---|
| Get | `GET /articles/{articleId}/sales-data` | `200` — also for an article never given any (all empty) | 404 (no such article) |
| Set | `PUT /articles/{articleId}/sales-data` body `{ "categoryId", "taxRateIds", "salesPrice" }` — all three present | `200` | 400, 404, 409 `REFERENCE_NOT_FOUND`, 409 `REFERENCE_INACTIVE` |
| List | `GET /articles/sales-data?search=&categoryId=&taxRateId=&priced=&isActive=&limit=&offset=` | `200` list envelope: one item per article of the tenant, ordered by article `code` | 400 |

`/articles/sales-data` is a fixed path and is not read as an article id.

### 4.B Barcodes

Representation (`ArticleBarcode`):

```json
{ "id": "uuid", "barcode": "3850123456789",
  "article": { "id": "uuid", "code": "ESP", "name": "Espresso" },
  "createdAt": "…Z", "createdBy": "uuid" }
```

| Operation | Request | Success | Errors |
|---|---|---|---|
| List / lookup | `GET /article-barcodes?barcode=&articleId=&limit=&offset=` | `200` list envelope, ordered by `barcode` (ordinal), then article `code` | 400 |
| Get | `GET /article-barcodes/{id}` | `200` | 404 |
| Create | `POST /article-barcodes` body `{ "articleId", "barcode" }` | `201` + `Location` | 400, 409 `REFERENCE_NOT_FOUND`, 409 `REFERENCE_INACTIVE`, 409 `BARCODE_TAKEN` |
| Delete | `DELETE /article-barcodes/{id}` | `204` | 404 |

A barcode is not replaced: delete it and create another.

### 4.C New error code (registry: architecture §6)

| HTTP | `code` | Meaning | `errors` keys |
|---|---|---|---|
| 409 | `BARCODE_TAKEN` | The article already has this barcode (invariant, no `rules`); or, with `article.barcodeUnique` = `true`, another article of the tenant has it (carries `rules`). | `barcode` |

`IN_USE` *(extended)*: delete of a category or a tax rate that an article's sales data names.

## 5. Operations — MCP

One tool per operation, same fields, limits and errors (ADR-0009); every tool with description (naming its
error codes and, where rules judge it, `rule_list` with the group), schemas and annotations, and covered by
the HTTP/MCP parity test. `tools/list` returns exactly the 62 names of spec 012 and these 22:

| Tools | Input | Output |
|---|---|---|
| `article_category_list`, `tax_rate_list`, `payment_method_list` | `{ search?, isActive?, limit?, offset? }` (+ `kind?` for payment methods) | list envelope |
| `article_category_get`, `tax_rate_get`, `payment_method_get` | `{ id?, code? }` — exactly one | the record |
| `article_category_create`, `tax_rate_create`, `payment_method_create` | the create body | the record |
| `article_category_update`, `tax_rate_update`, `payment_method_update` | `{ id }` + the replace body | the record |
| `article_category_delete`, `tax_rate_delete`, `payment_method_delete` | `{ id }` | `{ deleted: true }` |
| `article_sales_data_get` | `{ articleId }` | ArticleSalesData |
| `article_sales_data_set` | `{ articleId, categoryId: uuid\|null, taxRateIds: uuid[], salesPrice: number\|null }` | ArticleSalesData |
| `article_sales_data_list` | `{ search?, categoryId?, taxRateId?, priced?, isActive?, limit?, offset? }` | list envelope |
| `article_barcode_list` | `{ barcode?, articleId?, limit?, offset? }` | list envelope |
| `article_barcode_get` | `{ id }` | ArticleBarcode |
| `article_barcode_create` | `{ articleId, barcode }` | ArticleBarcode |
| `article_barcode_delete` | `{ id }` | `{ deleted: true }` |

The description of `article_sales_data_set` says that `salesPrice` **includes tax**.

## 6. Rules

### 6.1 Rules this spec introduces (architecture §11)

`<m>` is `articleCategory`, `taxRate` or `paymentMethod`. `true` means the statement in the name holds. No
stored record is changed, re-checked or refused later for having been written under another value (012/R15).

| Key | Name | Default | Switching it to the other value |
|---|---|---|---|
| `<m>.codeChangeable` (3 keys) | The code of an article category / a tax rate / a payment method may be changed | `true` | `false`: a replace that changes the code is refused (`CODE_CHANGE_NOT_ALLOWED`). |
| `<m>.nameUnique` (3 keys) | Names of article categories / tax rates / payment methods must be unique | `false` | `true`: a create or replace whose name another record of the resource has is refused (`NAME_TAKEN`). |
| `article.categoryRequired` | Category required in an article's sales data | `false` | `true`: sales data saved without a category is refused. |
| `article.taxRateRequired` | Tax rate required in an article's sales data | `false` | `true`: sales data saved with no tax rate is refused. |
| `article.salesPriceRequired` | Sales price required in an article's sales data | `false` | `true`: sales data saved without a price is refused. |
| `article.barcodeUnique` | A barcode may belong to only one article | `true` | `false`: several articles may have the same barcode; the lookup returns all of them. |

The registry then has **40** rules. The six `<m>` rules behave exactly as 013/R9–R16 describe for the four
masters of spec 013 — comparison, place in the order of checks, parallel writes, `rules` in the refusal.

### 6.2 Every other check of this spec is an invariant (`docs/rules.md` §2)

| Check | Invariant | Reason |
|---|---|---|
| Code and name of the three masters: required, form, length; code unique | I7, I9, I10 | as every master |
| `rate`: a JSON number, `0 ≤ rate ≤ 100`, at most 4 decimals | I9, I10 | A percentage; the bound is storage. |
| `kind`: one of `cash`, `card`, `transfer`, `other` | I8 | A definition: later specs treat the kinds differently (change is given for cash; a card payment has an outcome). |
| `salesPrice`: `null` or a JSON number, `0 ≤ salesPrice ≤ 999999999.999999`, at most 6 decimals | I10 | Bounds of a unit price. Zero is a price (a free item). |
| `salesPrice` includes tax | I8 | What the number means; switching it would re-price every article. *(default — owner decision 4 of the overview)* |
| `taxRateIds`: an array of at most 4 distinct ids | I8, I10 | A tax applies once; four is a bound. |
| `barcode`: after trimming 1–50 characters, each a printable ASCII character other than space (U+0021–U+007E); compared exactly, case-sensitively | I9, I10 | What a scanner sends; `a` and `A` differ in Code 128. |
| An article lists a barcode once | I8 | The second row would say nothing. |
| References exist in the tenant; an inactive record is not newly referenced; a referenced category or tax rate is not deleted | I1, I6 | ADR-0008 |

**This spec adds no check that is not in one of the two tables.**

### 6.3 The three masters (A)

- R1. Everything of 001 for a unit of measure holds for each: code and name rules, `isActive` default
  `true`, list with `search` over code and name and `isActive`, order by code, get by code case-insensitive.
- R2. `rate` is required on create and replace. `25`, `25.0` and `25.0000` are the same rate; it is returned
  as a JSON number. A quoted number is a wrong type (`400`, key `rate`).
- R3. `kind` is required on create and replace; a missing, `null` or unknown value -> `400`, key `kind`.
  `GET /payment-methods?kind=` filters by it; an unknown value -> `400`, key `kind`.
- R4. `rate` and `kind` may be changed by a replace. Nothing stores a copy of them yet. (From P07 on, bills
  copy the rate they used and payments freeze `kind`.)
- R5. Delete: `409 IN_USE` for a category or tax rate named by any article's sales data — also of an
  inactive article. A payment method is always deletable in this spec.
- R6. `<m>.codeChangeable` and `<m>.nameUnique`: 013/R9–R16 with `<m>` as given. Create and replace run the
  uniqueness check and the write under the tenant lock (013/R13).

### 6.4 Sales data (B)

- R7. Every article — `stock` or `service`, active or inactive — has sales data, empty until set. Setting it
  on an inactive article is allowed (it is the article's own data, not a new reference to it).
- R8. Set replaces all three members. The body must contain the three properties (I9): `categoryId` a UUID
  or `null`; `taxRateIds` an array (possibly empty); `salesPrice` a number or `null`. A missing property ->
  `400` with its key, no `rules`.
- R9. `taxRateIds`: more than four -> `400`, key `taxRateIds`; an element that is not a UUID, or that
  repeats an earlier one -> `400`, key `taxRateIds[i]` (zero-based index of the offending element).
- R10. The required rules judge **Set** only: with the rule `true`, `categoryId: null`, `taxRateIds: []` or
  `salesPrice: null` is refused — `400 VALIDATION_FAILED`, keys `categoryId`, `taxRateIds`, `salesPrice`
  respectively, each naming its rule (013/R17, R18), reported with every other invalid field. Creating or
  replacing an **article** is never judged by them: an article exists with empty sales data until someone
  sets it. (Whether such an article can be sold is a rule of spec P05.)
- R11. Order of checks of Set: validation -> the article exists (`404`) -> references: `categoryId`, then
  each `taxRateIds[i]` in order — `409 REFERENCE_NOT_FOUND` / `REFERENCE_INACTIVE` with that key; the first
  failing reference answers alone. An inactive category or tax rate the article **already has** may be kept
  (ADR-0008); only a newly assigned one is refused.
- R12. Set is attributed: `updatedAt`, `updatedBy`. It does not change the article's own `updatedAt`.
- R13. List returns one item per article, also articles never set. `search` is over the article's code and
  name (001/R9); `isActive` is the article's; `categoryId` and `taxRateId` filter by reference (an id that
  names nothing in the tenant gives an empty list; a malformed one `400`); `priced=true` only articles with
  a `salesPrice`, `priced=false` only those without.
- R14. Deleting an article (002, 005/R25, 007) also deletes its sales data, its tax-rate links and its
  barcodes; none of them makes an article `IN_USE`.
- R15. Renaming or re-coding a category or tax rate shows in every article's sales data at the next read
  (embedded summaries are current, ADR-0008).

### 6.5 Barcodes (B)

- R16. Create: validation (`articleId` a UUID, `barcode` by 6.2; both required) -> the article
  (`REFERENCE_NOT_FOUND` / `REFERENCE_INACTIVE`, key `articleId`) -> the article already has the barcode
  (`BARCODE_TAKEN`, no `rules`) -> with `article.barcodeUnique` = `true`, another article has it
  (`BARCODE_TAKEN` with `rules`: key `article.barcodeUnique`, value `true`, `fields` `["barcode"]`).
- R17. The barcode is trimmed and stored as sent otherwise; `00123` and `123` are different barcodes.
- R18. Under parallel creates of one tenant with the rule `true`, of several that would give one barcode to
  different articles exactly one succeeds (under the tenant lock, 013/R13).
- R19. List: `barcode` matches exactly after trimming (not a substring, not case-folded); `articleId`
  filters by article; both may be given; neither gives every barcode of the tenant. A `barcode` longer than
  50 characters or with a character outside 6.2 -> `400`, key `barcode`. Barcodes of inactive articles are
  returned (the summary does not say so; the client reads the article).
- R20. With `article.barcodeUnique` switched from `false` to `true`, existing duplicates stay and the
  lookup still returns all of them; only the next create is judged.
- R21. `BARCODE_TAKEN` names no other record: `detail` and `errors` repeat only the barcode sent (013/S2).

## 7. Edge cases

- E1. A tax rate of `0` is valid ("exempt"); `-1`, `100.01`, `12.34567` and `"25"` are `400`, key `rate`.
- E2. `salesPrice: 0` is saved and `priced=true` finds the article; with `article.salesPriceRequired` =
  `true` it passes (it is a price).
- E3. `taxRateIds: [a, a]` -> `400`, key `taxRateIds[1]`. `taxRateIds: [a, b, c, d, e]` -> `400`, key
  `taxRateIds`.
- E4. An article has the tax rate `VAT25`; the rate is deactivated. Set with the same `taxRateIds` -> `200`.
  Set adding it to another article -> `REFERENCE_INACTIVE`, key `taxRateIds[0]`.
- E5. `article.categoryRequired` = `true` and a body `{ "categoryId": null, "taxRateIds": [], "salesPrice":
  -1 }` -> `400` with keys `categoryId` and `salesPrice`; `rules` has one element, for `categoryId`.
- E6. A category named by an article is deleted -> `IN_USE`; after Set with `categoryId: null` -> `204`.
- E7. `article.barcodeUnique` = `false`: two articles get `3850123456789`;
  `GET /article-barcodes?barcode=3850123456789` -> two items. The same article a second time ->
  `BARCODE_TAKEN` without `rules`, whatever the rule.
- E8. `barcode: " 123 "` is stored as `123`; `"12 3"` and `""` are `400`, key `barcode`.
- E9. `GET /articles/sales-data` in a tenant with three articles and no sales data set -> three items, all
  empty.
- E10. A category `DRINKS` and a unit of measure named the same: no collision (name spaces are per
  resource, 013/R12).

## 8. Tenant isolation

- T1. All six tables are tenant-owned and filtered; every reference is checked within the tenant: a
  category, tax rate or article of another tenant is `REFERENCE_NOT_FOUND` (in a body) or `404` (in a path).
- T2. Codes, names and barcodes are unique per tenant only; `CODE_TAKEN`, `NAME_TAKEN` and `BARCODE_TAKEN`
  are never answered for another tenant's record, and the lookup by barcode never returns one.
- T3. A rule set by tenant X changes nothing for tenant Y.
- T4. The same through the tools. No `IgnoreQueryFilters()` is added.

## 9. Security requirements

- S1. Authentication and permissions as before: any tenant key (owner decision of spec 003; a "POS only"
  key is an open question of spec P02).
- S2. No input causes `500` (001/R16) — in particular not a delete racing a Set that names the record
  (the foreign key is the authority: the Set answers `REFERENCE_NOT_FOUND`, or the delete `IN_USE`).
- S3. Every write is attributed to the key (I3).

## 10. Acceptance criteria

Conventions of 012 §10 and 013 §10 ("Set(key, v)", "refused by (CODE, key, v)"). Each test uses its own
tenant. Unmarked criteria are black-box (tester).

Structure
- AC-01 *(manual)* Build and tests exit 0; earlier tests pass unweakened. The **only** earlier tests
  changed: (1) tests pinning the number of rules or their literal list: 30 -> 40 with the keys of AC-10;
  (2) tests pinning the rules of group `article` (013 AC-11): three -> seven; (3) tests pinning the number
  or the list of tools: 62 -> 84 with the names of section 5; (4) a rule search whose expected result now
  also matches one of the ten new keys or names. No test of spec 002 changes.
- AC-02 *(manual)* The ten decisions are booleans obtained through `IRules` and passed to Domain functions;
  `Api` reads no rule; the three masters reuse the master rule set and the write checks of spec 013 — no
  copy of them.
- AC-03 *(builder, unit)* The decisions of 6.2 for `rate`, `kind`, `salesPrice`, `taxRateIds` and `barcode`,
  and the barcode-uniqueness decision, as pure functions.

Registry and tools
- AC-10 In a new tenant `GET /rules?limit=500` -> `total == 40`: the 30 of 013 AC-10 and
  `article.barcodeUnique` `true`, `article.categoryRequired` `false`, `article.salesPriceRequired` `false`,
  `article.taxRateRequired` `false`, `articleCategory.codeChangeable` `true`, `articleCategory.nameUnique`
  `false`, `paymentMethod.codeChangeable` `true`, `paymentMethod.nameUnique` `false`,
  `taxRate.codeChangeable` `true`, `taxRate.nameUnique` `false`; in ordinal order of key; `group` equal to
  the key's first segment.
- AC-11 Each of the ten accepts both values and resets (012 §6).
- AC-12 `tools/list` returns exactly 84 names: the 62 and the 22 of section 5. For one operation of each of
  the five resources, the tool's result is JSON-equal to the HTTP response.

A — the three masters (each criterion for each of the three resources, as a theory)
- AC-20 Create with `code` and `name` (and `rate: 25` / `kind: "cash"`) -> `201`, `Location`, `isActive`
  `true`, audit members set; `GET` by id and by code (in another letter case) return it; it is in the list.
- AC-21 Validation as a unit of measure: missing or malformed `code`, missing `name`, a 201-character name,
  an unknown property -> `400` with the key, no `rules`. A second record with the same code in another
  case -> `CODE_TAKEN`.
- AC-22 Replace with every field -> `200`, `updatedAt` later; without `isActive` -> `400`; unknown id ->
  `404`. List: `search` finds by part of code and of name; `isActive=false` after a deactivation.
- AC-23 Delete -> `204`, then `404`.
- AC-24 Tax rate (E1): `rate` `0`, `5`, `13`, `25`, `100` and `12.3456` are accepted and read back
  numerically equal; missing, `null`, `-1`, `100.01`, `12.34567`, `"25"` -> `400`, key `rate`. A replace
  changes the rate.
- AC-25 Payment method: each of the four kinds is accepted; missing, `null`, `"Cash"`, `"cheque"` -> `400`,
  key `kind`; `GET /payment-methods?kind=card` returns only card methods; `kind=x` -> `400`.
- AC-26 Defaults (a tenant that sets nothing): a replace changes the code -> `200`; two records with the
  same name are created.
- AC-27 Set(`<m>.codeChangeable`, `false`): replace with the stored code -> `200`; with another -> refused
  by (`CODE_CHANGE_NOT_ALLOWED`, `<m>.codeChangeable`, `false`), key `code`, record unchanged; with another
  record's code -> `CODE_CHANGE_NOT_ALLOWED`, not `CODE_TAKEN`.
- AC-28 Set(`<m>.nameUnique`, `true`): `Acme` exists; create `acme` and `" ACME "` -> refused by
  (`NAME_TAKEN`, `<m>.nameUnique`, `true`), key `name`; a replace of `Acme` with its own body -> `200`. A
  category and a tax rate may share a name. 10 parallel creates with one name and different codes ->
  exactly one `201`, none `500`.
- AC-29 Separate: with `taxRate.nameUnique` = `true` only, two categories with one name are created.

B — sales data
- AC-40 `GET /articles/{id}/sales-data` of a new article -> `200`: `category` `null`, `taxRates` `[]`,
  `salesPrice` `null`, `updatedAt` and `updatedBy` `null`, `article` its summary. Unknown article -> `404`.
  `GET /articles/{id}` is JSON-equal before and after any Set (the article has no new member).
- AC-41 Set with a category, two tax rates and `salesPrice: 1.8` -> `200`; `GET` shows the category
  summary, both rates ordered by code with their `rate`, the price, `updatedAt` and `updatedBy`. A second
  Set with `{ null, [], null }` -> `200`, all empty again, `updatedAt` kept current.
- AC-42 Works for a `service` article and for an inactive article (R7).
- AC-43 Shape (R8, R9, E3): a body without `taxRateIds` -> `400`, key `taxRateIds`; `salesPrice` `-1`,
  `"1.8"`, `1.2345678` -> key `salesPrice`; `[a, a]` -> key `taxRateIds[1]`; five rates -> key
  `taxRateIds`; `categoryId: "x"` -> key `categoryId`; an unknown property -> its key. None has `rules`.
  `salesPrice: 0` -> `200` (E2).
- AC-44 References (R11): an unknown `categoryId` -> `REFERENCE_NOT_FOUND`, key `categoryId`; an inactive
  one -> `REFERENCE_INACTIVE`; an unknown second tax rate -> key `taxRateIds[1]`; unknown category **and**
  unknown tax rate -> only `categoryId`. E4 as written.
- AC-45 Defaults: Set with everything empty -> `200` in a tenant that sets nothing.
- AC-46 For each of the three required rules, in its own tenant: Set(rule, `true`); Set of sales data
  without that member -> refused by (`VALIDATION_FAILED`, rule, `true`) with the member's key, nothing
  stored; with it -> `200`. Creating an article and `PUT /articles/{id}` still succeed (R10). E5 as written.
- AC-47 Existing data: an article with empty sales data exists; Set(`article.salesPriceRequired`, `true`);
  its sales data reads unchanged; the list is JSON-equal to before.
- AC-48 Delete (R5, E6): a category and a tax rate named by sales data -> `IN_USE`; after the article's
  sales data no longer names them -> `204`. `DELETE /articles/{id}` of an article with sales data and
  barcodes -> `204`; afterwards the category and tax rate can be deleted and the barcode is not in the list.
- AC-49 List (R13, E9): one item per article ordered by article code; `categoryId`, `taxRateId`,
  `priced=true`, `priced=false`, `search` and `isActive` each filter as R13 says; a well-formed id that
  names nothing -> `200` empty; `categoryId=x` -> `400`; paging and `total` as every list.
- AC-50 R15: a category is renamed; the article's sales data shows the new name.

B — barcodes
- AC-60 Create -> `201`, `Location`, the representation with the article summary; `GET` by id; list by
  `articleId` and by `barcode`; delete -> `204`, then `404` and not in the list.
- AC-61 Form (E8, 6.2): `" 123 "` stored as `123`; `""`, `"12 3"`, a 51-character barcode, a barcode with
  `é`, a missing `barcode`, a missing or malformed `articleId` -> `400` with the key. `abc` and `ABC` are
  two barcodes of one article.
- AC-62 References: an unknown article -> `REFERENCE_NOT_FOUND`, key `articleId`; an inactive one ->
  `REFERENCE_INACTIVE`.
- AC-63 Default (`article.barcodeUnique` = `true`): the barcode of article 1 on article 2 -> refused by
  (`BARCODE_TAKEN`, `article.barcodeUnique`, `true`), key `barcode`, status `409`; the response contains
  neither the id nor the code of article 1 (R21). The same barcode on article 1 again -> `BARCODE_TAKEN`
  **without** `rules`.
- AC-64 Set to `false` (E7): article 2 gets the barcode; the lookup returns two items; article 1 again ->
  `BARCODE_TAKEN` without `rules`. Back to `true` (R20): both stay and are both returned; a third article
  with that barcode -> refused.
- AC-65 Lookup (R19): exact — `GET /article-barcodes?barcode=12` does not find `123`; `barcode=ABC` does
  not find `abc`; no filter returns all, paged.
- AC-66 Parallel (R18): 10 parallel creates of one barcode on ten articles -> exactly one `201`, nine
  `BARCODE_TAKEN`, none `500`.

Isolation and MCP
- AC-80 Tenant Y cannot read, replace, delete or reference X's category, tax rate, payment method, article
  sales data or barcode: `404` in a path, `REFERENCE_NOT_FOUND` in a body. Y creates the same codes, names
  (with `nameUnique` `true` in both) and barcodes as X -> `201`. Y's barcode lookup never returns X's.
- AC-81 X sets all ten rules to the other value; in Y the defaults of AC-26, AC-45 and AC-63 still hold.
- AC-82 Through tools only: create a category and a tax rate, `article_sales_data_set`,
  `article_barcode_create`, then `article_sales_data_get` and `article_barcode_list` with `barcode` ->
  the same data as HTTP. Tool errors `NAME_TAKEN`, `BARCODE_TAKEN` (with `rules`) and a required-field
  `VALIDATION_FAILED` carry the same `code`, `errors` keys and `rules` as HTTP.

## 11. Notes for the tester and the builder

Tester
- A is one theory over three resources (route, a valid create body, the extra field). Write it once.
- One tenant per test, always (rules).
- Do not pin `name` or `description` texts of rules; pin keys and defaults (AC-10).
- The approved changes to earlier tests are exactly those of AC-01. Anything else that fails goes to
  `docs/questions/P01-q.md` first.

Builder
- The three masters: `RuleRegistry.Master(...)` and `MasterWriteChecks` of spec 013 already do the code
  change and the name check; a master is its entity, its validation and its operations as
  `UnitOfMeasureOperations`.
- Sales data is one operation that replaces a small aggregate (row + links) inside
  `SerializedPerTenantAsync`; the three required rules fit the `RequiredFields` table if it is made to take
  "is the value missing" for a non-text member — do not write three branches.
- Barcode uniqueness cannot rest on a unique index across articles (the rule); within an article it can.
- `GET /articles/sales-data` must not load every article and filter in memory: filter and page in SQL.
- Route order: `/articles/sales-data` before `/articles/{id}`.
- Anything unclear or contradictory: `docs/questions/P01-q.md`, then continue with the rest.

## 12. Decisions behind this spec

Owner's requirement (2026-10-13): prices, taxes and payment methods come from the ERP's master data.
Open owner decisions that touch this spec: overview §9, rows 4 (prices include tax), 5 (several taxes per
article), 11 (categories on one level).

Architect's defaults — each stands until the owner says otherwise.

| # | Question | Default | Rule |
|---|---|---|---|
| 1 | Where do category, tax and price of an article live? | In a sub-resource, `sales-data`; the article's own contract does not change | §2, R7 |
| 2 | One price or price lists? | One sales price per article, tax-inclusive | 6.2 |
| 3 | How many taxes per article? | Zero to four, each a tax rate | 6.2, R9 |
| 4 | May two articles share a barcode? | No, by default (`article.barcodeUnique`) | 6.1 |
| 5 | Is a barcode checked for a check digit or a type? | No; any printable text without spaces | 6.2 |
| 6 | Are payment methods and tax rates created for a new tenant? | No; the tenant (or the POS set-up) creates them | §2 |
| 7 | May a tax rate's percentage be changed? | Yes; documents will copy the rate they used | R4 |
