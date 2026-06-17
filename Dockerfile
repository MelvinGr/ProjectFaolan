# syntax=docker/dockerfile:1
# Project Faolan server image. Build from the repo root:  docker build -t projectfaolan .
# The runtime needs the ASP.NET shared framework (the in-process MCP server references Microsoft.AspNetCore.App).

# ---- build ----------------------------------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore against just the project files first so the (slow) restore layer is cached until a .csproj changes.
# The server's dependency graph is Faolan -> { Faolan.Core, Faolan.Mcp -> Faolan.Core }. Directory.Build.props
# supplies the shared TargetFramework (net10.0); logo.ico is the projects' ApplicationIcon.
COPY Directory.Build.props logo.ico ./
COPY Faolan/Faolan.csproj Faolan/
COPY Faolan.Core/Faolan.Core.csproj Faolan.Core/
COPY Faolan.Mcp/Faolan.Mcp.csproj Faolan.Mcp/
RUN dotnet restore Faolan/Faolan.csproj

# Then the sources for the three projects in the graph and publish (Release -> IsDebug=false -> prod paths).
COPY Faolan/ Faolan/
COPY Faolan.Core/ Faolan.Core/
COPY Faolan.Mcp/ Faolan.Mcp/
RUN dotnet publish Faolan/Faolan.csproj -c Release -o /app/publish --no-restore

# ---- runtime --------------------------------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

COPY --from=build /app/publish ./
# faolan.json (loaded from cwd) + faolan.db (the committed game data; DatabasePath="faolan.db" is cwd-relative).
# NOTE: faolan.db is mutable at runtime (character saves + EF migrations) - mount a volume on /app/faolan.db to
# persist it across container restarts. NOTE: faolan.json ServerIpAddress and the Realms.IpAddress row both
# default to 127.0.0.1; for remote clients override ServerIpAddress (e.g. --ServerIpAddress=<host> via the
# command-line config provider) and update the realm row, or the handoff will advertise an unreachable address.
COPY faolan.json faolan.db ./

# UniverseAgent 7010 (the only fixed client entry port) - PlayerAgent 7011 - CSPlayerAgent 7001 -
# AgentServer/chat 7012 - GameServer realm 7014. (MCP 7099 binds localhost only; not exposed.)
EXPOSE 7001 7010 7011 7012 7014

ENTRYPOINT ["dotnet", "Faolan.dll"]
