# Month end

What to check, and what to send to the accountant.

## Checklist

1. No drafts left that should have been posted: `stock_document_list` with status `draft`, `invoice_list` with status `draft`, `purchase_order_list` and `sales_order_list` with status `draft`.
2. Stock agrees with the ledger: `stock_balance_difference_list` returns nothing. If it does, tell the user, then `stock_balance_rebuild`.
3. Counts: a stock count posted for the period (`stock` playbook), with the differences explained.
4. Payments: unpaid issued invoices (`invoice_list` with `paymentStatus`); record the bank statement with `invoice_payment_add`.
5. Company data and rules unchanged by accident: `company_get`, `rule_change_list`.
6. Backups: `setup_status` shows `backups`; it is `unknown` until monitoring exists, so ask whoever runs the server.

## For the accountant

The issued invoices of the month with their numbers and VAT recapitulation (`invoice_list`, `invoice_get`), the stock documents with numbers (`stock_document_list`) and the sales and purchase orders. See `reports` for the report catalogue.
