#!/usr/bin/env bash
set -Eeuo pipefail

gate="${1:-functional-fast}"
case "$gate" in
  functional-fast|functional-full|functional-extended) ;;
  *)
    printf 'Usage: %s [functional-fast|functional-full|functional-extended]\n' "$0" >&2
    exit 2
    ;;
esac

if [[ -z "${COGLATAS_SECURITY_CI_PASSWORD:-}" ]]; then
  if command -v openssl >/dev/null 2>&1; then
    COGLATAS_SECURITY_CI_PASSWORD="Fci07!$(openssl rand -hex 24)"
  else
    COGLATAS_SECURITY_CI_PASSWORD="Fci07!synthetic-${GITHUB_RUN_ID:-local}-${BASHPID}"
  fi
  export COGLATAS_SECURITY_CI_PASSWORD
fi

export COGLATAS_FCI07_GATE="$gate"
export FUNCTIONAL_COMPOSE_FILES="infra/compose/test/real-backend-smoke.yml,infra/compose/security/security.yml,infra/compose/security/fci07-functional-security.yml"

exec bash scripts/ci/functional-compose-harness.sh
