# syntax=docker/dockerfile:1
# ByteBench container image. Build:  docker build -t bytebench .
# Run:    docker compose up -d   (see docker-compose.yml and "Production" in README.md)

# ---------- build ----------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY ByteBench.csproj ./
RUN dotnet restore ByteBench.csproj
COPY . .
RUN dotnet publish ByteBench.csproj -c Release -o /app/publish --no-restore /p:UseAppHost=false

# ---------- runtime ----------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime

# git: Git Smart HTTP (git http-backend), git grep code search and source archives (git archive).
# openssh-client: ssh:// mirrors (git runs ssh); the SSH *server* for pushes is built into ByteBench (port 2222).
# postgresql-client: pg_dump / pg_restore for backups when Database__Provider=Postgres.
# curl: the container health check. sqlite3: optional manual inspection and the older scripts/backup.sh.
RUN apt-get update \
    && apt-get install -y --no-install-recommends git openssh-client postgresql-client curl ca-certificates sqlite3 \
    && rm -rf /var/lib/apt/lists/*

# Optional docker CLI for CI pipelines (talks to a daemon through a mounted socket or DOCKER_HOST).
# docker build --build-arg WITH_DOCKER_CLI=true -t bytebench .
ARG WITH_DOCKER_CLI=false
ARG DOCKER_CLI_VERSION=27.3.1
RUN if [ "$WITH_DOCKER_CLI" = "true" ]; then \
        arch="$(uname -m)" \
        && curl -fsSL "https://download.docker.com/linux/static/stable/${arch}/docker-${DOCKER_CLI_VERSION}.tgz" \
           | tar -xz -C /usr/local/bin --strip-components=1 docker/docker \
        && docker --version; \
    fi

# All state lives under /data (one volume per kind, see docker-compose.yml), owned by the non-root "app" user (uid 1654).
RUN mkdir -p /data/db /data/repos /data/attachments /data/packages /data/lfs /data/pages /data/keys /data/backups \
    && chown -R app:app /data \
    && chmod 700 /data/keys

WORKDIR /app
COPY --from=build /app/publish ./
COPY --chmod=755 scripts/ ./scripts/

# TLS is expected to terminate at a reverse proxy (see docker-compose.yml), so the app itself doesn't redirect to HTTPS.
ENV ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_HTTP_PORTS=8080 \
    ConnectionStrings__Default="Data Source=/data/db/bytebench.db" \
    Git__RepositoryRoot=/data/repos \
    Attachments__Root=/data/attachments \
    Packages__Root=/data/packages \
    Lfs__Root=/data/lfs \
    Pages__Root=/data/pages \
    Backup__Directory=/data/backups \
    DataProtection__KeysPath=/data/keys \
    Ssh__Enabled=true \
    Ssh__Port=2222 \
    Ssh__HostKeyPath=/data/keys/ssh_host_rsa_key \
    Ci__Enabled=false \
    Security__HttpsRedirection=false \
    Seed__DemoData=false \
    DOTNET_gcServer=0

USER app
# 8080: HTTP (behind the TLS proxy). 2222: git over SSH (host key in the /data/keys volume).
EXPOSE 8080 2222
VOLUME ["/data/db", "/data/repos", "/data/attachments", "/data/packages", "/data/lfs", "/data/pages", "/data/keys", "/data/backups"]

HEALTHCHECK --interval=30s --timeout=5s --start-period=30s --retries=3 \
    CMD curl -fsS http://localhost:8080/readyz || exit 1

ENTRYPOINT ["dotnet", "ByteBench.dll"]
