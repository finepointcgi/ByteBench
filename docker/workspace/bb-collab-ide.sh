#!/bin/sh
# Starts one collaborator's OpenVSCode server, as that collaborator's own OS user (bbc1..bbc10; ByteBench runs this through docker exec -u).
#   $1  port on 127.0.0.1 (13401..13410: the proxy reaches it through the container, nothing is published)
#   $2  folder to open (the repository checkout under /workspace)
#   stdin: the connection token, written to ~/.bb-token (mode 600) so it is readable by this user only
# Idempotent: when the server already answers on its port nothing is started again. Exits 0 once the server answers HTTP.
set -eu
port="$1"
dir="${2:-/workspace}"

HOME=$(getent passwd "$(id -u)" | cut -d: -f6)
export HOME
cd "$HOME"
(umask 077; cat > "$HOME/.bb-token")
umask 002 # files stay group-writable: the developer and the other collaborators edit the same checkout through the group

base="${BB_IDE_BASE_PATH:-}"
health() {
    # curl prints 000 itself when it cannot connect (and exits non-zero): do not append a second one.
    code=$(curl -s -o /dev/null -m 2 -w '%{http_code}' "http://127.0.0.1:${port}${base}/version" 2>/dev/null) || code=000
    [ "$code" -ge 100 ] 2>/dev/null && [ "$code" -lt 500 ]
}

if ! health; then
    args="--host 127.0.0.1 --port ${port} --connection-token-file ${HOME}/.bb-token --telemetry-level off --accept-server-license-terms"
    if [ -n "$base" ]; then args="$args --server-base-path ${base}"; fi
    if [ -d "$dir" ]; then cd "$dir"; args="$args --default-folder ${dir}"; fi
    # shellcheck disable=SC2086
    setsid nohup "${OPENVSCODE_SERVER_ROOT}/bin/openvscode-server" $args > "${HOME}/.bb-ide.log" 2>&1 < /dev/null &
fi

i=0
until health; do
    i=$((i + 1))
    if [ "$i" -gt 250 ]; then
        echo "bb-collab-ide: the IDE did not come up on port ${port}" >&2
        tail -n 20 "${HOME}/.bb-ide.log" >&2 2>/dev/null || true
        exit 1
    fi
    sleep 0.2
done
