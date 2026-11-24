# Partners: customers and suppliers

One record type for both roles: a partner may be a customer, a supplier, or both.

## Steps

1. Look first: `search` with the name or tax id, or `partner_list` with a `search` text. Avoid duplicates.
2. `partner_create` with `code`, `name`, `isCustomer` and/or `isSupplier`, optional `taxId` (OIB) and address fields.
3. `partner_get` reads one by `id` or `code`; `partner_update` replaces its data (send all fields, as in every update); `partner_delete` fails with `IN_USE` once an order, document or invoice refers to the partner: set it inactive with `partner_update` instead.

## Roles matter

A purchase order needs a partner with `isSupplier`; a sales order and an invoice need `isCustomer`. A partner without the role is refused (`PARTNER_ROLE_MISSING`); fix the partner's role with `partner_update` rather than creating a second partner.

## Rules that change this

The company may require a role, a tax id, address fields, unique names and unique tax ids (`rule_list` with `group` = `partner`). `CODE_TAKEN`, `NAME_TAKEN` and `TAX_ID_TAKEN` are the refusals; read `rules` in the answer.
