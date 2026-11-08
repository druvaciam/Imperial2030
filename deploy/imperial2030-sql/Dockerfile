# The game server image for the SQL Server deployment, built from source - no .NET SDK needed outside
# Docker. Build from the repo root:
#   docker build -f deploy/imperial2030-sql/Dockerfile -t imperial2030-sql-app .
# Dockerfile.dockerignore next to this file decides what of the repo goes in (only the three projects).

# --- build -------------------------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Project files first, so the package restore is cached until a project file changes. No -r here:
# a runtime given to restore flows into the Blazor client project too, which runs in the browser and
# has no Linux runtime. Publish below adds the linux-x64 packages for the server only.
COPY Shared/Imperial2030.Shared.csproj Shared/
COPY Client/Imperial2030.Client.csproj Client/
COPY Server/Imperial2030.Server.csproj Server/
RUN dotnet restore Server/Imperial2030.Server.csproj

COPY Shared/ Shared/
COPY Client/ Client/
COPY Server/ Server/
# The same build the SQLite instance runs (self-contained linux-x64). Which database it uses is decided
# by the connection string at runtime.
RUN dotnet publish Server/Imperial2030.Server.csproj -c Release -r linux-x64 --self-contained -o /out

# --- run ---------------------------------------------------------------------------------------------
# runtime-deps: the OS libraries a self-contained .NET app needs, and nothing else.
FROM mcr.microsoft.com/dotnet/runtime-deps:10.0
WORKDIR /app
COPY --from=build /out ./

# Writable places for the non-root user: the app's log files and the ASP.NET data-protection keys,
# both mounted as volumes by docker-compose.yml so they survive an update.
RUN mkdir -p /app/logs /home/app/.aspnet/DataProtection-Keys \
 && chown -R app:app /app/logs /home/app/.aspnet

# The base image's built-in unprivileged user (uid 1654): the game never runs as root.
USER app

ENV ASPNETCORE_URLS=http://+:8080 \
    LOG_DIRECTORY=/app/logs

EXPOSE 8080
ENTRYPOINT ["./Imperial2030.Server"]
