# syntax=docker/dockerfile:1
# Project Faolan server image. Build from the repo root:  docker build -t projectfaolan .
# The runtime needs the ASP.NET shared framework (the in-process MCP server references Microsoft.AspNetCore.App).

# ---- build ----------------------------------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore against just the project files first so the (slow) restore layer is cached until a .csproj changes.
# The server's runtime dependency graph (post the Faolan.Core split, #290):
#   Faolan -> { Faolan.Core, Faolan.Core.Network -> Faolan.Core.Database -> Faolan.Core, Faolan.Mcp,
#               Faolan.Core.Rdb (TerrainService), Faolan.Core.Database.Migrations.{Sqlite,Postgres} -> Faolan.Core.Database }
# (The two migration projects are Migrate()'d at startup, #379 - the server references both so EF loads the one the
# active provider needs.) Directory.Build.props supplies the shared TargetFramework (net10.0); logo.ico is the icon.
COPY Directory.Build.props logo.ico ./
COPY Faolan/Faolan.csproj Faolan/
COPY Faolan.Core/Faolan.Core.csproj Faolan.Core/
COPY Faolan.Core.Database/Faolan.Core.Database.csproj Faolan.Core.Database/
COPY Faolan.Core.Network/Faolan.Core.Network.csproj Faolan.Core.Network/
COPY Faolan.Core.Rdb/Faolan.Core.Rdb.csproj Faolan.Core.Rdb/
COPY Faolan.Mcp/Faolan.Mcp.csproj Faolan.Mcp/
COPY Faolan.Core.Database.Migrations.Sqlite/Faolan.Core.Database.Migrations.Sqlite.csproj Faolan.Core.Database.Migrations.Sqlite/
COPY Faolan.Core.Database.Migrations.Postgres/Faolan.Core.Database.Migrations.Postgres.csproj Faolan.Core.Database.Migrations.Postgres/
RUN dotnet restore Faolan/Faolan.csproj

# Then the sources for the projects in the graph and publish (Release -> IsDebug=false -> prod paths).
COPY Faolan/ Faolan/
COPY Faolan.Core/ Faolan.Core/
COPY Faolan.Core.Database/ Faolan.Core.Database/
COPY Faolan.Core.Network/ Faolan.Core.Network/
COPY Faolan.Core.Rdb/ Faolan.Core.Rdb/
COPY Faolan.Mcp/ Faolan.Mcp/
COPY Faolan.Core.Database.Migrations.Sqlite/ Faolan.Core.Database.Migrations.Sqlite/
COPY Faolan.Core.Database.Migrations.Postgres/ Faolan.Core.Database.Migrations.Postgres/
RUN dotnet publish Faolan/Faolan.csproj -c Release -o /app/publish --no-restore

# ---- runtime --------------------------------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

COPY --from=build /app/publish ./

# The game data. The image ships the SEED, not a database: the server builds one on first start from the EF
# migrations + faolan.sql (DatabaseInitializer) into the mounted /app/data, so a container never mutates a file
# from the repo and a clean deploy is just an empty directory. Global.SeedPath looks for faolan.sql beside the
# exe, which is where this lands; the sidecar carries the auto-increment counter resync.
COPY faolan.sql faolan.seq.sqlite.sql faolan.seq.postgres.sql ./

# Ports for the all-in-one default run
#EXPOSE 7001 7010 7011 7012 7014

# Default = all-in-one (login + the first realm's world). For a process-per-realm deployment (#324), override the
# command per container: `--role login` (router tier) or `--role realm=<id>` (one realm's game host, on that Realm
# row's ports) - e.g. `command: ["dotnet","Faolan.dll","--role","realm=3"]` and EXPOSE that realm's ports.
ENTRYPOINT ["dotnet", "Faolan.dll"]
