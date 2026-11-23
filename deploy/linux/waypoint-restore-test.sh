#!/usr/bin/env bash
# Checks that a Waypoint backup can really be restored, without touching the running server. Run it on the machine
# that holds the age private key, not on the server, with the backup folder copied over whole (or at least the backup
# and the attachments-* folders beside it):
#
#   ./waypoint-restore-test.sh waypoint-backup.key waypoint-2026-01-01-0315.tar.gz.age
#
# It decrypts the archive into a new temporary folder, puts back the attachments it lists from the attachments-*
# folders beside it (or in the folder given third), checks each one against its SHA-256 and the database with
# sqlite3, prints how many items and captures it holds, to compare with the app, and removes the folder again. It
# ends non-zero if any step fails, such as an attachment that's missing or doesn't match.
#
# To keep what was restored, name a folder that doesn't exist yet in RESTORE_TO:
#
#   RESTORE_TO=~/waypoint-restored ./waypoint-restore-test.sh waypoint-backup.key waypoint-2026-01-01-0315.tar.gz.age
set -euo pipefail

key="${1:?Give the age private key file, such as waypoint-backup.key}"
archive="${2:?Give a backup file, such as waypoint-2026-01-01-0315.tar.gz.age}"
store_root="${3:-$(dirname -- "$archive")}"

for tool in age tar sqlite3 sha256sum; do
    if ! command -v "$tool" > /dev/null; then
        echo "$tool isn't installed here, so the backup can't be checked." >&2
        exit 1
    fi
done
if [ -n "${RESTORE_TO:-}" ] && [ -e "$RESTORE_TO" ]; then
    echo "$RESTORE_TO is already there. Give RESTORE_TO a folder that doesn't exist yet." >&2
    exit 1
fi

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

# Backups from before attachments were kept apart hold them in the archive itself, and have no list.
list="$work/waypoint/attachments.list"
listed=no
restored=0
if [ -f "$list" ]; then
    listed=yes
    while IFS= read -r -d '' entry; do
        blob="${entry%% *}"
        path="${entry#* }"
        # Only names the backup itself makes, so a damaged list can't write outside the restored folder.
        if ! [[ "$blob" =~ ^attachments-[0-9a-f]+/([0-9a-f]{64})\.age$ ]]; then
            echo "The backup's list of attachments has an entry that isn't one: $blob" >&2
            exit 1
        fi
        hash="${BASH_REMATCH[1]}"
        if [ -z "$path" ] || [ "$path" = "$entry" ] || [[ "$path" == /* ]] || [[ "/$path/" == */../* ]]; then
            echo "The backup's list of attachments has a path that isn't under attachments: $path" >&2
            exit 1
        fi
        if [ ! -f "$store_root/$blob" ]; then
            echo "The attachment $path is missing: $store_root/$blob isn't there." >&2
            exit 1
        fi
        mkdir -p -- "$(dirname -- "$work/waypoint/attachments/$path")"
        age -d -i "$key" -o "$work/waypoint/attachments/$path" "$store_root/$blob"
        if [ "$(sha256sum < "$work/waypoint/attachments/$path" | cut -c1-64)" != "$hash" ]; then
            echo "The attachment $path doesn't match what was backed up." >&2
            exit 1
        fi
        restored=$((restored + 1))
    done < "$list"
    rm -f -- "$list"
fi

echo "The backup restores and its database is sound."
sqlite3 "$db" "SELECT 'Items, notes, and expenses: ' || count(*) FROM items;
               SELECT 'Captures: ' || count(*) FROM captures;"
if [ "$listed" = "yes" ]; then
    echo "Attachments put back and checked: $restored"
fi
echo "Also in it:"
(cd "$work/waypoint" && ls -1)

if [ -n "${RESTORE_TO:-}" ]; then
    mv -- "$work/waypoint" "$RESTORE_TO"
    echo "Kept in $RESTORE_TO"
fi
