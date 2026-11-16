#!/usr/bin/env bash
# Checks that a Waypoint backup can really be restored, without touching the running server. Run it on the machine
# that holds the age private key, not on the server:
#
#   ./waypoint-restore-test.sh waypoint-backup.key waypoint-2026-01-01-0315.tar.gz.age
#
# It decrypts the archive into a new temporary folder, checks the database with sqlite3, prints how many items
# and captures it holds, to compare with the app, and removes the folder again. It ends non-zero if any step fails.
set -euo pipefail

key="${1:?Give the age private key file, such as waypoint-backup.key}"
archive="${2:?Give a backup file, such as waypoint-2026-01-01-0315.tar.gz.age}"

for tool in age tar sqlite3; do
    if ! command -v "$tool" > /dev/null; then
        echo "$tool isn't installed here, so the backup can't be checked." >&2
        exit 1
    fi
done

work="$(mktemp -d)"
trap 'rm -rf -- "$work"' EXIT

age -d -i "$key" "$archive" | tar -xz -C "$work"
db="$work/waypoint/waypoint.db"
if [ ! -f "$db" ]; then
    echo "The backup holds no waypoint/waypoint.db." >&2
    exit 1
fi

check="$(sqlite3 "$db" "PRAGMA integrity_check;")"
if [ "$check" != "ok" ]; then
    echo "The database in the backup didn't pass the integrity check: $check" >&2
    exit 1
fi

echo "The backup restores and its database is sound."
sqlite3 "$db" "SELECT 'Items, notes, and expenses: ' || count(*) FROM items;
               SELECT 'Captures: ' || count(*) FROM captures;"
echo "Also in it:"
(cd "$work/waypoint" && ls -1)
