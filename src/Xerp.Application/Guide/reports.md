# Reports

How to answer "show me sales for this month" and similar questions.

## The report catalogue

Reports are listed by the tool `report_list` once spec RP01 is delivered; each report names its parameters. Until then, answer from the lists below and say that the figure is computed from them.

## What you can already read

- Sales: `invoice_list` with `status` `issued` and a date range; `invoice_get` for the lines and the VAT recapitulation. Open receivables: `invoice_list` with `paymentStatus`.
- Orders: `sales_order_list` and `purchase_order_list` (status, partner).
- Stock: `stock_on_hand_list`, `warehouse_stock_list`, `stock_ledger_entry_list`; differences with `stock_balance_difference_list`.
- Documents: `stock_document_list` and `search` by number or reference.

## How to present

Name the period, the filter and what is excluded (drafts and cancelled invoices are not sales). Quote totals as the system gives them; do not recompute VAT yourself.
