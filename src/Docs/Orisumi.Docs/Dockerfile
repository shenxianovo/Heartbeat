# syntax=docker/dockerfile:1

FROM node:24.21.0-bookworm-slim AS node-tools
RUN npm install --global pnpm@12.3.4

FROM node-tools AS dependencies
WORKDIR /repo/src/Docs/Orisumi.Docs
ENV pnpm_config_store_dir=/var/cache/pnpm/store \
    XDG_CACHE_HOME=/var/cache/pnpm/metadata
COPY src/Docs/Orisumi.Docs/package.json src/Docs/Orisumi.Docs/pnpm-lock.yaml src/Docs/Orisumi.Docs/pnpm-workspace.yaml ./
RUN --mount=type=cache,id=orisumi-docs-pnpm,target=/var/cache/pnpm,sharing=locked \
    pnpm install --frozen-lockfile

FROM dependencies AS build
RUN apt-get update && apt-get install --yes --no-install-recommends git && rm -rf /var/lib/apt/lists/*
COPY --exclude=*.md --exclude=Dockerfile src/Docs/Orisumi.Docs/ .
RUN --mount=type=bind,source=.git,target=/repo/.git \
    pnpm build

FROM node:24.21.0-bookworm-slim AS final
WORKDIR /app
ENV NODE_ENV=production PORT=3000 HOSTNAME=0.0.0.0 ORISUMI_TEST_REPORTS_DIR=/reports
COPY --from=build --chown=node:node /repo/src/Docs/Orisumi.Docs/.next/standalone/ .
COPY --from=build --chown=node:node /repo/src/Docs/Orisumi.Docs/.next/static/ .next/static/
COPY --from=build --chown=node:node /repo/src/Docs/Orisumi.Docs/public/ public/
EXPOSE 3000
USER node
CMD ["node", "server.js"]
