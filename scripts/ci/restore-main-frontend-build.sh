#!/usr/bin/env bash
set -Eeuo pipefail

artifact_root="${1:?artifact directory is required}"
expected_sha="${2:?expected source SHA is required}"
repo_root="${3:-$PWD}"

source_sha_file="$artifact_root/source-sha"
frontend_archive="$artifact_root/frontend-build-artifacts.tar"

for required in "$source_sha_file" "$frontend_archive"; do
  [[ -f "$required" ]] || {
    echo "Main frontend artifact is missing: $required" >&2
    exit 1
  }
done

source_sha="$(tr -d '\r\n' < "$source_sha_file")"
[[ "$source_sha" == "$expected_sha" ]] || {
  echo "Main frontend artifact SHA $source_sha does not match expected revision $expected_sha." >&2
  exit 1
}

tar -xf "$frontend_archive" -C "$repo_root"

stamp="$repo_root/artifacts/ci/frontend-build-sha"
[[ -f "$stamp" ]] || {
  echo "Redistributed frontend artifact is missing its SHA stamp." >&2
  exit 1
}
actual="$(tr -d '\r\n' < "$stamp")"
[[ "$actual" == "$expected_sha" ]] || {
  echo "Redistributed frontend artifact SHA $actual does not match expected revision $expected_sha." >&2
  exit 1
}

test -f "$repo_root/frontend/dist/coglatas-web/index.html"
test -f "$repo_root/frontend/dist/coglatas-web/angular-app.marker"
test -f "$repo_root/frontend/storybook-static/index.html"

echo "Restored trusted main frontend build for $expected_sha"
