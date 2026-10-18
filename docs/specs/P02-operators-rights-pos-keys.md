# Spec P02 — Operators, roles and rights, PIN sign-in, approval, POS-only API keys

Status: ready for the tester and the builder.
Owner requirements of 2026-10-13 (`docs/pos/requirements.md`): price and quantity change "with the proper
rights" (2), the waiter responsible for a bill (3), refund with rights (5), authorisation and audit (7).
Branches: `tests/P02-operators-rights-pos-keys` (tester), `feat/P02-operators-rights-pos-keys` (builder).
Read first: ADR-0022/6 and /7, `docs/pos/overview.md` §9 rows 6 and 7, spec **003** and ADR-0010 (API keys),
spec **001** (a master), spec **013** and **P01** (master rules `codeChangeable`, `nameUnique`), architecture
§4, §5, §6, §11.

**Inherited, not re-specified**: everything specs 001–P01 established — authentication, tenant scoping, the
error document, the order of checks, the list envelope and paging, `search`, optional text, ADR-0008
references, the rule mechanism, the master operations. This spec states only what is new.

**Nothing in this spec sells.** It gives later specs three things: who is acting (an operator session), what
that operator may do (rights on a role, with a second operator's approval), and a device key that can do
nothing but POS work. The sensitive acts themselves (price change, discount, void, refund, cash movement,
close of a shift) arrive with P05a, P06, P07a and P08 and call the check of R20.

**Seam.** Part A (roles, operators, sessions, authorize) and part B (POS-only keys, section 4.D and R30–R36)
share nothing. If one loop turn is too short, B follows as P02a with its criteria as numbered.

*(default)* marks an architect's decision the owner may change; all are collected in section 12.

## 1. Goal

A venue keeps a list of its staff (operators) with a role each; a role says which sensitive acts its
operators may do. A waiter signs in on a shared tablet with code and PIN in a second and switches to another
waiter as fast. Any client can ask "may this operator do X, or may this manager approve it" before the act.
A tablet's API key can be limited to POS work so that a stolen tablet cannot change rules, prices or keys.

## 2. Scope

In scope
- A. Two masters under `/pos`: **roles** (with seven rights) and **operators** (with a role and a PIN);
  the master operations of 001 plus "set PIN".
- A. **Operator sessions**: start (code + PIN), get, list (own key's), end.
- A. **Authorize**: the shared check "session operator has the right, or a second operator approves".
- B. **API key scope**: `full` (today's keys) or `pos`; chosen at creation, immutable; a `pos` key is refused
  everything outside the POS allow-list.
- Eight rules on the registry (6.1); new error codes `OPERATOR_SIGN_IN_FAILED`, `SESSION_NOT_ACTIVE`,
  `OPERATOR_RIGHT_MISSING`.
- 16 MCP tools; `tools/list` returns 100 for a `full` key.

Out of scope
- Sign-in with a PIN alone (without the code), cards, NFC, biometrics.
- Rights per key (spec 021 stays as planned); rights per operator outside the role; a right to manage
  operators (only `full` keys manage them).
- A record of every sign-in and authorize call (an audit log is a later spec). Failed attempts are counted,
  not listed.
- Seeding: a new tenant has no role and no operator.
- Any change to what a `full` key may do.

## 3. Data

New tenant-owned tables, audit columns and composite foreign keys as architecture §8 / ADR-0002.

| Table | Columns besides `Id`, `TenantId`, audit | Keys |
|---|---|---|
| `PosRoles` | `Code`, `Name`, `IsActive`, seven booleans `CanChangePrice`, `CanDiscount`, `CanVoidLine`, `CanVoidBill`, `CanRefund`, `CanCashMovement`, `CanCloseShift` | unique `(TenantId, lower(Code))` |
| `PosOperators` | `Code`, `Name`, `RoleId` null, `IsActive`, `PinHash`, `PinChangedAt`, `FailedPinAttempts` int, `LockedUntil` null | unique `(TenantId, lower(Code))`; FK to `PosRoles` (restrict) |
| `PosSessions` | `OperatorId`, `ApiKeyId`, `StartedAt`, `ExpiresAt`, `EndedAt` null; `CreatedBy` only | FK to `PosOperators` (restrict), `ApiKeys` (restrict); index `(TenantId, ApiKeyId, EndedAt)` |

`ApiKeys` gains `Scope` text not null, default `'full'` for existing rows. One new migration.

`PinHash`: a salted, slow password hash (ASP.NET `PasswordHasher` / PBKDF2 or stronger). Never a plain
SHA-256: a PIN has 10^4–10^8 values (ADR-0003's argument for unsalted SHA-256 does not apply).

## 4. Operations — HTTP

All under `/api/v1`, tenant key. `<pin>` is a JSON string.

### 4.A Roles — `/pos/roles`

The six operations of `/units-of-measure` (001 §4.3) with the refusals of 013: list (`search`, `isActive`),
get, get by code, create, replace, delete (`409 IN_USE` while an operator names the role).

```json
{ "id": "uuid", "code": "MANAGER", "name": "Manager", "isActive": true,
  "rights": { "changePrice": true, "discount": true, "voidLine": true, "voidBill": true,
              "refund": true, "cashMovement": true, "closeShift": true },
  "createdAt": "…Z", "updatedAt": "…Z", "createdBy": "uuid", "updatedBy": "uuid" }
```

Create `{ code, name, rights, isActive? }`, replace `{ code, name, rights, isActive }`; `rights` is an object
with **all seven** members, each a JSON boolean (I9).

### 4.B Operators — `/pos/operators`

The six master operations (list with `search`, `isActive`, `roleId`; delete `409 IN_USE` once the operator
has any session) and one more:

| Operation | Request | Success | Errors |
|---|---|---|---|
| Set PIN | `PUT /pos/operators/{id}/pin` body `{ "pin" }` | `200` Operator | 400, 404 |

```json
{ "id": "uuid", "code": "ANA", "name": "Ana", "isActive": true,
  "role": { "id": "uuid", "code": "WAITER", "name": "Waiter" },
  "pinChangedAt": "…Z", "lockedUntil": null,
  "createdAt": "…Z", "updatedAt": "…Z", "createdBy": "uuid", "updatedBy": "uuid" }
```

Create `{ code, name, roleId, pin, isActive? }`; replace `{ code, name, roleId, isActive }` — **no `pin`**
(a `pin` in a replace body is an unknown property, `400`). `roleId` is a UUID or `null` in both. No
representation, list, log line or error contains a PIN or its hash.

### 4.C Sessions and authorize — `/pos/sessions`, `/pos/authorize`

```json
{ "id": "uuid", "status": "active",
  "operator": { "id": "uuid", "code": "ANA", "name": "Ana" },
  "role": { "id": "uuid", "code": "WAITER", "name": "Waiter" },
  "rights": { "changePrice": false, "discount": false, "voidLine": true, "voidBill": false,
              "refund": false, "cashMovement": false, "closeShift": false },
  "apiKeyId": "uuid", "startedAt": "…Z", "expiresAt": "…Z", "endedAt": null }
```

`status` is `active` or `ended`; `role` is `null` and every right `false` when the operator has no role or its
role is inactive; `rights` are the role's **current** values at the read (R14).

| Operation | Request | Success | Errors |
|---|---|---|---|
| Start | `POST /pos/sessions` body `{ "operatorCode", "pin" }` | `201` Session + `Location` | 400, 409 `OPERATOR_SIGN_IN_FAILED` |
| Get | `GET /pos/sessions/{id}` | `200` | 404 |
| List | `GET /pos/sessions?status=&limit=&offset=` | `200` list envelope of the **calling key's** sessions, newest `startedAt` first | 400 |
| End | `POST /pos/sessions/{id}/end` (no body) | `200` Session, `status: ended` | 404 |
| Authorize | `POST /pos/authorize` body `{ "sessionId", "right", "approverCode"?, "approverPin"? }` | `200` `{ "right", "operator": summary, "approvedBy": summary\|null }` | 400, 403 `OPERATOR_RIGHT_MISSING`, 409 `REFERENCE_NOT_FOUND`, 409 `SESSION_NOT_ACTIVE`, 409 `OPERATOR_SIGN_IN_FAILED` |

`right` is one of the seven member names of `rights`. `approverCode` and `approverPin` come together or not
at all.

### 4.D API key scope (amends spec 003)

- `POST /api-keys` body `{ "name", "actorType", "scope"? }`; `scope` is `full` (omitted = `full`) or `pos`.
- The ApiKey representation gains `"scope"`; `GET /api-keys` gains the filter `scope=`; an empty value is absent, another value -> `400`, key `scope` *(P02-q Q3)*.
- `scope` cannot change (there is no operation that would change it). Revocation is unchanged.

### 4.E New error codes (registry: architecture §6)

| HTTP | `code` | Meaning | `errors` keys |
|---|---|---|---|
| 409 | `OPERATOR_SIGN_IN_FAILED` | Unknown operator code, wrong PIN, inactive operator, or operator locked — one answer for all four (R11). | `pin`, or `approverPin` in authorize |
| 409 | `SESSION_NOT_ACTIVE` | The named session is ended or expired. | `sessionId` |
| 403 | `OPERATOR_RIGHT_MISSING` | Neither the session's operator nor the approver may do the act (R20). | `right` or `approverCode` |

`FORBIDDEN` *(extended)*: a `pos` key on an operation outside the allow-list (R31). `IN_USE` *(extended)*:
delete of a role named by an operator, of an operator with a session.

## 5. Operations — MCP

One tool per operation, same fields, limits and errors (ADR-0009), descriptions naming the error codes and,
where rules judge, `rule_list` with the group; covered by the parity test. `tools/list` for a `full` key
returns the 84 names of P01 and these 16:

| Tools | Input | Output |
|---|---|---|
| `pos_role_list`, `pos_role_get`, `pos_role_create`, `pos_role_update`, `pos_role_delete` | as the master tools of P01 (`{ id?, code? }` for get) | as P01 |
| `pos_operator_list`, `pos_operator_get`, `pos_operator_create`, `pos_operator_update`, `pos_operator_delete` | as above; list also `roleId?` | as above |
| `pos_operator_set_pin` | `{ id, pin }` | Operator |
| `pos_session_start` | `{ operatorCode, pin }` | Session |
| `pos_session_get` | `{ id }` | Session |
| `pos_session_list` | `{ status?, limit?, offset? }` | list envelope |
| `pos_session_end` | `{ id }` | Session |
| `pos_authorize` | `{ sessionId, right, approverCode?, approverPin? }` | the authorize result |

Annotations *(amended after review, P02-q Q2)*: the ten master tools as P01; `pos_operator_set_pin` as an
update (`readOnlyHint` false, `destructiveHint` true, `idempotentHint` true); `pos_session_start` as a create
(`readOnlyHint` false, `destructiveHint` false, `idempotentHint` false); `pos_session_end` repeatable
(`readOnlyHint` false, `destructiveHint` false, `idempotentHint` true); `pos_session_get` and
`pos_session_list` read-only; **`pos_authorize` is not read-only**: `readOnlyHint` false, `destructiveHint`
false, `idempotentHint` false — with an approver it counts failed PINs and can lock that operator (R11, R21),
so a client must not run it unasked as a read.

A PIN is an **input** of three tools and never part of any tool result (ADR-0010/5 forbids secrets in
results; an agent that acts as an operator must send its PIN, owner decision 7 of the overview *(default:
allowed)*). `api_key_list` gains `scope?`, `api_key_get` and `api_key_list` return `scope`.

For a `pos` key, `tools/list` returns only the tools of the allow-list (R33), and calling another tool is a
tool error `FORBIDDEN`.

## 6. Rules

### 6.1 Rules this spec introduces

| Key | Name | Default | Switching it to the other value |
|---|---|---|---|
| `posRole.codeChangeable`, `posOperator.codeChangeable` | The code of a POS role / an operator may be changed | `true` | `false`: `CODE_CHANGE_NOT_ALLOWED` (013). |
| `posRole.nameUnique`, `posOperator.nameUnique` | Names of POS roles / operators must be unique | `false` | `true`: `NAME_TAKEN` (013). |
| `posOperator.roleRequired` | Role required on an operator | `true` | `false`: an operator may be saved with `roleId: null`; it has no rights. |
| `pos.secondOperatorApprovalAllowed` | A sensitive act may be approved by a second operator | `true` | `false`: an authorize with an approver is refused (`OPERATOR_RIGHT_MISSING` with `rules`, key `approverCode`); only the operator's own rights count. |
| `pos.severalOperatorsPerDeviceAllowed` | Several operators may be signed in on one device at once | `true` | `false`: a start ends the key's other active sessions in the same transaction (it is not refused). |
| `pos.operatorOnSeveralDevicesAllowed` | An operator may be signed in on several devices at once | `true` | `false`: a start ends the operator's active sessions on other keys in the same transaction. |

Groups: `posRole`, `posOperator`, `pos`. The registry then has **48** rules. No stored record is re-checked
when a rule changes (012/R15).

### 6.2 Every other check is an invariant (`docs/rules.md` §2)

| Check | Invariant | Reason |
|---|---|---|
| Code and name of roles and operators: form, length; code unique | I7, I9, I10 | As every master. |
| `rights`: an object with exactly the seven boolean members | I9 | Contract shape. |
| A right is role data, not a rule | ADR-0022/7 | It differs per person, not per tenant. |
| `pin`: a string of 4 to 8 ASCII digits `0`–`9`, not trimmed | I9, I10 | What a keypad sends; the bound of the hash space a lockout must protect. |
| PIN stored only as a salted slow hash; never returned, logged, or in a tool result | I2 | Key secrecy extended to PINs. |
| Sign-in failures answer identically; after **5** consecutive failures the operator is locked for **15 minutes**; a success resets the count; set PIN unlocks | I2 | Four to eight digits fall to guessing without it; the numbers are bounds of the platform, not a business choice. |
| A session lasts at most **12 hours** from start, belongs to the key that started it, and is ended by end, by set PIN, by deactivation or deletion of its operator | I2 | A session is a credential of a person on a device; it must not outlive the shift or move to another device. |
| An approver is another operator than the session's, and has the right itself | I8 | Definition of a second operator's approval. |
| `scope` is chosen at creation and never changes; the allow-list of a `pos` key | I2 | A credential that can widen itself is not limited. |
| A `pos` key cannot create, list or revoke keys, nor change rules | I2 | Otherwise the limit is one call away. |
| References exist in the tenant; inactive is not newly referenced; a referenced role or operator is not deleted | I1, I6 | ADR-0008. |

**This spec adds no check that is not in one of the two tables.**

### 6.3 Roles and operators

- R1. Roles and operators behave as a unit of measure (001) and as the P01 masters: code and name rules,
  `isActive` default `true`, list with `search` over code and name, order by code, get by code
  case-insensitive, the `codeChangeable` and `nameUnique` rules of 013/R9–R16 under the tenant lock.
- R2. `rights` on create and replace: missing, `null`, a missing member or a non-boolean member -> `400`, key
  `rights` or `rights.<member>`; an unknown member -> `400`, key `rights.<member>`.
- R3. Operator `roleId`: a UUID or `null`; with `posOperator.roleRequired` = `true`, `null` is refused —
  `400 VALIDATION_FAILED`, key `roleId`, with `rules` (013/R17). An unknown role -> `REFERENCE_NOT_FOUND`,
  an inactive one newly assigned -> `REFERENCE_INACTIVE`, key `roleId`; an inactive role the operator
  already has may be kept (ADR-0008).
- R4. `pin` required on create; malformed -> `400`, key `pin`. `" 1234"` is malformed (not trimmed).
- R5. Set PIN: validation -> the operator exists (`404`) -> store the new hash, `pinChangedAt` = now,
  `FailedPinAttempts` = 0, `lockedUntil` = `null`, end every active session of the operator; attributed
  (`updatedAt`, `updatedBy`). Setting the same PIN again is allowed and has the same effects.
- R6. Replace that sets `isActive: false` ends every active session of the operator in the same transaction.
  Deactivating or changing a **role** ends no session (R14 makes the change visible at once).
- R7. Delete: a role named by any operator (also inactive) -> `IN_USE`; an operator with any session ->
  `IN_USE` (sessions, later bills, name it; retire it by deactivation).
- R8. Two operators may have the same PIN; nothing compares PINs across operators.

### 6.4 Sessions

- R10. Start, order of checks: validation (`operatorCode` a code by I9, `pin` by R4; both required) -> find
  the operator by code (case-insensitive) -> the operator is active -> not locked (`lockedUntil` is `null` or
  in the past) -> the PIN verifies. Any failure after validation -> `409 OPERATOR_SIGN_IN_FAILED`, key `pin`,
  the same `detail` for every cause.
- R11. Counting: a wrong PIN for an existing, active, unlocked operator increments `FailedPinAttempts`; at
  5 the operator gets `lockedUntil` = now + 15 minutes and the count returns to 0. The increment is
  **committed although the request is refused**, under a row lock on the operator, so that parallel guesses
  are all counted. While locked, even the right PIN fails and nothing is counted. Unknown codes and inactive
  operators count nothing. `lockedUntil` is visible on the operator to `full` keys (a manager sees why).
- R12. Success: `FailedPinAttempts` = 0; a session with `startedAt` = now, `expiresAt` = now + 12 hours,
  `apiKeyId` = the calling key, attributed to the key. Then the two device rules of 6.1, in the same
  transaction: with `pos.severalOperatorsPerDeviceAllowed` = `false` every other active session of the
  calling key is ended; with `pos.operatorOnSeveralDevicesAllowed` = `false` every active session of the
  operator on another key is ended. An operator signing in again on the same key gets a second session
  (with the first rule `false`, the first one ends).
- R13. Get, list and end see only the **calling key's** sessions; another key's session of the same tenant is
  `404`, like another tenant's. `status=active|ended` filters; an unknown value -> `400`, key `status`.
- R14. A session's `status` is `active` exactly when `endedAt` is `null` and now < `expiresAt`. An expired
  session reads `status: ended` with `endedAt` = `expiresAt` (computed, nothing is written). `role` and
  `rights` are read from the operator's current role at each read.
- R15. End: an active session gets `endedAt` = now; ending an ended or expired session -> `200` unchanged
  (a lost response can be repeated; idempotency keys arrive with P05).
- R16. A revoked key cannot call anything (401, 003), so its sessions are unusable; they are not rewritten.

### 6.5 Authorize — the check every sensitive act will call

- R20. One Application function, `AuthorizeAsync(session, right, approver?)`, used by `POST /pos/authorize`
  now and by every operation of P05a, P06, P07a, P08 that names a right. Order of checks:
  1. validation: `sessionId` a UUID, `right` one of the seven names (`400`, key `right`), `approverCode` a
     code and `approverPin` by R4 if given; one of the two without the other -> `400`, key of the missing one;
  2. the session exists for the calling key -> else `409 REFERENCE_NOT_FOUND`, key `sessionId`;
  3. the session is active -> else `409 SESSION_NOT_ACTIVE`;
  4. **no approver**: the session's operator has the right (role present, active, right `true`) -> `200`,
     `approvedBy: null`; else `403 OPERATOR_RIGHT_MISSING`, key `right`;
  5. **with an approver** (whether or not the operator has the right):
     `pos.secondOperatorApprovalAllowed` = `false` -> `403 OPERATOR_RIGHT_MISSING`, key `approverCode`, with
     `rules`; the approver code is the session operator's -> `403 OPERATOR_RIGHT_MISSING`, key
     `approverCode`, no `rules`; the approver signs in as R10/R11 (failures counted on the approver) ->
     `409 OPERATOR_SIGN_IN_FAILED`, key `approverPin`; the approver's role has the right -> `200` with
     `approvedBy` the approver's summary; else `403 OPERATOR_RIGHT_MISSING`, key `approverCode`.
- R21. Authorize writes nothing except the PIN counters of R11. It creates no session for the approver.
- R22. Later specs record on the act both the session's operator and `approvedBy`; P02 records neither.

### 6.6 POS-only keys

- R30. `scope` on create: omitted -> `full`; `full` or `pos`; anything else, `null` or a non-string ->
  `400`, key `scope`. The tenant's `initial` key and every existing key are `full`.
- R31. A `pos` key is refused with `403 FORBIDDEN` on every operation outside the allow-list. The check
  comes after authentication and before validation; an unknown path stays `404`. `detail` says the key is
  limited to POS work.
- R32. **Allow-list** of a `pos` key (HTTP):
  - every route under `/api/v1/pos/` **except** create, replace, delete and set PIN of roles and operators
    (it may read them: the sign-in screen lists operators);
  - `GET` only: `/articles` and `/articles/{id}`, `/articles/by-code/{code}`, `/articles/{id}/sales-data`,
    `/articles/sales-data`, `/article-barcodes`, `/article-categories`, `/tax-rates`, `/payment-methods`,
    `/units-of-measure`, `/warehouses`, `/partners`, `/rules` — with their `{id}` and `by-code` forms;
    and `GET /whoami` *(amended after review, P02-q Q1: the caller's own tenant and key; every agent is told to
    call it first)*.
  Later specs: a new route under `/api/v1/pos/` is on the list unless its spec says otherwise; a new route
  elsewhere is not.
- R33. MCP: the tools that map to the operations of R32. `tools/list` for a `pos` key returns exactly them.
- R34. Attribution is unchanged: a `pos` key is an ordinary actor in `createdBy` / `updatedBy`.
- R35. `actorType` and `scope` are independent: an `agent` key can be `pos` (an agent at the till).
- R36. A `full` key may do everything it did before; it may also start sessions and authorize.

## 7. Edge cases

- E1. `pin` `"123"`, `"123456789"`, `"12a4"`, `1234` (a number), `" 1234"`, `"１２３４"` (full-width) ->
  `400`, key `pin`. `"0000"` is valid.
- E2. Operator code in another letter case at sign-in -> success.
- E3. Five wrong PINs, then the right one -> `OPERATOR_SIGN_IN_FAILED`; `lockedUntil` is set on the
  operator. After set PIN, the new PIN signs in.
- E4. Four wrong PINs, the right one, four wrong -> not locked (the success reset the count).
- E5. An operator whose role is deactivated keeps its session; the session reads `role: null`, all rights
  `false`; authorize without an approver -> `OPERATOR_RIGHT_MISSING`.
- E6. `posOperator.roleRequired` = `false`, an operator without a role: signs in; every right `false`; a
  manager's approval makes authorize succeed.
- E7. Approver is the operator itself, also by code in another case -> `OPERATOR_RIGHT_MISSING`, key
  `approverCode`, and no failure is counted.
- E8. Operator has the right **and** an approver is given with a wrong PIN -> `OPERATOR_SIGN_IN_FAILED` (an
  approver given is always checked).
- E9. A `pos` key: `PUT /rules/stock.negativeStockAllowed`, `POST /api-keys`, `GET /api-keys`,
  `PUT /articles/{id}/sales-data`, `POST /pos/operators` -> `403 FORBIDDEN`; `GET /articles/sales-data`,
  `GET /pos/operators`, `POST /pos/sessions` -> allowed. `GET /no-such-path` -> `404`.
- E10. A `pos` key with a body that is also invalid on a forbidden route -> `403`, not `400`.

## 8. Tenant isolation

- T1. The three tables are tenant-owned and filtered; operator codes are found in the caller's tenant only:
  tenant Y signing in with X's operator code and PIN -> `OPERATOR_SIGN_IN_FAILED`, and X's counter is
  unchanged.
- T2. A session is also bound to its key (R13): stricter than the tenant.
- T3. Rules set in X change nothing in Y. T4. The same through the tools. No `IgnoreQueryFilters()`.

## 9. Security requirements

- S1. No response, log entry, exception message or tool result contains a PIN, a PIN hash or a key.
- S2. PIN verification takes the same path for every cause of R10 that reaches it; for an unknown code the
  server still runs one hash verification against a fixed dummy hash (no timing difference worth measuring).
- S3. No input causes `500`, in particular not parallel sign-ins of one operator, nor a delete of an operator
  racing a sign-in (the foreign key is the authority).
- S4. Every write is attributed to the key (I3); a session names the key and the operator.

## 10. Acceptance criteria

Conventions of 012 §10, 013 §10, P01 §10. One tenant per test. Time-dependent criteria set `lockedUntil` /
`expiresAt` in the past directly in the database (as 001/AC-20 deactivates a key), or are builder unit tests
with a fake clock where marked.

Structure
- AC-01 *(manual)* Build and tests exit 0; earlier tests pass unweakened. The **only** earlier tests changed:
  (1) the number or literal list of rules: 40 -> 48 with the keys of AC-10; (2) the number or literal list
  of tools: 84 -> 100; (3) tests of spec 003 that pin the exact member set of ApiKey or the `api_key_*`
  schemas: they gain `scope` (`"full"` for every key they create); (4) a rule search whose expected result
  now also matches a new key or name.
- AC-02 *(manual)* The eight decisions are booleans from `IRules` passed to Domain functions; the masters
  reuse the master rule set of 013; rights are columns of the role, not rules; the allow-list is one table in
  one place, used by HTTP and MCP alike; PINs use a salted slow hasher.
- AC-03 *(builder, unit)* PIN form, the lockout arithmetic (5 failures -> 15 minutes, reset on success),
  session status from `endedAt` / `expiresAt` (12 hours), and the authorize decision of R20 as pure
  functions with a fake clock.

Registry and tools
- AC-10 New tenant `GET /rules?limit=500` -> `total == 48`: the 40 of P01 AC-10 and `pos.operatorOnSeveralDevicesAllowed`
  `true`, `pos.secondOperatorApprovalAllowed` `true`, `pos.severalOperatorsPerDeviceAllowed` `true`,
  `posOperator.codeChangeable` `true`, `posOperator.nameUnique` `false`, `posOperator.roleRequired` `true`,
  `posRole.codeChangeable` `true`, `posRole.nameUnique` `false`; ordinal order; `group` = first segment.
- AC-11 Each of the eight accepts both values and resets.
- AC-12 `tools/list` (full key) returns exactly 100 names. One operation of each of roles, operators,
  sessions and authorize: the tool result is JSON-equal to the HTTP response.

A — roles and operators
- AC-20 Role and operator (theory over both): create -> `201`, `Location`, `isActive` `true`; get by id and by
  code in another case; list with `search`; replace -> `200`; delete -> `204`, then `404`. Codes: validation
  as 001, `CODE_TAKEN`; `codeChangeable` and `nameUnique` at both values as P01 AC-26..AC-28.
- AC-21 Rights (R2): a role with all seven `true` and one with all `false` read back exactly; `rights`
  missing, a member missing, `"true"`, an unknown member `tip` -> `400` with the key of R2.
- AC-22 Operator create: the representation has no `pin` member and no hash; `pinChangedAt` set,
  `lockedUntil` `null`. `pin` per E1 -> `400`, key `pin`. A replace body with `pin` -> `400`, key `pin`.
- AC-23 Role (R3): default — `roleId: null` -> refused by (`VALIDATION_FAILED`, `posOperator.roleRequired`,
  `true`), key `roleId`; Set(rule, `false`) -> `201`. Unknown role -> `REFERENCE_NOT_FOUND`; inactive ->
  `REFERENCE_INACTIVE`; an operator keeps its role after the role is deactivated (replace with the same
  `roleId` -> `200`).
- AC-24 Delete (R7): a role named by an operator -> `IN_USE`; an operator after one sign-in -> `IN_USE`; an
  operator never signed in -> `204`.
- AC-25 Set PIN (R5): `200`, `pinChangedAt` later; the old PIN fails, the new one signs in; the operator's
  active sessions read `ended`; a malformed PIN -> `400`; unknown operator -> `404`.

A — sessions
- AC-30 Start with the right code (other case) and PIN -> `201`, `Location`, `status` `active`, operator,
  role and rights of the role, `apiKeyId` the caller, `expiresAt` 12 hours after `startedAt` (± 1 s).
- AC-31 One answer (R10, S1): unknown code, wrong PIN, inactive operator, locked operator -> `409
  OPERATOR_SIGN_IN_FAILED`, key `pin`; the four responses have the same status, `code`, `detail` and `errors`,
  and none contains the PIN.
- AC-32 Lockout (R11, E3, E4): 5 wrong PINs -> operator `lockedUntil` ~ now + 15 min; the right PIN fails;
  after `lockedUntil` is moved into the past, the right PIN signs in. E4 as written. 10 parallel wrong PINs
  on a fresh operator -> all `409`, none `500`, the operator is locked.
- AC-33 Set PIN on a locked operator -> `lockedUntil` `null`; the new PIN signs in.
- AC-34 Sessions per key (R13): key A starts a session; key B (same tenant) `GET`/`end` of it -> `404`;
  `GET /pos/sessions` with B does not list it; `status=active` / `ended` filter; `status=x` -> `400`.
- AC-35 End (R15): `200` `ended` with `endedAt`; ending again -> `200`, the same `endedAt`. A session with
  `expiresAt` moved into the past reads `ended`, `endedAt == expiresAt`.
- AC-36 Rights are live (R14, E5): a role's `voidLine` changed to `true` shows in an existing session at the
  next read; the role deactivated -> `role: null`, all `false`, session still `active`.
- AC-37 Operator deactivated (R6) -> its sessions read `ended`; sign-in fails.
- AC-38 Device rules (R12): defaults — two operators on one key and one operator on two keys all stay
  active. Set(`pos.severalOperatorsPerDeviceAllowed`, `false`): a second sign-in on the key ends the first
  session. Set(`pos.operatorOnSeveralDevicesAllowed`, `false`): a sign-in on key B ends the operator's
  session on key A, not other operators' sessions on A.

A — authorize
- AC-40 Own right: an operator whose role has `discount` -> `200`, `approvedBy: null`; `refund` (false) ->
  `403 OPERATOR_RIGHT_MISSING`, key `right`.
- AC-41 Approval: waiter without `refund`, manager with it; with the manager's code and PIN -> `200`,
  `approvedBy` the manager; with a manager whose role lacks `refund` -> `403`, key `approverCode`; wrong
  approver PIN -> `409 OPERATOR_SIGN_IN_FAILED`, key `approverPin`, and the manager's failure count rises
  (5 such calls lock the manager). E7, E8 as written.
- AC-42 Set(`pos.secondOperatorApprovalAllowed`, `false`): the same approval -> refused by
  (`OPERATOR_RIGHT_MISSING`, `pos.secondOperatorApprovalAllowed`, `false`), key `approverCode`; the waiter's
  own rights still answer `200`.
- AC-43 Order (R20): `right: "tip"` -> `400`, key `right`; only `approverCode` -> `400`, key `approverPin`;
  an unknown session or another key's -> `409 REFERENCE_NOT_FOUND`, key `sessionId`; an ended session ->
  `409 SESSION_NOT_ACTIVE`.
- AC-44 Authorize writes nothing (R21): `GET /pos/sessions` and the operators read the same before and after
  a successful authorize.

B — POS-only keys
- AC-50 `POST /api-keys` with `scope: "pos"` -> `201`, `scope` `"pos"`; omitted -> `"full"`; `"admin"`,
  `null`, `1` -> `400`, key `scope`. `GET /api-keys?scope=pos` filters; the `initial` key reads `"full"`.
- AC-51 A `pos` key: E9 and E10 as written; every route of R32 answers as for a full key (one `GET` each);
  `POST /pos/sessions` and `/pos/authorize` work.
- AC-52 MCP with a `pos` key: `tools/list` returns exactly the tools of R33 (no `rule_set`, no `api_key_*`,
  no `pos_operator_create`); calling `rule_set` -> tool error `FORBIDDEN`; `pos_session_start` works.
- AC-53 Writes of a `pos` key are attributed to it (`createdBy` of its session is its id).

Isolation and MCP
- AC-60 T1 as written; Y cannot read, replace, delete or reference X's role, operator or session (`404` in a
  path, `REFERENCE_NOT_FOUND` in a body); Y creates the same codes -> `201`.
- AC-61 X sets all eight rules to the other value; in Y the defaults of AC-23, AC-38 and AC-41 hold.
- AC-62 Through tools only: create a role and an operator, `pos_session_start`, `pos_authorize` with an
  approver -> the same data as HTTP; `OPERATOR_SIGN_IN_FAILED`, `OPERATOR_RIGHT_MISSING` (with `rules`) and
  `SESSION_NOT_ACTIVE` carry the same `code`, `errors` keys and `rules` as HTTP. No tool result contains a
  PIN.

## 11. Notes for the tester and the builder

Tester
- Do not pin `detail` texts; pin `code`, keys and `rules`. For AC-31 compare the documents member by member.
- One tenant per test. A second key: `POST /api-keys`.
- Approved changes to earlier tests are exactly those of AC-01; anything else -> `docs/questions/P02-q.md`.

Builder
- Roles and operators are masters as in P01 (`RuleRegistry.Master(...)`, `MasterWriteChecks`).
- The failed-attempt increment must survive the refusal: do it in its own short transaction with
  `SELECT … FOR UPDATE` on the operator, then return the refusal.
- The allow-list is data (route templates + tool names) checked in one middleware / one MCP filter; do not
  scatter `if (scope == pos)` over endpoints.
- `AuthorizeAsync` lives in Application `Pos/` and is what P05a, P06, P07a, P08 call; keep it free of HTTP.
- Anything unclear: `docs/questions/P02-q.md`, then continue.

## 12. Decisions behind this spec

Open owner decisions of the overview §9 that touch it: **6** (a POS-only key — this spec builds it as an
opt-in `scope`; existing keys stay unlimited, so the default "not limited" holds for every key the tenant
does not create as `pos`) and **7** (an agent may act as an operator with code and PIN — yes).

Architect's defaults, each until the owner says otherwise:

| # | Question | Default | Where |
|---|---|---|---|
| 1 | Which rights exist? | Seven: change price, discount, void line, void bill, refund, cash movement, close shift; a new role has all `false` | 4.A |
| 2 | Sign-in with code and PIN, or PIN alone? | Code and PIN; PINs need not be unique | R8, R10 |
| 3 | PIN form; lockout | 4–8 digits; 5 failures lock for 15 minutes; set PIN unlocks | 6.2, R11 |
| 4 | Session lifetime | 12 hours, or until ended, PIN change or deactivation | R12, R14 |
| 5 | Does a role change end sessions? | No; rights are read live | R6, R14 |
| 6 | May a `pos` key read operators and master data? | Yes, read only; it writes only under `/pos/` and never manages operators, keys or rules | R32 |
| 7 | Is approval a separate record or token? | No; an approver's code and PIN travel with the act, checked by one function | R20, R21 |
