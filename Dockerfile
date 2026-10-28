# syntax=docker/dockerfile:1.7
# One build for the server processes and the two web apps (ADR 0001, ADR 0002). Build from the repo root:
#   docker build --target api          -t fandomscore-api          .
#   docker build --target worker       -t fandomscore-worker       .
#   docker build --target migrate      -t fandomscore-migrate      .
#   docker build --target web          -t fandomscore-web          .   # fan web app
#   docker build --target promoter-web -t fandomscore-promoter-web .   # promoter dashboard
# Runtime images are Ubuntu "chiseled" (no shell or package manager) and run as the non-root `app` user
# (by its numeric id, APP_UID=1654, so Kubernetes' runAsNonRoot can verify it).

ARG DOTNET_VERSION=10.0

FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS build
WORKDIR /src

# Restore first so dependency layers are cached until a project file changes.
COPY global.json Directory.Build.props ./
COPY packages/spotify/FandomScore.Spotify.csproj packages/spotify/
COPY packages/storage/FandomScore.Storage.csproj packages/storage/
COPY packages/notifications/FandomScore.Notifications.csproj packages/notifications/
COPY database/FandomScore.Database.csproj database/
COPY services/api/FandomScore.Api.csproj services/api/
COPY workers/FandomScore.Worker.csproj workers/
RUN dotnet restore services/api/FandomScore.Api.csproj \
 && dotnet restore workers/FandomScore.Worker.csproj \
 && dotnet restore database/FandomScore.Database.csproj

COPY packages/spotify/ packages/spotify/
COPY packages/storage/ packages/storage/
COPY packages/notifications/ packages/notifications/
COPY database/ database/
COPY services/api/ services/api/
COPY workers/ workers/

# Analyzer warnings are errors (Directory.Build.props), same as CI.
RUN dotnet publish services/api/FandomScore.Api.csproj -c Release --no-restore -o /out/api \
 && dotnet publish workers/FandomScore.Worker.csproj -c Release --no-restore -o /out/worker \
 && dotnet publish database/FandomScore.Database.csproj -c Release --no-restore -o /out/migrate

# --- API: listens on 8080 (ASPNETCORE_HTTP_PORTS default in the base image). ---
FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION}-noble-chiseled AS api
WORKDIR /app
COPY --from=build /out/api .
ENV DOTNET_ENVIRONMENT=Production ASPNETCORE_ENVIRONMENT=Production
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "FandomScore.Api.dll"]

# --- Worker: background jobs from the Postgres queue. Serves nothing but its liveness endpoint (Health__Port). ---
FROM mcr.microsoft.com/dotnet/runtime:${DOTNET_VERSION}-noble-chiseled AS worker
WORKDIR /app
COPY --from=build /out/worker .
ENV DOTNET_ENVIRONMENT=Production Health__Port=8081
USER $APP_UID
EXPOSE 8081
ENTRYPOINT ["dotnet", "FandomScore.Worker.dll"]

# --- Migrator: applies database/migrations once and exits (Kubernetes Job before each rollout). ---
FROM mcr.microsoft.com/dotnet/runtime:${DOTNET_VERSION}-noble-chiseled AS migrate
WORKDIR /app
COPY --from=build /out/migrate .
USER $APP_UID
ENTRYPOINT ["dotnet", "FandomScore.Database.dll"]

# --- Web apps: built once without any environment's settings (they come from /config.js at runtime,
# infrastructure/web), then served as static files by unprivileged nginx (uid 101) on 8080. ---
FROM node:22-bookworm-slim AS web-build
WORKDIR /src
COPY apps/mobile/package.json apps/mobile/package-lock.json apps/mobile/
RUN cd apps/mobile && npm ci --no-audit --no-fund
COPY apps/mobile/ apps/mobile/
# --clear: no Metro cache can carry another build's EXPO_PUBLIC_ values in (apps/mobile/README.md).
RUN cd apps/mobile && EXPO_NO_DOTENV=1 EXPO_NO_TELEMETRY=1 CI=1 npx expo export --platform web --clear --output-dir /out/web

FROM node:22-bookworm-slim AS promoter-web-build
WORKDIR /src
COPY apps/promoter-web/package.json apps/promoter-web/package-lock.json apps/promoter-web/
RUN cd apps/promoter-web && npm ci --no-audit --no-fund
COPY apps/promoter-web/ apps/promoter-web/
RUN cd apps/promoter-web && npx tsc --noEmit && npx vite build --outDir /out/promoter-web

FROM nginxinc/nginx-unprivileged:1.29-alpine AS web-server
COPY infrastructure/web/default.conf.template /etc/nginx/templates/default.conf.template
COPY infrastructure/web/security-headers.conf /etc/nginx/snippets/security-headers.conf
# Defaults for /config.js; the Kubernetes ConfigMap web-config sets the real ones.
ENV API_URL="" DEV_SIGN_IN=false GOOGLE_WEB_CLIENT_ID="" SPOTIFY_CLIENT_ID=""
USER 101
EXPOSE 8080

FROM web-server AS web
COPY --from=web-build /out/web /usr/share/nginx/html

FROM web-server AS promoter-web
COPY --from=promoter-web-build /out/promoter-web /usr/share/nginx/html
