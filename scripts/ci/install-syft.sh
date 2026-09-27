#!/usr/bin/env bash
set -euo pipefail

readonly SYFT_VERSION="1.51.0"
readonly SYFT_LINUX_AMD64_ARCHIVE_SHA256="2a2e837a2c8d59ec9af5472ee22d3b04ee463c4e44476ecf993fd1e5ab6ebc7f"
readonly ARCHIVE="syft_${SYFT_VERSION}_linux_amd64.tar.gz"
readonly URL="https://github.com/anchore/syft/releases/download/v${SYFT_VERSION}/${ARCHIVE}"
readonly RELEASE_API_URL="https://api.github.com/repos/anchore/syft/releases/tags/v${SYFT_VERSION}"

install_dir="${1:-${RUNNER_TEMP:-/tmp}/coglatas-syft/bin}"
tmp_dir="$(mktemp -d)"
trap 'rm -rf "$tmp_dir"' EXIT

mkdir -p "$install_dir"

curl_common=(
  --fail
  --silent
  --show-error
  --location
  --proto '=https'
  --proto-redir '=https'
  --tlsv1.2
  --max-redirs 5
  --retry 5
  --retry-all-errors
  --retry-delay 2
  --connect-timeout 10
  --max-time 180
)

github_api_headers=(
  --header 'X-GitHub-Api-Version: 2022-11-28'
)
if [[ -n "${GITHUB_TOKEN:-}" ]]; then
  github_api_headers+=(--header "Authorization: Bearer ${GITHUB_TOKEN}")
fi

download_archive() {
  local url=$1
  curl \
    "${curl_common[@]}" \
    --write-out '%{url_effective}' \
    "$url" \
    --output "$tmp_dir/$ARCHIVE"
}

effective_url=""
if ! effective_url="$(download_archive "$URL")"; then
  echo "Direct Syft release download failed; retrying through the GitHub release asset API." >&2
  release_json="$(
    curl \
      "${curl_common[@]}" \
      "${github_api_headers[@]}" \
      --header 'Accept: application/vnd.github+json' \
      "$RELEASE_API_URL"
  )"
  asset_id="$(
    python3 -c '
import json
import sys

archive = sys.argv[1]
document = json.load(sys.stdin)
for asset in document.get("assets", []):
    if asset.get("name") == archive:
        asset_id = asset.get("id")
        if isinstance(asset_id, int):
            print(asset_id)
            raise SystemExit(0)
raise SystemExit(f"Syft release asset {archive!r} was not found in the pinned release metadata.")
' "$ARCHIVE" <<<"$release_json"
  )"
  rm -f "$tmp_dir/$ARCHIVE"
  effective_url="$(
    curl \
      "${curl_common[@]}" \
      "${github_api_headers[@]}" \
      --header 'Accept: application/octet-stream' \
      --write-out '%{url_effective}' \
      "https://api.github.com/repos/anchore/syft/releases/assets/${asset_id}" \
      --output "$tmp_dir/$ARCHIVE"
  )"
fi

effective_host="${effective_url#https://}"
effective_host="${effective_host%%/*}"
case "$effective_host" in
  github.com|api.github.com|*.githubusercontent.com)
    ;;
  *)
    echo "Syft release download ended at an untrusted host." >&2
    exit 1
    ;;
esac

printf '%s  %s\n' "$SYFT_LINUX_AMD64_ARCHIVE_SHA256" "$tmp_dir/$ARCHIVE" | sha256sum --check --strict
tar -xzf "$tmp_dir/$ARCHIVE" -C "$tmp_dir" syft
install -m 0755 "$tmp_dir/syft" "$install_dir/syft"

version_output="$($install_dir/syft version)"
printf '%s\n' "$version_output"
printf '%s\n' "$version_output" | grep -Eq "Version:[[:space:]]*${SYFT_VERSION}([[:space:]]|$)" || {
  echo "Installed Syft version does not match pinned version ${SYFT_VERSION}." >&2
  exit 1
}

if [[ -n "${GITHUB_PATH:-}" ]]; then
  printf '%s\n' "$install_dir" >> "$GITHUB_PATH"
else
  printf 'Add %s to PATH to use Syft.\n' "$install_dir"
fi
