#!/usr/bin/env bash
set -Eeuo pipefail

artifact_root="${1:?artifact directory is required}"
expected_sha="${2:?expected source SHA is required}"
repo_root="${3:-$PWD}"

source_sha_file="$artifact_root/source-sha"
image_name_file="$artifact_root/runtime-image-name"
image_archive="$artifact_root/runtime-image.tar.gz"
dotnet_archive="$artifact_root/dotnet-release-build.tar"

for required in "$source_sha_file" "$image_name_file" "$image_archive" "$dotnet_archive"; do
  [[ -f "$required" ]] || {
    echo "Main build artifact is missing: $required" >&2
    exit 1
  }
done

source_sha="$(tr -d '\r\n' < "$source_sha_file")"
if [[ "$source_sha" != "$expected_sha" ]]; then
  echo "Main build artifact SHA $source_sha does not match expected revision $expected_sha." >&2
  exit 1
fi

tar -xf "$dotnet_archive" -C "$repo_root"

dotnet_sha_file="$repo_root/artifacts/ci/dotnet-build-sha"
[[ -f "$dotnet_sha_file" ]] || {
  echo "Redistributed .NET artifact is missing its SHA stamp." >&2
  exit 1
}
dotnet_sha="$(tr -d '\r\n' < "$dotnet_sha_file")"
if [[ "$dotnet_sha" != "$expected_sha" ]]; then
  echo "Redistributed .NET artifact SHA $dotnet_sha does not match expected revision $expected_sha." >&2
  exit 1
fi

gzip -dc "$image_archive" | docker load >/dev/null
image_name="$(tr -d '\r\n' < "$image_name_file")"
docker image inspect "$image_name" >/dev/null

if [[ -n "${GITHUB_ENV:-}" ]]; then
  {
    printf 'COGLATAS_APP_IMAGE=%s\n' "$image_name"
    printf 'COGLATAS_REUSE_PREBUILT_APP_IMAGE=1\n'
    printf 'COGLATAS_REUSE_PREBUILT_DOTNET_BUILD=true\n'
  } >> "$GITHUB_ENV"
else
  printf 'Loaded main runtime image: %s\n' "$image_name"
fi
