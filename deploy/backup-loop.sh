#!/usr/bin/env bash
# Runs inside the `backup` service of compose.prod.yaml: a custom-format pg_dump of the database every
# XERP_BACKUP_INTERVAL_SECONDS (default one day) into /backups, and deletion of dumps older than
# XERP_BACKUP_KEEP_DAYS. A failed dump is logged and retried at the next interval; a partial file is removed.
# Restore with scripts/restore.sh. Copy /backups off this machine as well: a backup on the same disk
# does not survive the loss of that disk.
set -uo pipefail

interval="${XERP_BACKUP_INTERVAL_SECONDS:-86400}"
keep_days="${XERP_BACKUP_KEEP_DAYS:-14}"

until pg_isready -q; do sleep 2; done
# On a fresh start the API is still applying migrations; the first dump waits for them.
sleep "${XERP_BACKUP_FIRST_DELAY_SECONDS:-300}"

while true; do
  name="/backups/xerp-$(date -u +%Y%m%dT%H%M%SZ).dump"
  if pg_dump --format=custom --file="$name.partial" && mv "$name.partial" "$name"; then
    echo "backup: wrote $name ($(du -h "$name" | cut -f1))"
  else
    rm -f "$name.partial"
    echo "backup: FAILED at $(date -u +%FT%TZ)" >&2
  fi
  find /backups -maxdepth 1 -name 'xerp-*.dump' -mtime +"$keep_days" -print -delete | sed 's/^/backup: deleted /'
  sleep "$interval"
done
