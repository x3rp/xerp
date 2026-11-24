# Company setup

What a new company needs before its first document: its own data, warehouses, payment methods, a user who can sign in.

## Steps, in order

1. `company_get` then `company_set`: `legalName` (required), `taxId` (the OIB), the address fields (`addressLine1`, `postalCode`, `city`, `countryCode`), `iban`, contacts and `invoiceFooter`. `company_set` replaces the whole profile: send every field you want to keep. These are printed on every invoice.
2. Warehouses: every company has the default warehouse `CENTRAL`. `warehouse_list` shows it. Rename it with `warehouse_update` or add more with `warehouse_create` (`code`, `name`, optional address). `warehouse_set_default` moves the default (new documents without a `warehouseId` use it). `warehouse_get` reads one; `warehouse_delete` only works for a warehouse nothing refers to (`IN_USE` otherwise: deactivate it with `warehouse_update` instead); `warehouse_stock_list` shows what a warehouse holds.
3. Payment methods: `payment_method_create` (`code`, `name`, for example `CASH`, `CARD`, `TRANSFER`); `payment_method_list`, `payment_method_get`, `payment_method_update`, `payment_method_delete`.
4. Tax rates and units of measure belong to the article setup: see `articles-and-prices`.
5. A user who can sign in: users are created over HTTP (`POST /users`), not by a tool. Ask the user or the administrator.
6. Business rules: review them with the user (`business-rules`) and mark them reviewed.

## Check

Call `setup_status` after each step; the matching item turns `done`. `setup_status` also reports `backups`, which is `unknown` until backup monitoring exists.

## Credentials

`api_key_list` shows who can act for the company; `api_key_get` reads one; `api_key_revoke` cancels one (never the key you are using). A secret is never returned by a tool.
