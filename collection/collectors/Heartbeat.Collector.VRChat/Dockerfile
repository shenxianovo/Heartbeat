# VRChat Collector Package 的构建入口，构建上下文是仓库根。
#
# Package 里的 artifact selector 由 --create-package 运行时的 OS/arch 决定，所以 Headless
# 容器要用的 Package 必须在 linux 容器里产出，而不是在 macOS/Windows 宿主上直接构建。
# 用法见 scripts/build-vrchat-package.sh / .ps1。

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet restore collection/collectors/Heartbeat.Collector.VRChat/Heartbeat.Collector.VRChat.csproj \
    && dotnet publish collection/collectors/Heartbeat.Collector.VRChat/Heartbeat.Collector.VRChat.csproj \
        -c Release --no-restore -o /out/vrchat \
    && /out/vrchat/Heartbeat.Collector.VRChat --create-package /out/vrchat-package

# 只承载 Package 内容，供 `--output type=local` 导出到宿主目录。
FROM scratch AS package
COPY --from=build /out/vrchat-package/ /
