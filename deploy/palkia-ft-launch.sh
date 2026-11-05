#!/bin/sh
# palkia-ft-launch.sh
#
# Install as /usr/local/sbin/palkia-ft-launch (root:root, 0755).
# Started by /etc/init.d/palkia-ft-<profile> under supervise-daemon, as the
# unprivileged 'freetoken' user.
#
# Builds the `ft serve` command line for ONE profile from the root-owned
# config /etc/palkia/freetoken/palkia-ft-<profile>.conf and execs it. The
# command line is assembled with `set --` (positional parameters), never
# string-concatenated and eval'd, and every value is validated first.
#
# Why a launcher instead of putting flags in the init script: the flags are
# a per-profile list that changes while tuning (context, memory ratio, MoE
# strategy), and a conf file keeps that out of the init script.
#
# usage: palkia-ft-launch <fast|small|medium|big|max>

set -eu

profile="${1:?profile required (fast|small|medium|big|max)}"

case "${profile}" in
    fast|small|medium|big|max) ;;
    *) echo "refused: '${profile}' is not a Palkia profile" >&2; exit 3 ;;
esac

if [ "$(id -u)" -eq 0 ]; then
    echo "refused: do not run model servers as root" >&2
    exit 3
fi

# Fixed paths; not overridable from the environment.
CONF_DIR="/etc/palkia/freetoken"
conf="${CONF_DIR}/palkia-ft-${profile}.conf"
keys="${CONF_DIR}/keys.env"

[ -f "${conf}" ] || { echo "missing ${conf}" >&2; exit 4; }
[ -f "${keys}" ] || { echo "missing ${keys}" >&2; exit 4; }

# Defaults a profile conf may override.
FT_BIN="/opt/freetoken/.venv/bin/ft"
FT_GPU="0"
FT_MOE_STRATEGY=""
FT_TEXT_ONLY="0"
FT_EXTRA_ARGS=""

# Both files are root-owned and trusted; sourcing them is how they are read.
# shellcheck disable=SC1090
. "${conf}"
# shellcheck disable=SC1090
. "${keys}"

need_int() {
    eval "_v=\${$1:-}"
    case "${_v}" in
        ''|*[!0-9]*)
            echo "palkia-ft-launch: $1 must be a positive integer in ${conf} (got '${_v}')" >&2
            exit 4
            ;;
    esac
}

for var in FT_PORT FT_CONTEXT FT_PREFILL FT_MAX_OUTPUT FT_MAX_RUNNING; do
    need_int "${var}"
done

: "${FT_MODEL:?FT_MODEL required in ${conf}}"
: "${FT_SERVED_NAME:?FT_SERVED_NAME required in ${conf}}"
: "${FT_MEMORY_RATIO:?FT_MEMORY_RATIO required in ${conf}}"

case "${FT_MEMORY_RATIO}" in
    0.[0-9]|0.[0-9][0-9]|1|1.0) ;;
    *) echo "palkia-ft-launch: FT_MEMORY_RATIO must look like 0.85 (got '${FT_MEMORY_RATIO}')" >&2; exit 4 ;;
esac

case "${FT_MOE_STRATEGY}" in
    ''|fused|offload|cpu|hybrid) ;;
    *) echo "palkia-ft-launch: FT_MOE_STRATEGY must be empty/fused/offload/cpu/hybrid" >&2; exit 4 ;;
esac

case "${FT_GPU}" in
    ''|*[!0-9]*) echo "palkia-ft-launch: FT_GPU must be an nvidia-smi index" >&2; exit 4 ;;
esac

[ -d "${FT_MODEL}" ] || { echo "palkia-ft-launch: FT_MODEL '${FT_MODEL}' is not a directory" >&2; exit 4; }
[ -x "${FT_BIN}" ] || { echo "palkia-ft-launch: ${FT_BIN} not found/executable" >&2; exit 4; }

# The loopback shared secret (ft's own --api-key). Passed via the
# FREETOKEN_API_KEY environment variable, which ft reads as the default for
# --api-key, so it never appears on a command line (visible in `ps`).
# Fails closed: a profile without a key does not start, because the
# gateway always sends one (see FreeToken:UpstreamApiKeys in the app).
upper="$(printf '%s' "${profile}" | tr 'a-z' 'A-Z')"
eval "key=\${FT_UPSTREAM_KEY_${upper}:-}"
if [ -z "${key}" ]; then
    echo "palkia-ft-launch: FT_UPSTREAM_KEY_${upper} is empty in ${keys}; refusing to start unauthenticated" >&2
    exit 4
fi
FREETOKEN_API_KEY="${key}"
export FREETOKEN_API_KEY
unset key FT_UPSTREAM_KEY_FAST FT_UPSTREAM_KEY_SMALL FT_UPSTREAM_KEY_MEDIUM FT_UPSTREAM_KEY_BIG FT_UPSTREAM_KEY_MAX

# Runtime environment. HOME holds the JIT kernel cache (first start compiles
# CUDA kernels and needs nvcc from a CUDA 13 toolkit on PATH; /opt/cuda/bin
# is where Gentoo puts it). Offline: serving never needs the network, and
# this stops an HF repo id typo from silently downloading 20GB.
HOME="/var/lib/freetoken"
PATH="/opt/cuda/bin:/usr/local/bin:/usr/bin:/bin"
HF_HUB_OFFLINE=1
export HOME PATH HF_HUB_OFFLINE

# --host is NOT configurable: loopback only (deployment rules s8). The
# authenticated gateway in Palkia is the only thing that talks to this.
set -- "${FT_BIN}" serve \
    --model "${FT_MODEL}" \
    --served-model-name "${FT_SERVED_NAME}" \
    --host 127.0.0.1 \
    --port "${FT_PORT}" \
    --gpu "${FT_GPU}" \
    --max-seq-len-override "${FT_CONTEXT}" \
    --max-prefill-length "${FT_PREFILL}" \
    --max-output-tokens "${FT_MAX_OUTPUT}" \
    --max-running-requests "${FT_MAX_RUNNING}" \
    --memory-ratio "${FT_MEMORY_RATIO}"

[ -n "${FT_MOE_STRATEGY}" ] && set -- "$@" --moe-strategy "${FT_MOE_STRATEGY}"
[ "${FT_TEXT_ONLY}" = "1" ] && set -- "$@" --text-model-only

if [ -n "${FT_EXTRA_ARGS}" ]; then
    # Intentional word-splitting of a root-owned value, with globbing off.
    set -f
    # shellcheck disable=SC2086
    set -- "$@" ${FT_EXTRA_ARGS}
    set +f
fi

echo "palkia-ft-launch: profile=${profile} $*" >&2
exec "$@"
