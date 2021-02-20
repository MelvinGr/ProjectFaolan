FROM mcr.microsoft.com/dotnet/sdk:5.0-focal AS base

WORKDIR /app
EXPOSE 7010
EXPOSE 7011
EXPOSE 7012
EXPOSE 7013

FROM mcr.microsoft.com/dotnet/sdk:5.0-focal AS build
WORKDIR /src
COPY . .

RUN dotnet restore Faolan
WORKDIR /src
RUN dotnet build Faolan -c Release -o /app/build

FROM build AS publish
RUN dotnet publish Faolan -c Release -o /app/publish

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "Faolan.dll"]
