# Buying: purchase order to receipt

"We ordered goods from a supplier" and "the goods arrived".

## Steps

1. Make sure the supplier exists (`partner_list`, `partner_create` with `isSupplier` true) and the articles exist (`article_list`).
2. `purchase_order_create` with `orderDate`, `supplierId`, `warehouseId` (or the default), and `lines`: each `{ articleId, quantity, unitPrice }` (optionally `unitId`). It is a draft: change it with `purchase_order_update` (replaces everything), remove it with `purchase_order_delete`.
3. `purchase_order_confirm` gives it a number and makes it immutable; only confirmed orders can be received against. Read orders with `purchase_order_get` and `purchase_order_list` (filter by status).
4. Receive the goods: `stock_document_create` with `type` `receipt`, `documentDate`, `warehouseId`, `purchaseOrderId` and `lines` (each may name `orderLineNo`), then `stock_document_post`. Posting adds the stock. A receipt can be partial; the order tracks what is outstanding.
5. `purchase_order_close` when nothing more will arrive; `purchase_order_reopen` undoes it.

## Errors you will meet

- `ORDER_NOT_OPEN`: the order is a draft or closed. `QUANTITY_EXCEEDS_ORDER`: more than ordered; the rule `purchase.overReceiptAllowed` decides. `PARTNER_ROLE_MISSING`: the partner is not a supplier.
- A refusal that carries `rules` names a company rule. Do not change it unless the user wants the policy changed (`business-rules`).

## Goods without an order

A receipt without `purchaseOrderId` is allowed: `stock_document_create` with `type` `receipt` and the `partnerId` of the supplier (the company may require it).
