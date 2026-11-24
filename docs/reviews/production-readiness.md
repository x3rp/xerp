# Production readiness review

Architect, 2026-11-24. Goal set by the owner: a production-ready ERP (no POS) for a small Croatian company.
Sources read: `CLAUDE.md`, `docs/vision.md`, `docs/architecture.md`, `docs/roadmap.md`, `PLAN.md`,
`docs/reviews/open-items.md`, `docs/deployment.md`, `docs/specs/I01-invoices.md`, `U01-users-login.md`,
`015-stock-valuation.md`, `scripts/`, `.github/workflows/ci.yml`, and the layout of `src/`.
Spec 014 (on `feat/014-document-rules`, uncommitted work in the tree) is assumed to land as specified.

## 1. Assessment of the current state

What is solid:
- **Operational core is real.** Stock ledger with immutable entries, receipts/issues/transfers/counts/reversal,
  stored balance with verify/rebuild, purchase and sales orders with partial fulfilment, work orders (W01),
  outgoing invoices with gapless `RN-` numbers and VAT recapitulation per rate (I01), payments, cancellation by
  cancelling invoice, company profile. 123 MCP tools, HTTP and MCP over the same Application layer.
- **Architecture suits the goal.** Tenant-scoped queries with tenant-inclusive FKs (ADR-0002), a per-tenant write
  lock that gives gapless numbers and "no negative stock" (open-items 005/2), 57 tenant-configurable rules
  (ADR-0020), users with sign-in and lockout (U01), API keys with hashes, 1 MB body limit, rate limit, CI workflow.
- **Deployment exists.** Docker compose (Caddy / Tailscale variants), daily `pg_dump`, `scripts/restore.sh`,
  `/health`, JSON logs (`docs/deployment.md`).

What is missing for "a company runs its books on it":
- It is a **sales-side and stock ERP, not yet a business-complete one**: there is no incoming (supplier) invoice,
  no payables, no tax-authority compliance (fiscalisation, e-invoices), no way to know what stock is worth, and
  no accounting output an accountant can use. The owner's decisions of 2026-11-16 deferred audit log (020),
  permissions (021) and RLS (023); those become the main security/accountability gap as soon as a second human
  uses the system.
- Operations are "good enough for a hobby server": backups exist but restore is never verified automatically,
  there are no metrics/alerts, and no off-site copy is built in.
- Every user of a tenant (and every agent) can do everything, including changing rules, which are the
  mechanism that lets the business weaken its own controls.

Legal reality check (Croatia; owner/accountant must confirm, I am not a tax adviser, and I did not re-verify the
2026 rules in this session): B2B/B2G e-invoicing and fiscal reporting under the Fiskalizacija 2.0 regime
(Zakon o fiskalizaciji, in force from 1 Jan 2026 for issuing/receiving structured e-invoices via an information
intermediary, EN 16931 / UBL 2.1 with the HR CIUS, plus reporting to the Tax Administration) and the older
fiscalisation of cash/card sales (JIR/ZKI) are obligations of the company, not of the software, but an invoice
module that cannot satisfy them cannot be the company's only invoicing tool. I01 already says so ("Not in I01").
Cash and card payments on an invoice are explicitly unsafe today.

## 2. Ranked gaps

Ranking = how hard it blocks a real Croatian small company, then cost of delay. Size: S = one builder session,
M = 2-3, L = 4+ or external dependency.

| # | Gap | Why it matters | Size | Depends on | Owner decision? |
|---|---|---|---|---|---|
| 1 | **Fiskalizacija 2.0: issuing and receiving e-računi** (UBL 2.1 / EN 16931 HR CIUS XML, signing, delivery through an information intermediary or the Tax Administration's system, status/reporting back, storing the XML as the legal original; archive 11 years) | Legal obligation for VAT-registered companies; without it issued `RN-` invoices are not legally valid B2B documents. The single biggest blocker. | L | OIB/VAT data in company profile (exists), incoming invoices (#2) for the receive side, number series (#6), a decision on the intermediary | **Yes**: which access point (own FINA/Tax Admin certificate vs a paid intermediary), who holds the certificate, and the exact compliance deadline for the owner's company. Needs a research spike with the current official technical specification first. |
| 2 | **Incoming (supplier) invoices and payables** (record/receive, link to purchase order/receipt, VAT input, due date, payment, open items by supplier, three-way match optional as a rule) | A company must record what it owes and its input VAT (PDV-ulaz). I01 only covers outgoing. e-računi arrive here (#1). Roadmap 018/019 intended both sides. | M-L | #1 for receiving XML; 009 receipts (exist); I01 patterns | Small: gapless numbering is not needed for incoming; decide whether payables may be entered manually without XML (recommended: yes, as a rule). |
| 3 | **Fiscalisation of cash/card payments (JIR/ZKI)** | Legally required for cash/card (and for POS, out of scope). If the company takes only bank transfers it is not needed. | L | A fiscal certificate, #1 infrastructure reused | **Yes**: confirm that the target company invoices only by bank transfer for now. If yes, defer and keep the I01 warning; add a rule that blocks cash/card payment methods on invoices until fiscalisation exists (S). |
| 4 | **Roles and permissions per key/user (021)** | Today every user and every agent may post, cancel invoices, change rules, create keys. The AI-user model needs "agent may draft, human posts". Bookkeeping and segregation of duties for even 3 staff. | M | U01 (users), 010 key lifecycle; rules mechanism | **Yes** (deferred on 2026-11-16): reopen it. Proposal: fixed small role set (owner, accountant, clerk, read-only, agent-draft) as tenant data, not a free-form matrix; "who may change rules" is its own permission. |
| 5 | **Audit log (020)** | Master data (prices, partners, tax rates, rules) has only `updatedBy`; rule changes have a log, nothing else does. Needed for disputes, tax inspection, and to supervise agents. | M | None technically; most useful with #4 | **Yes** (deferred): reopen. Cheap form: one append-only table written in the DbContext save hook (one place, covers all write paths), with retention as a rule. |
| 6 | **Number series (017)** | I01 uses fixed `RN-000001`. Croatian practice usually expects a yearly or location-based pattern (e.g. `1/POS1/1`); e-invoice and fiscal rules fix some elements. Gapless handling exists per series; configuration is what is missing. | M | None; do before e-invoices lock formats | Small: confirm desired invoice number format with the accountant. |
| 7 | **Stock valuation (015, specified, unbuilt)** | Without cost there is no stock value for the balance sheet, no margin, no year-end inventory list (popis). Spec is ready and gated on 014. | M | 014 merged | No (ADR-0021 decided moving average). |
| 8 | **Accounting output**: either a chart of accounts + journal (016) or a minimum export for the accountant: sales/purchase VAT books (knjiga URA/IRA), PDV-return totals, ledger as CSV/XML in a format common Croatian accounting packages import | A small company normally keeps books with an external accountant. A VAT book and a clean export gives 90% of the value at 20% of the cost of a full G/L. Full G/L (016) is L. | M (export) / L (G/L) | #2 for URA, I01 for IRA | **Yes**: full double-entry G/L inside Xerp, or export to the accountant's software? Recommend export first. |
| 9 | **Backup verification and off-site copy** | `docs/deployment.md` says "test a restore now and then" which in practice never happens. On a laptop server the disk is the single copy. | S | None | Small: choose an off-site target (rsync to another tailnet machine, rclone to a bucket, encrypted). |
| 10 | **Monitoring and alerting** | Only `/health`. No metrics, no alert when backup fails, disk fills, or error rate rises. A silent failure is the real production risk. | S-M | None | No. Proposal: `/metrics` or structured counters, a dead-man's-switch ping from the backup job, uptime monitor on `/health`. |
| 11 | **Data export / portability** (CSV/JSON per resource, full tenant export, PDF of invoice) | Owner and accountant need data out; GDPR portability; PDF of an invoice is what the customer actually receives (I01: "the web client prints", and there is no web client in the repo). | M | #1 changes the legal form (XML + PDF visual) | Small: PDF generation approach (server-side library vs client). |
| 12 | **User interface for humans** | The repo has API, MCP and CLI only; humans need to sign in (U01 exists) and use invoices. Vision says thin clients, and `src/` has no web project. | L | Stable OpenAPI (#14) | **Yes**: build a React client, or declare agents + CLI the only front ends for the first customers. |
| 13 | **Security hardening**: key `scope` beyond `full`, key expiry/rotation, password reset, 2FA, per-user rate limits and login throttling by IP, dependency scanning, secrets handling, RLS (023, deferred), DB-level immutability (open-items 005/4), CORS/CSP for a future web client | Tenant isolation rests on Application-layer filtering only. One missed `WHERE` leaks across tenants. Acceptable for a single-tenant home server, not for hosting others. | M-L | #4 | **Yes** (023 deferred): if one company on its own server, keep deferring; if hosting several tenants, do RLS before the second tenant. |
| 14 | **OpenAPI document** | Roadmap 018/022. Needed for any generated client, for the web UI, for integrators and for e-invoice intermediaries' test tooling. MCP already self-describes. | S-M | None | No. |
| 15 | **Currencies, advance invoices, credit-note variants, returns to supplier, quotations** | Croatia uses EUR; multi-currency is rare for the target. Advance invoices (avansni račun) and credit notes appear in real life. Invoice cancellation exists; true partial credit notes do not. | M each | I01 | No; pick on customer demand. |
| 16 | **Open review items** (`docs/reviews/open-items.md`) | 22 open, none "before MVP". Most are cosmetics. Worth doing before release: 007/1 (a read that can end as `500`), 011/2 (rebuild `500`), 005/4 (immutability only in app code), 003/7 (try with a real agent client), 009/1 and 013/1 only if data grows. | S | None | No. |
| 17 | **Concurrency and scale limits**: a per-tenant lock serialises all writes of a tenant; one API instance only (migrations at start) | Fine for a small company. Becomes visible only with heavy agent traffic. | - | - | No; note only. |
| 18 | **Test and release hygiene**: CI exists; no staging, no migration rollback story, no upgrade-with-data test on a restored dump | A production upgrade that fails halfway must be recoverable. | S | #9 | No. |

Notes on ordering: items 1-3 are legal; 4-5 are controls; 6-8 are completeness of money flow; 9-10 are survival;
11-14 are usability and integration; 15-18 are backlog.

## 3. Recommended next 5 steps

Order rationale: finish what is in flight (014, 015) because 015 is already specified and valuation feeds the
year-end; do the cheap reliability fixes that remove silent risk; then the compliance chain (series, incoming
invoices, e-invoice) because it has the longest lead time and an external dependency; reopen the two deferred
control items before real staff use the system.

### Step 0 (in flight, not counted): merge 014, then build 015 stock valuation
Already specified (`docs/specs/015-stock-valuation.md`, ADR-0021). Nothing to decide. Do it right after 014 merges
because it touches every posting path and later specs (incoming invoices carry cost, accounting export needs
values) should not have to retrofit it.

### Step 1: Reliability bundle (S, one spec "R01")
Automated restore verification (a script and compose service or cron that restores the latest dump into a
scratch database, runs `stock_balance` verify and a row-count sanity check, and fails loudly), off-site copy
(encrypted rsync/rclone to a configurable target), a dead-man's-switch ping and failure alert from the backup
service, `/metrics`-style counters or at minimum structured log events for 5xx and failed logins, and the
open-items that can produce a `500`: 007/1, 011/2. This is small, has no owner decision beyond "where does the
off-site copy go", and removes the worst silent failure (a backup that does not restore) before real data
accumulates. Update `docs/deployment.md` accordingly.

### Step 2: Number series (017) with the e-invoice format in mind (M)
Per-tenant series per document type (prefix, year pattern, yearly restart, gapless flag, date-effective lines),
replacing the fixed patterns of spec 005 and the `RN-` series of I01 while keeping existing numbers valid. Do
it before e-invoicing because the number appears in the XML, in the PDF and in fiscal data, and changing it
afterwards means migrating legal documents. Ask the accountant for the exact invoice number format the company
wants (owner decision, small). Yes/no settings go on the rules registry; prefix and digits are series data
(`docs/rules.md` section 3 "With other specs").

### Step 3: Incoming invoices and payables, with a pluggable "invoice document" boundary (M-L)
Supplier invoice entity (draft -> recorded), linked to a purchase order/receipt where one exists, VAT input per
rate, due date, payments, open items and aging per supplier, optional three-way-match rule (invoice quantity not
above received). Design the invoice model so an e-invoice XML can later be attached as the original document
(store bytes + hash, immutable). This closes procure-to-pay and gives the VAT input side that URA needs. Rules
(configurable, defaults mirror I01): `purchaseInvoice.partnerRequired`, `purchaseInvoice.overpaymentAllowed`,
`purchaseInvoice.matchReceiptRequired`. Decision for the owner: may a payable be entered manually (no XML)? Recommend
yes.

### Step 4: Fiskalizacija 2.0 e-invoicing, spike then build (L, external)
Begin with a research spike (S, one session, no code): fetch the current official Tax Administration technical
specification and the list of information intermediaries, confirm the owner company's start date and which
obligations apply (issue, receive, report), and decide intermediary vs direct integration. Then specify and
build: UBL 2.1 / EN 16931 HR CIUS generation from an issued invoice, validation against the official schema and
business rules, delivery and status tracking through the chosen channel, receipt of incoming e-invoices into step
3's entity, archival of the XML, and a rule that blocks cash/card payment methods until fiscalisation of those
exists. This is the largest item and the one with an external dependency (certificate, test environment), so it
starts as soon as steps 2-3 give it stable numbers and a receiving side. Owner decisions: intermediary,
certificate custody, test environment access.

### Step 5: Controls: permissions (021) plus audit log (020), reopened together (M + M)
Ask the owner to lift the 2026-11-16 deferral for these two before anyone other than the owner uses the live
system. Permissions: a small fixed role set per user/key (owner, accountant, clerk, read-only, agent-draft),
`FORBIDDEN` paths, "who may change rules" and "who may issue/cancel invoices" as explicit permissions, key
expiry. Audit log: append-only table written from the DbContext save hook (single choke point), actor, actor
type, operation, entity, before/after, queryable over HTTP and MCP, retention configurable. Together they let
the owner give an AI agent draft-only rights and still answer "who changed this price and when" to an
inspector. RLS (023) stays deferred while there is one company on one server; revisit before a second tenant.

### After these (not ranked here)
Accounting export / URA-IRA VAT books (gap 8), data export and invoice PDF (11), OpenAPI (14), a human UI (12),
chart of accounts (016) only if the owner wants bookkeeping inside Xerp.

## 4. Owner decisions needed

1. Does the target company invoice only by bank transfer for now (cash/card fiscalisation then deferred)?
2. E-invoicing: own access point vs a paid information intermediary; who holds the certificate; start date.
3. Reopen audit log (020) and permissions (021), deferred on 2026-11-16, before the first non-owner user?
4. Accounting: export to the accountant's software (recommended) or a double-entry ledger inside Xerp (016)?
5. Human UI: build a web client, or agents plus CLI only for now?
6. Single-company server (RLS stays deferred) or hosting several tenants (RLS before the second one)?
7. Off-site backup target and invoice number format (small, but needed for steps 1 and 2).

## 5. Caveat

Croatian tax and fiscalisation specifics above come from the architect's background knowledge and the repo's own
notes (`docs/specs/I01-invoices.md` "Not in I01"); they were not re-checked against current official sources in
this review. Step 4's spike exists to do exactly that before any money is spent on building.
