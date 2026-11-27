#!/bin/sh
# Waits until ByteBench has locked the container's network down and checked the repository out (it creates /tmp/.bb-ready
# last, after writing the connection token to /tmp/.bb-token), then runs OpenVSCode Server in the foreground.
# Collaborators' IDE servers are not started here: ByteBench starts each one, as that collaborator's own OS user, with bb-collab-ide.
# Nothing user-controlled runs before that, so there is no window with unrestricted network access.
set -eu
umask 002 # files stay group-writable, so the AI assistant's user and the collaborators' users (same group) and the developer can edit each other's files
chmod 700 "$HOME" 2>/dev/null || true # the developer's home (secrets, git credential, IDE token) is private; collaborators have homes of their own

# A restart (docker restart) recreates the network namespace without ByteBench's egress rules: wait for ByteBench again.
rm -f /tmp/.bb-ready
: > /tmp/.bb-waiting

i=0
while [ ! -f /tmp/.bb-ready ]; do
    i=$((i + 1))
    if [ "$i" -gt 1200 ]; then echo "bb-entrypoint: ByteBench never finished setting this workspace up" >&2; exit 1; fi
    sleep 0.1
done

args="--host 0.0.0.0 --port ${BB_IDE_PORT:-3000} --connection-token-file /tmp/.bb-token --telemetry-level off --accept-server-license-terms"
if [ -n "${BB_IDE_BASE_PATH:-}" ]; then args="$args --server-base-path ${BB_IDE_BASE_PATH}"; fi
if [ -n "${BB_WORKDIR:-}" ] && [ -d "${BB_WORKDIR}" ]; then
    cd "${BB_WORKDIR}"
    args="$args --default-folder ${BB_WORKDIR}"
fi
# shellcheck disable=SC2086
exec "${OPENVSCODE_SERVER_ROOT}/bin/openvscode-server" $args
