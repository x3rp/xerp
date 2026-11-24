# Selling: sales order to delivery, and the till

"A customer wants to buy" -> order -> delivery -> invoice (`invoicing`).

## Steps

1. Customer: `partner_list` or `partner_create` with `isCustomer` true.
2. `sales_order_create` with `orderDate`, `customerId`, `warehouseId` and `lines` `{ articleId, quantity, unitPrice }`. Draft: `sales_order_update`, `sales_order_delete`. Read with `sales_order_get`, `sales_order_list`.
3. `sales_order_confirm`: numbered, immutable, and the quantities are reserved.
4. Deliver: `stock_document_create` with `type` `issue`, `salesOrderId`, `documentDate`, `warehouseId` and `lines`, then `stock_document_post`. `INSUFFICIENT_STOCK` means the goods are not on hand (see `stock`); `STOCK_RESERVED` means other orders hold them.
5. `sales_order_close` when the rest will not be delivered; `sales_order_reopen` undoes it.
6. Invoice the delivery: see `invoicing`.

## The till (point of sale)

For a shop with tills: roles `pos_role_create`, `pos_role_list`, `pos_role_get`, `pos_role_update`, `pos_role_delete`; operators `pos_operator_create`, `pos_operator_list`, `pos_operator_get`, `pos_operator_update`, `pos_operator_delete`, `pos_operator_set_pin`; a shift is `pos_session_start` (`operatorCode`, `pin`) to `pos_session_end`, read with `pos_session_get` and `pos_session_list`. A sensitive act that the operator's role does not allow can be approved by a second operator with `pos_authorize`. A key with the `pos` scope sees only these tools.

## Rules that change this

`sales.partnerRequired`, `sales.overDeliveryAllowed`, `sales.reservedStockProtected` and the document rules (`rule_list` with group `sales` and `document`).
