# Spec P03 — Modifiers (options and add-ons) and recipes (normatives)

Status: ready for the tester and the builder.
Owner requirements of 2026-10-13 (`docs/pos/requirements.md` §1): "articles with options and add-ons (e.g.
extra milk)"; "normatives and ingredients for articles that are prepared"; §7: "stock is reduced by
ingredients according to the normative".
Branches: `tests/P03-modifiers-recipes` (tester), `feat/P03-modifiers-recipes` (builder).
Read first: ADR-0022/3 (what a paid bill issues), `docs/pos/overview.md` §3–§5, spec **002** (article `type`,
base unit), **005** (only `stock` articles move stock), **007** (a sub-resource of an article; frozen base
unit), **P01** (masters, `sales-data` as a sub-resource, `salesPrice` includes tax), **P02** R32 (the `pos`
key allow-list), architecture §5, §6, §11.

**Inherited, not re-specified**: everything specs 001–P02 established. This spec states only what is new.

**Nothing in this spec sells or moves stock.** It stores what a sale will need: which choices an article
offers, what each choice costs, and what an article and a choice consume. P05 puts options on a bill line
(copying name and price); P07 turns recipes into the stock issue as ADR-0022/3 says.

**Seam.** Part A (modifier groups, options, assignment to articles) and part B (recipes of articles and of
options) meet only in the option recipe (R24). If one loop turn is too short, B follows as P03a with its
criteria as numbered; the option recipe goes with B.

*(default)* marks an architect's decision the owner may change; all are collected in section 12.

## 1. Goal

A café says once: a latte offers "Milk" (required, one of: regular, oat +0.30, lactose-free +0.20) and
"Extras" (optional, several: extra shot +0.50, syrup +0.40); a latte consumes 0.018 kg coffee and 0.2 l milk;
an extra shot consumes another 0.009 kg coffee; oat milk consumes 0.2 l oat drink. From P07 on, selling a
latte with oat milk and an extra shot issues 0.027 kg coffee, 0.2 l milk and 0.2 l oat drink.

## 2. Scope

In scope
- A. **Modifier groups** — a master (the operations of a unit of measure) with two flags.
- A. **Modifier options** — records of a group: list, get, create, replace, delete.
- A. **Modifier groups of an article** — get, set (an ordered list).
- B. **Recipe of an article** and **recipe of an option** — get, set (whole ingredient list).
- B. `PUT /articles/{id}` and `DELETE /articles/{id}` gain `IN_USE` cases (R31, R32).
- Five rules (6.1); one new error code, `RECIPE_NOT_ALLOWED`.
- 16 MCP tools; `tools/list` returns 116 for a `full` key. The `pos` key allow-list gains the reads (R40).

Out of scope
- Selling, prices on a bill, stock issue (P05, P07). Tax of an option: it is the tax of the article it is
  added to (P05 copies it).
- Recipes in another unit than the ingredient's base unit; yield and waste; versions of a recipe; recipes of
  stock articles (semi-products, production) *(default)*.
- Options that remove an ingredient ("without milk" is an option with no ingredients and a note on the line)
  *(default)*.
- Nested groups, an option opening another group, a group per POS station, a different price of one option
  per article.
- "Where used" reports (which recipes name milk); a later read if asked for.
- Seeding: none.

## 3. Data

New tenant-owned tables, audit columns and composite foreign keys as architecture §8 / ADR-0002. Storage of
recipes is the builder's choice; the suggested shape:

| Table | Columns besides `Id`, `TenantId`, audit | Keys |
|---|---|---|
| `ModifierGroups` | `Code`, `Name`, `IsRequired`, `AllowsMultiple`, `IsActive` | unique `(TenantId, lower(Code))` |
| `ModifierOptions` | `GroupId`, `Code`, `Name`, `PriceDelta` numeric, `SortOrder` int, `IsActive` | unique `(TenantId, GroupId, lower(Code))`; FK to `ModifierGroups` (cascade) |
| `ArticleModifierGroups` | `ArticleId`, `GroupId`, `Position` int; no audit columns | unique `(TenantId, ArticleId, GroupId)`; FK to `Articles` (cascade), `ModifierGroups` (restrict) |
| `Recipes` | `ArticleId` null, `OptionId` null (exactly one set), `UpdatedAt`, `UpdatedBy` | unique per owner; FK to `Articles` / `ModifierOptions` (cascade) |
| `RecipeIngredients` | `RecipeId`, `IngredientId` (an article), `Quantity` numeric(18,6) | unique `(TenantId, RecipeId, IngredientId)`; FK to `Recipes` (cascade), `Articles` (restrict) |

The "last set" of an article's group list (`updatedAt`, `updatedBy`) is stored where the builder likes. One
new migration; no existing table changes.

## 4. Operations — HTTP

All under `/api/v1`, tenant key.

### 4.A Modifier groups — `/modifier-groups`

The six operations of `/units-of-measure` (001 §4.3) with the refusals of 013. List: `search`, `isActive`.
Delete: `409 IN_USE` while any article names the group; otherwise the group is deleted **with its options**
and their recipes.

```json
{ "id": "uuid", "code": "MILK", "name": "Milk", "isRequired": true, "allowsMultiple": false,
  "isActive": true, "createdAt": "…Z", "updatedAt": "…Z", "createdBy": "uuid", "updatedBy": "uuid" }
```

Create `{ code, name, isRequired?, allowsMultiple?, isActive? }` (flags default `false`, `isActive` `true`);
replace carries every field. `isRequired`: at least one option must be chosen on a bill line;
`allowsMultiple`: more than one may be chosen. Both are **data of the group**, used by P05.

### 4.A Modifier options — `/modifier-options`

```json
{ "id": "uuid", "group": { "id": "uuid", "code": "MILK", "name": "Milk" },
  "code": "OAT", "name": "Oat milk", "priceDelta": 0.3, "sortOrder": 20, "isActive": true,
  "createdAt": "…Z", "updatedAt": "…Z", "createdBy": "uuid", "updatedBy": "uuid" }
```

| Operation | Request | Success | Errors |
|---|---|---|---|
| List | `GET /modifier-options?groupId=&search=&isActive=&limit=&offset=` | `200` list envelope, ordered by group `code`, `sortOrder`, `code` | 400 |
| Get | `GET /modifier-options/{id}` | `200` | 404 |
| Create | `POST /modifier-options` body `{ groupId, code, name, priceDelta?, sortOrder?, isActive? }` | `201` + `Location` | 400, 409 `REFERENCE_NOT_FOUND`, 409 `REFERENCE_INACTIVE`, 409 `CODE_TAKEN`, 409 `NAME_TAKEN` |
| Replace | `PUT /modifier-options/{id}` body `{ code, name, priceDelta, sortOrder, isActive }` | `200` | 400, 404, 409 `CODE_CHANGE_NOT_ALLOWED`, 409 `CODE_TAKEN`, 409 `NAME_TAKEN` |
| Delete | `DELETE /modifier-options/{id}` | `204` (with its recipe) | 404 |

An option never moves to another group (`groupId` in a replace body is an unknown property). `priceDelta`
defaults to `0`, `sortOrder` to `0`. `priceDelta` is **tax-inclusive**, like `salesPrice` (P01).

### 4.A Modifier groups of an article

```json
{ "article": { "id": "uuid", "code": "LATTE", "name": "Latte" },
  "groups": [ { "id": "uuid", "code": "MILK", "name": "Milk", "isRequired": true, "allowsMultiple": false } ],
  "updatedAt": "…Z", "updatedBy": "uuid" }
```

| Operation | Request | Success | Errors |
|---|---|---|---|
| Get | `GET /articles/{articleId}/modifier-groups` | `200` (empty `groups`, `null` audit when never set) | 404 |
| Set | `PUT /articles/{articleId}/modifier-groups` body `{ "groupIds": [uuid] }` | `200` | 400, 404, 409 `REFERENCE_NOT_FOUND`, 409 `REFERENCE_INACTIVE` |

`groups` are in the order of `groupIds` (the order in which the till asks).

### 4.B Recipes

```json
{ "article": { "id": "uuid", "code": "LATTE", "name": "Latte" },
  "ingredients": [
    { "article": { "id": "uuid", "code": "COFFEE", "name": "Coffee beans" },
      "unit": { "id": "uuid", "code": "kg", "name": "Kilogram" }, "quantity": 0.018 } ],
  "updatedAt": "…Z", "updatedBy": "uuid" }
```

For an option the first member is `"option": { "id", "code", "name", "group": { "id", "code", "name" } }`
instead of `"article"`. `unit` is the ingredient's **base unit**, shown for reading; `quantity` is in it,
per **one base unit** of the sold article (per one choice of the option). `ingredients` are ordered by
ingredient `code`; `[]` and `null` audit when never set.

| Operation | Request | Success | Errors |
|---|---|---|---|
| Get | `GET /articles/{articleId}/recipe`, `GET /modifier-options/{optionId}/recipe` | `200` | 404 |
| Set | `PUT /articles/{articleId}/recipe`, `PUT /modifier-options/{optionId}/recipe` body `{ "ingredients": [ { "articleId", "quantity" } ] }` | `200` | 400, 404, 409 `RECIPE_NOT_ALLOWED`, 409 `REFERENCE_NOT_FOUND`, 409 `REFERENCE_INACTIVE`, 409 `ARTICLE_NOT_STOCKED` |

Set with `[]` removes the recipe.

### 4.C Error codes (registry: architecture §6)

| HTTP | `code` | Meaning | `errors` keys |
|---|---|---|---|
| 409 | `RECIPE_NOT_ALLOWED` | A non-empty recipe was set on a `stock` article (R30). | `ingredients` |

`ARTICLE_NOT_STOCKED` *(extended)*: an ingredient is a `service` article, key `ingredients[i].articleId`.
`IN_USE` *(extended)*: R31, R32, and delete of a modifier group named by an article.

## 5. Operations — MCP

One tool per operation, same fields, limits and errors (ADR-0009), descriptions naming the error codes and,
where rules judge, `rule_list` with the group; covered by the parity test. `tools/list` for a `full` key
returns the 100 of P02 and these 16:

| Tools | Input | Output |
|---|---|---|
| `modifier_group_list`, `modifier_group_get` (`{ id?, code? }`), `modifier_group_create`, `modifier_group_update`, `modifier_group_delete` | as the master tools of P01 | as P01 |
| `modifier_option_list` | `{ groupId?, search?, isActive?, limit?, offset? }` | list envelope |
| `modifier_option_get`, `modifier_option_delete` | `{ id }` | the record / `{ deleted: true }` |
| `modifier_option_create` | the create body | the record |
| `modifier_option_update` | `{ id }` + the replace body | the record |
| `article_modifier_groups_get` | `{ articleId }` | as 4.A |
| `article_modifier_groups_set` | `{ articleId, groupIds }` | as 4.A |
| `article_recipe_get`, `modifier_option_recipe_get` | `{ articleId }` / `{ optionId }` | Recipe |
| `article_recipe_set`, `modifier_option_recipe_set` | `{ articleId \| optionId, ingredients }` | Recipe |

The descriptions of the recipe tools say that `quantity` is in the ingredient's base unit per one base unit
of the article (per one choice of the option); those of the option tools say `priceDelta` includes tax.

## 6. Rules

### 6.1 Rules this spec introduces

| Key | Name | Default | Switching it to the other value |
|---|---|---|---|
| `modifierGroup.codeChangeable` | The code of a modifier group may be changed | `true` | `false`: `CODE_CHANGE_NOT_ALLOWED` (013). |
| `modifierGroup.nameUnique` | Names of modifier groups must be unique | `false` | `true`: `NAME_TAKEN` (013). |
| `modifierOption.codeChangeable` | The code of a modifier option may be changed | `true` | `false`: `CODE_CHANGE_NOT_ALLOWED`. |
| `modifierOption.nameUnique` | Names of options must be unique within their group | `false` | `true`: a create or replace whose name another option **of the same group** has -> `NAME_TAKEN`. |
| `modifierOption.priceReductionAllowed` | An option may lower the price | `true` | `false`: a `priceDelta` below zero is refused — `400 VALIDATION_FAILED`, key `priceDelta`, with `rules`. |

Groups `modifierGroup`, `modifierOption`. The registry then has **53** rules. No stored record is re-checked
when a rule changes (012/R15).

### 6.2 Every other check is an invariant (`docs/rules.md` §2)

| Check | Invariant | Reason |
|---|---|---|
| Code and name of groups and options: form, length; group code unique in the tenant, option code unique in its group | I7, I9, I10 | As every master; an option is found within its group. |
| `isRequired`, `allowsMultiple`: JSON booleans | I9 | Contract shape; they are data of a group, not rules. |
| `priceDelta`: a JSON number, `-999999999.999999 ≤ x ≤ 999999999.999999`, at most 6 decimals; tax-inclusive | I8, I10 | Bounds of a unit price; what the number means (as `salesPrice`, P01). |
| `sortOrder`: an integer `0 ≤ x ≤ 9999` | I9, I10 | A position, bounded. |
| `groupIds`: at most 20 distinct UUIDs | I8, I10 | A group is asked once; twenty is a bound of a screen and a request. |
| A recipe belongs to a `service` article or to an option; its ingredients are `stock` articles | I8 | Definition (ADR-0022/3): an article with a recipe is made at the sale and holds no stock; only `stock` articles move stock (005). It also makes recipes one level deep — no cycles. |
| `ingredients`: at most 200, one per ingredient article; `quantity` a JSON number `> 0`, at most 6 decimals, at most 999999999.999999, in the ingredient's base unit | I8, I10 | An ingredient is consumed once per recipe; bounds of a quantity and of a document (I10). |
| An ingredient's base unit and `type` do not change while a recipe names it; a recipe's article does not become `stock` while its recipe is not empty | I6 | The stored quantities would change meaning. |
| References exist in the tenant; inactive is not newly referenced; a group named by an article or an article named as an ingredient is not deleted | I1, I6 | ADR-0008. |

**This spec adds no check that is not in one of the two tables.**

### 6.3 Modifier groups and options (A)

- R1. Groups behave as a unit of measure (001) and the P01 masters, with the `modifierGroup.*` rules of 6.1 as
  013/R9–R16 under the tenant lock.
- R2. Options: code and name rules of a master; `code` compared case-insensitively **within the group**
  (`CODE_TAKEN` only against options of the same group; two groups may both have `REG`). The
  `modifierOption.*` rules behave as 013/R9–R16 with "the same resource" read as "the same group".
- R3. Option create, order of checks: validation (with `modifierOption.priceReductionAllowed`, reported with
  every other invalid field) -> `groupId` (`REFERENCE_NOT_FOUND`, `REFERENCE_INACTIVE`) -> code -> name.
  A new option in an inactive group -> `REFERENCE_INACTIVE`, key `groupId`; existing options of a group
  deactivated later stay and may be replaced.
- R4. Replace changes `code`, `name`, `priceDelta`, `sortOrder`, `isActive`. Nothing copies an option yet;
  from P05 on a bill line copies name and price at the time it is added.
- R5. List by `groupId`: a well-formed id naming nothing -> `200` empty; malformed -> `400`.
- R6. Group delete: `IN_USE` while any article (also inactive) names it; otherwise options and their recipes
  go with it. Option delete is always allowed in this spec (P05 adds `IN_USE` for options on bill lines).
- R7. A group with no active option, or `isRequired` with no option at all, is accepted here; what a till does
  with it is P05's.

### 6.4 Modifier groups of an article (A)

- R10. Every article (`stock` or `service`, active or not) has a list, empty until set. Set replaces it; the
  body has `groupIds` (an array, possibly empty). More than 20 -> `400`, key `groupIds`; a malformed or
  repeated element -> `400`, key `groupIds[i]`.
- R11. Order of checks: validation -> article (`404`) -> each `groupIds[i]` in order: `REFERENCE_NOT_FOUND`,
  or `REFERENCE_INACTIVE` for a group the article does not already have; the first failing answers alone.
- R12. Attributed (`updatedAt`, `updatedBy`); the article's own `updatedAt` does not change. Renaming a group
  shows at the next read (ADR-0008).

### 6.5 Recipes (B)

- R20. Set replaces the whole list. The body has `ingredients` (an array, possibly empty). Shape: more than
  200 -> `400`, key `ingredients`; per element `articleId` a UUID (key `ingredients[i].articleId`), `quantity`
  by 6.2 (key `ingredients[i].quantity`); an `articleId` repeating an earlier one -> key
  `ingredients[i].articleId`. All shape errors are reported together, no `rules`.
- R21. Order of checks: validation -> the owner exists (`404` for the article or option in the path) ->
  R30 -> each ingredient in order: `REFERENCE_NOT_FOUND`; `REFERENCE_INACTIVE` for an ingredient the recipe
  does not already have; `ARTICLE_NOT_STOCKED` for a `service` article (key `ingredients[i].articleId`); the
  first failing ingredient answers alone.
- R22. `0.018`, `0.0180` are one quantity; it is read back as a JSON number. The unit is not sent: the
  ingredient's base unit is the unit. A `unitId` member -> `400` (unknown property).
- R23. Set on an inactive article or option is allowed (its own data). Attributed (`updatedAt`, `updatedBy`);
  the article's or option's own `updatedAt` does not change.
- R24. An option's recipe is consumed **in addition** to the article's (or to the article itself, for a
  `stock` article), per choice of the option. An option recipe may name any `stock` article, whatever the
  articles the group is assigned to.
- R25. What a sale will issue (P07, stated here so the data is complete): for a sold `service` article with
  a recipe — its ingredients × sold quantity; for a `stock` article — itself; for each chosen option — its
  ingredients × sold quantity; summed per ingredient article; a `service` article without a recipe and an
  option without one issue nothing.

### 6.6 Changes to articles (B)

- R30. A non-empty recipe on a `stock` article -> `409 RECIPE_NOT_ALLOWED`, key `ingredients`. `[]` on a
  `stock` article -> `200` (nothing to store).
- R31. `PUT /articles/{id}` -> `409 IN_USE`: changing `type` to `stock` while its recipe is not empty (key
  `type`); changing `type` to `service` or changing `baseUnitId` while any recipe (of an article or an option)
  names it as an ingredient (key `type` / `baseUnitId`). Both may be reported together. Order: where 002/007
  already put `IN_USE`.
- R32. `DELETE /articles/{id}` -> `409 IN_USE` while a recipe names it as an ingredient. Otherwise its own
  recipe and its group list go with it (as its sales data, P01/R14).

### 6.7 POS keys

- R40. The P02/R32 allow-list gains `GET` of `/modifier-groups` (and `{id}`, `by-code`), `/modifier-options`
  (and `{id}`), `/articles/{id}/modifier-groups`, `/articles/{id}/recipe`, `/modifier-options/{id}/recipe`,
  and the matching `*_list` / `*_get` tools. Writes stay `full`-key only.

## 7. Edge cases

- E1. `priceDelta` `-0.2` with the default -> `201`; with `modifierOption.priceReductionAllowed` = `false` ->
  refused by (`VALIDATION_FAILED`, that rule, `false`), key `priceDelta`. `0.1234567` and `"0.3"` -> `400`
  without `rules`.
- E2. Two groups `MILK` and `SIZE` each with option `REG` -> both `201`; a second `reg` in `MILK` ->
  `CODE_TAKEN`.
- E3. `groupIds: [a, a]` -> `400`, key `groupIds[1]`.
- E4. A latte (service) recipe naming a "Water" service article -> `ARTICLE_NOT_STOCKED`, key
  `ingredients[0].articleId`. A recipe on "Bottled beer" (stock) -> `RECIPE_NOT_ALLOWED`.
- E5. Quantity `0`, `-1`, `0.0000001`, `"0.2"` -> `400`, key `ingredients[i].quantity`.
- E6. Milk is deactivated; the latte's recipe set again with milk -> `200`; a new recipe naming milk ->
  `REFERENCE_INACTIVE`.
- E7. Milk (in a recipe) gets another base unit -> `IN_USE`, key `baseUnitId`; deleted -> `IN_USE`; after the
  recipe is set to `[]` -> both allowed (as far as other uses allow).
- E8. A group assigned to the latte is deleted -> `IN_USE`; after `groupIds: []` -> `204`, its options gone.
- E9. A `pos` key: `GET /articles/{id}/recipe` -> `200`; `PUT` of it -> `403 FORBIDDEN`.

## 8. Tenant isolation

- T1. All tables tenant-owned and filtered; groups, options, ingredient articles of another tenant are
  `REFERENCE_NOT_FOUND` in a body and `404` in a path.
- T2. Codes and names unique per tenant (options: per group); no refusal names another tenant's record.
- T3. Rules set in X change nothing in Y. T4. The same through the tools. No `IgnoreQueryFilters()`.

## 9. Security requirements

- S1. Authentication and the `pos`-key limit as P02 (R40).
- S2. No input causes `500` — in particular not an article delete or type change racing a recipe Set that
  names it (the foreign key and the tenant lock decide: one of them answers `IN_USE` or
  `REFERENCE_NOT_FOUND`).
- S3. Every write is attributed (I3).

## 10. Acceptance criteria

Conventions of 012 §10, 013 §10, P01 §10. One tenant per test.

Structure
- AC-01 *(manual)* Build and tests exit 0; earlier tests pass unweakened. The **only** earlier tests changed:
  (1) the number or literal list of rules: 48 -> 53; (2) the number or literal list of tools: 100 -> 116;
  (3) the `pos`-key allow-list or its `tools/list` (P02 AC-51, AC-52) gains exactly the reads of R40;
  (4) a rule search whose expected result now also matches a new key or name.
- AC-02 *(manual)* The five decisions are booleans from `IRules` passed to Domain functions; groups and options
  reuse the master rule set of 013 (options with the group as scope); one recipe Set operation serves articles
  and options.
- AC-03 *(builder, unit)* `priceDelta`, `quantity`, `groupIds` and `ingredients` shape decisions; R30 and the
  ingredient-type decision; the issue computation of R25 as a pure function (ready for P07): a latte with oat
  milk and an extra shot gives the quantities of section 1.

Registry and tools
- AC-10 New tenant `GET /rules?limit=500` -> `total == 53`: the 48 of P02 AC-10 and
  `modifierGroup.codeChangeable` `true`, `modifierGroup.nameUnique` `false`, `modifierOption.codeChangeable`
  `true`, `modifierOption.nameUnique` `false`, `modifierOption.priceReductionAllowed` `true`.
- AC-11 Each of the five accepts both values and resets.
- AC-12 `tools/list` (full key) returns exactly 116 names. For a group, an option, an article's groups and a
  recipe, the tool result is JSON-equal to HTTP.

A — groups and options
- AC-20 Group: the master criteria of P01 AC-20..AC-29 (create, get by code in another case, list, replace,
  delete, validation, `CODE_TAKEN`, both `modifierGroup.*` rules at both values); flags default `false` and
  read back; a flag `"true"` -> `400`; replace without `allowsMultiple` -> `400`.
- AC-21 Option: create -> `201`, `Location`, `group` summary, `priceDelta` `0` and `sortOrder` `0` when
  omitted; get; replace; delete -> `204` then `404`; `groupId` in a replace -> `400`.
- AC-22 Option codes and names (R2, E2): `CODE_TAKEN` only within the group; with
  `modifierOption.codeChangeable` = `false` a code change is refused by (`CODE_CHANGE_NOT_ALLOWED`, rule,
  `false`); with `modifierOption.nameUnique` = `true` a same-named option in the same group is refused by
  (`NAME_TAKEN`, rule, `true`) and one in another group is `201`. 10 parallel creates in one group with one
  name -> exactly one `201`, none `500`.
- AC-23 `priceDelta` (E1) at both values of the rule; `sortOrder` `-1`, `10000`, `1.5` -> `400`.
- AC-24 Option references (R3): unknown group -> `REFERENCE_NOT_FOUND`, inactive -> `REFERENCE_INACTIVE`, key
  `groupId`; an existing option of a group deactivated afterwards can still be replaced.
- AC-25 List (R5): by `groupId` in order `sortOrder`, `code`; `search`; `isActive`; unknown well-formed
  `groupId` -> empty; malformed -> `400`.
- AC-26 Article groups (R10–R12): get of a new article -> empty; set three groups -> `200` in the given order;
  set `[]` -> empty; E3; 21 groups -> `400`, key `groupIds`; unknown -> `REFERENCE_NOT_FOUND`, key
  `groupIds[i]`; an inactive group kept -> `200`, newly added -> `REFERENCE_INACTIVE`; unknown article ->
  `404`; a renamed group shows renamed.
- AC-27 Delete group (R6, E8).

B — recipes
- AC-40 Article recipe: get of a new service article -> `ingredients` `[]`, `null` audit; set two
  ingredients -> `200`, ordered by code, each with its base unit and quantity, audit set; set `[]` -> empty.
- AC-41 Option recipe: the same through `/modifier-options/{id}/recipe`, with the `option` member and its
  group summary.
- AC-42 Shape (R20, E5): 201 ingredients -> key `ingredients`; repeated article -> key
  `ingredients[1].articleId`; E5 keys; a `unitId` member -> `400`. None has `rules`.
- AC-43 Types (R21, R30, E4): `ARTICLE_NOT_STOCKED` with its index; `RECIPE_NOT_ALLOWED` on a stock article;
  `[]` on a stock article -> `200`; an option recipe naming any stock article -> `200`.
- AC-44 References (R21, E6): unknown ingredient -> `REFERENCE_NOT_FOUND` with its index; first failing
  answers alone; E6 as written; unknown article or option in the path -> `404`.
- AC-45 Articles (R31, R32, E7): each `IN_USE` case with its key, for an ingredient named by an article recipe
  and, separately, by an option recipe; the latte (recipe not empty) changed to `stock` -> `IN_USE`, key
  `type`; deleting the latte -> `204` and its recipe and group list are gone (an ingredient previously
  `IN_USE` because of it can then be deleted).
- AC-46 Deleting an option or a group deletes its recipe: the ingredient is no longer `IN_USE` because of it.

POS key, isolation, MCP
- AC-60 R40 and E9: a `pos` key reads every new `GET` and the matching tools; every write -> `403 FORBIDDEN`
  / tool error `FORBIDDEN`.
- AC-61 Y cannot read, change, delete or reference X's groups, options, recipes or article groups (`404` /
  `REFERENCE_NOT_FOUND`); Y creates the same codes -> `201`; X's ingredient is not `IN_USE` because of Y.
- AC-62 X sets all five rules to the other value; in Y the defaults of AC-22 and AC-23 hold.
- AC-63 Through tools only: a group, two options, the article's groups, an article recipe and an option
  recipe -> the same data as HTTP; `RECIPE_NOT_ALLOWED`, `ARTICLE_NOT_STOCKED` and the
  `priceReductionAllowed` refusal carry the same `code`, `errors` keys and `rules` as HTTP.

## 11. Notes for the tester and the builder

Tester
- Do not pin `name` or `description` texts of rules or tools beyond what AC-12 needs; pin keys and defaults.
- One tenant per test. Approved changes to earlier tests are exactly those of AC-01; anything else ->
  `docs/questions/P03-q.md`.

Builder
- Groups are a master as in P01; options are a master scoped to a group — reuse `MasterWriteChecks` with the
  group as scope rather than copying it.
- One recipe operation with two owners (article, option); one Set replaces a small aggregate inside
  `SerializedPerTenantAsync`, as P01 sales data.
- The `IN_USE` checks of R31, R32 go where 007 put its own on `PUT` / `DELETE /articles/{id}`.
- Put the R25 computation in Domain now (AC-03); P07 calls it.
- Route order: `/articles/{id}/recipe` and `/articles/{id}/modifier-groups` as `/articles/{id}/sales-data`.
- Anything unclear: `docs/questions/P03-q.md`, then continue.

## 12. Decisions behind this spec

Open owner decisions of the overview §9 that touch it: none directly (10: no happy hour or vouchers — an
option is not a discount mechanism). Defaults of P01 (prices include tax) apply to `priceDelta`.

Architect's defaults, each until the owner says otherwise:

| # | Question | Default | Where |
|---|---|---|---|
| 1 | How is "required / one or several" expressed? | Two flags on a group: `isRequired`, `allowsMultiple`; no min/max counts | 4.A |
| 2 | May an option lower the price? | Yes (`modifierOption.priceReductionAllowed`) | 6.1 |
| 3 | Does an option have its own tax? | No; the tax of the article it is added to | §2 |
| 4 | Recipe units | The ingredient's base unit only, per one base unit of the sold article | 4.B, R22 |
| 5 | Recipes on stock articles (semi-products) | No: a recipe belongs to a `service` article or an option; ingredients are `stock` articles | 6.2, R30 |
| 6 | "Without milk" | An option with no recipe (and a line note); options never subtract an ingredient | §2 |
| 7 | One price of an option for every article | Yes; a per-article price is a later change | §2 |
