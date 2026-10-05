FROM mcr.microsoft.com/dotnet/sdk:10.0.401 AS restore
WORKDIR /src

COPY src/Contracts/Heartbeat.Contracts/Heartbeat.Contracts.csproj src/Contracts/Heartbeat.Contracts/
COPY global.json Directory.Build.props Directory.Packages.props Heartbeat.slnx ./
COPY src/Hub/Heartbeat.Hub.Client/Heartbeat.Hub.Client.csproj src/Hub/Heartbeat.Hub.Client/
COPY src/Hub/Heartbeat.Hub/Heartbeat.Hub.csproj src/Hub/Heartbeat.Hub/
COPY src/Hub/Heartbeat.Hub.Host/Heartbeat.Hub.Host.csproj src/Hub/Heartbeat.Hub.Host/
COPY src/Hub/Heartbeat.Hub.Runtime/Heartbeat.Hub.Runtime.csproj src/Hub/Heartbeat.Hub.Runtime/
RUN dotnet restore src/Hub/Heartbeat.Hub.Host/Heartbeat.Hub.Host.csproj

FROM restore AS development
COPY . .
ENTRYPOINT ["dotnet", "watch", "--project", "src/Hub/Heartbeat.Hub.Host/Heartbeat.Hub.Host.csproj", "run", "--no-launch-profile"]

FROM restore AS build
COPY . .
RUN dotnet publish src/Hub/Heartbeat.Hub.Host/Heartbeat.Hub.Host.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0.10 AS final
WORKDIR /app
COPY --from=build /app/publish .
RUN mkdir /data && chown "$APP_UID:$APP_UID" /data
USER $APP_UID
ENTRYPOINT ["dotnet", "Heartbeat.Hub.Host.dll"]
