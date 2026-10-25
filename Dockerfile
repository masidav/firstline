# syntax=docker/dockerfile:1.7
# One build for the three server processes (ADR 0001, ADR 0002). Build from the repo root:
#   docker build --target api     -t fandomscore-api     .
#   docker build --target worker  -t fandomscore-worker  .
#   docker build --target migrate -t fandomscore-migrate .
# Runtime images are Ubuntu "chiseled" (no shell or package manager) and run as the non-root `app` user.

ARG DOTNET_VERSION=10.0

FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS build
WORKDIR /src

# Restore first so dependency layers are cached until a project file changes.
COPY global.json Directory.Build.props ./
COPY packages/spotify/FandomScore.Spotify.csproj packages/spotify/
COPY packages/storage/FandomScore.Storage.csproj packages/storage/
COPY database/FandomScore.Database.csproj database/
COPY services/api/FandomScore.Api.csproj services/api/
COPY workers/FandomScore.Worker.csproj workers/
RUN dotnet restore services/api/FandomScore.Api.csproj \
 && dotnet restore workers/FandomScore.Worker.csproj \
 && dotnet restore database/FandomScore.Database.csproj

COPY packages/spotify/ packages/spotify/
COPY packages/storage/ packages/storage/
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
USER app
EXPOSE 8080
ENTRYPOINT ["dotnet", "FandomScore.Api.dll"]

# --- Worker: background jobs from the Postgres queue. No ports. ---
FROM mcr.microsoft.com/dotnet/runtime:${DOTNET_VERSION}-noble-chiseled AS worker
WORKDIR /app
COPY --from=build /out/worker .
ENV DOTNET_ENVIRONMENT=Production
USER app
ENTRYPOINT ["dotnet", "FandomScore.Worker.dll"]

# --- Migrator: applies database/migrations once and exits (Kubernetes Job before each rollout). ---
FROM mcr.microsoft.com/dotnet/runtime:${DOTNET_VERSION}-noble-chiseled AS migrate
WORKDIR /app
COPY --from=build /out/migrate .
USER app
ENTRYPOINT ["dotnet", "FandomScore.Database.dll"]
