# Business rules: the company's policy in plain language

A rule is a yes/no switch. `true` always means that the statement in the rule's `name` holds. Xerp ships a default for each; the company may change it. A change applies from the next operation on and never rewrites existing data.

## Look

- `rule_group_list`: the groups (`stock`, `document`, `purchase`, `sales`, `invoice`, `partner`, ...) with a sentence each and how many rules the company has changed.
- `rule_list` with `group`, `search` or `source` = `tenant` (only what the company changed); `rule_get` for one rule's description: it says which operations it judges.
- `rule_change_list`: the history with who, when, why (`reason`).

## Change — only when the user asks for the policy to change

1. Understand the policy the user wants ("we never sell what we don't have", "no back-dating").
2. Propose a ready-made profile: `rule_profile_list`, `rule_profile_get` (`defaults`, `strict`, `retail`, `production`), or pick keys with `rule_list`.
3. Show the difference: `rule_preview` with `changes` `[{ key, value }]` gives, for each, old and new value, what it judges and `affectedDrafts` (for example "3 drafts will need a reference").
4. After the user agrees: `rule_set_many` with the same `changes` and a `reason` in the user's words. All or none; one history entry per changed rule.
5. One rule: `rule_set` (`key`, `value`); back to default: `rule_reset`.
6. If the user is content with the defaults: `setup_mark_rules_reviewed`.

## Never

Do not change a rule because a call was refused. A refusal with `rules` tells you which policy refused; tell the user and ask. See the `errors` playbook.
