# Multi-stage build for the Magpilot Agent on Linux (debian-slim, x64).
#
# Build context MUST be the repo root so we can reach Magpilot.Shared:
#   docker build -f src/Magpilot.Agent/Dockerfile -t magpilot-agent:latest .
#
# This is the generic Magpilot agent image -- any Linux host can run it.
# Site-specific behaviour (creating named pinned sessions, syncing extra
# git repos, installing plugins, seeding memory files) is the deployer's
# responsibility, achieved by bind-mounting a directory of *.sh hooks at
# /etc/magpilot/bootstrap.d/ and setting MAGPILOT_BOOTSTRAP_HOOK_DIR
# to that path. See src/Magpilot.Agent/bootstrap.sh for the contract.

ARG DOTNET_SDK=9.0
ARG DOTNET_RUNTIME=9.0

# --------- Restore + publish (SDK image) ---------
FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_SDK} AS build
WORKDIR /src

# Copy only csproj files first for layer caching of `dotnet restore`.
COPY src/Magpilot.Shared/Magpilot.Shared.csproj src/Magpilot.Shared/
COPY src/Magpilot.Agent/Magpilot.Agent.csproj   src/Magpilot.Agent/
RUN dotnet restore src/Magpilot.Agent/Magpilot.Agent.csproj

COPY src/ src/
RUN dotnet publish src/Magpilot.Agent/Magpilot.Agent.csproj \
        -c Release --no-restore -o /publish-agent /p:UseAppHost=false

# --------- Runtime image ---------
FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_RUNTIME} AS runtime

# Install Node.js 22 (Copilot CLI prerequisite) + git/curl/openssh-client/ca-certs
# + ffmpeg (audio transcoding for voice tools / Wyoming pipeline)
# + pwsh (PowerShell 7, required by plugins that ship .ps1 scripts -- e.g.
#   the task-context plugin's list-contexts.ps1).
RUN apt-get update && apt-get install -y --no-install-recommends \
        ca-certificates curl gnupg git openssh-client tini procps ffmpeg \
 && curl -fsSL https://deb.nodesource.com/setup_22.x | bash - \
 && apt-get install -y --no-install-recommends nodejs \
 && curl -fsSL https://packages.microsoft.com/config/debian/12/packages-microsoft-prod.deb \
        -o /tmp/packages-microsoft-prod.deb \
 && dpkg -i /tmp/packages-microsoft-prod.deb \
 && rm /tmp/packages-microsoft-prod.deb \
 && apt-get update && apt-get install -y --no-install-recommends powershell \
 && rm -rf /var/lib/apt/lists/* \
 && npm install -g @github/copilot \
 && npm cache clean --force

# The agent runs as a non-root user. Default: "magnus" (uid 1000) for
# historical reasons; overridable via MAGPILOT_AGENT_USER + a custom image.
# The compose stack bind-mounts the deployer's persistent /home over
# /home/magnus/ so state survives container recreation.
RUN useradd -m -u 1000 -s /bin/bash magnus

WORKDIR /app
COPY --from=build /publish-agent/ ./

# Bootstrap script runs at container start: ensures HOME exists with
# correct ownership, optionally runs deployer-supplied hooks
# (MAGPILOT_BOOTSTRAP_HOOK_DIR), then exec's the agent process.
COPY src/Magpilot.Agent/bootstrap.sh /usr/local/bin/bootstrap.sh
RUN chmod +x /usr/local/bin/bootstrap.sh

ENV ASPNETCORE_URLS=http://0.0.0.0:5099 \
    DOTNET_RUNNING_IN_CONTAINER=true \
    HOME=/home/magnus \
    MAGPILOT_AGENT_NAME=magnus \
    PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin

EXPOSE 5099

# tini for proper signal handling; bootstrap drops privileges to the agent
# user before launching the agent + child copilot processes.
ENTRYPOINT ["/usr/bin/tini", "--", "/usr/local/bin/bootstrap.sh"]
