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

FROM nginx:1.30.5-alpine AS final
COPY src/Docs/Orisumi.Docs/nginx.conf /etc/nginx/nginx.conf
COPY --from=build /repo/src/Docs/Orisumi.Docs/out/ /usr/share/nginx/html/
EXPOSE 3000
USER nginx
ENTRYPOINT ["nginx", "-g", "daemon off;"]
