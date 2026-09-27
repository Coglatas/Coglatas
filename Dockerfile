# syntax=docker/dockerfile:1.7

FROM node:24@sha256:64af3819f9275802414d7cdc38c27e9d82bd564dec4d4da87d008255d36c63b4 AS frontend-build
WORKDIR /src/frontend
COPY frontend/package.json frontend/package-lock.json frontend/.npmrc ./
COPY scripts/ci/verify-npm-lockfile.mjs /usr/local/lib/coglatas/verify-npm-lockfile.mjs
RUN --mount=type=cache,id=coglatas-docker-npm,target=/root/.npm,sharing=locked \
    npm install --global npm@11.17.0 \
      --ignore-scripts \
      --allow-git=none \
      --allow-remote=none \
      --no-audit \
      --no-fund
RUN node /usr/local/lib/coglatas/verify-npm-lockfile.mjs .
RUN --mount=type=cache,id=coglatas-docker-npm,target=/root/.npm,sharing=locked \
    npm ci \
      --prefer-online \
      --strict-allow-scripts \
      --allow-git=none \
      --allow-remote=none \
      --no-audit \
      --no-fund
COPY frontend/ ./
RUN --mount=type=secret,id=syncfusion_license,required=true \
    --mount=type=cache,id=coglatas-docker-angular,target=/src/frontend/.angular/cache,sharing=locked \
    set -eu; \
    test -x node_modules/.bin/syncfusion-license || { echo "Syncfusion License CLI is not installed." >&2; exit 1; }; \
    SYNCFUSION_LICENSE="$(tr -d '\r\n' < /run/secrets/syncfusion_license)"; \
    test -n "$SYNCFUSION_LICENSE" || { echo "SYNCFUSION_LICENSE is not configured." >&2; exit 1; }; \
    export SYNCFUSION_LICENSE; \
    npm run build:licensed; \
    ! grep -R -F -q -- "$SYNCFUSION_LICENSE" dist || { echo "Syncfusion license material was found in frontend build output." >&2; exit 1; }; \
    unset SYNCFUSION_LICENSE

FROM mcr.microsoft.com/dotnet/sdk:10.0.401@sha256:35d40304542c8689331f8cab17c65926cdf48fe711e289321d71924b230a7d29 AS build
WORKDIR /src

COPY Coglatas.slnx ./
COPY src/Coglatas.Domain/Coglatas.Domain.csproj src/Coglatas.Domain/
COPY src/Coglatas.Application/Coglatas.Application.csproj src/Coglatas.Application/
COPY src/Coglatas.Infrastructure/Coglatas.Infrastructure.csproj src/Coglatas.Infrastructure/
COPY src/Coglatas.Web/Coglatas.Web.csproj src/Coglatas.Web/
RUN --mount=type=cache,id=coglatas-docker-nuget,target=/root/.nuget/packages,sharing=locked \
    dotnet restore src/Coglatas.Web/Coglatas.Web.csproj

COPY . .
RUN rm -rf src/Coglatas.Web/wwwroot/*
COPY --from=frontend-build /src/frontend/dist/coglatas-web/ src/Coglatas.Web/wwwroot/
# BuildKit cache mounts are mutable and can be pruned independently from cached
# restore layers. Force a restore in the same cache mount as publish so missing
# NuGet packages are repaired before --no-restore is used.
RUN --mount=type=cache,id=coglatas-docker-nuget,target=/root/.nuget/packages,sharing=locked \
    dotnet restore src/Coglatas.Web/Coglatas.Web.csproj --force && \
    dotnet publish src/Coglatas.Web/Coglatas.Web.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0.12@sha256:2d584d8147faddb0d678c5748d47953e5b8e18621ed4fb7049a91381d9d7746f AS runtime
WORKDIR /app
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/* \
    && mkdir -p /app/storage/uploads
COPY --from=build /app/publish .
ENV PORT=8080
EXPOSE 8080
ENTRYPOINT ["sh", "-c", "exec dotnet Coglatas.Web.dll --urls http://0.0.0.0:${PORT:-8080}"]
