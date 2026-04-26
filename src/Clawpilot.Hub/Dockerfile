# Multi-stage build for the Clawpilot Hub (ASP.NET on linux-x64).
# Build context MUST be the repo root (where the .sln-equivalent project tree lives)
# so we can copy sibling projects (Clawpilot.Web, Clawpilot.UI, Clawpilot.Shared).
#
#   docker build -f src/Clawpilot.Hub/Dockerfile -t clawpilot-hub:latest .

ARG DOTNET_SDK=9.0
ARG DOTNET_RUNTIME=9.0

# --------- Restore + publish (SDK image) ---------
FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_SDK} AS build
WORKDIR /src

# Copy only csproj files first for layer caching of `dotnet restore`.
COPY src/Clawpilot.Shared/Clawpilot.Shared.csproj src/Clawpilot.Shared/
COPY src/Clawpilot.UI/Clawpilot.UI.csproj         src/Clawpilot.UI/
COPY src/Clawpilot.Web/Clawpilot.Web.csproj       src/Clawpilot.Web/
COPY src/Clawpilot.Hub/Clawpilot.Hub.csproj       src/Clawpilot.Hub/
RUN dotnet restore src/Clawpilot.Web/Clawpilot.Web.csproj \
 && dotnet restore src/Clawpilot.Hub/Clawpilot.Hub.csproj

# Now copy source.
COPY src/ src/

# Publish the Blazor WASM SPA, then copy its wwwroot output into the hub project's
# wwwroot so the hub serves it at "/".
RUN dotnet publish src/Clawpilot.Web/Clawpilot.Web.csproj \
        -c Release --no-restore -o /publish-web \
 && rm -rf src/Clawpilot.Hub/wwwroot \
 && mkdir -p src/Clawpilot.Hub/wwwroot \
 && cp -r /publish-web/wwwroot/. src/Clawpilot.Hub/wwwroot/

# Publish the hub. wwwroot is now packed into the Web SDK output.
RUN dotnet publish src/Clawpilot.Hub/Clawpilot.Hub.csproj \
        -c Release --no-restore -o /publish-hub /p:UseAppHost=false

# --------- Runtime image ---------
FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_RUNTIME} AS runtime
WORKDIR /app
COPY --from=build /publish-hub/ ./

# Default config; overridable via env or compose.
ENV ASPNETCORE_URLS=http://0.0.0.0:7088 \
    DOTNET_RUNNING_IN_CONTAINER=true \
    CLAWPILOT_HUB_DATA=/data

VOLUME ["/data"]
EXPOSE 7088

ENTRYPOINT ["dotnet", "Clawpilot.Hub.dll"]
