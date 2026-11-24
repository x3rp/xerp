# Stock: receipts, issues, transfers, counts, corrections

Stock changes only through posted stock documents. Every posting writes ledger entries; nothing else changes stock.

## Documents

`stock_document_create` with `type`: `receipt` (goods in), `issue` (goods out), `transfer` (between warehouses, with `toWarehouseId`) or `count` (inventory count: the counted quantity replaces the book quantity). `documentDate`, `warehouseId`, `lines` `{ articleId, quantity, unitId? }`, optional `partnerId`, `reference`, `note`. It is a draft until `stock_document_post`. Edit a draft with `stock_document_update`, remove it with `stock_document_delete`. `stock_document_get` and `stock_document_list` read documents (filter by `type`, `status`).

## Corrections

A posted document is never edited. `stock_document_reverse` (`id`, `documentDate`, optional `note`) posts the exact opposite as a new document. For a wrong count, post a new count.

## Looking at stock

- `stock_on_hand_list` (filters `articleId`, `warehouseId`): what is on hand and available now.
- `warehouse_stock_list`: everything one warehouse holds.
- `stock_ledger_entry_list` (filters `articleId`, `warehouseId`, `documentId`): the history behind a number.
- `stock_balance_difference_list` compares the quick balances with the ledger; if they differ, `stock_balance_rebuild` recomputes the balances from the ledger. Say so to the user before you do it.

## Errors

`INSUFFICIENT_STOCK` (rule `stock.negativeStockAllowed`), `STOCK_RESERVED` (rule `sales.reservedStockProtected`), `DATE_NOT_ALLOWED` (rules `document.pastDateAllowed`, `document.futureDateAllowed`), `ARTICLE_NOT_STOCKED` (a service has no stock), `COUNT_OUTDATED` (stock moved after the count was made). Never switch a rule only to get one posting through.
