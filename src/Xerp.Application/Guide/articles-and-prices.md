# Articles and prices

Units, tax rates, categories, articles, their sales price and tax, alternative units and barcodes.

## Order

1. Units of measure: `uom_create` (`code`, `name`, for example `kom`, `kg`); `uom_list`, `uom_get`, `uom_update`, `uom_delete`.
2. Tax rates: `tax_rate_create` (a `code`, a `name` and the percentage, for example `PDV25`); `tax_rate_list`, `tax_rate_get`, `tax_rate_update`, `tax_rate_delete`.
3. Optional categories: `article_category_create`, `article_category_list`, `article_category_get`, `article_category_update`, `article_category_delete`.
4. The article: `article_create` with `code`, `name`, `type` (a stock article or a service) and `baseUnitId` (the `id` of a unit). Stock is always kept in the base unit. `article_get`, `article_list` (use `search` text), `article_update`, `article_delete` (`IN_USE` once documents refer to it: set it inactive instead).
5. Sales data: `article_sales_data_set` with `articleId`, `categoryId` (or null), `taxRateIds` and `salesPrice`. Invoices take the price and tax from here. `article_sales_data_get` and `article_sales_data_list` read it.
6. Other units: `article_unit_set` (`articleId`, `unitId`, `factor` = how many base units one of that unit is; for example a box of 12 has factor 12); `article_unit_list`, `article_unit_get`, `article_unit_delete`.
7. Barcodes: `article_barcode_create` (`articleId`, `barcode`); `article_barcode_list`, `article_barcode_get`, `article_barcode_delete`. Find an article by barcode with `search`.

## Rules that change this

The company may require a category, a tax rate or a sales price on every article, and unique barcodes (`rule_list` with `group` = `article`). A refusal names the rule in `rules`; do not switch the rule to get one article through.

## Typical mistakes

- Passing the code where an `id` is needed.
- Sending a price or quantity as a string.
- Deleting a unit or an article that documents use: `IN_USE`.
