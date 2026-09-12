FROM mcr.microsoft.com/dotnet/sdk:10.0.302 AS build
WORKDIR /src

COPY global.json Directory.Build.props Directory.Packages.props Heartbeat.slnx ./
COPY src/Backend/Heartbeat.Api/Heartbeat.Api.csproj src/Backend/Heartbeat.Api/
COPY src/Backend/Heartbeat.Application/Heartbeat.Application.csproj src/Backend/Heartbeat.Application/
COPY src/Backend/Heartbeat.Domain/Heartbeat.Domain.csproj src/Backend/Heartbeat.Domain/
COPY src/Backend/Heartbeat.Infrastructure/Heartbeat.Infrastructure.csproj src/Backend/Heartbeat.Infrastructure/
RUN dotnet restore src/Backend/Heartbeat.Api/Heartbeat.Api.csproj

COPY . .
RUN dotnet publish src/Backend/Heartbeat.Api/Heartbeat.Api.csproj --configuration Release --no-restore --output /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0.10 AS final
WORKDIR /app
RUN apt-get update \
    && apt-get install --yes --no-install-recommends libgssapi-krb5-2 \
    && rm -rf /var/lib/apt/lists/*
COPY --from=build /app/publish .
USER $APP_UID
ENTRYPOINT ["dotnet", "Heartbeat.Api.dll"]
