# Getting started

Start here: who you are, what the company still needs, and which playbook to read for the task in front of you.

## First three calls

1. `whoami` — the company (tenant) and the actor your calls are attributed to. Everything you read or write belongs to that company.
2. `setup_status` — a checklist computed from the company's data. Each open item has a `nextStep` naming the tool and the guide topic. A new company is not set up: work through the open items with the user.
3. `guide` — without `topic` it lists the playbooks; with `topic` it returns one. Read the playbook for a task before you do it.

## The workflow map

setup (`company-setup`) -> masters (`articles-and-prices`, `partners`) -> purchasing (`buying`) -> stock (`stock`) -> selling (`selling`) -> invoicing (`invoicing`) -> payments -> reports (`reports`, `month-end`). A manufacturer adds `production`.

## Finding things

Users remember names, not ids. `search` with `text` finds articles, partners, warehouses, documents, orders and invoices in one call (at most 10 hits per type); then use the `id` with the matching `*_get` tool, for example `article_get`, `partner_get`, `invoice_get`. Records are always referenced by `id` (a UUID); a `code` is only a label for lookup.

## Rules of conduct

- A refused call returns `code`, `detail` and sometimes `errors` and `rules`. Read `errors` to see which argument to correct. See the `errors` playbook.
- The company's business rules (`rule_list`) are its policy. Never change a rule to get one call through; change it only when the user asked for the policy to change (`business-rules`).
- Keys: `api_key_list`, `api_key_get` and `api_key_revoke` manage the credentials that can act for the company; revoking is for the user to decide.
- Amounts and quantities are JSON numbers, not strings. Dates are `YYYY-MM-DD`.
