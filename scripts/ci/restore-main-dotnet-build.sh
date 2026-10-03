#!/usr/bin/env bash
set -Eeuo pipefail

artifact_root="${1:?artifact directory is required}"
expected_sha="${2:?expected source SHA is required}"
repo_root="${3:-$PWD}"

source_sha_file="$artifact_root/source-sha"
dotnet_archive="$artifact_root/dotnet-release-build.tar"

for required in "$source_sha_file" "$dotnet_archive"; do
  [[ -f "$required" ]] || {
    echo "Main .NET artifact is missing: $required" >&2
    exit 1
  }
done

source_sha="$(tr -d '\r\n' < "$source_sha_file")"
if [[ "$source_sha" != "$expected_sha" ]]; then
  echo "Main artifact SHA $source_sha does not match expected revision $expected_sha." >&2
  exit 1
fi

tar -xf "$dotnet_archive" -C "$repo_root"

stamp="$repo_root/artifacts/ci/dotnet-build-sha"
[[ -f "$stamp" ]] || {
  echo "Redistributed .NET artifact is missing its SHA stamp." >&2
  exit 1
}
actual="$(tr -d '\r\n' < "$stamp")"
if [[ "$actual" != "$expected_sha" ]]; then
  echo "Redistributed .NET artifact SHA $actual does not match expected revision $expected_sha." >&2
  exit 1
fi

if [[ -n "${GITHUB_ENV:-}" ]]; then
  printf 'COGLATAS_REUSE_PREBUILT_DOTNET_BUILD=true\n' >> "$GITHUB_ENV"
fi

echo "Restored trusted main .NET build for $expected_sha"
