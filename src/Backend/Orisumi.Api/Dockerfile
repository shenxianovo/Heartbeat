# syntax=docker/dockerfile:1

FROM mcr.microsoft.com/dotnet/sdk:10.0.401 AS dependencies
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/Core/Orisumi.Core/Orisumi.Core.csproj src/Core/Orisumi.Core/
COPY src/Backend/Orisumi.Application/Orisumi.Application.csproj src/Backend/Orisumi.Application/
COPY src/Backend/Orisumi.Infrastructure/Orisumi.Infrastructure.csproj src/Backend/Orisumi.Infrastructure/
COPY src/Backend/Orisumi.Api/Orisumi.Api.csproj src/Backend/Orisumi.Api/
RUN dotnet restore src/Backend/Orisumi.Api/Orisumi.Api.csproj
COPY .config/dotnet-tools.json .config/
RUN dotnet tool restore

FROM dependencies AS build
COPY .editorconfig ./
COPY --exclude=**/*.md --exclude=**/Dockerfile src/Core/ src/Core/
COPY --exclude=**/*.md --exclude=**/Dockerfile src/Backend/ src/Backend/
RUN dotnet publish src/Backend/Orisumi.Api/Orisumi.Api.csproj -c Release -o /out/api --no-restore
RUN ConnectionStrings__Orisumi="Host=localhost;Database=orisumi" dotnet ef migrations bundle \
    --project src/Backend/Orisumi.Infrastructure/Orisumi.Infrastructure.csproj \
    --startup-project src/Backend/Orisumi.Api/Orisumi.Api.csproj \
    --configuration Release --no-build --output /out/efbundle

FROM mcr.microsoft.com/dotnet/aspnet:10.0.12 AS final
WORKDIR /app
COPY --from=build /out/ .
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "api/Orisumi.Api.dll"]
