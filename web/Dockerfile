# Build context: web/.   docker build -t invoiceflow-web --build-arg API_BASE_URL=http://api:8080 web
# NEXT_PUBLIC_* values and API_BASE_URL (proxy target) are baked in at build time.
FROM node:22-alpine AS deps
WORKDIR /app
COPY package.json package-lock.json ./
RUN npm ci --no-audit --no-fund

FROM node:22-alpine AS build
WORKDIR /app
ARG API_BASE_URL=http://api:8080
ARG NEXT_PUBLIC_AUTH_MODE=entra
ARG NEXT_PUBLIC_ENTRA_CLIENT_ID=
ARG NEXT_PUBLIC_ENTRA_TENANT_ID=
ARG NEXT_PUBLIC_ENTRA_API_SCOPE=
ENV API_BASE_URL=$API_BASE_URL \
    NEXT_PUBLIC_AUTH_MODE=$NEXT_PUBLIC_AUTH_MODE \
    NEXT_PUBLIC_ENTRA_CLIENT_ID=$NEXT_PUBLIC_ENTRA_CLIENT_ID \
    NEXT_PUBLIC_ENTRA_TENANT_ID=$NEXT_PUBLIC_ENTRA_TENANT_ID \
    NEXT_PUBLIC_ENTRA_API_SCOPE=$NEXT_PUBLIC_ENTRA_API_SCOPE \
    NEXT_TELEMETRY_DISABLED=1
COPY --from=deps /app/node_modules ./node_modules
COPY . .
RUN npm run build

FROM node:22-alpine AS final
WORKDIR /app
ENV NODE_ENV=production NEXT_TELEMETRY_DISABLED=1 PORT=3000 HOSTNAME=0.0.0.0
COPY --from=build --chown=node:node /app/.next/standalone ./
COPY --from=build --chown=node:node /app/.next/static ./.next/static
USER node
EXPOSE 3000
CMD ["node", "server.js"]
