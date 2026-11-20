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
mkdir -p "$dir" 2>/dev/null || true
name="xerp-$(date -u +%Y%m%dT%H%M%SZ).dump"
file="$dir/$name"
# The directory is created by Docker (owned by root) when the backup service started first: then let that
# service, which mounts it as /backups, write the dump.
if [ ! -w "$dir" ] && docker compose ps --services --status running 2>/dev/null | grep -qx backup; then
    docker compose exec -T backup sh -c "pg_dump --format=custom -f /backups/$name.partial && mv /backups/$name.partial /backups/$name"
    echo "wrote $file"
    exit 0
fi
docker compose exec -T db pg_dump -U "${POSTGRES_USER:-xerp}" -d "${POSTGRES_DB:-xerp}" --format=custom > "$file.partial"
mv "$file.partial" "$file"
echo "wrote $file"
