# Multi-stage build for the Magpilot Hub (ASP.NET on linux-x64).
# Build context MUST be the repo root (where the .sln-equivalent project tree lives)
# so we can copy sibling projects (Magpilot.Web, Magpilot.UI, Magpilot.Shared).
#
#   docker build -f src/Magpilot.Hub/Dockerfile -t magpilot-hub:latest .

ARG DOTNET_SDK=9.0
ARG DOTNET_RUNTIME=9.0

# --------- Restore + publish (SDK image) ---------
FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_SDK} AS build
WORKDIR /src

# Copy only csproj files first for layer caching of `dotnet restore`.
COPY src/Magpilot.Shared/Magpilot.Shared.csproj src/Magpilot.Shared/
COPY src/Magpilot.UI/Magpilot.UI.csproj         src/Magpilot.UI/
COPY src/Magpilot.Web/Magpilot.Web.csproj       src/Magpilot.Web/
COPY src/Magpilot.Hub/Magpilot.Hub.csproj       src/Magpilot.Hub/
RUN dotnet restore src/Magpilot.Web/Magpilot.Web.csproj \
 && dotnet restore src/Magpilot.Hub/Magpilot.Hub.csproj

# Now copy source.
COPY src/ src/

# Publish the Blazor WASM SPA, then copy its wwwroot output into the hub project's
# wwwroot so the hub serves it at "/".
RUN dotnet publish src/Magpilot.Web/Magpilot.Web.csproj \
        -c Release --no-restore -o /publish-web \
 && rm -rf src/Magpilot.Hub/wwwroot \
 && mkdir -p src/Magpilot.Hub/wwwroot \
 && cp -r /publish-web/wwwroot/. src/Magpilot.Hub/wwwroot/

# Publish the hub. wwwroot is now packed into the Web SDK output.
RUN dotnet publish src/Magpilot.Hub/Magpilot.Hub.csproj \
        -c Release --no-restore -o /publish-hub /p:UseAppHost=false

# --------- Runtime image ---------
FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_RUNTIME} AS runtime
WORKDIR /app
COPY --from=build /publish-hub/ ./

# Default config; overridable via env or compose.
ENV ASPNETCORE_URLS=http://0.0.0.0:7088 \
    DOTNET_RUNNING_IN_CONTAINER=true \
    MAGPILOT_HUB_DATA=/data

VOLUME ["/data"]
EXPOSE 7088

ENTRYPOINT ["dotnet", "Magpilot.Hub.dll"]
