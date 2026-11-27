# syntax=docker/dockerfile:1
# ByteBench workspace image: OpenVSCode Server (gitpod-io/openvscode-server) plus a small toolbox (git, node, python).
#
#   docker build -t bytebench-workspace:2 docker/workspace
#   # with the .NET SDK too (any Debian-based SDK image works as the base):
#   docker build -t bytebench-workspace:2-dotnet --build-arg BASE_IMAGE=mcr.microsoft.com/dotnet/sdk:10.0 docker/workspace
#
# Set Workspaces:Image to the tag you built. The container is started by ByteBench with everything locked down
# (non-root, no capabilities, no-new-privileges, resource limits, a private network); see the README, "Workspaces".
# The image itself needs no secrets and no network access beyond what ByteBench allows at run time.

ARG BASE_IMAGE=debian:bookworm-slim
FROM ${BASE_IMAGE}

# OpenVSCode Server release, pinned. The download is checked against the SHA-256 from the release's asset digest
# (GitHub: releases/tag/openvscode-server-v1.109.5); the build fails if it doesn't match. To bump it, change the version
# and both hashes together.
ARG OPENVSCODE_VERSION=1.109.5
ARG OPENVSCODE_SHA256_X64=b433bf4f0227321a7014d8460d10a8f958adc0f45aa79bd889e84e65e8f88363
ARG OPENVSCODE_SHA256_ARM64=36d9c14036489b63de84ebace837fcacf7e60e669a0dc715802c5443684ea4dc
ARG TARGETARCH

ENV DEBIAN_FRONTEND=noninteractive

# git: clone/commit/push to ByteBench. nodejs/npm, python3: the usual toolbox. socat: lets ByteBench's preview proxy reach
# servers that only listen on 127.0.0.1. iptables: applied once from a throw-away helper container sharing this container's
# network namespace (the workspace itself never has NET_ADMIN), see bb-apply-egress. openssh-server: not started with the container; ByteBench starts an
# unprivileged sshd on 127.0.0.1:2222 (key authentication only) when the owner connects a local editor, and only if the administrator allowed that.
USER root
RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        git ca-certificates curl wget less nano vim-tiny jq unzip zip procps openssh-client openssh-server \
        nodejs npm python3 python3-pip python3-venv make g++ \
        socat iptables tmux \
    && rm -rf /var/lib/apt/lists/*

RUN set -eu; \
    case "${TARGETARCH:-$(dpkg --print-architecture)}" in \
        amd64|x64)     arch=x64;   want="${OPENVSCODE_SHA256_X64}" ;; \
        arm64|aarch64) arch=arm64; want="${OPENVSCODE_SHA256_ARM64}" ;; \
        *) echo "unsupported architecture" >&2; exit 1 ;; \
    esac; \
    url="https://github.com/gitpod-io/openvscode-server/releases/download/openvscode-server-v${OPENVSCODE_VERSION}/openvscode-server-v${OPENVSCODE_VERSION}-linux-${arch}.tar.gz"; \
    curl -fsSL -o /tmp/openvscode.tgz "$url"; \
    echo "${want}  /tmp/openvscode.tgz" | sha256sum -c -; \
    mkdir -p /opt/openvscode-server; \
    tar -xzf /tmp/openvscode.tgz -C /opt/openvscode-server --strip-components=1; \
    rm /tmp/openvscode.tgz

# The unprivileged user everything runs as (uid 1000; a base image's own uid-1000 user is replaced).
RUN set -eu; \
    if getent passwd 1000 >/dev/null; then userdel -r "$(getent passwd 1000 | cut -d: -f1)" 2>/dev/null || true; fi; \
    if getent group 1000 >/dev/null; then groupdel "$(getent group 1000 | cut -d: -f1)" 2>/dev/null || true; fi; \
    groupadd -g 1000 coder; \
    useradd -m -u 1000 -g 1000 -s /bin/bash coder; \
    mkdir -p /workspace && chown coder:coder /workspace && chmod 2775 /workspace; \
    # bbai: the user ByteBench's AI assistant runs its commands as. Same group as coder (files are group-writable, see umask 002),
    # but it can't read coder's git credentials (mode 600) or ~/.gitconfig.
    useradd -m -u 1001 -g 1000 -s /bin/bash bbai; \
    git config --system safe.directory '*'; \
    git config --system init.defaultBranch main; \
    # Mount points of ByteBench's named volumes (project and dependency caches). They must exist and belong to coder so a fresh volume
    # is writable by the unprivileged user.
    mkdir -p /home/coder/.bbcache && chown coder:coder /home/coder/.bbcache

COPY --chmod=755 bb-entrypoint.sh /usr/local/bin/bb-entrypoint
COPY --chmod=755 bb-apply-egress.sh /usr/local/bin/bb-apply-egress
COPY --chmod=755 bb-healthcheck.sh /usr/local/bin/bb-healthcheck

USER 1000:1000
WORKDIR /workspace
ENV HOME=/home/coder \
    SHELL=/bin/bash \
    BB_IDE_PORT=3000 \
    OPENVSCODE_SERVER_ROOT=/opt/openvscode-server

EXPOSE 3000
HEALTHCHECK --interval=10s --timeout=3s --start-period=20s --retries=3 CMD ["/usr/local/bin/bb-healthcheck"]
ENTRYPOINT ["/usr/local/bin/bb-entrypoint"]
