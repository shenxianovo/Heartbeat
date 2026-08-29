# syntax=docker/dockerfile:1

# --- build stage ---
FROM node:20-alpine AS build
WORKDIR /app

COPY frontend/package.json frontend/package-lock.json ./
RUN npm ci

COPY frontend/ .
# Frontend calendar tests type-check against the cross-runtime golden fixture.
# Keep the fixture single-sourced while making the production build reproducible.
COPY shared/calendar-window-golden-scenarios.json /shared/calendar-window-golden-scenarios.json
RUN npm run build

# --- runtime stage ---
FROM nginx:alpine AS runtime

COPY frontend/nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=build /app/dist /usr/share/nginx/html

EXPOSE 8080
