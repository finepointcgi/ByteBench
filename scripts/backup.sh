#!/bin/sh
# Backs up a ByteBench installation into one timestamped .tar.gz:
#   db/bytebench.db      a consistent SQLite snapshot (sqlite3 ".backup", safe while ByteBench is running)
#   repos/              the bare git repositories
#   attachments/        issue attachments and release assets
#   keys/               the Data Protection key ring (needed to keep users signed in and to decrypt stored secrets)
#
# Usage:  scripts/backup.sh [output-dir]
# Paths default to a source checkout; override them to match your configuration, e.g. in the Docker image:
#   docker compose exec bytebench sh -c 'BYTEBENCH_DB=/data/db/bytebench.db BYTEBENCH_REPOS=/data/repos \
#     BYTEBENCH_ATTACHMENTS=/data/attachments BYTEBENCH_KEYS=/data/keys sh /app/scripts/backup.sh /data/db/backups'
#
# Restore (with ByteBench stopped):
#   tar -xzf bytebench-backup-YYYYmmdd-HHMMSS.tar.gz -C /tmp/restore
#   cp /tmp/restore/db/bytebench.db "$BYTEBENCH_DB"; rm -f "$BYTEBENCH_DB-wal" "$BYTEBENCH_DB-shm"
#   rsync -a --delete /tmp/restore/repos/ "$BYTEBENCH_REPOS/"     (likewise attachments/ and keys/)
#   then start ByteBench (pending migrations run at startup).
# The archive holds password hashes, webhook secrets and the key ring: store it encrypted and access-controlled.
set -eu

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
DB="${BYTEBENCH_DB:-$ROOT/bytebench.db}"
REPOS="${BYTEBENCH_REPOS:-$ROOT/repos}"
ATTACHMENTS="${BYTEBENCH_ATTACHMENTS:-$ROOT/attachments}"
KEYS="${BYTEBENCH_KEYS:-$ROOT/keys}"
OUT_DIR="${1:-$ROOT/backups}"

command -v sqlite3 >/dev/null 2>&1 || { echo "backup.sh: sqlite3 is required" >&2; exit 1; }
[ -f "$DB" ] || { echo "backup.sh: database not found at $DB (set BYTEBENCH_DB)" >&2; exit 1; }

STAMP="$(date -u +%Y%m%d-%H%M%S)"
mkdir -p "$OUT_DIR"
STAGE="$(mktemp -d "${TMPDIR:-/tmp}/bytebench-backup.XXXXXX")"
trap 'rm -rf "$STAGE"' EXIT INT TERM
umask 077

# 1. Database: an online, transactionally consistent copy (never copy the live .db/-wal files directly).
mkdir -p "$STAGE/db"
sqlite3 "$DB" ".timeout 10000" ".backup '$STAGE/db/bytebench.db'"
sqlite3 "$STAGE/db/bytebench.db" "PRAGMA journal_mode=DELETE;" >/dev/null   # a single self-contained file, no -wal/-shm
sqlite3 "$STAGE/db/bytebench.db" "PRAGMA integrity_check;" | grep -qx ok || { echo "backup.sh: integrity check failed" >&2; exit 1; }
rm -f "$STAGE/db/bytebench.db-wal" "$STAGE/db/bytebench.db-shm"   # empty after the switch to DELETE mode

# 2. Repositories, attachments and keys. Git writes objects before it moves refs, so a copy taken during a push
#    may miss the newest push but is never corrupt; for a perfectly quiet copy, stop ByteBench first.
copy_dir() {
    if [ -d "$1" ]; then mkdir -p "$STAGE/$2" && cp -R "$1/." "$STAGE/$2/"; else echo "backup.sh: note: $1 does not exist, skipped" >&2; fi
}
copy_dir "$REPOS" repos
copy_dir "$ATTACHMENTS" attachments
copy_dir "$KEYS" keys

ARCHIVE="$OUT_DIR/bytebench-backup-$STAMP.tar.gz"
tar -czf "$ARCHIVE" -C "$STAGE" .
echo "Backup written to $ARCHIVE ($(du -h "$ARCHIVE" | cut -f1))"
