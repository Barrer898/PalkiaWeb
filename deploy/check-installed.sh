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

# --- FreeToken AI profiles (README section 10) -------------------------------
# The launcher and the shared init script are needed once; the rest is per
# profile. A profile you chose not to install shows MISSING - that's fine as
# long as it's not one you intend to start.
echo
echo "FreeToken:"
check "$REPO_DIR/palkia-ft-launch.sh"    /usr/local/sbin/palkia-ft-launch
check "$REPO_DIR/palkia-ft.initd"        /etc/init.d/palkia-ft

for p in fast small medium big max; do
    check "$REPO_DIR/freetoken/profiles/$p.conf" "/etc/palkia/freetoken/palkia-ft-$p.conf"
    check "$REPO_DIR/freetoken/confd/$p.confd"   "/etc/conf.d/palkia-ft-$p"
    if [ -L "/etc/init.d/palkia-ft-$p" ]; then
        echo "OK:      /etc/init.d/palkia-ft-$p (symlink)"
    else
        echo "MISSING: /etc/init.d/palkia-ft-$p (ln -s palkia-ft /etc/init.d/palkia-ft-$p)"
    fi
done

if [ -f /etc/palkia/freetoken/keys.env ]; then
    echo "OK:      /etc/palkia/freetoken/keys.env (contents not compared)"
else
    echo "MISSING: /etc/palkia/freetoken/keys.env"
fi

echo
echo "Note: conf.d files are expected to differ if you've edited them for this"
echo "box (e.g. AdminSeed values, memory limits)
echo "and so are the freetoken profile .conf files once you have tuned them" - that's not a problem."
