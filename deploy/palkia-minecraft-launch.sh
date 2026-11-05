#!/bin/sh
# palkia-minecraft-launch.sh
#
# Install as /usr/local/sbin/palkia-minecraft-launch (root:root, 0755).
# Started by /etc/init.d/minecraft under supervise-daemon, as the minecraft user.
#
# Why this exists: supervise-daemon's --stdin did not attach the console pipe
# on this box (the java process's fd 0 ended up as /dev/null), so the stdin
# plumbing is done here instead, where it can't be ignored.
#
# 'exec ... 0<>FIFO' opens the pipe read-WRITE as the server's stdin. That
# solves both classic FIFO problems at once:
#   - opening a FIFO read-write never blocks on Linux (a plain read-only
#     open would hang the service start until someone wrote to it)
#   - java itself holds a write end, so the pipe never reports end-of-file
#     when the web app (or you) close their writer after sending a command.
#     Without this, the first command works and the console then dies.
#
# usage: palkia-minecraft-launch <server dir> <jar name> <heap, e.g. 5G> <fifo path>

set -eu

dir="${1:?server dir required}"
jar="${2:?jar name required}"
heap="${3:?heap size required}"
fifo="${4:?fifo path required}"

cd "$dir"

exec /usr/bin/java -Xms1G -Xmx"$heap" -XX:+UseG1GC -jar "$dir/$jar" --nogui 0<>"$fifo"
