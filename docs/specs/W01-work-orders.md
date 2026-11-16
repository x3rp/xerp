# Spec W01 — Bills of materials and work orders

Status: built (2026-11-16). Owner request: "radni nalozi i stvari u vezi toga". Written after the code, by the
same agent, as the record of what was built; the acceptance tests are `tests/Xerp.IntegrationTests/WorkOrderTests.cs`.

## 1. Purpose

Make a stock article from other stock articles and book it in stock: the components leave the warehouse, the
product comes in. Single-level production; no routing, operations, machines, labour or costing (valuation is
spec 015).

## 2. Model

- **Bill of materials** of a stock article (the product): an ordered list of components, each a stock article
  and the quantity of it per **one base unit of the product**, in the component's base unit. At most one bill
  per article; set and replaced as a whole; may be deleted.
- **Work order**: header (product, `quantity` in the product's base unit, warehouse, `plannedDate`, reference,
  note) and 1–200 component lines (article, `plannedQuantity`, after completion `consumedQuantity`).
- Status: `draft` → `released` (number `WO-000001`, fixed from then on) → `completed`; `released` → `cancelled`;
  `completed` → `reversed`. A draft can be completed directly (it is released on the way) and deleted.
- Completion posts, in one transaction with the work order:
  - an **issue** (`SI-…`) of every component with a consumed quantity above 0, and
  - a **receipt** (`SR-…`) of the produced quantity of the product,
  both in the work order's warehouse, dated `documentDate`, with the work order's number as reference.
  The ledger stays the only record of stock; balances, numbering and stock rules are those of spec 005–012.
- Reversal reverses the receipt (product out), then the issue (components back); the work order becomes
  `reversed`. These two stock documents can not be reversed on their own (`INVALID_STATE`).

## 3. Rules

Invariants: product and components are stock articles (`ARTICLE_NOT_STOCKED`); an article is not its own
component; quantities as on stock documents (> 0, 6 decimals; a consumed quantity may be 0).

Configurable (ADR-0020), all default `true`:

| Key | Judges | `false` means |
|---|---|---|
| `production.componentsMayDifferFromBom` | create / replace with `lines` | lines must equal the bill × quantity |
| `production.consumptionMayDifferFromPlan` | complete | consumed = planned × produced / quantity |
| `production.producedQuantityMayDifferFromPlan` | complete | produced = quantity |

Also applied: `stock.negativeStockAllowed` and `sales.reservedStockProtected` to the issue at completion;
`stock.negativeStockAllowed` to the reversal.

Defaults where nothing is given: lines from the bill (planned = per-unit × quantity, rounded half away from
zero to 6 decimals; a component planned at 0 is refused under `quantity`); warehouse = the tenant's default;
`producedQuantity` = `quantity`; consumption = planned scaled to the produced quantity; `documentDate` = today (UTC).

## 4. Interface

HTTP (under `/api/v1`) and MCP tools, one operation each:

| HTTP | Tool |
|---|---|
| `GET /articles/{id}/bom` | `bom_get` |
| `PUT /articles/{id}/bom` `{components:[{articleId, quantity}]}` | `bom_set` |
| `DELETE /articles/{id}/bom` | `bom_delete` |
| `GET /work-orders?status&articleId&warehouseId&search&limit&offset` | `work_order_list` |
| `GET /work-orders/{id}`, `GET /work-orders/by-number/{number}` | `work_order_get` |
| `POST /work-orders` | `work_order_create` |
| `PUT /work-orders/{id}` | `work_order_update` |
| `DELETE /work-orders/{id}` | `work_order_delete` |
| `POST /work-orders/{id}/release` | `work_order_release` |
| `POST /work-orders/{id}/complete` `{documentDate?, producedQuantity?, lines?:[{lineNo, consumedQuantity}]}` | `work_order_complete` |
| `POST /work-orders/{id}/cancel` | `work_order_cancel` |
| `POST /work-orders/{id}/reverse` `{documentDate?, note?}` | `work_order_reverse` |

Stock errors of a completion are keyed `lines[lineNo - 1].consumedQuantity` (components) and
`producedQuantity` (product). Every query is tenant-scoped; another tenant's work order or bill is `NOT_FOUND`.
An article in a bill (as product or component) or on a work order, and a warehouse on a work order, are in use
(`IN_USE` on delete; type and base unit frozen). POS-scoped keys cannot use any of it.

## 5. Not included

Multi-level explosion, scrap/yield factors, by-products, partial completions, routing and capacity, cost of
production, reservations of components by released work orders.
