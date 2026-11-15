#!/usr/bin/env bash
# One backup now, outside the schedule (e.g. before an upgrade): writes ./backups/xerp-<UTC time>.dump.
#
#   scripts/backup.sh
#
# Works with both the development and the production stack: it runs pg_dump inside the `db` container.
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/.."
[ -f .env ] && set -a && . ./.env && set +a
dir="${XERP_BACKUP_DIR:-./backups}"
mkdir -p "$dir"
file="$dir/xerp-$(date -u +%Y%m%dT%H%M%SZ).dump"
docker compose exec -T db pg_dump -U "${POSTGRES_USER:-xerp}" -d "${POSTGRES_DB:-xerp}" --format=custom > "$file.partial"
mv "$file.partial" "$file"
echo "wrote $file"
