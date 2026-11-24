# A01 — The company's agent: guidance, setup, business rules in one conversation

Owner, 2026-11-24: **"what matters most is that their agent can use this very simply, set their business
rules and generate their reports."** The product's user is the customer's AI agent (vision: "AI is not a
feature of the ERP, AI is a user of the ERP"). Reports are spec RP01; this spec is everything else an agent
needs to be productive from its first call.
Branches: `tests/A01-agent-experience`, `feat/A01-agent-experience`. Before 015 (owner priority).

## 1. Problem

An agent connecting to `/mcp` today gets 123+ tools and a short paragraph of instructions. It must discover by
itself in which order a company is set up, which tool does a business task ("we received goods from X"),
and which of 70 switches express "we never want to sell what we don't have". It works — but slowly, with many
wrong calls, and every agent re-learns it.

## 2. What is built

### 2.1 Server instructions (MCP `initialize`)

`McpServerSetup.Instructions` is rewritten as a short orientation (at most ~40 lines): what Xerp is; the
workflow map (setup -> masters -> purchasing -> stock -> sales -> invoicing -> payments -> reports); the
three tools to start with (`setup_status`, `guide`, `report_list`); the error and `rules` conventions (as
now); "never change a business rule unless the user asked for the policy to change".

### 2.2 `guide` — playbooks on demand

`guide { topic? }` (read-only, also `GET /guide`, `GET /guide/{topic}`). Without `topic`: the list of topics
with one line each. With a topic: a playbook in Markdown — the steps, the tools in order with the arguments
that matter, the usual errors and what to do. Topics (at least): `getting-started`, `company-setup`,
`articles-and-prices`, `partners`, `buying` (order -> receipt), `selling` (order -> delivery -> invoice),
`invoicing` (invoice -> issue -> payment -> cancel), `stock` (receipts, issues, transfers, counts,
corrections), `production` (BOM -> work order), `business-rules`, `reports`, `month-end` (what to check and
which reports to send to the accountant), `errors`.
The playbooks are files in the repository (`src/Xerp.Application/Guide/*.md`, embedded resources), written
for an agent, reviewed like code. A test checks that every tool name a playbook mentions exists, and that
every tool is mentioned by at least one playbook (so a new tool cannot ship without guidance).

### 2.3 `setup_status` — what this company still needs

`setup_status` (read-only, `GET /setup-status`): a checklist computed from the tenant's data, each item
`{ key, done, detail, nextStep }`:
company profile filled (name, OIB, address, IBAN); at least one warehouse besides the default or the default
renamed; tax rates exist; payment methods exist; units of measure; at least one article with a sales price
and tax rate; at least one customer and one supplier; business rules reviewed (`rulesReviewedAt` set, see
2.4); at least one user who can log in; backups healthy (from `/health/backup` once R01 exists, otherwise
`unknown`). `nextStep` names the tool to call and the `guide` topic. Overall `{ ready, done, total }`.

### 2.4 Business rules in plain language

- `rule_list` gains `area` grouping in its output and a `group` overview call: `rule_group_list` returns
  each group (`stock`, `document`, `purchase`, `sales`, `invoice`, `partner`, …) with a one-sentence
  plain-language description and how many rules are changed from the default.
- `rule_set_many { changes: [{key, value}], reason? }` — several rules in one call, **atomically** (all or
  none: any unknown key or non-boolean value refuses the whole call with `errors` per item), one history
  entry per changed rule carrying the same `reason`. This is how an agent applies the policy a user describes
  in one sentence ("we are a strict wholesale company: partner and reference on every document, no negative
  stock, no back-dating").
- **Profiles**: `rule_profile_list` / `rule_profile_get { key }` return ready-made combinations an agent can
  propose to the user — at least `defaults` (our shipped values), `strict` (wholesale/accounting-heavy:
  partner and reference required, no past/future dates, no negative stock, supplier numbers unique, totals
  must match…), `retail` (customer not required on invoices, issue moves stock, repeats allowed), `production`
  (work-order rules for a manufacturer). A profile is just a list of `{key, value}`; applying it is
  `rule_set_many` with that list, so the user sees and confirms the diff first. Profiles are data in code,
  reviewed like rules; a test checks that every key of every profile exists.
- `rule_preview { changes }` (read-only): for each change, the rule's name, old and new value, the operations
  it will judge (from its description) and a count of current drafts that would be refused at their next save
  or posting where that is cheap to compute (documents without partner/reference, past-dated drafts);
  otherwise `null`. Lets the agent tell the user "3 drafts will need a reference".
- `company_set` gains `rulesReviewedAt` (set by `rule_set_many` and by an explicit
  `setup_mark_rules_reviewed`), shown in `setup_status`.

### 2.5 Find anything by name

`search { text, types? }` (read-only, `GET /search?q=`): one call that finds articles (code, name, barcode),
partners (code, name, tax id), warehouses, documents and orders (number, reference), invoices (number,
supplier number) — max 10 per type, each `{ type, id, code|number, name|label, status? }`. Agents mostly
know a name, not an id; today they need to guess which `*_list` to search.

## 3. Not in A01

MCP prompts/resources (client support is uneven; tools work everywhere); natural-language parsing on the
server (the agent is the language layer); permissions (021, deferred).

## 4. Acceptance criteria (selection)

- `initialize` returns the new instructions; `tools/list` grows by the new tools (`guide`, `setup_status`,
  `rule_group_list`, `rule_set_many`, `rule_profile_list`, `rule_profile_get`, `rule_preview`,
  `setup_mark_rules_reviewed`, `search`).
- The playbook coverage test (2.2) passes and fails when a tool is added without a mention.
- A new tenant: `setup_status` lists every item not done with a `nextStep`; after the steps it is `ready`.
- `rule_set_many` with one unknown key changes nothing; with valid keys writes one history row per change with
  the reason; `rule_profile_get strict` applied through `rule_set_many` makes `rule_list source=tenant` show
  exactly the profile's non-default values.
- `search` finds an article by part of its name and an invoice by its number, never another tenant's.
- **End-to-end agent test** (manual, recorded in the merge note): a fresh Claude session with only the MCP
  connection and the instruction "set up a small wholesale company, buy 10 items, sell 4 and invoice them,
  then show me sales for this month" completes it; count the tool calls and the errors before and after
  this spec.
