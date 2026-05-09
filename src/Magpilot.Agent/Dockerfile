# Multi-stage build for the Magpilot Agent on Linux (debian-slim, x64).
#
# Build context MUST be the repo root so we can reach Magpilot.Shared:
#   docker build -f src/Magpilot.Agent/Dockerfile -t magpilot-agent:latest .
#
# This image is intended for the always-on "Magnus" instance on LXC 102 but
# is generic -- any Linux host can run it as a Magpilot agent.

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

# Create the magnus user (uid 1000) and home dir. The compose stack bind-mounts
# /srv/magpilot-agent/home/ over /home/magnus/ so state persists.
RUN useradd -m -u 1000 -s /bin/bash magnus

WORKDIR /app
COPY --from=build /publish-agent/ ./

# Bootstrap script runs at container start: ensures magnus/MEMORY etc exist,
# launches the agent, then (after agent is up) creates-or-adopts Magnus's
# always-on session.
COPY src/Magpilot.Agent/bootstrap.sh /usr/local/bin/bootstrap.sh
RUN chmod +x /usr/local/bin/bootstrap.sh

ENV ASPNETCORE_URLS=http://0.0.0.0:5099 \
    DOTNET_RUNNING_IN_CONTAINER=true \
    HOME=/home/magnus \
    MAGPILOT_AGENT_NAME=magnus \
    PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin

EXPOSE 5099

# tini for proper signal handling; bootstrap drops privileges to magnus
# before launching the agent + child copilot processes.
ENTRYPOINT ["/usr/bin/tini", "--", "/usr/local/bin/bootstrap.sh"]
