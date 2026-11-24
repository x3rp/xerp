# Production: bill of materials to work order

For a company that makes what it sells.

## Steps

1. Bill of materials: `bom_set` with `articleId` (the product) and `components` `[{ articleId, quantity }]` — the quantity of each component for one base unit of the product. `bom_get` reads it; `bom_delete` removes it.
2. `work_order_create` with `articleId`, `quantity`, `plannedDate` and optionally `warehouseId` and own `lines` (otherwise the BOM is exploded). A draft: `work_order_update`, `work_order_delete`. Read with `work_order_get` and `work_order_list`.
3. `work_order_release`: numbered (`WO-`), ready to make.
4. `work_order_complete` with `documentDate`, optionally `producedQuantity` and `lines` `{ lineNo, consumedQuantity }` for what was really used. In one transaction it issues the components and receives the product.
5. Mistakes: `work_order_reverse` undoes a completed order; `work_order_cancel` abandons one that was not completed.

## Rules that change this

`production.componentsMayDifferFromBom`, `production.consumptionMayDifferFromPlan`, `production.producedQuantityMayDifferFromPlan` (`rule_list` with `group` = `production`). The `production` rule profile (`rule_profile_get`) makes a work order follow its plan strictly.
