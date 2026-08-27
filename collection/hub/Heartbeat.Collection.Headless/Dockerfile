FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet restore collection/hub/Heartbeat.Collection.Headless/Heartbeat.Collection.Headless.csproj \
    && dotnet restore collection/collectors/Heartbeat.Collector.VRChat/Heartbeat.Collector.VRChat.csproj \
    && dotnet publish collection/hub/Heartbeat.Collection.Headless/Heartbeat.Collection.Headless.csproj \
        -c Release --no-restore -o /out/headless \
    && dotnet publish collection/collectors/Heartbeat.Collector.VRChat/Heartbeat.Collector.VRChat.csproj \
        -c Release --no-restore -o /out/vrchat \
    && /out/vrchat/Heartbeat.Collector.VRChat --create-package /out/vrchat-package

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /out/headless/ ./
COPY --from=build /out/vrchat-package/ ./vrchat-package/
VOLUME ["/data"]
EXPOSE 8080
ENTRYPOINT ["/app/Heartbeat.Collection.Headless", "/app/heartbeat-headless.json"]
