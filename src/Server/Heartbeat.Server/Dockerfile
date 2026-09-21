FROM mcr.microsoft.com/dotnet/sdk:10.0.401 AS restore
WORKDIR /src

COPY src/Contracts/Heartbeat.Contracts/Heartbeat.Contracts.csproj src/Contracts/Heartbeat.Contracts/
COPY global.json Directory.Build.props Directory.Packages.props Heartbeat.slnx ./
COPY src/Hub/Heartbeat.Hub.Client/Heartbeat.Hub.Client.csproj src/Hub/Heartbeat.Hub.Client/
COPY src/Hub/Heartbeat.Hub/Heartbeat.Hub.csproj src/Hub/Heartbeat.Hub/
COPY src/Hub/Heartbeat.Hub.Host/Heartbeat.Hub.Host.csproj src/Hub/Heartbeat.Hub.Host/
COPY src/Hub/Heartbeat.Hub.Runtime/Heartbeat.Hub.Runtime.csproj src/Hub/Heartbeat.Hub.Runtime/
COPY src/Collectors/Heartbeat.Collector.VRChat/Heartbeat.Collector.VRChat.csproj src/Collectors/Heartbeat.Collector.VRChat/
COPY src/Server/Heartbeat.Server/Heartbeat.Server.csproj src/Server/Heartbeat.Server/
RUN dotnet restore src/Server/Heartbeat.Server/Heartbeat.Server.csproj

FROM restore AS development
COPY . .
ENTRYPOINT ["dotnet", "watch", "--project", "src/Server/Heartbeat.Server/Heartbeat.Server.csproj", "run", "--no-launch-profile"]

FROM restore AS build
COPY . .
RUN dotnet publish src/Server/Heartbeat.Server/Heartbeat.Server.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0.10 AS final
WORKDIR /app
COPY --from=build /app/publish .
RUN mkdir /data && chown "$APP_UID:$APP_UID" /data
USER $APP_UID
ENTRYPOINT ["dotnet", "Heartbeat.Server.dll"]
