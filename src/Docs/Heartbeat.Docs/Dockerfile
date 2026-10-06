# syntax=docker/dockerfile:1

FROM node:24.21.0-bookworm-slim AS node-tools
RUN npm install --global pnpm@12.3.4

FROM node-tools AS development
WORKDIR /src/src/Docs/Heartbeat.Docs
COPY --exclude=*.md --exclude=Dockerfile src/Docs/Heartbeat.Docs/ ./
ENV pnpm_config_store_dir=/src/src/Docs/Heartbeat.Docs/node_modules/.pnpm-store \
    XDG_CACHE_HOME=/src/src/Docs/Heartbeat.Docs/node_modules/.pnpm-store/cache
EXPOSE 3000
CMD ["sh", "-c", "pnpm install --frozen-lockfile && exec pnpm dev --hostname 0.0.0.0"]

FROM node-tools AS dependencies
WORKDIR /app
ENV pnpm_config_store_dir=/var/cache/pnpm/store \
    XDG_CACHE_HOME=/var/cache/pnpm/metadata
COPY src/Docs/Heartbeat.Docs/package.json src/Docs/Heartbeat.Docs/pnpm-lock.yaml src/Docs/Heartbeat.Docs/pnpm-workspace.yaml ./
RUN --mount=type=cache,id=heartbeat-docs-pnpm,target=/var/cache/pnpm,sharing=locked \
    pnpm install --frozen-lockfile

FROM dependencies AS build
COPY --exclude=*.md --exclude=Dockerfile src/Docs/Heartbeat.Docs/ .
RUN mkdir -p public && pnpm exec next build

FROM node:24.21.0-bookworm-slim AS final
WORKDIR /app
ENV NODE_ENV=production PORT=3000 HOSTNAME=0.0.0.0 HEARTBEAT_TEST_REPORTS_DIR=/reports
COPY --from=build --chown=node:node /app/.next/standalone/ .
COPY --from=build --chown=node:node /app/.next/static/ .next/static/
COPY --from=build --chown=node:node /app/public/ public/
EXPOSE 3000
USER node
CMD ["node", "server.js"]
