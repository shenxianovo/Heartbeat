# The server's Runtime with the bundled Observers. The build context is the output of
# orisumi observers pack for linux-x64 or linux-arm64, matching the image's platform.
FROM mcr.microsoft.com/dotnet/runtime-deps:10.0
COPY runtime /runtime
COPY bundled /bundled
RUN mkdir -p /data && chown "$APP_UID" /data
USER $APP_UID
VOLUME /data
ENTRYPOINT ["/runtime/Orisumi.Observer.Runtime", "--service", "--data-dir", "/data", "--bundled", "/bundled"]
