#!/bin/sh
# deploy/check-installed.sh
#
# Compares the root-owned files installed on this box against the copies in
# the repo, and says which are stale. None of these files update themselves
# on git pull - each needs its own 'install' command (see deploy/README.md) -
# and a stale one produces confusing runtime errors (e.g. "service not in
# the allow-list", or a console pipe that was never created).
#
# Run after every pull that touches deploy/. Read-only, safe to run as any user.

set -u
REPO_DIR="$(cd "$(dirname "$0")" && pwd)"

check() {
    repo_file="$1"
    installed_file="$2"

    if [ ! -f "$installed_file" ]; then
        echo "MISSING: $installed_file (not installed)"
    elif cmp -s "$repo_file" "$installed_file"; then
        echo "OK:      $installed_file"
    else
        echo "STALE:   $installed_file differs from $(basename "$repo_file") - reinstall it"
    fi
}

check "$REPO_DIR/palkia-rc-ctl.sh"       /usr/local/sbin/palkia-rc-ctl
check "$REPO_DIR/palkia-web.initd"       /etc/init.d/palkia-web
check "$REPO_DIR/palkia-web.confd"       /etc/conf.d/palkia-web
check "$REPO_DIR/palkia-minecraft-launch.sh" /usr/local/sbin/palkia-minecraft-launch
check "$REPO_DIR/palkia-minecraft.initd" /etc/init.d/minecraft
check "$REPO_DIR/palkia-minecraft.confd" /etc/conf.d/minecraft

echo
echo "Note: conf.d files are expected to differ if you've edited them for this"
echo "box (e.g. AdminSeed values, memory limits) - that's not a problem."
