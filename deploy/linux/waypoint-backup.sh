#!/usr/bin/env bash
# Nightly encrypted backup of the Waypoint data folder to a second location.
#
# It takes a consistent copy of the database with sqlite3, packs it with settings.json and the sign-in files, and
# encrypts the result with age to a public key. Attachments are encrypted once each into an attachments-* folder
# beside the backups and only listed in each night's archive, so they aren't stored again every night; one is removed
# once no backup kept there lists it. waypoint-restore-test.sh puts them back. The private key is kept off the
# server, so a stolen backup or a stolen server can't be read with what's on it. Settings live in
# /etc/waypoint/backup.env, which the systemd unit reads for it.
#
# It runs as the waypoint user, not root, so nothing in the data folder is ever opened with more rights than the
# server itself has. Only the backup unit is given the waypoint-backup group that can write to BACKUP_DIR, so the
# running server can't touch the backups. Nothing unencrypted is written outside the data folder: the archive is
# streamed straight into age.
set -euo pipefail

# Run by hand with the settings readable, the file is read here. Under systemd they're already in the environment.
if [ -z "${AGE_RECIPIENT:-}" ] && [ -r /etc/waypoint/backup.env ]; then
    # shellcheck source=/dev/null
    source /etc/waypoint/backup.env
fi

# Any failure from here on is said in the journal, ends with a non-zero status so systemd marks the unit failed, and
# pings the check's /fail link when BACKUP_PING_URL is set, so a missed backup reaches the phone.
fail() {
    trap - ERR
    echo "Backup FAILED: $1" >&2
    if [ -n "${BACKUP_PING_URL:-}" ]; then
        curl -fsS -m 15 --retry 3 --data-raw "$1" "${BACKUP_PING_URL%/}/fail" > /dev/null || true
    fi
    exit 1
}
trap 'fail "a step failed at line $LINENO. See journalctl -u waypoint-backup."' ERR

if [ "$(id -u)" -eq 0 ]; then
    fail "Run this as the waypoint user, as the waypoint-backup unit does, not as root."
fi

if [ -z "${AGE_RECIPIENT:-}" ]; then
    fail "Set AGE_RECIPIENT in /etc/waypoint/backup.env to your age public key."
fi
if [ -z "${BACKUP_DIR:-}" ]; then
    fail "Set BACKUP_DIR in /etc/waypoint/backup.env to the second location, such as a mounted USB drive."
fi
DATA_DIR="${DATA_DIR:-/var/lib/waypoint}"
KEEP_DAYS="${KEEP_DAYS:-30}"
# How many backups the rclone remote keeps, the newest. About two months of nightly ones.
RCLONE_KEEP="${RCLONE_KEEP:-60}"
# Kept in a folder only the backup unit's group can open, so the running server can't read the remote's sign-in.
export RCLONE_CONFIG="${RCLONE_CONFIG:-/etc/waypoint/rclone/rclone.conf}"

# Never 0 or less, which would leave none.
if ! [[ "$RCLONE_KEEP" =~ ^[1-9][0-9]*$ ]]; then
    fail "RCLONE_KEEP in /etc/waypoint/backup.env has to be a whole number of 1 or more."
fi

if [ "${BACKUP_DIR_IS_MOUNT:-yes}" = "yes" ] && ! mountpoint -q "$BACKUP_DIR"; then
    fail "$BACKUP_DIR isn't mounted, so nothing was backed up."
fi

if [ ! -d "$BACKUP_DIR" ] || [ -L "$BACKUP_DIR" ] || [ ! -w "$BACKUP_DIR" ]; then
    fail "$BACKUP_DIR isn't a folder the backup can write to. Run: sudo chgrp waypoint-backup $BACKUP_DIR && sudo chmod 2770 $BACKUP_DIR"
fi

stamp="$(date +%Y-%m-%d-%H%M)"
target="$BACKUP_DIR/waypoint-$stamp.tar.gz.age"
refs="$BACKUP_DIR/waypoint-$stamp.refs"

# The snapshot sits in the data folder, which only the service user can open, and is removed however this ends.
snapshot="$DATA_DIR/backup-snapshot.db"
attachment_list="$DATA_DIR/backup-attachments.list"
hash_cache="$DATA_DIR/backup-attachment-hashes"
cleanup() {
    rm -f -- "$snapshot" "$snapshot-wal" "$snapshot-shm" "$target.partial" "$attachment_list" "$refs.partial" \
        "$hash_cache.new" "${blob_partial:-}"
    # The list of attachments for a backup that was never finished would only keep them from being removed.
    if [ ! -f "$target" ]; then
        rm -f -- "$refs"
    fi
}
trap cleanup EXIT
rm -f -- "$snapshot" "$snapshot-wal" "$snapshot-shm"

# The online backup API copies a consistent snapshot even while the server is writing, including what's still in
# waypoint.db-wal. The snapshot is then given a plain journal, so it's one file with no log beside it.
sqlite3 "$DATA_DIR/waypoint.db" ".backup '$snapshot'"
if [ "$(sqlite3 "$snapshot" "PRAGMA journal_mode=DELETE;")" != "delete" ]; then
    fail "the copy of waypoint.db couldn't be made into a single file, so it wasn't kept."
fi
if ! sqlite3 "$snapshot" "PRAGMA integrity_check;" | grep -qx ok; then
    fail "the copy of waypoint.db didn't pass sqlite3's integrity check, so it wasn't kept."
fi

# Regular files only. A symlink put in place of one is left out rather than followed.
members=(backup-snapshot.db)
for file in settings.json bank-profiles.json outlook.token discord.token server-token.sha256; do
    if [ -f "$DATA_DIR/$file" ] && [ ! -L "$DATA_DIR/$file" ]; then
        members+=("$file")
    fi
done

# Files kept with notes, such as lecture audio the user chose to keep, are large and rarely change, so each one is
# encrypted once into BACKUP_DIR/attachments-<key>/<sha256>.age and only listed in each night's archive, rather than
# packed into it again every night. The folder is named for the age key, so files made for an earlier key aren't
# mixed in. The names show each file's SHA-256 and the sizes show roughly how big it is, but nothing of what's in it.
# Regular files only: a symlink is left out rather than followed.
store="attachments-$(printf '%s' "$AGE_RECIPIENT" | sha256sum | cut -c1-12)"
# Each file's SHA-256 by its size and time changed, so files that haven't changed aren't read again every night.
declare -A known_hashes=()
if [ -f "$hash_cache" ] && [ ! -L "$hash_cache" ]; then
    while IFS= read -r -d '' record; do
        known_hashes["${record#* }"]="${record%% *}"
    done < "$hash_cache"
fi
: > "$attachment_list"
: > "$refs.partial"
: > "$hash_cache.new"
if [ -d "$DATA_DIR/attachments" ] && [ ! -L "$DATA_DIR/attachments" ]; then
    mkdir -p -- "$BACKUP_DIR/$store"
    while IFS= read -r -d '' found; do
        size="${found%% *}"
        rest="${found#* }"
        changed="${rest%% *}"
        path="${rest#* }"
        hash="${known_hashes["$size $changed $path"]:-}"
        if ! [[ "$hash" =~ ^[0-9a-f]{64}$ ]]; then
            hash="$(sha256sum < "$DATA_DIR/attachments/$path" | cut -c1-64)"
        fi
        blob="$store/$hash.age"
        if [ ! -f "$BACKUP_DIR/$blob" ]; then
            blob_partial="$BACKUP_DIR/$blob.partial"
            age -r "$AGE_RECIPIENT" -o "$BACKUP_DIR/$blob.partial" < "$DATA_DIR/attachments/$path"
            mv -- "$BACKUP_DIR/$blob.partial" "$BACKUP_DIR/$blob"
            blob_partial=""
        fi
        printf '%s %s\0' "$blob" "$path" >> "$attachment_list"
        printf '%s\n' "$blob" >> "$refs.partial"
        printf '%s %s %s %s\0' "$hash" "$size" "$changed" "$path" >> "$hash_cache.new"
    done < <(find "$DATA_DIR/attachments" -type f ! -name '*.part' -printf '%s %T@ %P\0')
fi
mv -- "$hash_cache.new" "$hash_cache"
members+=(backup-attachments.list)
# Written before the archive, so the attachments it lists are never removed while it's being made.
mv -- "$refs.partial" "$refs"

# Everything goes under waypoint/ in the archive, with the snapshot named waypoint.db, as before, and the list of
# attachments as attachments.list: one "<file in BACKUP_DIR> <path under attachments/>" per entry, each ending in NUL.
tar -C "$DATA_DIR" --create --gzip --file=- \
    --transform='s,^backup-snapshot\.db$,waypoint.db,S;s,^backup-attachments\.list$,attachments.list,S;s,^,waypoint/,S' \
    -- "${members[@]}" \
    | age -r "$AGE_RECIPIENT" -o "$target.partial"
mv -- "$target.partial" "$target"

# An optional copy somewhere else, such as an rclone remote. It only ever sees encrypted files: attachments it doesn't
# have yet, then the list of the ones this backup uses, then the backup itself.
if [ -n "${RCLONE_REMOTE:-}" ]; then
    if [ ! -r "$RCLONE_CONFIG" ]; then
        fail "the backup was made in $BACKUP_DIR, but rclone's settings aren't in $RCLONE_CONFIG. See RCLONE_REMOTE in /etc/waypoint/backup.env."
    fi
    case "$RCLONE_REMOTE" in
        *:) remote_prefix="$RCLONE_REMOTE" ;;
        *) remote_prefix="${RCLONE_REMOTE%/}/" ;;
    esac
    if [ -d "$BACKUP_DIR/$store" ] && ! rclone copy --ignore-existing --exclude '*.partial' "$BACKUP_DIR/$store" "$remote_prefix$store"; then
        fail "the backup was made in $BACKUP_DIR, but copying its attachments to $RCLONE_REMOTE didn't work."
    fi
    if ! rclone copy "$refs" "$RCLONE_REMOTE" || ! rclone copy "$target" "$RCLONE_REMOTE"; then
        fail "the backup was made in $BACKUP_DIR, but copying it to $RCLONE_REMOTE didn't work."
    fi

    # The newest RCLONE_KEEP are kept there, and older ones removed with their lists. Names sort by date.
    if ! listing="$(rclone lsf --files-only --max-depth 1 --include 'waypoint-*.tar.gz.age' --include 'waypoint-*.refs' "$RCLONE_REMOTE")"; then
        fail "the backup was copied to $RCLONE_REMOTE, but its older backups couldn't be listed to remove."
    fi
    kept_remote="$(printf '%s\n' "$listing" | grep '\.tar\.gz\.age$' | sort | tail -n "$RCLONE_KEEP" || true)"
    old_remote="$(printf '%s\n' "$listing" | grep '\.tar\.gz\.age$' | sort | head -n -"$RCLONE_KEEP" || true)"
    while read -r old; do
        if [ -n "$old" ] && ! rclone deletefile "$remote_prefix$old"; then
            fail "the backup was copied to $RCLONE_REMOTE, but $old couldn't be removed from there."
        fi
    done <<< "$old_remote"
    while read -r old; do
        if [ -n "$old" ] && ! grep -qxF -- "${old%.refs}.tar.gz.age" <<< "$kept_remote" && ! rclone deletefile "$remote_prefix$old"; then
            fail "the backup was copied to $RCLONE_REMOTE, but $old couldn't be removed from there."
        fi
    done <<< "$(printf '%s\n' "$listing" | grep '\.refs$' || true)"

    # Then the attachments none of the backups kept there still list. If any list can't be read, none are removed.
    if ! wanted_remote="$(rclone cat --max-depth 1 --include 'waypoint-*.refs' "$RCLONE_REMOTE")" ||
        ! stored_remote="$(rclone lsf -R --files-only --include 'attachments-*/*' "$RCLONE_REMOTE")"; then
        fail "the backup was copied to $RCLONE_REMOTE, but which of its attachments are still needed couldn't be checked."
    fi
    while read -r stored; do
        if [ -n "$stored" ] && ! grep -qxF -- "$stored" <<< "$wanted_remote" && ! rclone deletefile "$remote_prefix$stored"; then
            fail "the backup was copied to $RCLONE_REMOTE, but $stored couldn't be removed from there."
        fi
    done <<< "$stored_remote"
fi

# Backups older than KEEP_DAYS go, with their lists, and then the attachments no backup left here lists. The lists
# are all read first, so a list that can't be read stops this before anything is removed.
find "$BACKUP_DIR" -maxdepth 1 -type f -name 'waypoint-*.tar.gz.age' -mtime +"$KEEP_DAYS" -delete
shopt -s nullglob
for list in "$BACKUP_DIR"/waypoint-*.refs; do
    if [ ! -f "${list%.refs}.tar.gz.age" ]; then
        rm -f -- "$list"
    fi
done
lists=("$BACKUP_DIR"/waypoint-*.refs)
wanted=""
if [ "${#lists[@]}" -gt 0 ]; then
    wanted="$(cat -- "${lists[@]}")"
fi
declare -A wanted_here=()
while read -r blob; do
    if [ -n "$blob" ]; then
        wanted_here["$blob"]=1
    fi
done <<< "$wanted"
for folder in "$BACKUP_DIR"/attachments-*/; do
    folder="${folder%/}"
    for file in "$folder"/*; do
        if [ -f "$file" ] && [ -z "${wanted_here["${folder##*/}/${file##*/}"]:-}" ]; then
            rm -f -- "$file"
        fi
    done
    rmdir -- "$folder" 2> /dev/null || true
done
shopt -u nullglob

if [ -n "${BACKUP_PING_URL:-}" ]; then
    curl -fsS -m 15 --retry 3 "$BACKUP_PING_URL" > /dev/null || true
fi

echo "Backed up to $target"
