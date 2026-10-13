# Spec 013 — Rules, batch 2: master data

Status: ready for the tester and the builder.
Owner requirement of 2026-10-10: every validation and business rule is configurable per tenant; we ship the
default (`CLAUDE.md`, ADR-0020, architecture §11). Owner's order of work: 012, then the retrofit 013 and 014,
then valuation.
Branches: `tests/013-master-data-rules` (tester), `feat/013-master-data-rules` (builder).
Read first: **spec 012** (the mechanism — registry, values, history, the `rules` member of an error; this
spec adds rules to it and changes nothing of it), architecture §11, `docs/rules.md` §3 (batch 013), specs
**001**, **002** and **004** (the four masters and their validation).
Why this spec and why this number: `docs/roadmap.md` section 3, row 13.

**Inherited, not re-specified** (roadmap §4): everything specs 001–012 established. This spec states only
what is new or changed. Numbers are local; "004/R15" means rule R15 of spec 004.

**The one sentence that governs everything below: a tenant that sets no rule behaves exactly as specs
001–012 say.** Every default is today's behaviour.

**Decisions of the architect that the owner may change** are marked *(default)* and collected in section 12.

## 1. Goal

The business choices in the four masters — units of measure, articles, partners, warehouses — become
switches a tenant can set: whether a code may be changed, whether names (and tax ids) must be unique, which
optional fields are required, and whether a partner must be a customer or a supplier. 24 rules join the six
of spec 012; nothing else about the masters changes. When one of them refuses a write, the error names it.

## 2. Scope

In scope
1. 24 new entries in the rule registry (6.1); the registry then has 30.
2. Create and replace of a unit of measure, an article, a partner and a warehouse read these rules and judge
   the write by them.
3. Three new error codes: `CODE_CHANGE_NOT_ALLOWED`, `NAME_TAKEN`, `TAX_ID_TAKEN`.

Out of scope
- **No new operation and no new tool.** The five rule operations and tools of spec 012 serve the new rules
  unchanged; `tools/list` still returns 62.
- No change of any request or response shape: every field a rule can require is already nullable for every
  tenant and stays so (architecture §11, "the contract does not vary per tenant").
- What `docs/rules.md` classifies as an invariant: the form and length of a code, that a code is required
  and unique, that a name is required, lengths, the country-code form, article `type` and base unit, the
  default warehouse. Their refusals carry no `rules` member, as before.
- Delete, deactivation and `set-default` of a master; unit conversions of an article (007); API keys; the
  tenant itself. No rule of this spec judges them.
- A form for the tax id (`docs/rules.md` §3: none is checked and none is added).
- Rules of documents and orders: specs 014 and 014a.

## 3. Data

No table changes and no column changes. The registry is code.

The builder may add **non-unique** indexes that serve the lookups of R12 and R15 (lower-cased name per
resource; lower-cased tax id of partners). A unique index on a name or a tax id is **not** allowed: whether
they are unique is a tenant's rule, and tenants with the rule off keep their duplicates.

## 4. Operations — HTTP

No new route. Create (`POST`) and replace (`PUT`) of `/units-of-measure`, `/articles`, `/partners` and
`/warehouses` are judged by the rules of section 6. Requests, responses and every existing error are those
of specs 001–012.

New error codes (registry: architecture §6):

| HTTP | `code` | Meaning | `errors` keys |
|---|---|---|---|
| 409 | `CODE_CHANGE_NOT_ALLOWED` | With `<m>.codeChangeable` = `false`: a replace would change the record's code (R9). | `code` |
| 409 | `NAME_TAKEN` | With `<m>.nameUnique` = `true`: another record of the same resource in the tenant has this name (R12). | `name` |
| 409 | `TAX_ID_TAKEN` | With `partner.taxIdUnique` = `true`: another partner of the tenant has this tax id (R15). | `taxId` |

`<m>` is `uom`, `article`, `partner` or `warehouse`. The three codes are answered only by a rule, so each
always carries `rules`. `CODE_TAKEN` is unchanged: an invariant (I7), without `errors` and without `rules`.

Refusals of this spec that keep an existing code: `VALIDATION_FAILED` for a missing required field (R4) and
for a partner without a role (R7, as 004/R15) — now with `rules`.

## 5. Operations — MCP

No new tool; `tools/list` returns exactly the 62 names of spec 012. No input or output schema changes: the
schemas do not depend on a rule's value.

- `rule_list`, `rule_get`, `rule_set`, `rule_reset`, `rule_change_list` work for the 24 new keys as for the
  six.
- A tool error of a create or update tool of the four masters carries `rules` exactly as the HTTP problem
  does (012 §5).
- Descriptions of `uom_create` / `uom_update`, `article_create` / `article_update`, `partner_create` /
  `partner_update`, `warehouse_create` / `warehouse_update` (the tool names as built) say: this company's
  rules can make optional fields required and names unique (`rule_list` with `group` = `uom`, `article`,
  `partner` or `warehouse`); a refusal's `rules` names the rule. Where a description lists the tool's error
  codes it gains the new ones that tool can answer: `NAME_TAKEN` (all eight), `CODE_CHANGE_NOT_ALLOWED` (the
  four update tools), `TAX_ID_TAKEN` (the two partner tools).

## 6. Rules

### 6.1 Rules this spec introduces (architecture §11)

`true` means the statement in the name holds. "Does not rewrite" is the same for all: **no stored record is
changed, re-checked or refused later for having been written under another value** (012/R15); the column
says what that means for the rule.

| Key | Name | Default | Switching it to the other value | It does not rewrite |
|---|---|---|---|---|
| `uom.codeChangeable` | The code of a unit of measure may be changed | `true` | `false`: a replace that changes the code is refused (`CODE_CHANGE_NOT_ALLOWED`). | Codes changed earlier stay as they are. |
| `article.codeChangeable` | The code of an article may be changed | `true` | the same for articles | the same |
| `partner.codeChangeable` | The code of a partner may be changed | `true` | the same for partners | the same |
| `warehouse.codeChangeable` | The code of a warehouse may be changed | `true` | the same for warehouses | the same |
| `uom.nameUnique` | Names of units of measure must be unique | `false` | `true`: a create or replace whose name another unit already has is refused (`NAME_TAKEN`). | Existing duplicates stay; each is judged at its own next replace. |
| `article.nameUnique` | Names of articles must be unique | `false` | the same for articles | the same |
| `partner.nameUnique` | Names of partners must be unique | `false` | the same for partners | the same |
| `warehouse.nameUnique` | Names of warehouses must be unique | `false` | the same for warehouses | the same |
| `article.descriptionRequired` | Description required on an article | `false` | `true`: an article saved without a description is refused. | Articles without one stay valid; the next replace of one must give it. |
| `partner.roleRequired` | A partner must be a customer or a supplier | `true` | `false`: a partner with neither role may be saved. | Partners with roles are untouched. After `true` again, a partner without a role stays and is read as before; its next replace must give it a role. |
| `partner.taxIdRequired` | Tax id required on a partner | `false` | `true`: a partner saved without a tax id is refused. | as for the description |
| `partner.taxIdUnique` | Tax ids of partners must be unique | `false` | `true`: a create or replace whose tax id another partner already has is refused (`TAX_ID_TAKEN`). | Existing duplicates stay; each is judged at its own next replace. |
| `partner.addressLine1Required` | Address line 1 required on a partner | `false` | `true`: a partner saved without that field is refused. | Records without the field stay valid; the next replace of one must give it. |
| `partner.addressLine2Required` | Address line 2 required on a partner | `false` | the same | the same |
| `partner.postalCodeRequired` | Postal code required on a partner | `false` | the same | the same |
| `partner.cityRequired` | City required on a partner | `false` | the same | the same |
| `partner.regionRequired` | Region required on a partner | `false` | the same | the same |
| `partner.countryCodeRequired` | Country code required on a partner | `false` | the same | the same |
| `warehouse.addressLine1Required` | Address line 1 required on a warehouse | `false` | the same, for a warehouse | the same |
| `warehouse.addressLine2Required` | Address line 2 required on a warehouse | `false` | the same | the same |
| `warehouse.postalCodeRequired` | Postal code required on a warehouse | `false` | the same | the same |
| `warehouse.cityRequired` | City required on a warehouse | `false` | the same | the same |
| `warehouse.regionRequired` | Region required on a warehouse | `false` | the same | the same |
| `warehouse.countryCodeRequired` | Country code required on a warehouse | `false` | the same | the same |

Fixed checks these replace: 001/R6, 002/R12, 004/R8 (a replace may change the code); the absence of a name
check in 001, 002, 004; 002/R3 (description optional); 004/R15 (a role is needed); 004/R3 and R5 (tax id and
address fields optional); 004/R16 (tax id not unique).

**This spec adds no check that is not one of these 24 rules.** Every other check of the four masters is an
invariant of `docs/rules.md` §2 and is unchanged.

### 6.2 What is judged, and when

- R1. The registry has exactly 30 rules: the six of spec 012 and the 24 of 6.1. Everything of 012 §6.2–6.3
  holds for the new ones — listing, set, reset, history, "from the next operation", "nothing stored is
  touched".
- R2. The rules of this spec judge **create and replace of the resource their key names** — nothing else.
  Reading, listing, delete, deactivation and `set-default` are never judged. Creating a tenant (its
  `CENTRAL` warehouse) is not judged.
- R3. **A replace is judged whole** (`docs/rules.md` §5): the body must satisfy the rules in force now, also
  in the fields it leaves as they were. A record written under looser rules stays valid and readable until
  then; only its next replace is judged. *(default)*
- R4. A write reads the rules once and is judged entirely by that one set (012/R14). Under parallel writes
  the uniqueness rules hold (R13).

### 6.3 Required fields — `article.descriptionRequired`, `partner.taxIdRequired`, the twelve address rules

- R5. With `true`, a create or replace in which the field is omitted (create), `null`, or empty after
  trimming is refused: `400 VALIDATION_FAILED` with the field's key (`description`, `taxId`,
  `addressLine1`, `addressLine2`, `postalCode`, `city`, `region`, `countryCode`), naming the rule (R17). It
  is part of validation: reported together with every other invalid field of the body.
- R6. With `false`, the field is optional exactly as today. For either value the form of a value that is
  given is judged as before, by an invariant and without `rules`: lengths, control characters, the
  two-letter country code. A replace body that lacks the property is `400` with that key and **no** `rules`
  (replace carries every field, I9) — whatever the rule's value.
- R7. Each of the fourteen rules is separate: `partner.cityRequired` says nothing about a warehouse's city.

### 6.4 `partner.roleRequired`

- R8. With `true` (default) 004/R15 holds as built, and the refusal names the rule: keys `isCustomer` and
  `isSupplier`, `rules` with `fields` `["isCustomer", "isSupplier"]`. With `false`, a partner with
  `isCustomer == false` and `isSupplier == false` is saved, on create and on replace. A missing or
  wrong-typed role field is the form of the request: reported alone as today, without `rules` (004/R15,
  004-q B-Q2). The role checks of orders and stock documents are untouched: a partner without a role still
  cannot be named where a supplier or a customer is needed (`PARTNER_ROLE_MISSING`).

### 6.5 `<m>.codeChangeable`

- R9. With `false`, a replace whose `code` differs from the stored code is refused:
  `409 CODE_CHANGE_NOT_ALLOWED` with key `code`. The comparison is exact after trimming: a change of letter
  case only is a change *(default)*. A replace that sends the stored code passes. Create is never judged.
- R10. Place in the order of checks of a replace: validation -> the record exists (`404`) -> … ->
  **code change** -> `CODE_TAKEN` -> the rest. It stands immediately before the `CODE_TAKEN` check of the
  resource, and every other stage stays where it is. So a changed code that is also taken answers
  `CODE_CHANGE_NOT_ALLOWED`.
- R11. With `true` (default) 001/R6, 002/R12 and 004/R8 hold as built.

### 6.6 `<m>.nameUnique` and `partner.taxIdUnique`

- R12. With `<m>.nameUnique` = `true`, a create or replace is refused with `409 NAME_TAKEN`, key `name`,
  when **another** record of the same resource in the tenant has the same name. Names are compared like
  codes: after trimming, case-insensitively. Inactive records count. The four resources are four name
  spaces: a unit and an article may share a name.
- R13. Under parallel writes of one tenant with the rule `true`, of several creates or replaces that would
  give the same name to different records exactly one succeeds and the others answer `NAME_TAKEN`. (The
  check and the write are serialized per tenant; no unique index can do it, section 3.)
- R14. A record never collides with itself: a replace that keeps a name nobody else has passes. By R3 a
  replace of a record whose name another record also has is refused — also when the name is not being
  changed — until one of the two is renamed. *(default)*
- R15. `partner.taxIdUnique` = `true`: the same as R12–R14 for the `taxId` of partners, with
  `409 TAX_ID_TAKEN` and key `taxId`. Partners without a tax id never collide: any number may have `null`.
- R16. Place in the order of checks, create and replace: … -> `CODE_TAKEN` -> **`NAME_TAKEN`** ->
  **`TAX_ID_TAKEN`** -> the rest as before (frozen fields, references). Each stage answers alone.

### 6.7 Errors name the rule

- R17. Every refusal by R5, R8, R9, R12 or R15 carries `rules` (012 §4): the rule's key, the value in
  force, and as `fields` exactly the `errors` keys that rule produced.

  | Refusal | `rules[].key` | `value` |
  |---|---|---|
  | `VALIDATION_FAILED`, a required field missing | the field's `…Required` rule of the resource | `true` |
  | `VALIDATION_FAILED`, a partner without a role | `partner.roleRequired` | `true` |
  | `CODE_CHANGE_NOT_ALLOWED` | `<m>.codeChangeable` | `false` |
  | `NAME_TAKEN` | `<m>.nameUnique` | `true` |
  | `TAX_ID_TAKEN` | `partner.taxIdUnique` | `true` |

- R18. A `VALIDATION_FAILED` reports all invalid fields together; `rules` has one element per rule that
  contributed, ordered by the first of its fields in `errors`; fields an invariant refused are in no
  element's `fields` (012/R39). A response in which no rule refused has no `rules` member.
- R19. Error codes, statuses and `errors` keys of specs 001–012 are unchanged.

## 7. Edge cases

- E1. `partner.cityRequired` = `true`: `"city": ""` and `"city": "   "` are refused like `null` (they
  normalise to `null`, ADR-0011/7); `"city": "Split"` passes.
- E2. `partner.countryCodeRequired` = `true` and `"countryCode": "Croatia"` -> `400`, key `countryCode`,
  **without** `rules`: the value is given, its form is wrong (an invariant, R6).
- E3. `partner.taxIdRequired` = `true`, `partner.cityRequired` = `true`, and a body without both and with an
  over-long `name` -> `400` with keys `name`, `taxId`, `city`; `rules` has two elements, for `taxId` and
  for `city`; `name` is in neither.
- E4. `article.codeChangeable` = `false`: `PUT` with the stored code `A-1` -> `200`; with `a-1` ->
  `CODE_CHANGE_NOT_ALLOWED`; with the code of another article -> `CODE_CHANGE_NOT_ALLOWED` (not
  `CODE_TAKEN`, R10); for an id that does not exist -> `404`.
- E5. `partner.nameUnique` = `true`: `Acme` exists; create `acme` and create ` ACME ` -> `NAME_TAKEN`; an
  article named `Acme` is created without complaint (another resource).
- E6. Two partners named `Acme` were created while the rule was `false`; it is set to `true`. Both are read
  and listed unchanged. `PUT` of the first with its own body -> `NAME_TAKEN`; with the name `Acme d.o.o.` ->
  `200`; then `PUT` of the second with its own body -> `200`.
- E7. `partner.taxIdUnique` = `true`: three partners without a tax id are created; a second partner with
  the tax id of an inactive partner -> `TAX_ID_TAKEN`.
- E8. `partner.roleRequired` = `false`: a partner with neither role is created; a purchase order naming it
  -> `PARTNER_ROLE_MISSING` (009/R2, unchanged). The rule is set to `true`: the partner reads unchanged;
  `PUT` with its own body -> `400` naming the rule; with `isSupplier: true` -> `200`.
- E9. `warehouse.cityRequired` = `true`: the tenant's `CENTRAL` warehouse has no city and stays the default
  and usable on documents; `PUT` of it without a city -> refused; `set-default` of another warehouse without
  a city -> `200` (R2).
- E10. A code that is both changed (rule `false`) and malformed -> `400` (validation first).

## 8. Tenant isolation

- T1. No new table. The rules' values are tenant-owned as in spec 012; the registry is code.
- T2. A value set by tenant X changes nothing for tenant Y: Y's writes are judged by Y's values.
- T3. Uniqueness (R12, R15) is within the tenant and the resource: a name or tax id used by another tenant
  never collides and is never revealed — `NAME_TAKEN` and `TAX_ID_TAKEN` are answered only for a record of
  the caller's own tenant.
- T4. The same through the tools. No `IgnoreQueryFilters()` is added.

## 9. Security requirements

- S1. Authentication and permissions as before: every tenant key may write masters and change rules
  (012/S1). Nothing here is reachable without a tenant key.
- S2. `NAME_TAKEN` and `TAX_ID_TAKEN` name no other record: `detail` and `errors` repeat only the value the
  caller sent. They do not carry the id or code of the record that has it.
- S3. No input may cause `500` (001/R16) — in particular not a write racing a rule change or another write
  of the same name.

## 10. Acceptance criteria

Conventions of spec 012 §10: "Set(key, v)", "refused by (CODE, key, v)" — the response has that `code`, a
non-empty `errors`, and `rules` with exactly one element whose `key` and `value` are those and whose
`fields` equal the keys of `errors`. Each test uses its own tenant. A "minimal" unit, article, partner or
warehouse is one created with the required properties of specs 001–004 only (a partner: with
`isSupplier: true`). Unmarked criteria are black-box (tester).

Structure
- AC-01 *(manual)* Build and tests exit 0; earlier tests pass unweakened. **No test of specs 001–012 that
  asserts a status, a code, an `errors` key or a stored value changes** — they run in tenants that set no
  rule (AC-20). The **only** earlier tests changed:
  1. tests that pin the number of rules or their literal list (012 AC-20 and its mirrors through `rule_list`
     and in the builder's unit tests): 6 -> 30, with the list of AC-10;
  2. tests that use `partner.roleRequired` as the example of "a key of a later batch is `404` /
     `NOT_FOUND`" (012 E2): it is a rule now. Replace the example with a string that is in no batch
     (`document.maxLines`), not with a key of batch 014;
  3. tests asserting the **exact member set** of the problem document or tool error for a partner with
     neither role (they gain `rules`);
  4. tests pinning the error-code list in the description of a create or update tool of the four masters
     (they gain the codes of section 5);
  5. a test whose expected result of `GET /rules?search=…` or `rule_list` without `group` now also matches
     one of the 24 new keys or names: the expected list grows by exactly those;
  6. the builder's unit tests that pinned the **entity's own** refusal of a partner without a role
     (`Partner.Create` / `Replace` throwing): the entity no longer refuses, `partner.roleRequired` decides in
     validation (013-q B-N1); and the unit test that held rule keys to letters only (a segment may carry a
     digit, 013-q B-N2).
  No migration is required; one is allowed for the indexes of section 3 only.
- AC-02 *(manual)* The 24 decisions are made by booleans obtained through `IRules` and passed to Domain
  functions; `Api` reads no rule; no check of 6.1 remains as a constant (architecture §11).
- AC-03 *(builder, unit)* The required-field, role, code-change and uniqueness decisions as pure functions
  of the values and the rule's boolean.

The registry
- AC-10 In a new tenant `GET /rules?limit=500` -> `total == 30`, keys in ordinal order with these defaults
  (literal — the inventory): `article.codeChangeable` `true`, `article.descriptionRequired` `false`,
  `article.nameUnique` `false`, `partner.addressLine1Required` `false`, `partner.addressLine2Required`
  `false`, `partner.cityRequired` `false`, `partner.codeChangeable` `true`, `partner.countryCodeRequired`
  `false`, `partner.nameUnique` `false`, `partner.postalCodeRequired` `false`, `partner.regionRequired`
  `false`, `partner.roleRequired` `true`, `partner.taxIdRequired` `false`, `partner.taxIdUnique` `false`,
  `purchase.overReceiptAllowed` `false`, `purchase.partnerRequired` `true`, `sales.overDeliveryAllowed`
  `false`, `sales.partnerRequired` `true`, `sales.reservedStockProtected` `false`,
  `stock.negativeStockAllowed` `false`, `uom.codeChangeable` `true`, `uom.nameUnique` `false`,
  `warehouse.addressLine1Required` `false`, `warehouse.addressLine2Required` `false`,
  `warehouse.cityRequired` `false`, `warehouse.codeChangeable` `true`, `warehouse.countryCodeRequired`
  `false`, `warehouse.nameUnique` `false`, `warehouse.postalCodeRequired` `false`,
  `warehouse.regionRequired` `false`. Each has the nine properties of 012 §4, `group` equal to the key's
  first segment, `source == "default"`.
- AC-11 `GET /rules?group=partner` -> the eleven `partner.` rules; `group=uom` -> two; `group=article` ->
  three; `group=warehouse` -> eight.
- AC-12 Every one of the 24 accepts both values and resets (Set `true`, Set `false`, reset -> `200`; `GET`
  shows the value; the history has three changes).
- AC-13 `tools/list` still returns exactly 62 names; `rule_list` with `limit: 500` is JSON-equal to
  `GET /rules?limit=500`.

Defaults reproduce specs 001–012
- AC-20 In a tenant that sets nothing: a replace of each of the four masters that changes the code ->
  `200` (a change of case only included); two units, two articles, two partners and two warehouses with the
  same name are created; two partners with the same tax id are created; an article without a description,
  and a partner and a warehouse with no tax id and no address field, are created; a partner with neither
  role -> `400` with keys `isCustomer` and `isSupplier`.
- AC-21 The same in a tenant in which every one of the 24 rules was Set to its other value and then reset.
- AC-22 **The default refusal names its rule**: the partner without a role of AC-20 is refused by
  (`VALIDATION_FAILED`, `partner.roleRequired`, `true`), `fields` `["isCustomer", "isSupplier"]`.
- AC-23 A refusal no rule caused has no `rules` member: a missing or malformed `code`, a missing `name`, a
  name of 201 characters, `"countryCode": "Croatia"`, `CODE_TAKEN`, a partner body with `isCustomer: "yes"`,
  a replace body without the property `city` — in a default tenant and in one where every `…Required` rule
  is `true`.

Required fields
- AC-30 For each of the fourteen rules, in its own tenant: Set(rule, `true`); create of a minimal record
  without the field, with the field `null`, and with it `"  "` -> each refused by (`VALIDATION_FAILED`,
  rule, `true`) with the field's key, nothing created; create with a valid value -> `201`; a replace of that
  record with the field `null` -> refused likewise, the record unchanged.
- AC-31 Separate (R7): with only `partner.cityRequired` = `true`, a warehouse without a city is created, and
  a partner without a postal code is created.
- AC-32 Several at once (E3): the response has the three keys, and `rules` has exactly the two elements
  `partner.taxIdRequired` (`fields` `["taxId"]`) and `partner.cityRequired` (`fields` `["city"]`).
- AC-33 Existing data (R3): an article without a description exists; Set(`article.descriptionRequired`,
  `true`); `GET` of it is JSON-equal to before; it can be put on a stock document and the document posted;
  `PUT` with its own body -> refused; with a description -> `200`. After reset, `PUT` with
  `"description": null` -> `200`.
- AC-34 E9: with `warehouse.cityRequired` = `true`, a receipt into `CENTRAL` posts; `POST
  /warehouses/{id}/set-default` for another warehouse without a city -> `200`.

`partner.roleRequired`
- AC-40 Set to `false`: create with both roles `false`, and with both omitted -> `201`, both `false`; a
  replace of a supplier with both `false` -> `200`. `isCustomer: null` -> `400` with key `isCustomer`,
  without `rules`.
- AC-41 E8: such a partner on a purchase order and on a sales order -> `PARTNER_ROLE_MISSING`. Back to
  `true`: `GET` unchanged; `PUT` with its own body -> refused by (`VALIDATION_FAILED`,
  `partner.roleRequired`, `true`); with `isCustomer: true` -> `200`.

`<m>.codeChangeable`
- AC-50 For each of the four masters, in its own tenant: Set(`<m>.codeChangeable`, `false`); `PUT` with the
  stored code and another name -> `200`, the name changed; `PUT` with another code -> refused by
  (`CODE_CHANGE_NOT_ALLOWED`, `<m>.codeChangeable`, `false`) with key `code`, status `409`, the record
  unchanged (`updatedAt` included); `PUT` with the code in another letter case -> refused likewise; create
  with any free code -> `201`.
- AC-51 Order (E4, E10): changed to another record's code -> `CODE_CHANGE_NOT_ALLOWED`; changed and
  malformed -> `400` without `rules`; unknown id -> `404`.
- AC-52 Separate: with only `article.codeChangeable` = `false`, a partner's code is changed.
- AC-53 After reset the code is changed (`200`), and `GET …/by-code/` finds the record under the new code.

`<m>.nameUnique`
- AC-60 For each of the four masters, in its own tenant: Set(`<m>.nameUnique`, `true`); a record named
  `Acme` exists; create with `Acme`, with `acme` and with `" ACME "` -> each refused by (`NAME_TAKEN`,
  `<m>.nameUnique`, `true`) with key `name`, status `409`, nothing created; create with `Acme 2` -> `201`;
  a replace of that one with the name `Acme` -> refused, unchanged; a replace of `Acme` itself with its own
  body, and with the name `ACME` -> `200`.
- AC-61 An inactive record's name counts; a record of another resource with the same name does not (E5).
- AC-62 Existing duplicates (E6), as written there, for partners.
- AC-63 Order (R16): a create whose code and name are both taken -> `CODE_TAKEN` (no `rules`); for a
  partner whose name and tax id are both taken, with both rules `true` -> `NAME_TAKEN`.
- AC-64 Parallel (R13): rule `true`; 10 parallel creates of partners with different codes and the same
  name -> exactly one `201`, nine `NAME_TAKEN`, none `500`; the list has one partner of that name.

`partner.taxIdUnique`
- AC-70 Set to `true`; a partner with tax id `HR123` exists. Create with `HR123` and with `hr123` -> refused
  by (`TAX_ID_TAKEN`, `partner.taxIdUnique`, `true`) with key `taxId`; with `HR124` -> `201`; three
  partners with `taxId` `null` -> `201` each (E7). A replace of the first with its own body -> `200`.
- AC-71 The response of a `NAME_TAKEN` and of a `TAX_ID_TAKEN` contains neither the id nor the code of the
  record that has the value (S2).

Existing data and timing
- AC-80 For one Set of a rule of each kind (a required field, `partner.roleRequired`, a `codeChangeable`,
  a `nameUnique`, `partner.taxIdUnique`): the list of the resource read before the Set is JSON-equal to
  the same read after it.
- AC-81 Read-your-writes: 10 times in a row — Set(`article.descriptionRequired`, `true`), create an article
  without a description (expect refused), Set to `false`, create it (expect `201`). No iteration differs.

Tenant isolation
- AC-90 Tenant X sets all 24 rules to the value that is not the default. In tenant Y everything of AC-20
  still holds, and `GET /rules` shows 30 defaults.
- AC-91 X has `partner.nameUnique` and `partner.taxIdUnique` = `true` and a partner `Acme` with tax id
  `HR123`. Y, with the same two rules `true`, creates a partner `Acme` with `HR123` -> `201`.

MCP
- AC-100 Through tools only: `rule_set` `{ key: "partner.cityRequired", value: true }`; `partner_create`
  without `city` -> tool error `VALIDATION_FAILED` with key `city` and `rules` JSON-equal to the HTTP
  response's `rules` for the same request; with a city -> the partner.
- AC-101 Tool errors `CODE_CHANGE_NOT_ALLOWED`, `NAME_TAKEN` and `TAX_ID_TAKEN` (one case each) carry the
  same `code`, `errors` keys and `rules` as HTTP.
- AC-102 The input schemas of the eight create and update tools are identical before and after every rule
  of this spec is set to its other value.

## 11. Notes for the tester and the builder

Tester
- The weight is on three things: the defaults change nothing (AC-20–AC-23); each rule at its other value
  refuses exactly what its name says and names itself; a change touches nothing stored (AC-33, AC-62,
  AC-80).
- One tenant per test, always: a rule set in a shared tenant changes other tests' results.
- The fourteen required-field rules and the four masters are the same test with different data: write
  them as theories over (resource, field, rule key), not fourteen copies.
- Do not pin `name` or `description` texts of rules. Do pin keys and defaults (AC-10).
- The approved changes to earlier tests are exactly those of AC-01. If an earlier test fails for another
  reason, write it in `docs/questions/013-q.md` before changing it.
- **Reversing a linked document in a test.** This spec needs no reversal, but if a test of yours reverses
  a receipt or delivery created by the order helper (`OrderApi.Document`): that document is dated
  `Stock.NextDay`, one day later than the default date of `Stock.ReverseAsync`, and a reversal dated before
  its original is refused (006/R12). Pass `Stock.NextDay` (or later) as the reversal date. This mistake
  reached review in 009, 010 and 012.

Builder
- A new rule is one `RuleDefinition` in `RuleRegistry` plus the check that receives its boolean — as the
  six of spec 012. Nothing in `Rules/`, `IRules`, the rule operations, the tables or the error mapping
  changes.
- The validations of the masters (`…Validation.Create` / `.Replace`) take the booleans they need as
  arguments; the operation reads `IRules` once and passes them. Keep the fourteen required-field rules a
  table (field -> rule), not fourteen branches.
- `VALIDATION_FAILED` with several rules: `AppError.RefusedBy` once per rule, each with its own fields
  (R18).
- Uniqueness (R13) cannot rest on a unique index. The simplest correct form: create and replace of the four
  masters run inside `SerializedPerTenantAsync` and read the rules there (articles' and warehouses' replace
  already do). If you take the lock only when a uniqueness rule is `true`, say so in the code and keep
  AC-64 green.
- The code comparison of R9 is ordinal on the normalised code; the name and tax-id comparisons of R12 and
  R15 use the same lower-casing as the code lookup.
- Anything unclear or contradictory: `docs/questions/013-q.md`, then continue with the rest.

## 12. Decisions behind this spec

Owner's decisions (2026-10-10; ADR-0020): a rule is a boolean; only business logic is configurable; order
of work 012, 013, 014. **This spec needs no new decision of the owner.**

Architect's defaults — each stands until the owner says otherwise.

| # | Question | Default | Rule |
|---|---|---|---|
| 1 | A rule is tightened and old records do not satisfy it | They stay valid and usable; the next replace of such a record must satisfy the rule, also in fields it does not change | R3 |
| 2 | Names must be unique, and two old records share a name | Both stay; neither can be replaced until one is renamed | R14 |
| 3 | How names and tax ids are compared | Like codes: trimmed, case-insensitive; inactive records count | R12, R15 |
| 4 | Is `abc` -> `ABC` a change of code? | Yes | R9 |
| 5 | "Address line 2 required" and the other five address fields | One switch per field and per resource (twelve), as catalogued; none is grouped | 6.1 |
| 6 | A tenant's first warehouse and a required address field | `CENTRAL` is created before any rule can be set and stays valid; its next replace is judged | R2, E9 |
