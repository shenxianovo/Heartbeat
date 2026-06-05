# syntax=docker/dockerfile:1

# --- build stage ---
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore (cached when csproj files unchanged)
COPY shared/Heartbeat.Core/Heartbeat.Core.csproj shared/Heartbeat.Core/
COPY server/Heartbeat.Server/Heartbeat.Server.csproj server/Heartbeat.Server/
RUN dotnet restore server/Heartbeat.Server/Heartbeat.Server.csproj

# Build + publish
COPY shared/ shared/
COPY server/Heartbeat.Server/ server/Heartbeat.Server/
RUN dotnet publish server/Heartbeat.Server/Heartbeat.Server.csproj \
    -c Release -o /app/publish --no-restore /p:UseAppHost=false

# --- runtime stage ---
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# Kestrel listens on 8080 inside the container
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

COPY --from=build /app/publish .

ENTRYPOINT ["dotnet", "Heartbeat.Server.dll"]
