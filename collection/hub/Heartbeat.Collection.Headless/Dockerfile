FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet restore collection/hub/Heartbeat.Collection.Headless/Heartbeat.Collection.Headless.csproj \
    && dotnet publish collection/hub/Heartbeat.Collection.Headless/Heartbeat.Collection.Headless.csproj \
        -c Release --no-restore -o /out/headless

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /out/headless/ ./
VOLUME ["/data"]
EXPOSE 8080
ENTRYPOINT ["/app/Heartbeat.Collection.Headless", "/app/heartbeat-headless.json"]
