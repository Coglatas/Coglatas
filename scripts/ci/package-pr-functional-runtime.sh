#!/usr/bin/env bash
set -Eeuo pipefail

inputs="${1:?same-run producer artifact directory is required}"
output="${2:?runtime artifact output directory is required}"
expected="$(git rev-parse HEAD)"
[[ "$expected" == "$GITHUB_SHA" ]]
mkdir -p "$output" artifacts/main-runtime/publish artifacts/main-runtime/frontend
tar -xf "$inputs/dotnet-release-build.tar" -C "$GITHUB_WORKSPACE"
tar -xf "$inputs/frontend-build-artifacts.tar" -C "$GITHUB_WORKSPACE"
for stamp in artifacts/ci/dotnet-build-sha artifacts/ci/frontend-build-sha; do
  [[ "$(tr -d '\r\n' < "$stamp")" == "$expected" ]]
done
test -s frontend/dist/coglatas-web/index.html
test -s frontend/dist/coglatas-web/angular-app.marker
dotnet restore src/Coglatas.Web/Coglatas.Web.csproj --verbosity minimal
dotnet publish src/Coglatas.Web/Coglatas.Web.csproj --configuration Release \
  --no-build --no-restore --output artifacts/main-runtime/publish
cp -a frontend/dist/coglatas-web/. artifacts/main-runtime/frontend/

# Public PRs use the existing secret-free PR frontend product. Licensed release
# distribution remains independently validated by Main's protected producer.
image="coglatas-pr-functional:$expected"
docker build --file infra/docker/runtime-prebuilt.Dockerfile --tag "$image" .
printf '%s\n' "$expected" > "$output/source-sha"
printf '%s\n' "${PR_HEAD_SHA:?PR head provenance is required}" > "$output/pr-head-sha"
printf '%s\n' "$image" > "$output/runtime-image-name"
cp "$inputs/dotnet-release-build.tar" "$output/dotnet-release-build.tar"
docker save "$image" | gzip -1 > "$output/runtime-image.tar.gz"
