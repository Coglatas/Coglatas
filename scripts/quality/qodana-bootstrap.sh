#!/usr/bin/env bash
set -Eeuo pipefail

readonly DOTNET_INSTALL_SCRIPTS_COMMIT="47940ac9fc30a2f2dd19167165d0bb0774625f67"
readonly DOTNET_INSTALL_SCRIPT_BLOB_SHA1="bd13ffa6656fe776c95b561fe4df640918867523"
readonly DOTNET_INSTALL_SCRIPT_URL="https://raw.githubusercontent.com/dotnet/install-scripts/${DOTNET_INSTALL_SCRIPTS_COMMIT}/src/dotnet-install.sh"

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$repo_root"

export DOTNET_CLI_TELEMETRY_OPTOUT="${DOTNET_CLI_TELEMETRY_OPTOUT:-1}"
export DOTNET_NOLOGO="${DOTNET_NOLOGO:-1}"

required_sdk="$(awk -F'"' '/"version"[[:space:]]*:/ { print $4; exit }' global.json)"
if [[ -z "$required_sdk" ]]; then
  echo "Unable to read sdk.version from global.json" >&2
  exit 1
fi

download_verified_dotnet_installer() {
  local installer="$1"
  local actual_blob_sha

  for command_name in curl git; do
    if ! command -v "$command_name" >/dev/null 2>&1; then
      echo "Required command not found for verified .NET bootstrap: $command_name" >&2
      exit 1
    fi
  done

  curl \
    --fail \
    --silent \
    --show-error \
    --proto '=https' \
    --tlsv1.2 \
    --retry 3 \
    --retry-all-errors \
    --connect-timeout 10 \
    --max-time 120 \
    "$DOTNET_INSTALL_SCRIPT_URL" \
    --output "$installer"

  actual_blob_sha="$(git hash-object "$installer")"
  if [[ "$actual_blob_sha" != "$DOTNET_INSTALL_SCRIPT_BLOB_SHA1" ]]; then
    echo "Downloaded dotnet-install.sh does not match the repository-pinned Microsoft Git blob." >&2
    rm -f "$installer"
    exit 1
  fi
  chmod 0700 "$installer"
}

install_dotnet_sdk() {
  local install_dir="${QODANA_DOTNET_INSTALL_DIR:-/usr/share/dotnet}"
  local installer="/tmp/dotnet-install.sh"

  download_verified_dotnet_installer "$installer"

  if [[ ! -w "$install_dir" ]]; then
    if command -v sudo >/dev/null 2>&1; then
      sudo bash "$installer" --version "$required_sdk" --install-dir "$install_dir" --no-path
      return
    fi

    install_dir="${HOME}/.dotnet"
    mkdir -p "$install_dir"
  fi

  bash "$installer" --version "$required_sdk" --install-dir "$install_dir" --no-path
  export DOTNET_ROOT="$install_dir"
  export PATH="$install_dir:$PATH"
}

if ! dotnet --list-sdks 2>/dev/null | awk '{ print $1 }' | grep -Fxq "$required_sdk"; then
  echo "Installing .NET SDK $required_sdk required by global.json"
  install_dotnet_sdk
fi

echo "Using .NET SDKs:"
dotnet --list-sdks
dotnet --info
dotnet msbuild -version

echo "Restoring canonical solution Coglatas.slnx"
dotnet restore Coglatas.slnx --verbosity normal

if [[ "${QODANA_REUSE_PREBUILT_DOTNET_BUILD:-false}" == "true" ]]; then
  echo "Reusing trusted main .NET build outputs; Qodana bootstrap build is skipped."
  test -f artifacts/ci/dotnet-build-sha
  test -d src/Coglatas.Web/bin/Release
  test -d tests/Coglatas.Tests/bin/Release
else
  echo "Building canonical solution Coglatas.slnx"
  dotnet build Coglatas.slnx --configuration Release --no-restore
fi

if [[ "${QODANA_SKIP_FRONTEND_BOOTSTRAP:-false}" == "true" ]]; then
  echo "Skipping frontend bootstrap for the .NET-only Qodana inventory."
elif command -v npm >/dev/null 2>&1; then
  echo "Using Node.js $(node --version)"
  echo "Using npm $(npm --version)"

  echo "Restoring root UI test dependencies with reviewed lifecycle policy"
  bash scripts/ci/npm-ci-retry.sh .

  echo "Restoring active Angular workspace dependencies with reviewed lifecycle policy"
  bash scripts/ci/npm-ci-retry.sh frontend

  echo "Building active Angular workspace"
  npm --prefix frontend run build
else
  if [[ "${QODANA_FRONTEND_REQUIRED:-false}" == "true" ]]; then
    echo "npm is required for qodana-dotnet full-stack analysis but is unavailable." >&2
    exit 1
  fi

  echo "npm is unavailable; skipping frontend bootstrap for the Community .NET-only linter."
fi
