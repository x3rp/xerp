# ADR-0021: Stock valuation — moving average per article and warehouse, value on the ledger

Status: accepted (2026-10-13) for spec 015 — the architect's decision; the points the owner may want to
decide himself are listed in spec 015 §12.

## Context
The stock ledger holds quantities only (ADR-0012); purchase orders carry prices that nothing uses (009).
The owner ordered valuation after the rule retrofit: purchase cost on receipts and the value of stock per
warehouse. Two earlier decisions bind it: the ledger is the truth and the balance is a stored, rebuildable
sum (ADR-0018); negative stock is a tenant's switch, so valuation must say what stock below zero costs
(ADR-0020, owner's decision 5). A rule is a boolean, so a valuation *method* cannot be a rule.

## Decision
1. **One method: moving (weighted) average, per article and warehouse** — the pair the balance already has.
2. **Value lives on the ledger.** Every `StockLedgerEntry` gets a signed `Value` (2 decimals) when it is
   written and never changes. `StockBalance` stores `Value` and the current `UnitCost`, both derived; verify
   and rebuild cover them. Stored value == sum of the ledger's value, per pair, always.
3. **Valued at posting, in posting order.** No recalculation afterwards: a back-dated document, a reversal
   or a later price changes no earlier entry.
4. **Out at the average, in at the amount.** A movement out takes `q × V / Q`; the one that empties the pair
   takes all of `V`, so quantity zero always means value zero. A receipt comes in at quantity × cost; a
   transfer carries the value it took; a reversed issue returns what the issue took; a reversed receipt
   leaves at the current average.
5. **Stock below zero costs the last known cost of the pair** (zero if it never had one). When goods arrive
   into stock below zero, what remains is valued at the cost of what arrived, and the arriving entry's
   value is the change of the balance — it absorbs the correction of the issues made below zero.
6. **Cost is entered on the receipt line** (`unitCost`); a receipt against a purchase order takes the order
   line's price. Whether a cost is required, whether it may differ from the order and what an uncosted
   receipt is worth are tenant rules (spec 015 §6.1).

## Alternatives
- **FIFO / cost layers.** Exact, and what BC and Odoo offer, but needs a layer table, layer consumption on
  every issue and a defined behaviour for reversals and negative stock per layer. Not needed to answer
  "what is my stock worth"; can be added later as a second method on the same ledger column.
- **One average per article for the whole tenant.** Simpler transfers (no value moves), but value per
  warehouse — which the owner asked for — would be quantity × a company-wide cost, not what the goods there
  cost. Rejected as the first method; possible later.
- **Value entries in a separate table** (BC). Allows later cost adjustment of posted entries. We have no
  adjustments yet; one column keeps quantity and value in one append-only row and one rebuild.
- **Repricing on back-dated postings** (ERPNext repost). Makes history correct by date at the price of
  rewriting values under posted documents and, later, posted G/L entries. Rejected: posted is final (I4).
- **Reversal negates the original's value exactly.** Symmetric, but after an issue at the average it leaves
  value on a pair with no quantity (or a negative value on positive stock). Rejected for decision 4.
- **Plain arithmetic through negative stock** (`V + w`). After the stock returns above zero the average is
  distorted without bound (5 units worth 150 after buying at 20). Rejected for decision 5.

## Consequences
- The ledger's value sums to the balance's and to the report; one invariant, checked by verify.
- Values of original and reversal need not sum to zero; quantities still do. A receipt entry's value can
  differ from its line's amount when it lands on stock below zero. Spec 016/018 will post these differences
  as cost variance; until then they are visible on the ledger.
- The average depends on the order of posting, not on document dates.
- A tiny remaining quantity can round its value to 0.00 and so its cost to 0; accepted.
- Stock posted before spec 015 is worth zero; an opening-value / revaluation document is future work.
- Cost of goods sold for a delivery is already there: the value of its ledger entries.
