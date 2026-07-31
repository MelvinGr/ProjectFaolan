# syntax=docker/dockerfile:1
# Project Faolan CDN MIRROR - a store-and-forward mirror of Age of Conan's UPM content CDN (Faolan.Web.Content).
# Build from the repo ROOT:
#   docker build -f Faolan.Web.Content/Dockerfile -t projectfaolan-cdn .

# ---- build ----------------------------------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore against just the project files first so the (slow) restore layer is cached until a .csproj changes.
# The mirror's dependency graph is one edge - Faolan.Web.Content -> Faolan.Core - because it only needs the shared
# config/CLI plumbing; it touches neither the database nor the RDB reader.
# Directory.Build.props supplies the shared TargetFramework (net10.0); logo.ico is the projects' ApplicationIcon.
COPY Directory.Build.props logo.ico ./
COPY Faolan.Web.Content/Faolan.Web.Content.csproj Faolan.Web.Content/
COPY Faolan.Core/Faolan.Core.csproj Faolan.Core/
RUN dotnet restore Faolan.Web.Content/Faolan.Web.Content.csproj

COPY Faolan.Web.Content/ Faolan.Web.Content/
COPY Faolan.Core/ Faolan.Core/
RUN dotnet publish Faolan.Web.Content/Faolan.Web.Content.csproj -c Release -o /app/publish --no-restore

# ---- runtime --------------------------------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app/publish ./

# ⚠️ The mirror binds CdnMirror:Ip, which defaults to 127.0.0.1 - LOOPBACK INSIDE THE CONTAINER, i.e. a published
# port would connect to nothing. So the container default is 0.0.0.0; the port mapping is what limits exposure.
# Config comes from env vars (faolan.json is optional and not baked in) - see docker-compose.yml.
ENV CdnMirror__Ip=0.0.0.0 \
    CdnMirror__Port=7081 \
    CdnMirror__Store=/app/store

# The archive of fetched CDN blobs. Declared so it persists even when compose is not the thing running the image;
# compose binds a host directory over it, because a full patcher run is many GB and worth keeping.
VOLUME /app/store

EXPOSE 7081
ENTRYPOINT ["dotnet", "Faolan.Web.Content.dll"]
