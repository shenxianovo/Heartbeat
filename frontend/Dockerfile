# syntax=docker/dockerfile:1

# --- build stage ---
FROM node:20-alpine AS build
WORKDIR /app

ARG VITE_BASE_PATH=/heartbeat/

COPY package.json package-lock.json ./
RUN npm ci

COPY . .
ENV VITE_BASE_PATH=$VITE_BASE_PATH
RUN npm run build

# --- runtime stage ---
FROM nginx:alpine AS runtime

COPY nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=build /app/dist /usr/share/nginx/html

EXPOSE 8080
