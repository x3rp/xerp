# Spec P04 — POS stations and menus: warehouse, button layout, category pages, one menu read, lookup

Status: ready for the tester and the builder.
Owner requirements of 2026-10-13 (`docs/pos/requirements.md` §1): "choose the articles shown on the POS",
"quick view of articles by category and an adjustable button layout", "search articles by name and barcode".
Branches: `tests/P04-stations-menus` (tester), `feat/P04-stations-menus` (builder).
Read first: `docs/pos/overview.md` §4, §7 ("one call for the menu"), ADR-0022/1, spec **011** (default
warehouse), **P01** (categories, sales data, barcodes), **P02** R32 (`pos` key allow-list), **P03** (modifier
groups and options of an article), architecture §5, §6, §11.

**Inherited, not re-specified**: everything specs 001–P03 established. This spec states only what is new.

**Nothing in this spec sells.** It says where a till stands (its warehouse, its fiscal labels) and what it
shows, and gives `xerp-pos` everything it needs to draw the selling screen in one cacheable read. P05 opens
bills on a station.

**Seam.** Part A (stations, menus and their layout) and part B (the menu read and the lookup, 4.C) — B reads
what A stores. If one loop turn is too short, B follows as P04a with its criteria as numbered.

*(default)* marks an architect's decision the owner may change; all are collected in section 12.

## 1. Goal

A bar has two tablets and one register; all three sell from the warehouse `BAR` and show the menu `BAR`:
a "Favourites" page with twelve buttons placed by hand, and the pages "Coffee", "Beer", "Spirits" that show
whatever articles are in those categories. The frontend asks once for the station's menu and gets pages,
buttons, articles with price, taxes, unit, category and their modifier groups and options, and a version it
can cache; it asks again only when the version changes.

## 2. Scope

In scope
- A. **POS stations** `/pos/stations`: a master with a warehouse, a menu, a premises code and a device code.
- A. **POS menus** `/pos/menus`: a master; its **layout** (pages and buttons) is read and replaced whole.
- B. **The station menu**: `GET /pos/stations/{id}/menu` — one read, with a version (`ETag`).
- B. **Lookup** `GET /pos/stations/{id}/articles?search=&barcode=` — by name, code or barcode.
- Eight rules (6.1). No new error code.
- 14 MCP tools; `tools/list` returns 130 for a `full` key.

Out of scope
- Bills, shifts, tables, preparation (P05+). Prices per station or menu (one price per article, P01).
- Images, fonts and sizes of buttons beyond `label` and `color`; drawing is the frontend's.
- Server push of menu changes (polling the version is enough, overview §7).
- Number series and the legal form of a receipt number (P07, P11); the two station codes are stored here
  only *(owner decision 8 of the overview, unverified legal detail)*.
- Seeding: none.

## 3. Data

New tenant-owned tables, audit columns and composite foreign keys as architecture §8 / ADR-0002.

| Table | Columns besides `Id`, `TenantId`, audit | Keys |
|---|---|---|
| `PosStations` | `Code`, `Name`, `WarehouseId`, `MenuId` null, `PremisesCode`, `DeviceCode`, `IsActive` | unique `(TenantId, lower(Code))`; unique `(TenantId, lower(PremisesCode), lower(DeviceCode))`; FK to `Warehouses`, `PosMenus` (restrict) |
| `PosMenus` | `Code`, `Name`, `IsActive`, `LayoutUpdatedAt` null, `LayoutUpdatedBy` null | unique `(TenantId, lower(Code))` |
| `PosMenuPages` | `MenuId`, `Position`, `Name`, `Color` null, `CategoryId` null; no audit | FK to `PosMenus` (cascade), `ArticleCategories` (restrict) |
| `PosMenuButtons` | `PageId`, `ArticleId`, `Row`, `Column`, `Label` null, `Color` null; no audit | unique `(TenantId, PageId, Row, Column)`; FK to `PosMenuPages` (cascade), `Articles` (restrict) |

One new migration; no existing table changes.

## 4. Operations — HTTP

All under `/api/v1`, tenant key.

### 4.A Stations — `/pos/stations`

The six master operations of 001 §4.3 with the refusals of 013. List: `search`, `isActive`, `warehouseId`,
`menuId`. Delete: always allowed in this spec (P05 adds `IN_USE` for bills).

```json
{ "id": "uuid", "code": "BAR-1", "name": "Bar tablet 1",
  "warehouse": { "id": "uuid", "code": "BAR", "name": "Bar" },
  "menu": { "id": "uuid", "code": "BAR", "name": "Bar menu" },
  "premisesCode": "PP1", "deviceCode": "1", "isActive": true,
  "createdAt": "…Z", "updatedAt": "…Z", "createdBy": "uuid", "updatedBy": "uuid" }
```

Create `{ code, name, warehouseId?, menuId?, premisesCode, deviceCode, isActive? }` — `warehouseId` omitted
or `null` = the tenant's default warehouse at that moment, stored (011/R8); `menuId` omitted = `null`.
Replace carries every field (`warehouseId` and `menuId` present; `warehouseId: null` = the default again).
`409 CODE_TAKEN` also when another station has the same `premisesCode` **and** `deviceCode` (`errors` keys
`premisesCode`, `deviceCode`).

### 4.A Menus — `/pos/menus`

The six master operations with the refusals of 013. List: `search`, `isActive`. Delete: `409 IN_USE` while a
station names the menu; otherwise with its layout.

```json
{ "id": "uuid", "code": "BAR", "name": "Bar menu", "isActive": true,
  "createdAt": "…Z", "updatedAt": "…Z", "createdBy": "uuid", "updatedBy": "uuid" }
```

Layout:

```json
{ "menu": { "id": "uuid", "code": "BAR", "name": "Bar menu" },
  "pages": [
    { "name": "Favourites", "color": "#C0392B", "category": null,
      "buttons": [ { "article": { "id": "uuid", "code": "ESP", "name": "Espresso" },
                     "row": 0, "column": 0, "label": "Esp", "color": null } ] },
    { "name": "Beer", "color": null, "category": { "id": "uuid", "code": "BEER", "name": "Beer" },
      "buttons": [] } ],
  "updatedAt": "…Z", "updatedBy": "uuid" }
```

| Operation | Request | Success | Errors |
|---|---|---|---|
| Get layout | `GET /pos/menus/{id}/layout` | `200` (`pages: []`, `null` audit when never set) | 404 |
| Set layout | `PUT /pos/menus/{id}/layout` body `{ "pages": [ { "name", "color", "categoryId", "buttons": [ { "articleId", "row", "column", "label", "color" } ] } ] }` | `200` | 400, 404, 409 `REFERENCE_NOT_FOUND`, 409 `REFERENCE_INACTIVE` |

Every member of a page and of a button is present (`color`, `categoryId`, `label` may be `null`). Pages are
in array order; buttons are returned ordered by `row`, `column`.

### 4.C Station menu and lookup (B)

`GET /pos/stations/{id}/menu`:

```json
{ "station": { "id": "uuid", "code": "BAR-1", "name": "Bar tablet 1" },
  "warehouse": { "id": "uuid", "code": "BAR", "name": "Bar" },
  "menu": { "id": "uuid", "code": "BAR", "name": "Bar menu" },
  "version": "opaque string", "serverTime": "…Z",
  "pages": [ { "name": "Favourites", "color": "#C0392B", "category": null,
               "buttons": [ { "articleId": "uuid", "row": 0, "column": 0, "label": "Esp", "color": null } ] },
             { "name": "Beer", "color": null, "category": { "id": "uuid", "code": "BEER", "name": "Beer" },
               "buttons": [ { "articleId": "uuid", "row": null, "column": null, "label": null, "color": null } ] } ],
  "articles": [ PosArticle ] }
```

`PosArticle` — every article named by a button, once, ordered by `code`:

```json
{ "id": "uuid", "code": "LATTE", "name": "Latte", "type": "service",
  "baseUnit": { "id": "uuid", "code": "pcs", "name": "Piece" },
  "category": { "id": "uuid", "code": "COFFEE", "name": "Coffee" },
  "salesPrice": 2.5, "taxRates": [ { "id": "uuid", "code": "VAT25", "name": "VAT 25 %", "rate": 25 } ],
  "barcodes": [ "3850123456789" ],
  "modifierGroups": [ { "id": "uuid", "code": "MILK", "name": "Milk", "isRequired": true, "allowsMultiple": false,
      "options": [ { "id": "uuid", "code": "OAT", "name": "Oat milk", "priceDelta": 0.3 } ] } ] }
```

| Operation | Request | Success | Errors |
|---|---|---|---|
| Menu | `GET /pos/stations/{id}/menu`, optional header `If-None-Match: "<version>"` | `200` + `ETag: "<version>"`; `304` (no body, same `ETag`) when the version matches | 404 |
| Lookup | `GET /pos/stations/{id}/articles?search=&barcode=&limit=&offset=` | `200` list envelope of `PosArticle`, ordered by `code` | 400, 404 |

### 4.D Error codes

None new. `CODE_TAKEN` *(extended)*: the premises/device pair (4.A). `IN_USE` *(extended)*: delete of a menu
named by a station; delete of a warehouse named by a station; delete of a category named by a menu page;
delete of an article named by a menu button (R14).

## 5. Operations — MCP

One tool per operation, same fields, limits and errors (ADR-0009), covered by the parity test. `tools/list`
for a `full` key returns the 116 of P03 and these 14:

| Tools | Input | Output |
|---|---|---|
| `pos_station_list`, `pos_station_get` (`{ id?, code? }`), `pos_station_create`, `pos_station_update`, `pos_station_delete` | as the master tools of P01; list also `warehouseId?`, `menuId?` | as P01 |
| `pos_menu_list`, `pos_menu_get` (`{ id?, code? }`), `pos_menu_create`, `pos_menu_update`, `pos_menu_delete` | as the master tools | as P01 |
| `pos_menu_layout_get` | `{ id }` | Layout |
| `pos_menu_layout_set` | `{ id, pages }` | Layout |
| `pos_station_menu_get` | `{ id, ifVersion? }` | the station menu, or `{ "notModified": true, "version" }` when `ifVersion` equals the current version (the MCP form of `304`) |
| `pos_station_article_search` | `{ id, search?, barcode?, limit?, offset? }` | list envelope of `PosArticle` |

## 6. Rules

### 6.1 Rules this spec introduces

| Key | Name | Default | Switching it to the other value |
|---|---|---|---|
| `posStation.codeChangeable`, `posMenu.codeChangeable` | The code of a POS station / menu may be changed | `true` | `false`: `CODE_CHANGE_NOT_ALLOWED` (013). |
| `posStation.nameUnique`, `posMenu.nameUnique` | Names of POS stations / menus must be unique | `false` | `true`: `NAME_TAKEN` (013). |
| `posStation.warehouseRequired` | The warehouse must be named on a POS station | `false` | `true`: `warehouseId` omitted or `null` -> `400 VALIDATION_FAILED`, key `warehouseId`, with `rules` (as `document.warehouseRequired`, 014). |
| `posMenu.unpricedArticleAllowed` | An article without a sales price may be put on a menu | `true` | `false`: a layout with a button for an article whose `salesPrice` is `null` -> `400 VALIDATION_FAILED`, key `pages[i].buttons[j].articleId`, with `rules`. |
| `posMenu.repeatedArticleAllowed` | An article may appear on several buttons of one menu | `true` | `false`: a layout naming one article on two buttons -> `400 VALIDATION_FAILED`, key of the later button's `articleId`, with `rules`. |
| `pos.searchOutsideMenuAllowed` | The till's search finds articles that are not on its menu | `false` | `true`: the lookup searches every active article of the tenant. |

Groups `posStation`, `posMenu`, `pos`. The registry then has **61** rules. No stored record is re-checked
when a rule changes (012/R15): a menu saved before `unpricedArticleAllowed` became `false` still reads.

### 6.2 Every other check is an invariant (`docs/rules.md` §2)

| Check | Invariant | Reason |
|---|---|---|
| Code and name of stations and menus: form, length; code unique | I7, I9, I10 | As every master. |
| `premisesCode`, `deviceCode`: required, 1–20 letters and digits (ASCII); the pair unique in the tenant, case-insensitive | I7, I9, I10 | The pair names one sequence of receipt numbers (P07); two stations with one pair would issue the same number. *(Form to be confirmed with decision 8.)* |
| A station has a warehouse (I11 extended); omitted means the default | I11 | Every sale issues from one warehouse. |
| Layout shape: at most 50 pages, at most 200 buttons per page; `row`, `column` integers 0–19; one button per cell of a page; page `name` 1–200 characters, `label` `null` or 1–50; `color` `null` or `#RRGGBB` (hex, either case) | I8, I9, I10 | A cell holds one button; bounds of a screen and a request. |
| A category page (with `categoryId`) has no buttons of its own | I8 | Its buttons are its category's articles; mixing the two has no defined order. |
| References exist in the tenant; inactive is not newly referenced; a referenced menu, warehouse, category or article is not deleted | I1, I6 | ADR-0008. |
| What the menu read contains (R20–R24) and the version changes whenever it does | I9 | Contract: a client caches by the version. |

**This spec adds no check that is not in one of the two tables.**

### 6.3 Stations and menus (A)

- R1. Stations and menus behave as the P01 masters, with their `codeChangeable` and `nameUnique` rules as
  013/R9–R16 under the tenant lock.
- R2. Station create/replace, order of checks: validation (with `posStation.warehouseRequired`) ->
  `warehouseId` (`REFERENCE_NOT_FOUND` / `REFERENCE_INACTIVE` for a newly assigned one) -> `menuId` (same) ->
  code -> premises/device pair (`CODE_TAKEN`, keys `premisesCode`, `deviceCode`) -> name. An omitted or `null`
  `warehouseId` resolves to the default warehouse, which is always active (I11).
- R3. Deactivating a warehouse or a menu that a station names is allowed; the station keeps it (ADR-0008).
  `DELETE /warehouses/{id}` -> `IN_USE` while a station names it.

### 6.4 Layout (A)

- R10. Set replaces all pages and buttons. Shape errors (6.2) are reported together with their keys:
  `pages`, `pages[i].name`, `pages[i].color`, `pages[i].categoryId`, `pages[i].buttons`,
  `pages[i].buttons[j].articleId|row|column|label|color`. A second button on an occupied cell -> key
  `pages[i].buttons[j].row` of the later one. A category page with buttons -> key `pages[i].buttons`.
- R11. The two `posMenu` rules of 6.1 are judged after the references (they need the articles). Order of
  checks: shape -> menu exists (`404`) -> references in order of pages, then buttons
  (`categoryId`, then each `articleId`): `REFERENCE_NOT_FOUND`, `REFERENCE_INACTIVE` for an article or
  category the menu does not already name; the first failing reference answers alone -> the two rules,
  reported together, each naming itself.
- R12. Any article type may be on a menu (`stock` sells itself, `service` by recipe, P03/R25).
- R13. Set on an inactive menu is allowed. Attributed (`updatedAt`, `updatedBy` of the layout); the menu's own
  `updatedAt` does not change.
- R14. `DELETE /articles/{id}` -> `IN_USE` while a button names it; `DELETE /article-categories/{id}` ->
  `IN_USE` while a page names it. Deactivating either is allowed.

### 6.5 Station menu (B)

- R20. Content: the station's menu's pages in order. A manual page lists its buttons whose article is active
  (an inactive article's button is left out). A category page lists every **active** article whose sales
  data names the category, ordered by article `code`, with `row`, `column`, `label`, `color` `null`; an
  inactive category gives the page with no buttons. A station without a menu, or whose menu is inactive,
  returns `pages: []` and `articles: []`.
- R21. `articles` holds each article named by a listed button, once, as `PosArticle`: its current sales data
  (P01: `salesPrice` may be `null`), barcodes ordered by `barcode`, and its modifier groups (P03) in the
  article's order — only **active** groups, and in each only **active** options ordered by `sortOrder`, `code`.
- R22. `version` is derived from the whole content except `serverTime` (e.g. a hash): equal content gives an
  equal version, any change of the content (a price, a name, a layout, an option, a deactivation) gives
  another. `If-None-Match` with the current version -> `304` with the `ETag` and no body; any other or a
  malformed value -> `200`.
- R23. An inactive station still answers (its menu is readable; P05 refuses to sell on it).
- R24. `serverTime` is the server's clock at the read (overview §7).

### 6.6 Lookup (B)

- R30. `search` (1–100 characters, 001/R9) matches part of an article's code or name, case-insensitively;
  `barcode` matches exactly as P01/R19. Both may be given (AND); neither -> every article in scope.
- R31. Scope: with `pos.searchOutsideMenuAllowed` = `false`, the active articles the station's menu read
  lists (R20); with `true`, every active article of the tenant. Items are `PosArticle` (R21).
- R32. A station without a menu, with the rule `false`, finds nothing (`200`, empty).

### 6.7 POS keys

- R40. Amends P02/R32: under `/api/v1/pos/` a `pos` key may **read** stations, menus and layouts and use the
  menu read and the lookup; creating, replacing or deleting stations and menus and setting a layout stay
  `full`-key only (`403 FORBIDDEN`). The matching tools likewise.

## 7. Edge cases

- E1. A station created without `warehouseId` gets the default warehouse; after `set-default` to another
  warehouse, the station keeps the first one.
- E2. Two stations `PP1`/`1` -> the second `CODE_TAKEN` (keys `premisesCode`, `deviceCode`); `pp1`/`1` is
  the same pair; `PP1`/`2` is fine.
- E3. Two buttons at row 0, column 0 of one page -> `400`, key `pages[0].buttons[1].row`; at row 0, column 0
  of two pages -> fine.
- E4. `color` `"#c0392b"` accepted; `"red"`, `"#C0392"`, `"C0392B"` -> `400`.
- E5. An article deactivated after the layout was saved: its button disappears from the menu read and the
  version changes; the layout read still shows the button; a layout set again with it -> `200` (already
  named); a new menu naming it -> `REFERENCE_INACTIVE`.
- E6. A sales price changed -> the next menu read has another `version`; `If-None-Match` with the old one ->
  `200`.
- E7. A "Beer" category page and an article added to the category -> it appears on the page without a
  layout change.
- E8. `posMenu.unpricedArticleAllowed` = `false` and a layout with an unpriced article and a cell collision
  -> `400` with the collision key only (shape first; the rule is judged only after the references, R11).

## 8. Tenant isolation

- T1. All tables tenant-owned and filtered; another tenant's warehouse, menu, category or article is
  `REFERENCE_NOT_FOUND` in a body and `404` in a path; the menu read and the lookup never contain another
  tenant's article, even with `pos.searchOutsideMenuAllowed` = `true`.
- T2. Codes, names and premises/device pairs unique per tenant only.
- T3. Rules set in X change nothing in Y. T4. The same through the tools. No `IgnoreQueryFilters()`.

## 9. Security requirements

- S1. Authentication and the `pos`-key limit as P02, amended by R40.
- S2. No input causes `500` — in particular not a layout set racing the delete of an article it names.
- S3. The menu read is bounded (50 pages × 200 buttons) and computed in a fixed number of queries, not one per
  article.
- S4. Every write is attributed (I3).

## 10. Acceptance criteria

Conventions of 012 §10, 013 §10, P01 §10. One tenant per test.

Structure
- AC-01 *(manual)* Build and tests exit 0; earlier tests pass unweakened. The **only** earlier tests changed:
  (1) the number or literal list of rules: 53 -> 61; (2) the number or literal list of tools: 116 -> 130;
  (3) the `pos`-key allow-list tests of P02/P03 gain exactly the reads of R40 and assert the writes are
  forbidden; (4) a rule search whose expected result now also matches a new key or name.
- AC-02 *(manual)* The eight decisions are booleans from `IRules` passed to Domain functions; stations and
  menus reuse the master rule set of 013; the version is computed from the read content in one place.
- AC-03 *(builder, unit)* Layout shape decisions (bounds, cells, colors, category page) and the two
  `posMenu` rule decisions as pure functions; version stability (same content -> same version; one changed
  field -> another).

Registry and tools
- AC-10 New tenant `GET /rules?limit=500` -> `total == 61`: the 53 of P03 AC-10 and
  `pos.searchOutsideMenuAllowed` `false`, `posMenu.codeChangeable` `true`, `posMenu.nameUnique` `false`,
  `posMenu.repeatedArticleAllowed` `true`, `posMenu.unpricedArticleAllowed` `true`,
  `posStation.codeChangeable` `true`, `posStation.nameUnique` `false`, `posStation.warehouseRequired` `false`.
- AC-11 Each of the eight accepts both values and resets.
- AC-12 `tools/list` (full key) returns exactly 130 names. For a station, a layout, a station menu and a
  lookup, the tool result is JSON-equal to HTTP; `pos_station_menu_get` with the current `ifVersion` ->
  `{ notModified: true, version }`.

A — stations and menus
- AC-20 Station and menu (theory): the master criteria of P01 AC-20..AC-29 (create, get by code in another
  case, list, replace, delete, validation, `CODE_TAKEN`, both rules at both values).
- AC-21 Station warehouse (R2, E1): omitted and `null` -> the default; an unknown -> `REFERENCE_NOT_FOUND`,
  inactive -> `REFERENCE_INACTIVE`; Set(`posStation.warehouseRequired`, `true`): omitted -> refused by
  (`VALIDATION_FAILED`, rule, `true`), key `warehouseId`. `DELETE /warehouses/{id}` of a station's warehouse ->
  `IN_USE`.
- AC-22 Premises/device (E2, 6.2): missing, `""`, 21 characters, `"P-1"` -> `400` with the key; E2 as written.
- AC-23 Menu on a station: unknown -> `REFERENCE_NOT_FOUND`; inactive newly -> `REFERENCE_INACTIVE`; delete of
  a menu named by a station -> `IN_USE`; `GET /pos/stations?menuId=` filters.
- AC-24 Layout: get of a new menu -> `pages: []`, `null` audit; set two pages (one manual with three buttons,
  one category page) -> `200`, read back in order, buttons by row and column; set `pages: []` -> empty.
- AC-25 Layout shape (R10, E3, E4): 51 pages, 201 buttons, `row` 20, `column` -1, `1.5`, a 51-character
  label, a missing `label` member, a category page with a button -> `400` with the keys of R10, no `rules`.
- AC-26 Layout references (R11, E5): unknown article -> `REFERENCE_NOT_FOUND`, key
  `pages[i].buttons[j].articleId`; unknown category -> key `pages[i].categoryId`; first failing alone; E5.
- AC-27 Layout rules: defaults — an unpriced article and one article on two buttons -> `200`.
  Set(`posMenu.unpricedArticleAllowed`, `false`) -> refused by (`VALIDATION_FAILED`, rule, `false`);
  Set(`posMenu.repeatedArticleAllowed`, `false`) -> refused by (`VALIDATION_FAILED`, rule, `false`) with the
  later button's key; both at once -> one `400`, two elements in `rules`. E8.
- AC-28 R14: an article on a button and a category on a page cannot be deleted (`IN_USE`); after the layout
  no longer names them -> `204`.

B — menu read and lookup
- AC-40 Content (R20, R21): a station with the menu of AC-24 -> pages in order; the manual page's buttons; the
  category page lists the category's active articles by code; `articles` has each article once with sales
  data, taxes, barcodes, and active modifier groups with active options in order (an inactive option and an
  inactive group are absent).
- AC-41 Left out (R20, E5, E7): a deactivated article's button disappears; a newly categorised article
  appears; a station without a menu -> `pages: []`, `articles: []`.
- AC-42 Version (R22, E6): two reads without a change -> equal `version` and `ETag`; `If-None-Match` with it
  -> `304`, no body; after each of: a price change, an option rename, a layout set, an article deactivation
  -> another version and `200`.
- AC-43 `serverTime` is within a few seconds of the test's clock; `GET` of an unknown station -> `404`.
- AC-44 Lookup (R30–R32): `search=lat` finds `LATTE` by name part; `barcode` exact; both together; an article
  not on the menu is not found by default and is found with Set(`pos.searchOutsideMenuAllowed`, `true`); an
  inactive article is never found; `search` of 101 characters -> `400`.

POS key, isolation, MCP
- AC-60 R40: a `pos` key reads stations, menus, layouts, the menu read and the lookup (HTTP and tools); every
  write of this spec -> `403 FORBIDDEN` / tool error `FORBIDDEN`.
- AC-61 Y cannot read, change, delete or reference X's stations, menus or layouts; Y's lookup with
  `pos.searchOutsideMenuAllowed` = `true` never returns X's article; Y may use X's premises/device pair.
- AC-62 X sets all eight rules to the other value; in Y the defaults of AC-21, AC-27 and AC-44 hold.
- AC-63 Through tools only: create a menu with a layout and a station, `pos_station_menu_get`,
  `pos_station_article_search` -> the same data as HTTP; a layout refused by a `posMenu` rule carries the same
  `code`, `errors` keys and `rules` as HTTP.

## 11. Notes for the tester and the builder

Tester
- Do not pin the form of `version`; pin equality and change.
- One tenant per test. Approved changes to earlier tests are exactly those of AC-01; anything else ->
  `docs/questions/P04-q.md`.

Builder
- Stations and menus are masters as in P01. The layout Set replaces a small aggregate inside
  `SerializedPerTenantAsync`, as P01 sales data.
- The menu read: load pages, buttons, category articles, sales data, taxes, barcodes, groups and options in a
  fixed number of queries (S3); compute the version from the serialized content (e.g. SHA-256, base64url).
- `304` and `ETag` are HTTP mapping only; the Application operation returns the content and its version and
  takes the client's version as an optional input.
- The P02 allow-list table gains the reads of R40 and excludes the writes.
- Anything unclear: `docs/questions/P04-q.md`, then continue.

## 12. Decisions behind this spec

Open owner decisions of the overview §9 that touch it: **4** (one tax-inclusive price; no price per station),
**8** (premises and device codes are stored for the receipt number; their legal form is unverified and may
change before P11), **11** (categories on one level; pages do the grouping).

Architect's defaults, each until the owner says otherwise:

| # | Question | Default | Where |
|---|---|---|---|
| 1 | Is the menu its own record or part of a station? | Its own record, shared by stations | 4.A |
| 2 | "Which articles are shown" | The articles of the station's menu: buttons and category pages | R20 |
| 3 | Category view | A page bound to a category lists its active articles by code, with no hand-placed buttons | 6.2, R20 |
| 4 | Button layout | A grid of up to 20 × 20 cells per page, a label and a colour per button | 6.2 |
| 5 | Does the till's search look beyond the menu? | No (`pos.searchOutsideMenuAllowed`) | R31 |
| 6 | Unpriced or repeated articles on a menu | Allowed by default; each a switch | 6.1 |
| 7 | How does the frontend cache the menu? | `ETag` / `If-None-Match` on one read; polling | R22 |
