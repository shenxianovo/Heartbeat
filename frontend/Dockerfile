# syntax=docker/dockerfile:1

# Compile the Dashboard into static assets.
FROM node:24-alpine AS dashboard-build
WORKDIR /app

COPY package.json package-lock.json ./
RUN --mount=type=cache,id=heartbeat-dashboard-npm,target=/root/.npm npm ci

COPY . .
RUN npm run build

# Serve the immutable Dashboard assets.
FROM nginx:alpine AS dashboard-runtime

COPY nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=dashboard-build /app/dist /usr/share/nginx/html

EXPOSE 8080
