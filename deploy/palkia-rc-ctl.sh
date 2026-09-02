#!/bin/sh
# palkia-rc-ctl.sh
#
# The ONLY thing the palkia app user is allowed to run as root. Deliberately
# tiny and paranoid: hardcoded allow-list of service names and actions, no
# shell interpolation of the arguments into a command string, no PATH trust.
#
# Install:
#   install -o root -g root -m 0755 palkia-rc-ctl.sh /usr/local/sbin/palkia-rc-ctl
#
# Then add to /etc/sudoers (via visudo, NOT by editing the file directly):
#   palkia ALL=(root) NOPASSWD: /usr/local/sbin/palkia-rc-ctl
#
# Usage:
#   palkia-rc-ctl <service> <start|stop|restart|status>

set -eu
PATH=/sbin:/usr/sbin:/bin:/usr/bin

# Keep this list in lockstep with Palkia.Core.Services.ManagedServiceCatalog.
# Anything not listed here is refused, full stop - the app's own catalog is
# not trusted as the source of truth for privilege, this file is.
ALLOWED_SERVICES="samba nfs nfsclient"
ALLOWED_ACTIONS="start stop restart status"

usage() {
    echo "usage: $(basename "$0") <service> <start|stop|restart|status>" >&2
    exit 2
}

[ $# -eq 2 ] || usage

service="$1"
action="$2"

is_allowed() {
    needle="$1"
    shift
    for candidate in "$@"; do
        [ "$candidate" = "$needle" ] && return 0
    done
    return 1
}

if ! is_allowed "$service" $ALLOWED_SERVICES; then
    echo "refused: service '$service' is not in the allow-list" >&2
    exit 3
fi

if ! is_allowed "$action" $ALLOWED_ACTIONS; then
    echo "refused: action '$action' is not in the allow-list" >&2
    exit 3
fi

exec /sbin/rc-service "$service" "$action"
