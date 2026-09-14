FROM node:24.15.0-alpine AS dependencies
WORKDIR /app
COPY package.json package-lock.json ./
RUN npm ci

FROM dependencies AS development
COPY . .
CMD ["npm", "run", "dev", "--", "--hostname", "0.0.0.0"]

FROM dependencies AS build
ARG HEARTBEAT_API_URL=http://api:8080
ARG NEXT_PUBLIC_OIDC_AUTHORITY=https://auth.shenxianovo.com
ARG NEXT_PUBLIC_OIDC_CLIENT_ID=heartbeat-web
ARG NEXT_PUBLIC_OIDC_SCOPE="openid profile offline_access"
ENV HEARTBEAT_API_URL=$HEARTBEAT_API_URL
ENV NEXT_PUBLIC_OIDC_AUTHORITY=$NEXT_PUBLIC_OIDC_AUTHORITY
ENV NEXT_PUBLIC_OIDC_CLIENT_ID=$NEXT_PUBLIC_OIDC_CLIENT_ID
ENV NEXT_PUBLIC_OIDC_SCOPE=$NEXT_PUBLIC_OIDC_SCOPE
COPY . .
RUN npm run build

FROM node:24.15.0-alpine AS final
WORKDIR /app
ENV NODE_ENV=production
ENV HOSTNAME=0.0.0.0
ENV PORT=3000
COPY --from=build --chown=node:node /app/.next/standalone ./
COPY --from=build --chown=node:node /app/.next/static ./.next/static
USER node
EXPOSE 3000
CMD ["node", "server.js"]
