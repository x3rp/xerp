# Deployment

How to run Xerp (the ERP API, HTTP and MCP) in production on one Linux server with Docker. For a local
development stack see `docs/getting-started.md`.

The POS modules on `main` (P01, P02) are deployed with it but are not complete: POS bills, payment and
fiscalisation do not exist yet. Do not use Xerp to take payments.

## What runs

`docker compose -f compose.yaml -f compose.prod.yaml` starts four services:

| Service | What | Reachable |
|---|---|---|
| `proxy` | Caddy 2: HTTPS with an automatic Let's Encrypt certificate for `XERP_DOMAIN`, HSTS, compression | ports 80 and 443 of the server |
| `api` | the Xerp API; applies database migrations at start; JSON logs; rate limit per client address | only from `proxy`, never from the host or the internet |
| `db` | PostgreSQL 18, data in the volume `xerp_db_data` | only from `api` and `backup` |
| `backup` | `pg_dump` every 24 h into `XERP_BACKUP_DIR`, dumps older than `XERP_BACKUP_KEEP_DAYS` deleted | — |

All four restart on failure and after a reboot of the server (`restart: unless-stopped`, Docker enabled at boot).

## First install

Needs: a Linux server with Docker Engine and the Compose plugin (2.24 or newer), a DNS name pointing at it,
and ports 80 and 443 open to the internet (80 is needed for the certificate challenge).

```bash
git clone git@github.com:x3rp/xerp.git && cd xerp
cp .env.example .env
```

Edit `.env`:

| Variable | Required | Value |
|---|---|---|
| `XERP_DOMAIN` | yes | public host name, e.g. `erp.example.com` |
| `POSTGRES_PASSWORD` | yes | `openssl rand -base64 24` |
| `XERP_ADMIN_KEY` | yes | `openssl rand -base64 32` (at least 32 characters). Only creates tenants. |
| `XERP_RATE_LIMIT_PER_MINUTE` | no, `600` | requests per minute per client address on `/api/v1` and `/mcp`; `0` switches the limit off |
| `XERP_BACKUP_DIR` | no, `./backups` | host directory for the dumps |
| `XERP_BACKUP_KEEP_DAYS` | no, `14` | days a dump is kept |

Compose refuses to start when a required value is missing. Keep `.env` readable by root only
(`chmod 600 .env`); it holds every secret of the installation.

```bash
docker compose -f compose.yaml -f compose.prod.yaml up -d --build
curl https://$XERP_DOMAIN/health          # {"status":"ok","db":"ok"}
```

Then create the first tenant with the admin key and store the tenant key it returns (section 3 of
`docs/getting-started.md`, with `https://$XERP_DOMAIN/api/v1` as the base address).

To save typing, put `COMPOSE_FILE=compose.yaml:compose.prod.yaml` into `.env`; plain `docker compose …`
then uses both files. The commands below assume that.

## Upgrade

```bash
scripts/backup.sh                         # a dump right now, before anything changes
git pull
docker compose up -d --build
docker compose logs -f api                # migrations are applied at start; watch for errors
```

Migrations run at API start, inside one transaction per migration. Run one API instance only: two
instances starting at the same moment would both try to migrate.

## Backup and restore

The `backup` service writes `xerp-<UTC time>.dump` (PostgreSQL custom format) once a day. Its log shows
every dump and every deletion: `docker compose logs backup`.

**Copy the dumps off the server.** A dump on the same disk does not survive the loss of that disk or of
the server. For example, a nightly `rclone copy ./backups remote:xerp-backups` or `rsync` from another machine.

A dump on demand: `scripts/backup.sh`.

Restore (replaces **all** data; asks for the database name as confirmation, stops the API meanwhile):

```bash
scripts/restore.sh backups/xerp-20261115T020000Z.dump
```

Test a restore now and then, on another machine: a backup that was never restored is not known to work.

## Operations

| What | How |
|---|---|
| Is it up? | `GET /health` → `200 {"status":"ok","db":"ok"}`; `503` when the database is down. Point an uptime monitor at it. |
| Logs | `docker compose logs -f api` — one JSON object per line (`Logging__Console__FormatterName=json`); ship them with any log collector that reads Docker logs. |
| Too many `429 RATE_LIMITED` | raise `XERP_RATE_LIMIT_PER_MINUTE` and `docker compose up -d`. Each client address has its own window; many agents behind one NAT share one. |
| Revoke a leaked tenant key | `POST /api/v1/api-keys/{id}/revoke` with another key of that tenant. |
| Change the admin key | edit `.env`, `docker compose up -d`. Existing tenant keys are unaffected. |
| Database shell | `docker compose exec db psql -U xerp xerp` |

## Security notes

- Only Caddy is reachable from outside. The API trusts `X-Forwarded-For` (`Xerp__ForwardedHeaders=true`)
  because Caddy is the only client that can reach it; never publish the API port while this is on.
- Requests to `/api/v1` and `/mcp` are limited to 1 MB of body (`413 PAYLOAD_TOO_LARGE`).
- API keys are stored as SHA-256 hashes; a lost key cannot be recovered, only revoked and replaced.
- MCP from a browser is refused unless the page's origin is in `Xerp:Mcp:AllowedOrigins`.
- Not there yet (roadmap 020–024): an audit log, permissions per API key, an OpenAPI document,
  row-level security in the database. Tenant isolation is enforced in the Application layer on every query.
