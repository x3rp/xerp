# Errors: what a refusal means and what to do

A failed tool call returns JSON `{ code, detail, errors?, rules? }`. Branch on `code`; read `detail` for the explanation; read the keys of `errors` to see which argument to correct. The HTTP API answers the same.

## The codes you meet most

- `VALIDATION_FAILED`: an argument is wrong. `errors` names it (`lines[0].quantity`, `changes[1].key`). Correct it and retry.
- `NOT_FOUND` / `REFERENCE_NOT_FOUND`: the id is not in this company. Look it up with `search` or the `*_list` tool.
- `CODE_TAKEN`, `NAME_TAKEN`, `TAX_ID_TAKEN`, `BARCODE_TAKEN`: the value exists; find the existing record instead of creating another.
- `IN_USE`: something refers to the record; deactivate it instead of deleting.
- `INVALID_STATE`: the record is in the wrong status (a posted document, a draft invoice). Read it with its `*_get` tool.
- `INSUFFICIENT_STOCK`, `STOCK_RESERVED`, `QUANTITY_EXCEEDS_ORDER`, `COUNT_OUTDATED`, `DATE_NOT_ALLOWED`: business refusals of stock and orders; see `stock`, `buying`, `selling`.
- `RATE_LIMITED`: wait and retry. `UNAUTHENTICATED` / `FORBIDDEN`: the key is wrong or limited.

## When `rules` is present

A company rule refused the call: it names the rule's `key` and its `value`. You can change the rule (`rule_set`), but it is the company's policy, not an obstacle: tell the user what refused and why, and change it only if they say so (`business-rules`). When `rules` is absent, no switch lifts the refusal.

## Retrying

Do not retry a call that failed with `VALIDATION_FAILED` unchanged. A call that failed leaves nothing changed; `rule_set_many` and posting are all-or-nothing.
