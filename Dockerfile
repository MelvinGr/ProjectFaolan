# syntax=docker/dockerfile:1
# Project Faolan server image. Build from the repo root:  docker build -t projectfaolan .
# The runtime needs the ASP.NET shared framework (the in-process MCP server references Microsoft.AspNetCore.App).

# ---- build ----------------------------------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore against just the project files first so the (slow) restore layer is cached until a .csproj changes.
# The server's runtime dependency graph (post the Faolan.Core split, #290):
#   Faolan -> { Faolan.Core, Faolan.Core.Network -> Faolan.Core.Database -> Faolan.Core, Faolan.Mcp }
# (Faolan.Core.Rdb is NOT in the server graph - it's importer/web-only.) Directory.Build.props supplies the
# shared TargetFramework (net10.0); logo.ico is the projects' ApplicationIcon.
COPY Directory.Build.props logo.ico ./
COPY Faolan/Faolan.csproj Faolan/
COPY Faolan.Core/Faolan.Core.csproj Faolan.Core/
COPY Faolan.Core.Database/Faolan.Core.Database.csproj Faolan.Core.Database/
COPY Faolan.Core.Network/Faolan.Core.Network.csproj Faolan.Core.Network/
COPY Faolan.Mcp/Faolan.Mcp.csproj Faolan.Mcp/
RUN dotnet restore Faolan/Faolan.csproj

# Then the sources for the projects in the graph and publish (Release -> IsDebug=false -> prod paths).
COPY Faolan/ Faolan/
COPY Faolan.Core/ Faolan.Core/
COPY Faolan.Core.Database/ Faolan.Core.Database/
COPY Faolan.Core.Network/ Faolan.Core.Network/
COPY Faolan.Mcp/ Faolan.Mcp/
RUN dotnet publish Faolan/Faolan.csproj -c Release -o /app/publish --no-restore

# ---- runtime --------------------------------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

COPY --from=build /app/publish ./

# Ports for the all-in-one default run
#EXPOSE 7001 7010 7011 7012 7014

# Default = all-in-one (login + the first realm's world). For a process-per-realm deployment (#324), override the
# command per container: `--role login` (router tier) or `--role realm=<id>` (one realm's game host, on that Realm
# row's ports) - e.g. `command: ["dotnet","Faolan.dll","--role","realm=3"]` and EXPOSE that realm's ports.
ENTRYPOINT ["dotnet", "Faolan.dll"]
