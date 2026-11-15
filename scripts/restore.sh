#!/usr/bin/env bash
# Restores a dump made by scripts/backup.sh or the backup service, REPLACING all data in the database.
#
#   scripts/restore.sh backups/xerp-20261115T020000Z.dump
#
# Stops the API (and the backup service, if running) first so nothing writes during the restore, then
# starts them again; the API re-applies any migrations newer than the dump at start.
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/.."
dump="${1:?usage: scripts/restore.sh <file.dump>}"
[ -f "$dump" ] || { echo "no such file: $dump" >&2; exit 1; }
[ -f .env ] && set -a && . ./.env && set +a
user="${POSTGRES_USER:-xerp}" db="${POSTGRES_DB:-xerp}"

read -r -p "This replaces ALL data in database '$db' with $dump. Type the database name to continue: " answer
[ "$answer" = "$db" ] || { echo "aborted"; exit 1; }

compose=(docker compose)
[ -n "${XERP_COMPOSE_FILES:-}" ] && for f in $XERP_COMPOSE_FILES; do compose+=(-f "$f"); done

"${compose[@]}" stop api backup 2>/dev/null || "${compose[@]}" stop api
"${compose[@]}" exec -T db pg_restore -U "$user" -d "$db" --clean --if-exists --no-owner --exit-on-error < "$dump"
"${compose[@]}" start api
"${compose[@]}" start backup 2>/dev/null || true
echo "restored $dump into $db"
