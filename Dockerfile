# syntax=docker/dockerfile:1
# GitBench container image. Build:  docker build -t gitbench .
# Run:    docker compose up -d   (see docker-compose.yml and "Deploying GitBench" in README.md)

# ---------- build ----------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY GitBench.csproj ./
RUN dotnet restore GitBench.csproj
COPY . .
RUN dotnet publish GitBench.csproj -c Release -o /app/publish --no-restore /p:UseAppHost=false

# ---------- runtime ----------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime

# git: Git Smart HTTP (git http-backend), git grep code search and source archives (git archive).
# curl: the container health check. sqlite3: consistent backups from inside the container (scripts/backup.sh).
RUN apt-get update \
    && apt-get install -y --no-install-recommends git curl ca-certificates sqlite3 \
    && rm -rf /var/lib/apt/lists/*

# Optional docker CLI for CI pipelines (talks to a daemon through a mounted socket or DOCKER_HOST).
# docker build --build-arg WITH_DOCKER_CLI=true -t gitbench .
ARG WITH_DOCKER_CLI=false
ARG DOCKER_CLI_VERSION=27.3.1
RUN if [ "$WITH_DOCKER_CLI" = "true" ]; then \
        arch="$(uname -m)" \
        && curl -fsSL "https://download.docker.com/linux/static/stable/${arch}/docker-${DOCKER_CLI_VERSION}.tgz" \
           | tar -xz -C /usr/local/bin --strip-components=1 docker/docker \
        && docker --version; \
    fi

# All state lives under /data (one volume per kind, see docker-compose.yml), owned by the non-root "app" user (uid 1654).
RUN mkdir -p /data/db /data/repos /data/attachments /data/keys \
    && chown -R app:app /data \
    && chmod 700 /data/keys

WORKDIR /app
COPY --from=build /app/publish ./
COPY --chmod=755 scripts/ ./scripts/

# TLS is expected to terminate at a reverse proxy (see docker-compose.yml), so the app itself doesn't redirect to HTTPS.
ENV ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_HTTP_PORTS=8080 \
    ConnectionStrings__Default="Data Source=/data/db/gitbench.db" \
    Git__RepositoryRoot=/data/repos \
    Attachments__Root=/data/attachments \
    DataProtection__KeysPath=/data/keys \
    Ci__Enabled=false \
    Security__HttpsRedirection=false \
    Seed__DemoData=false \
    DOTNET_gcServer=0

USER app
EXPOSE 8080
VOLUME ["/data/db", "/data/repos", "/data/attachments", "/data/keys"]

HEALTHCHECK --interval=30s --timeout=5s --start-period=30s --retries=3 \
    CMD curl -fsS http://localhost:8080/health/live || exit 1

ENTRYPOINT ["dotnet", "GitBench.dll"]
