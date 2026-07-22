# syntax=docker/dockerfile:1
# Project Faolan WEB - the Blazor armory / 3D character viewer / GM tools (#275). Build from the repo ROOT:
#   docker build -f Faolan.Web/Dockerfile -t projectfaolan-web .

# ---- build ----------------------------------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore against just the project files first so the (slow) restore layer is cached until a .csproj changes.
# Faolan.Web's dependency graph:
#   Faolan.Web -> { Faolan.Core, Faolan.Core.Network -> Faolan.Core.Database -> Faolan.Core, Faolan.Core.Rdb,
#                   Faolan.Core.Database.Migrations.{Sqlite,Postgres} -> Faolan.Core.Database }
# Directory.Build.props supplies the shared TargetFramework (net10.0); logo.ico is the projects' ApplicationIcon.
COPY Directory.Build.props logo.ico ./
COPY Faolan.Web/Faolan.Web.csproj Faolan.Web/
COPY Faolan.Core/Faolan.Core.csproj Faolan.Core/
COPY Faolan.Core.Network/Faolan.Core.Network.csproj Faolan.Core.Network/
COPY Faolan.Core.Database/Faolan.Core.Database.csproj Faolan.Core.Database/
COPY Faolan.Core.Rdb/Faolan.Core.Rdb.csproj Faolan.Core.Rdb/
COPY Faolan.Core.Database.Migrations.Sqlite/Faolan.Core.Database.Migrations.Sqlite.csproj Faolan.Core.Database.Migrations.Sqlite/
COPY Faolan.Core.Database.Migrations.Postgres/Faolan.Core.Database.Migrations.Postgres.csproj Faolan.Core.Database.Migrations.Postgres/
RUN dotnet restore Faolan.Web/Faolan.Web.csproj

# Then the sources for the projects in the graph and publish.
COPY Faolan.Web/ Faolan.Web/
COPY Faolan.Core/ Faolan.Core/
COPY Faolan.Core.Network/ Faolan.Core.Network/
COPY Faolan.Core.Database/ Faolan.Core.Database/
COPY Faolan.Core.Rdb/ Faolan.Core.Rdb/
COPY Faolan.Core.Database.Migrations.Sqlite/ Faolan.Core.Database.Migrations.Sqlite/
COPY Faolan.Core.Database.Migrations.Postgres/ Faolan.Core.Database.Migrations.Postgres/
RUN dotnet publish Faolan.Web/Faolan.Web.csproj -c Release -o /app/publish --no-restore

# ---- runtime --------------------------------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app/publish ./

# Kestrel listens on 8080 in the container (the aspnet base image default). Config comes from env vars (faolan.json
# is optional and not baked in): the game DB (DatabaseProvider / DatabaseConnectionString), RdbDir (point it at a
# mounted client RDB dir) and CdnUrl (the Faolan.Web.Content CDN proxy). See docker-compose.yml.
EXPOSE 8080
ENTRYPOINT ["dotnet", "Faolan.Web.dll"]
