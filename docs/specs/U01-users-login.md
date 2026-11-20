# U01 — Users and sign-in with username and password

Owner's request, 2026-11-20: people sign in to their tenant with a username and a password.

## Model

- **User** (tenant-owned): `username` (1–50 of `a-z 0-9 . _ -`, starts with a letter or digit, stored in lower
  case, unique per tenant, never changed), `name`, password hash (PBKDF2-SHA256, salted, like POS PINs),
  `isActive`, failure counter, `lockedUntil`, `lastLoginAt`, audit columns. Never deleted, only deactivated.
- **Session** = an API key of actor type `human`, scope `full`, named `login: <username>`, with `userId` and
  `expiresAt` (12 h). Every write still has a key as its actor (ADR-0010). An expired key is `401`.

## HTTP (HTTP only, like API keys: the bodies carry secrets)

| Route | Credential | What |
|---|---|---|
| `POST /api/v1/login/{tenantCode}` `{username, password}` | none | `200 {key, apiKeyId, expiresAt, user, tenant}`; every failure `401 LOGIN_FAILED` |
| `POST /api/v1/logout` | session key | revokes the calling session; a key not issued by a login → `409 INVALID_STATE` |
| `GET /api/v1/users`, `GET /users/{id}` | tenant key | list (`search`, `isActive`, `limit`, `offset`), get |
| `POST /api/v1/users` `{username, name, password}` | tenant key | `201`; taken username `409 USERNAME_TAKEN` |
| `PUT /api/v1/users/{id}` `{name}` | tenant key | rename |
| `POST /api/v1/users/{id}/password` `{password}` | tenant key | new password: unlocks, ends the user's other sessions |
| `POST /api/v1/users/{id}/deactivate` / `activate` | tenant key | deactivation ends all of the user's sessions |

`GET /whoami` also returns `user` (`null` for other keys) and `expiresAt`.

## Rules

`users.lockAfterFailedLogins` (default `true`): the 5th wrong password in a row locks the user for 15 minutes.
`users.shortPasswordsAllowed` (default `false`): otherwise a password has at least 8 characters.

Unknown tenant, unknown user, wrong password, inactive and locked user all answer the same `LOGIN_FAILED`
after the same hashing work. Sign-ins of one tenant are serialised by the tenant lock.

## Not in U01

Permissions per user (roadmap 021), password reset by e-mail, two-factor sign-in.
