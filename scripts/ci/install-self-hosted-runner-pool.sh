#!/usr/bin/env bash
set -Eeuo pipefail

readonly DEFAULT_RUNNER_VERSION="2.335.1"
readonly DEFAULT_RUNNER_X64_SHA256="4ef2f25285f0ae4477f1fe1e346db76d2f3ebf03824e2ddd1973a2819bf6c8cf"
readonly DEFAULT_RUNNER_ARM64_SHA256="6d1e85bfd1a506a8b17c1f1b9b57dba458ffed90898799aaa9f599520b0d9207"

usage() {
  cat <<'EOF'
Install additional GitHub Actions runners on the current Linux host.

The existing runner remains in place. By default this script adds three more
runner services, allowing up to four self-hosted jobs to execute concurrently.

Usage:
  sudo RUNNER_TOKEN='<registration-token>' \
    ./scripts/ci/install-self-hosted-runner-pool.sh \
    --url https://github.com/NYGsatoshi/Coglatas

Options:
  --url URL               GitHub.com repository or organization URL. Required.
  --token TOKEN           Registration token. Prefer RUNNER_TOKEN instead.
  --count NUMBER          Additional runners to create. Default: 3.
  --start-index NUMBER    First numeric suffix. Default: 2.
  --name-prefix PREFIX    Runner name prefix. Default: coglatasci.
  --user-prefix PREFIX    Linux account prefix. Default: aiprunner.
  --root PATH             Installation root. Default: /opt/coglatas-actions-runners.
  --version VERSION       actions/runner version. Default: 2.335.1.
  --sha256 SHA256         Archive SHA256. Required for non-default versions.
                          May also be supplied through RUNNER_SHA256.
  --labels LABELS         Additional comma-separated labels.
                          Default: coglatasci-pool.
  -h, --help              Show this help.

The default actions/runner release digests are repository-pinned from GitHub's
release metadata. A custom runner version is never downloaded unless its SHA256
is supplied explicitly.

A repository registration token is short-lived. Generate it immediately before
running this script from Settings > Actions > Runners > New self-hosted runner.
EOF
}

repo_url=""
runner_token="${RUNNER_TOKEN:-}"
runner_count=3
start_index=2
name_prefix="coglatasci"
user_prefix="aiprunner"
install_root="/opt/coglatas-actions-runners"
runner_version="$DEFAULT_RUNNER_VERSION"
runner_sha256="${RUNNER_SHA256:-}"
extra_labels="coglatasci-pool"

while [[ $# -gt 0 ]]; do
  case "$1" in
    --url)
      repo_url="${2:-}"
      shift 2
      ;;
    --token)
      runner_token="${2:-}"
      shift 2
      ;;
    --count)
      runner_count="${2:-}"
      shift 2
      ;;
    --start-index)
      start_index="${2:-}"
      shift 2
      ;;
    --name-prefix)
      name_prefix="${2:-}"
      shift 2
      ;;
    --user-prefix)
      user_prefix="${2:-}"
      shift 2
      ;;
    --root)
      install_root="${2:-}"
      shift 2
      ;;
    --version)
      runner_version="${2:-}"
      shift 2
      ;;
    --sha256)
      runner_sha256="${2:-}"
      shift 2
      ;;
    --labels)
      extra_labels="${2:-}"
      shift 2
      ;;
    -h|--help)
      usage
      exit 0
      ;;
    *)
      echo "Unknown option: $1" >&2
      usage >&2
      exit 2
      ;;
  esac
done

if [[ $EUID -ne 0 ]]; then
  echo "Run this installer with sudo or as root." >&2
  exit 1
fi

if [[ -z "$repo_url" ]]; then
  echo "--url is required." >&2
  exit 2
fi

case "$repo_url" in
  https://github.com/*)
    ;;
  *)
    echo "--url must use https://github.com/." >&2
    exit 2
    ;;
esac

if [[ -z "$runner_token" ]]; then
  echo "Set RUNNER_TOKEN or provide --token." >&2
  exit 2
fi

if ! [[ "$runner_count" =~ ^[1-9][0-9]*$ ]]; then
  echo "--count must be a positive integer." >&2
  exit 2
fi

if ! [[ "$start_index" =~ ^[1-9][0-9]*$ ]]; then
  echo "--start-index must be a positive integer." >&2
  exit 2
fi

if ! [[ "$runner_version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
  echo "--version must be a semantic version such as 2.335.1." >&2
  exit 2
fi

for command_name in curl sha256sum tar useradd usermod systemctl; do
  if ! command -v "$command_name" >/dev/null 2>&1; then
    echo "Required command not found: $command_name" >&2
    exit 1
  fi
done

if ! getent group docker >/dev/null 2>&1; then
  echo "Docker group does not exist. Install Docker before adding the runner pool." >&2
  exit 1
fi

case "$(uname -m)" in
  x86_64|amd64)
    runner_arch="x64"
    ;;
  aarch64|arm64)
    runner_arch="arm64"
    ;;
  *)
    echo "Unsupported architecture: $(uname -m)" >&2
    exit 1
    ;;
esac

if [[ -z "$runner_sha256" ]]; then
  case "${runner_version}:${runner_arch}" in
    "${DEFAULT_RUNNER_VERSION}:x64")
      runner_sha256="$DEFAULT_RUNNER_X64_SHA256"
      ;;
    "${DEFAULT_RUNNER_VERSION}:arm64")
      runner_sha256="$DEFAULT_RUNNER_ARM64_SHA256"
      ;;
    *)
      echo "A custom actions/runner version requires --sha256 or RUNNER_SHA256." >&2
      exit 2
      ;;
  esac
fi

if ! [[ "$runner_sha256" =~ ^[0-9A-Fa-f]{64}$ ]]; then
  echo "Runner archive SHA256 must be exactly 64 hexadecimal characters." >&2
  exit 2
fi
runner_sha256="${runner_sha256,,}"

cache_dir="$install_root/cache"
archive_name="actions-runner-linux-${runner_arch}-${runner_version}.tar.gz"
archive_path="$cache_dir/$archive_name"
download_path="$cache_dir/.${archive_name}.download.$$"
download_url="https://github.com/actions/runner/releases/download/v${runner_version}/${archive_name}"

install -d -m 0755 "$cache_dir"
trap 'rm -f "$download_path"' EXIT

if [[ ! -s "$archive_path" ]]; then
  echo "Downloading actions/runner v${runner_version} for ${runner_arch}..."
  effective_url="$(
    curl \
      --fail \
      --silent \
      --show-error \
      --location \
      --proto '=https' \
      --proto-redir '=https' \
      --tlsv1.2 \
      --max-redirs 5 \
      --retry 3 \
      --retry-all-errors \
      --connect-timeout 10 \
      --max-time 600 \
      --write-out '%{url_effective}' \
      --output "$download_path" \
      "$download_url"
  )"
  effective_host="${effective_url#https://}"
  effective_host="${effective_host%%/*}"
  case "$effective_host" in
    github.com|*.githubusercontent.com)
      ;;
    *)
      echo "actions/runner download ended at an untrusted host." >&2
      exit 1
      ;;
  esac
  mv -f "$download_path" "$archive_path"
  chmod 0644 "$archive_path"
fi

if ! printf '%s  %s\n' "$runner_sha256" "$archive_path" | sha256sum --check --strict; then
  echo "actions/runner archive failed the pinned SHA256 check; refusing to extract it." >&2
  rm -f "$archive_path"
  exit 1
fi

last_index=$((start_index + runner_count - 1))

for index in $(seq "$start_index" "$last_index"); do
  runner_name="${name_prefix}-${index}"
  runner_user="${user_prefix}${index}"
  runner_dir="$install_root/$runner_name"

  echo
  echo "Configuring $runner_name as Linux user $runner_user"

  if ! id "$runner_user" >/dev/null 2>&1; then
    useradd --create-home --shell /bin/bash "$runner_user"
  fi

  usermod --append --groups docker "$runner_user"
  install -d -o "$runner_user" -g "$runner_user" -m 0755 "$runner_dir"

  if [[ ! -x "$runner_dir/config.sh" ]]; then
    tar --extract --gzip --file "$archive_path" --directory "$runner_dir"
    chown -R "$runner_user:$runner_user" "$runner_dir"
  fi

  if [[ ! -f "$runner_dir/.runner" ]]; then
    runuser -u "$runner_user" -- \
      "$runner_dir/config.sh" \
      --unattended \
      --url "$repo_url" \
      --token "$runner_token" \
      --name "$runner_name" \
      --labels "$extra_labels" \
      --work _work \
      --replace
  else
    echo "$runner_name is already configured; preserving its registration."
  fi

  if [[ ! -f "$runner_dir/.service" ]]; then
    (
      cd "$runner_dir"
      ./svc.sh install "$runner_user"
    )
  fi

  (
    cd "$runner_dir"
    ./svc.sh start
    ./svc.sh status
  )
done

cat <<EOF

Runner pool installation completed.

Existing runner: expected to provide one concurrent slot.
Additional runners installed: $runner_count
Expected total concurrent self-hosted jobs: $((runner_count + 1))

All added runners have the default self-hosted/Linux architecture labels plus:
  $extra_labels

Verify them in GitHub:
  Settings > Actions > Runners
EOF
