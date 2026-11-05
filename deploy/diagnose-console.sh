#!/bin/sh
# deploy/diagnose-console.sh
#
# Gathers everything needed to debug the Minecraft console pipe in one run.
# Read-only except for one harmless "say" test line sent through the pipe.
# Run from the repo root (so it can compare installed files to deploy/):
#
#   sh deploy/diagnose-console.sh 2>&1 | tee /tmp/console-diag.txt
#
# Needs sudo for a few /proc and log reads - it will prompt once.

FIFO=/var/lib/minecraft/console.fifo
REPO_DEPLOY="$(cd "$(dirname "$0")" && pwd)"

section() { printf '\n===== %s =====\n' "$1"; }

section "installed files vs repo (STALE = reinstall that file)"
for pair in \
    "palkia-minecraft-launch.sh:/usr/local/sbin/palkia-minecraft-launch" \
    "palkia-minecraft.initd:/etc/init.d/minecraft" \
    "palkia-rc-ctl.sh:/usr/local/sbin/palkia-rc-ctl"
do
    repo_file="$REPO_DEPLOY/${pair%%:*}"
    installed="${pair#*:}"
    if [ ! -f "$installed" ]; then
        echo "MISSING: $installed"
    elif cmp -s "$repo_file" "$installed"; then
        echo "OK:      $installed"
    else
        echo "STALE:   $installed"
    fi
done
ls -l /usr/local/sbin/palkia-minecraft-launch 2>&1

section "service status"
rc-service minecraft status 2>&1

section "supervise-daemon command line (full width)"
ps -ww -o pid,args= -C supervise-daemon 2>&1

section "minecraft-user processes"
ps -ww -o pid,ppid,args= -u minecraft 2>&1

section "java stdin (fd 0) - should point at console.fifo"
for pid in $(pgrep -u minecraft java); do
    echo "java pid $pid:"
    sudo ls -l "/proc/$pid/fd/0" 2>&1
done
[ -z "$(pgrep -u minecraft java)" ] && echo "no java process running as minecraft"

section "console pipe"
ls -l "$FIFO" 2>&1
echo "-- who has it open:"
sudo fuser -v "$FIFO" 2>&1

section "palkia app user group membership (needs 'minecraft')"
id palkia 2>&1

section "last 15 lines of minecraft.err"
sudo tail -n 15 /var/log/minecraft/minecraft.err 2>&1

section "write test (3s timeout - exit 124 means the write HUNG, i.e. nothing is reading)"
timeout 3 sh -c "echo 'say diag-test' > $FIFO"
echo "write exit code: $?"
sleep 2
echo "-- last 6 lines of minecraft.log:"
tail -n 6 /var/log/minecraft/minecraft.log 2>&1

section "second write test (catches the 'only the first command works' failure)"
timeout 3 sh -c "echo 'say diag-test-2' > $FIFO"
echo "write exit code: $?"
sleep 2
tail -n 4 /var/log/minecraft/minecraft.log 2>&1

echo
echo "Done - paste everything above."
