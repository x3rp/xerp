# Spec R01 — Reliability: verified backups, off-site copy, backup alerts, consistent reads

Status: ready for the builder after spec 015 is merged (or in parallel with it: it touches no business code).
Owner goal of 2026-11-24: a production-ready ERP, everything except fiscalisation (`docs/roadmap.md`,
"Production plan"). Input: `docs/reviews/production-readiness.md`, `docs/deployment.md`,
`docs/reviews/open-items.md` (007/1).
Branch: `feat/R01-reliability`. No tester branch: the acceptance criteria are scripts and a few integration
tests the builder writes.

## 1. Goal

Today a backup is written every day and nobody knows whether it can be restored, whether it left the machine,
or whether the last one failed. After this spec every backup is **proven restorable** when it is written, can
be **copied off the machine**, and a failure **reaches a human** without anyone reading logs. A document read
can no longer answer `500` because a master changed between two statements of the read.

## 2. Scope

In scope
1. Verification of every dump by a real restore into a scratch database.
2. An optional off-site copy command run after every verified dump.
3. A status file and `GET /health/backup`; an optional ping URL for an external monitor; and the `backups` item of `setup_status` (A01), which reads the same status.
4. A restore drill script and its runbook in `docs/deployment.md`.
5. Open item 007/1: document and order reads in one read-only snapshot.

Out of scope
- Point-in-time recovery (WAL archiving), replicas, high availability.
- Metrics/Prometheus, log shipping. A JSON log on stdout stays the interface (`compose.prod.yaml`).
- Business rules: nothing here is a tenant's choice. These are deployment settings (environment), the same
  for every tenant on the server, so architecture §11 does not apply.

## 3. Backups

- R1. **Verify.** After `pg_dump` writes `xerp-<time>.dump`, the backup service restores it with
  `pg_restore --no-owner --no-privileges --exit-on-error` into a scratch database `xerp_verify` (dropped and
  created anew each time, on the same PostgreSQL server) and runs one check query: the number of tenants and
  of stock ledger entries in the scratch database equal those of the live database read **before** the
  dump started, or are greater by at most what was written meanwhile — so the check is
  `verify >= before`. Then the scratch database is dropped. A dump that fails any step is renamed
  `xerp-<time>.dump.failed`, is never counted as a backup and is deleted by the same retention.
- R2. **Off-site.** With `XERP_BACKUP_OFFSITE_CMD` set (a shell command; `{file}` is replaced by the dump's
  path), it runs after a verified dump, with a timeout of `XERP_BACKUP_OFFSITE_TIMEOUT_SECONDS` (default 1800).
  Examples in the runbook: `rclone copy {file} remote:xerp-backups/`, `rsync -a {file} user@host:/backups/`.
  Its failure does not delete the local dump; it is reported (R3). Unset: no copy, and the status says so.
- R3. **Status.** After every run the service writes `/backups/status.json` atomically (write then rename):
  ```json
  { "lastRunAt": "…Z", "ok": true, "lastGoodFile": "xerp-….dump", "lastGoodAt": "…Z",
    "sizeBytes": 123, "verified": true, "offsite": "ok" | "failed" | "disabled", "error": null }
  ```
  `ok` is false when the dump, the verify or a configured off-site copy failed; `error` is one line of text.
  `lastGood*` names the newest verified dump and survives later failures.
- R4. **Ping.** With `XERP_BACKUP_PING_URL` set, the service calls it with `GET` after a successful run and
  `GET {url}/fail` after a failed one (the healthchecks.io convention), timeout 10 s, failure of the ping
  only logged. The backup image therefore has `curl` (a small image under `deploy/backup/`, built from
  `postgres:18`; compose builds it).
- R5. **Retention** stays `XERP_BACKUP_KEEP_DAYS`; it never deletes `lastGoodFile`, also when it is older.
- R6. `scripts/backup.sh` (manual backup) does R1 and R3 too, by running the same script in the backup
  container (`backup-once`), so a manual dump before an upgrade is verified the same way.

## 4. Health

- R7. `GET /health` is unchanged (database only), so existing monitors keep working.
- R8. `GET /health/backup`, unauthenticated like `/health`, reads `status.json` from a read-only mount of the
  backup directory into the API container (`XERP_BACKUP_STATUS_FILE`, default `/backups/status.json`):
  - `200 {"status":"ok","lastGoodAt":…,"offsite":…}` when `ok` is true and `lastGoodAt` is younger than
    `2 × XERP_BACKUP_INTERVAL_SECONDS` (+ 1 h);
  - `503 {"status":"stale" | "failed" | "unknown", …}` otherwise — `unknown` when the file is missing or
    unreadable (e.g. a development stack without the backup service).
  It never shows a path outside the file name, a command, a URL or any error text beyond `error` of R3.
- R9. `docs/deployment.md`: how to point an uptime monitor at `/health` and `/health/backup`, and how to
  use `XERP_BACKUP_PING_URL` with healthchecks.io (free tier) as the dead-man's switch for "no backup ran".

## 5. Restore drill

- R10. `scripts/restore-drill.sh [dump]` (default: `lastGoodFile`): restores into `xerp_drill`, prints the
  per-table row counts next to the live ones, and drops `xerp_drill`. It changes nothing in the live
  database. The runbook in `docs/deployment.md` says to run it monthly and after every upgrade, and how to do
  a real restore with `scripts/restore.sh` (unchanged).

## 6. Consistent reads (open item 007/1)

- R11. `IXerpDb` gets `ReadSnapshotAsync<T>(Func<CancellationToken, Task<T>>)`: a transaction at
  `REPEATABLE READ`, read-only, no tenant lock. `GetAsync`, `GetByNumberAsync` and `ListAsync` of stock
  documents, purchase orders, sales orders, work orders and invoices run inside it, so the document and the
  masters it shows are read from one snapshot. No response changes. Close 007/1 in `open-items.md`.

## 7. Acceptance criteria

- AC-01 Build and all tests pass; no earlier test changes.
- AC-02 *(builder, script)* `deploy/backup/test-backup.sh` against the dev stack: one run writes a dump and
  a `status.json` with `ok: true, verified: true, offsite: "disabled"`; with `XERP_BACKUP_OFFSITE_CMD="cp {file} /tmp/offsite/"`
  the file is there and `offsite: "ok"`; with `XERP_BACKUP_OFFSITE_CMD=false` `ok: false, offsite: "failed"`
  and the local dump is kept; a corrupted dump (truncate it before verify, via a test hook
  `XERP_BACKUP_TEST_CORRUPT=1`) gives `ok: false`, a `.failed` file, and `lastGoodFile` unchanged.
- AC-03 *(integration)* `/health/backup`: no file -> `503 unknown`; a fresh ok file -> `200`; an ok file
  older than the window -> `503 stale`; `ok: false` -> `503 failed`; no authentication needed; `/health`
  unchanged.
- AC-04 *(integration)* 007/1: a document read while another connection replaces the draft and deletes the
  master it named, interleaved by a test hook, answers `200` (the old or the new state), never `500`.
- AC-05 *(manual, on the laptop server)* after deploy, `/health/backup` answers `200` after the first run,
  and `scripts/restore-drill.sh` prints matching counts. Report the output in the merge note.

## 8. Decisions behind this spec (architect's defaults)

| # | Question | Default |
|---|---|---|
| 1 | Verify every dump or weekly? | Every dump: databases of this size restore in seconds; a weekly check leaves six unproven backups. |
| 2 | Where is the off-site target configured? | A shell command in the environment, not a fixed provider: rclone covers S3, B2, Google Drive, SFTP. |
| 3 | A stale backup makes `/health` fail? | No: a separate `/health/backup`, so a backup problem never restarts or de-routes the API. |
| 4 | Which owner input is needed? | The off-site target (a bucket, a second machine) and a ping URL; the spec works without both. |
