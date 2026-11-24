# Invoicing: invoice, issue, payment, cancel

An invoice is a draft until it is issued; an issued invoice is never edited, only cancelled by a cancelling invoice.

## Steps

1. `invoice_create` with `customerId`, `issueDate`, `lines` `{ articleId, quantity }` (price and tax come from the article's sales data: `article_sales_data_set`), optional `dueDate`, `paymentMethodId`, `reference`. Change a draft with `invoice_update`; remove it with `invoice_delete`. Read with `invoice_get` and `invoice_list` (filters `status`, `paymentStatus`, `customerId`).
2. `invoice_issue` gives it the next gapless `RN-` number, freezes the amounts and the VAT recapitulation per rate. Depending on the rule `invoice.issueMovesStock` it also issues the goods from the warehouse.
3. Payments: `invoice_payment_add` (`id`, `amount`, `paymentDate`, optional `paymentMethodId`, `note`); `invoice_payment_delete` removes one recorded by mistake. Overpayment is refused unless the rule `invoice.overpaymentAllowed` is on.
4. Mistake after issue: `invoice_cancel` (optional `issueDate`, `note`) creates a cancelling invoice and, if the issue moved stock, returns it.

## Before you issue

The company's own data must be filled (`company_get`, `company_set`): it is printed on the invoice. The customer may be required (`invoice.customerRequired`); back-dating is governed by `invoice.backdatingAllowed`.

## Errors

`INVALID_STATE`: the invoice is not a draft (or not issued, for a payment). `INSUFFICIENT_STOCK` at issue when the rule moves stock and the goods are not on hand.
