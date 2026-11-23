#!/usr/bin/env bash
# Sets up the Waypoint server on Ubuntu Server. Run as root from the folder that holds this script, with the
# published server beside it:
#
#   sudo ./install.sh /path/to/published/server
#
# It's safe to run again after publishing a new version. Each version goes in a folder of its own under
# /opt/waypoint/releases, and /opt/waypoint/current points at the one in use. waypoint.db is copied to
# /var/backups/waypoint before the new version starts, and if it doesn't answer /health, current is pointed back at
# the previous version, that copy is put back, and the previous version is started again with the unit files it ran
# with. The same happens if the install stops partway, from an error, Ctrl+C, or a dropped SSH session, so the server
# is never left stopped. settings.json and /etc/waypoint are never replaced. Signing in to Tailscale and to Claude
# are left to you, since both open a browser link.
#
# Security updates install themselves, but the machine never reboots on its own unless you ask for it:
#
#   sudo AUTO_REBOOT=yes ./install.sh /path/to/published/server
#
# With full-disk encryption and a passphrase, a reboot waits at the unlock prompt until someone types it, so
# reminders stop. Turn automatic reboots on only once the disk unlocks by itself (TPM) or remotely (dropbear).
# See "Full-disk encryption and reboots" in the README.
set -euo pipefail

if [ "$(id -u)" -ne 0 ]; then
    echo "Run this with sudo." >&2
    exit 1
fi

published="${1:?Give the folder from dotnet publish, which holds waypoint-server.dll}"
here="$(cd "$(dirname "$0")" && pwd)"
if [ ! -f "$published/waypoint-server.dll" ]; then
    echo "$published doesn't hold waypoint-server.dll." >&2
    exit 1
fi

echo "== Packages: .NET 10 runtime, SQLite, age for backups, text recognition, unattended upgrades"
apt-get update

# True when apt has a version of the package it could install.
can_install() {
    apt-cache policy "$1" 2> /dev/null | grep -q 'Candidate: [0-9]'
}

# Ubuntu 24.04's own archive may not carry .NET 10. The .NET backports PPA, kept by Ubuntu's .NET team, is tried
# first, then Microsoft's feed for this release.
if ! can_install aspnetcore-runtime-10.0; then
    echo "== aspnetcore-runtime-10.0 isn't in this Ubuntu's archive, so adding ppa:dotnet/backports"
    apt-get install -y software-properties-common
    add-apt-repository -y ppa:dotnet/backports
    apt-get update
fi
if ! can_install aspnetcore-runtime-10.0; then
    release="$(. /etc/os-release && echo "$VERSION_ID")"
    echo "== Still not there, so adding Microsoft's feed for Ubuntu $release"
    add-apt-repository -y --remove ppa:dotnet/backports || true
    feed="$(mktemp)"
    curl -fsSL "https://packages.microsoft.com/config/ubuntu/$release/packages-microsoft-prod.deb" -o "$feed"
    dpkg -i "$feed"
    rm -f "$feed"
    apt-get update
fi
if ! can_install aspnetcore-runtime-10.0; then
    echo "aspnetcore-runtime-10.0 couldn't be found in Ubuntu's archive, ppa:dotnet/backports, or Microsoft's feed." >&2
    echo "Install the ASP.NET Core 10 runtime by hand, then run this again:" >&2
    echo "  https://learn.microsoft.com/dotnet/core/install/linux-ubuntu" >&2
    exit 1
fi

# tesseract reads text in pictures sent from Discord, and pdftoppm from poppler-utils draws scanned PDF pages for it.
apt-get install -y aspnetcore-runtime-10.0 sqlite3 age curl tesseract-ocr tesseract-ocr-eng poppler-utils unattended-upgrades

echo "== Time zone: $(timedatectl show -p Timezone --value)"
echo "   If that isn't yours, run: sudo timedatectl set-timezone <Area/City>"

echo "== Security updates install themselves"
cat > /etc/apt/apt.conf.d/20auto-upgrades <<'EOF'
APT::Periodic::Update-Package-Lists "1";
APT::Periodic::Unattended-Upgrade "1";
EOF
# Off unless asked for, since an encrypted disk with a passphrase would sit at the unlock prompt after the reboot.
encrypted=no
if lsblk -n -o TYPE 2> /dev/null | grep -qx crypt; then
    encrypted=yes
fi
if [ "${AUTO_REBOOT:-no}" = "yes" ]; then
    echo "   It reboots at 4:30 AM when an update needs it."
    if [ "$encrypted" = "yes" ]; then
        echo "   The disk is encrypted. Make sure it unlocks without a passphrase (TPM or dropbear), or a reboot stops reminders." >&2
    fi
    cat > /etc/apt/apt.conf.d/52waypoint-upgrades <<'EOF'
Unattended-Upgrade::Automatic-Reboot "true";
Unattended-Upgrade::Automatic-Reboot-Time "04:30";
EOF
else
    echo "   It never reboots on its own. When /var/run/reboot-required exists, reboot by hand while you can unlock it."
    echo "   To allow reboots at 4:30 AM, run this again with AUTO_REBOOT=yes once the disk can unlock by itself."
    cat > /etc/apt/apt.conf.d/52waypoint-upgrades <<'EOF'
Unattended-Upgrade::Automatic-Reboot "false";
EOF
fi

echo "== Service user"
if ! id waypoint > /dev/null 2>&1; then
    # A real home folder, because the claude CLI keeps its sign-in there.
    useradd --system --create-home --home-dir /home/waypoint --shell /bin/bash waypoint
fi
chmod 700 /home/waypoint
# Only the backup unit runs with this group, and it's the one that can write to BACKUP_DIR. The waypoint user is
# never added to it, so the running server can't change or delete backups.
if ! getent group waypoint-backup > /dev/null; then
    groupadd --system waypoint-backup
fi

echo "== Program files"
# The new version goes in a folder of its own beside the running one, so nothing in use is copied over.
releases=/opt/waypoint/releases
stamp="$(date -u +%Y%m%d-%H%M%S)"
release="$releases/$stamp"
if [ -e "$release" ]; then
    echo "$release is already there. Wait a second and run this again." >&2
    exit 1
fi
data=/var/lib/waypoint
db="$data/waypoint.db"
saved=/var/backups/waypoint
current=/opt/waypoint/current
units="waypoint.service waypoint-backup.service waypoint-backup.timer"

# What the steps from here have changed, so a failure, Ctrl+C, or a dropped SSH session at any point puts the server
# back the way it was and running, rather than leaving it stopped or on a version that never answered.
release_made=no
units_saved=""
stopped=no
started_new=no
previous=""
backup=""
finished=no
# Releases already used current before this install, so the unit files from before still fit them.
linked_before=no
if [ -L "$current" ]; then
    linked_before=yes
fi

# Points current at a release in one step, so it never names a half-made link.
point_at() {
    ln -sfn "$1" "$current.next"
    mv -Tf "$current.next" "$current"
}

# Puts back the unit files as they were before this install, and removes the ones it added.
restore_units() {
    if [ -z "$units_saved" ] || [ "$linked_before" != "yes" ]; then
        return 0
    fi
    local unit
    for unit in $units; do
        if [ -f "$units_saved/$unit" ]; then
            install -m 644 "$units_saved/$unit" "/etc/systemd/system/$unit"
        else
            rm -f -- "/etc/systemd/system/$unit"
        fi
    done
    systemctl daemon-reload || true
}

# Goes back to the release from before, with its unit files, and the copy of waypoint.db from just before the new
# version started, since the new version may have changed the database for itself. Starts it again if it was stopped.
go_back() {
    if [ "$started_new" = "yes" ]; then
        systemctl stop waypoint.service || true
    fi
    restore_units
    if [ -n "$previous" ] && [ -d "$previous" ]; then
        point_at "$previous"
        if [ "$started_new" = "yes" ] && [ -n "$backup" ]; then
            install -m 600 -o waypoint -g waypoint "$backup" "$data/upgrade-restore.db"
            runuser -u waypoint -- sqlite3 "$db" ".restore '$data/upgrade-restore.db'"
            rm -f -- "$data/upgrade-restore.db"
            echo "   waypoint.db put back from $backup" >&2
        fi
    fi
    rm -f -- "$data/upgrade-backup.db"
    if [ "$release_made" = "yes" ] && [ "$(readlink -f "$current" 2> /dev/null)" != "$release" ]; then
        rm -rf -- "$release"
    fi
    if [ "$stopped" = "yes" ] && [ -L "$current" ]; then
        systemctl start waypoint.service || true
    fi
}

on_exit() {
    local status=$?
    trap - EXIT INT TERM HUP
    # With the SSH session gone, writing to it can fail, and that mustn't stop the steps below.
    trap '' PIPE
    set +e
    if [ "$finished" != "yes" ]; then
        if [ "$started_new" = "yes" ] && systemctl is-active --quiet waypoint.service && healthy_now; then
            echo "The install stopped partway, but the new version is running and answering, so it was left in." >&2
        elif [ "$release_made" = "yes" ] || [ -n "$units_saved" ] || [ "$stopped" = "yes" ]; then
            echo "The install stopped partway, so the server is being put back the way it was." >&2
            go_back
        fi
    fi
    if [ -n "$units_saved" ]; then
        rm -rf -- "$units_saved"
    fi
    exit "$status"
}
trap on_exit EXIT
trap 'exit 130' INT
trap 'exit 143' TERM
trap 'exit 129' HUP

# The address the server listens on, for checking /health.
health_url() {
    local listen url
    listen="$(sed -n 's/^Waypoint__Listen=//p' /etc/waypoint/waypoint.env 2> /dev/null | tail -n 1)"
    listen="${listen:-http://127.0.0.1:5280}"
    url="${listen%%;*}"
    url="$(printf '%s' "$url" | sed -E 's#//(0\.0\.0\.0|\[::\]|\*|\+)(:|/|$)#//127.0.0.1\2#')"
    printf '%s/health' "${url%/}"
}

healthy_now() {
    curl -fsS -m 3 "$(health_url)" > /dev/null 2>&1
}

# True once the server answers /health on the address it listens on, within about a minute.
healthy() {
    for _ in $(seq 1 30); do
        if systemctl is-active --quiet waypoint.service && healthy_now; then
            return 0
        fi
        sleep 2
    done
    return 1
}

release_made=yes
install -d -m 755 /opt/waypoint /opt/waypoint/deploy "$releases" "$release"
cp -r "$published"/. "$release"/
install -m 755 "$here/waypoint-backup.sh" /opt/waypoint/deploy/waypoint-backup.sh
chown -R root:root /opt/waypoint

echo "== Settings, kept from any earlier install"
install -d -m 750 -o root -g waypoint /etc/waypoint
if [ ! -f /etc/waypoint/waypoint.env ]; then
    install -m 640 -o root -g waypoint "$here/waypoint.env.example" /etc/waypoint/waypoint.env
fi
if [ ! -f /etc/waypoint/backup.env ]; then
    install -m 600 -o root -g root "$here/backup.env.example" /etc/waypoint/backup.env
fi
# rclone's settings for an offsite copy: only the backup unit's group can open this, not the running server.
install -d -m 2770 -o root -g waypoint-backup /etc/waypoint/rclone
install -d -m 700 -o waypoint -g waypoint /var/lib/waypoint

echo "== systemd"
# Kept until the new version answers, so going back puts back the unit files the previous version ran with.
units_saved="$(mktemp -d)"
for unit in $units; do
    if [ -f "/etc/systemd/system/$unit" ]; then
        cp -p -- "/etc/systemd/system/$unit" "$units_saved/$unit"
    fi
done
install -m 644 "$here/waypoint.service" /etc/systemd/system/waypoint.service
install -m 644 "$here/waypoint-backup.service" /etc/systemd/system/waypoint-backup.service
install -m 644 "$here/waypoint-backup.timer" /etc/systemd/system/waypoint-backup.timer
systemctl daemon-reload
systemctl enable waypoint.service

echo "== Switching to the new version"
# Stopped first, so the copy of waypoint.db holds everything written, and nothing is written after it.
stopped=yes
systemctl stop waypoint.service || true

if [ -L "$current" ]; then
    previous="$(readlink -f "$current")"
elif [ -f /opt/waypoint/waypoint-server.dll ]; then
    # An install from before releases had their own folders: its files become the previous release.
    previous="$releases/$stamp-before"
    install -d -m 755 "$previous"
    find /opt/waypoint -mindepth 1 -maxdepth 1 ! -name releases ! -name deploy ! -name current ! -name current.next \
        -exec mv -t "$previous" -- {} +
fi

if [ -f "$db" ]; then
    install -d -m 700 -o root -g root "$saved"
    # Made by the service user inside the data folder, so sqlite3 never leaves root-owned files beside waypoint.db.
    rm -f -- "$data/upgrade-backup.db"
    if ! runuser -u waypoint -- sqlite3 "$db" ".backup '$data/upgrade-backup.db'"; then
        echo "waypoint.db couldn't be copied, so the new version wasn't started. The one before is started again." >&2
        finished=yes
        go_back
        exit 1
    fi
    mv -- "$data/upgrade-backup.db" "$saved/waypoint-$stamp.db"
    backup="$saved/waypoint-$stamp.db"
    chown root:root "$backup"
    chmod 600 "$backup"
    echo "   waypoint.db copied to $backup"
    # The last five copies are kept.
    find "$saved" -maxdepth 1 -type f -name 'waypoint-*.db' -printf '%f\n' | sort | head -n -5 \
        | while read -r old; do rm -f -- "$saved/$old"; done
fi

point_at "$release"
started_new=yes
systemctl start waypoint.service || true
if healthy; then
    finished=yes
    echo "   Running $stamp."
    # The new release and the one before it are kept, so the next install can go back to this one.
    for old in "$releases"/*; do
        if [ "$old" != "$release" ] && [ "$old" != "$previous" ]; then
            rm -rf -- "$old"
        fi
    done
elif [ -n "$previous" ] && [ -d "$previous" ]; then
    echo "The new version didn't answer /health, so the previous one is going back in. See: journalctl -u waypoint -n 50" >&2
    finished=yes
    go_back
    if healthy; then
        echo "   The previous version is running again. Nothing else was changed by this install." >&2
    else
        echo "   The previous version didn't answer /health either. See: journalctl -u waypoint -n 50" >&2
    fi
    exit 1
else
    # Left in, so there's something to look at in the journal.
    finished=yes
    echo "The server didn't answer /health, and there's no earlier version to go back to. See: journalctl -u waypoint -n 50" >&2
    exit 1
fi

# The nightly backup is turned on only once it has somewhere to go, so it never fails every night unnoticed.
backup_ready() {
    (
        set +u
        # shellcheck source=/dev/null
        . /etc/waypoint/backup.env
        if [ -z "${AGE_RECIPIENT:-}" ] || [ -z "${BACKUP_DIR:-}" ] || [ ! -d "$BACKUP_DIR" ]; then
            exit 1
        fi
        if [ "${BACKUP_DIR_IS_MOUNT:-yes}" = "yes" ]; then
            mountpoint -q "$BACKUP_DIR"
        fi
    )
}
if backup_ready; then
    echo "== Nightly backup turned on"
    systemctl enable --now waypoint-backup.timer
    backup_note="The nightly backup is on. Test one now with step 4's last line."
else
    echo "== Nightly backup left off: AGE_RECIPIENT or BACKUP_DIR isn't set in /etc/waypoint/backup.env, or BACKUP_DIR isn't mounted yet"
    systemctl disable --now waypoint-backup.timer 2> /dev/null || true
    backup_note="The nightly backup is OFF until you do step 4."
fi

cat <<EOF

Installed. $backup_note

Still to do by hand, in this order:

  1. Tailscale, if it isn't already:
       curl -fsSL https://tailscale.com/install.sh | sh && sudo tailscale up
     Then put the address from "tailscale ip -4" in /etc/waypoint/waypoint.env as
       Waypoint__Listen=http://<that address>:5280
     It has to be the IP address. A name such as the MagicDNS one is refused, since it would listen on every network.
     and run: sudo systemctl restart waypoint
  2. Claude Code, signed in as the service user:
       sudo -u waypoint -H bash -lc 'curl -fsSL https://claude.ai/install.sh | bash'
       sudo -u waypoint -H bash -lc 'claude'      (sign in, then quit)
     If the sign-in doesn't last unattended, see "Claude sign-in" in the README for claude setup-token.
  3. The pairing token, written once to a file only the service user can read:
       sudo cat /var/lib/waypoint/pairing-token.txt
     and once the PC is paired: sudo rm /var/lib/waypoint/pairing-token.txt
  4. Backups: mount the second disk, set AGE_RECIPIENT and BACKUP_DIR in /etc/waypoint/backup.env, and give the
     backup group the folder:
       sudo chgrp waypoint-backup <BACKUP_DIR> && sudo chmod 2770 <BACKUP_DIR>
     Then run this script again, which turns the nightly timer on, and test one:
       sudo systemctl start waypoint-backup && journalctl -u waypoint-backup -n 5 && ls -l <BACKUP_DIR>
  5. The heartbeat: put a healthchecks.io ping link in Waypoint__HeartbeatUrl and restart.

See "Running it on a server" in the README for moving waypoint.db over and switching the PC. Set the PC to
server mode before the server's Discord goes live, so the two never send at once.
EOF
