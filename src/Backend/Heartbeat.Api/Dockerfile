# syntax=docker/dockerfile:1

FROM mcr.microsoft.com/dotnet/sdk:10.0.401 AS dependencies
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/Core/Heartbeat.Core/Heartbeat.Core.csproj src/Core/Heartbeat.Core/
COPY src/Backend/Heartbeat.Application/Heartbeat.Application.csproj src/Backend/Heartbeat.Application/
COPY src/Backend/Heartbeat.Infrastructure/Heartbeat.Infrastructure.csproj src/Backend/Heartbeat.Infrastructure/
COPY src/Backend/Heartbeat.Api/Heartbeat.Api.csproj src/Backend/Heartbeat.Api/
RUN dotnet restore src/Backend/Heartbeat.Api/Heartbeat.Api.csproj
COPY .config/dotnet-tools.json .config/
RUN dotnet tool restore

FROM dependencies AS build
COPY .editorconfig ./
COPY --exclude=**/*.md --exclude=**/Dockerfile src/Core/ src/Core/
COPY --exclude=**/*.md --exclude=**/Dockerfile src/Backend/ src/Backend/
RUN dotnet publish src/Backend/Heartbeat.Api/Heartbeat.Api.csproj -c Release -o /out/api --no-restore
RUN ConnectionStrings__Heartbeat="Host=localhost;Database=heartbeat" dotnet ef migrations bundle \
    --project src/Backend/Heartbeat.Infrastructure/Heartbeat.Infrastructure.csproj \
    --startup-project src/Backend/Heartbeat.Api/Heartbeat.Api.csproj \
    --configuration Release --no-build --output /out/efbundle

FROM mcr.microsoft.com/dotnet/aspnet:10.0.12 AS final
WORKDIR /app
COPY --from=build /out/ .
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "api/Heartbeat.Api.dll"]
