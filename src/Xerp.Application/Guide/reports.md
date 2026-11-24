# Reports

How to answer "show me sales for this month", "who owes us money", "what is in stock" and similar questions.

## Two tools

1. `report_list` — the catalogue: each report's `key`, what question it answers, its `parameters` and its `columns`. Read the descriptions and pick the report that fits the question.
2. `report_run` with `key`, optional `parameters` (an object) and optional `format`. It returns `{ report, parameters, generatedAt, columns, rows, totals, rowCount, truncated, notes }`. `parameters` shows the values used, including defaults. You render, chart and explain; the server only returns numbers.

## Which report for which question

- How much did we invoice (per day, week, month, quarter, year)? `sales-by-period` with `from`, `to`, `groupBy`.
- Who are our best customers? `sales-by-customer`. What sells? `sales-by-article` (quantity and net revenue, with category).
- How much VAT did we charge? `vat-output` (tax base and tax per rate and period).
- How much did we collect, and how (cash, card, transfer)? `payments-received`.
- Who has not paid? `unpaid-invoices` (open amount, days overdue; as of now).
- What do we have, and where? `stock-on-hand`. What moved in a period (opening, in, out, closing)? `stock-movements`. What is not selling? `slow-movers` (`days`, default 90).
- What did customers order and we have not delivered? `open-sales-orders`. What did we order and not receive? `open-purchase-orders`.
- Who did we buy from? `purchases-by-supplier`. What did we make? `production-output`.

## Parameters

- `from`, `to`: `YYYY-MM-DD`, both inclusive. Default: the current month. Reports "as of now" (`unpaid-invoices`, `stock-on-hand`, `slow-movers`, `open-sales-orders`, `open-purchase-orders`) take no period.
- `groupBy`: `day`, `week` (starts Monday), `month`, `quarter`, `year` where the report groups time. A period is shown as its first day.
- Filters, only where listed for the report: `partnerId` (customer or supplier), `articleId`, `categoryId`, `warehouseId`. Find the id with `search`.
- A wrong value is `VALIDATION_FAILED` keyed by the parameter name (`from`, `groupBy`, `days`): correct it and run again.

## Results

- Money columns are in the company's currency, quantities in the article's base unit. `columns[].type` tells how to format each: `date`, `text`, `number`, `money`, `quantity`, `percent`.
- `totals` sums the summable columns of all rows; it is null when there are no rows.
- At most 5000 rows: then `truncated` is true and `notes` says which parameter narrows the report. Do not present a truncated report as complete.
- Drafts are not counted. A cancelled invoice and its cancelling invoice net to zero in the sales reports.

## CSV for the accountant or Excel

`report_run` with `format` `csv` returns the same rows as CSV text: `;` as separator, a decimal point, dates as `YYYY-MM-DD`. Over HTTP, `GET /reports/{key}?format=csv` (or `Accept: text/csv`) returns a file with a UTF-8 byte-order mark that Croatian Excel opens directly.

## Without a report

For a single record use the `*_list` and `*_get` tools (`invoice_list`, `stock_on_hand_list`, `stock_ledger_entry_list`); for a question no report answers, say so rather than adding up lists by hand.
