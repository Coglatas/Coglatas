#!/usr/bin/env bash
set -Eeuo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$repo_root"

# shellcheck source=scripts/ci/parallel-lane-lib.sh
source scripts/ci/parallel-lane-lib.sh

enabled() {
  [[ "${1:-false}" == "true" ]]
}

run_build() { npm --prefix frontend run build; }
run_unit() { npm --prefix frontend test; }

run_static() {
  if enabled "${FRONTEND_ARCHITECTURE:-false}"; then
    npm --prefix frontend run check:architecture
    npm --prefix frontend run test:architecture
  fi
  if enabled "${FRONTEND_LICENSE_GUARD:-false}"; then
    npm --prefix frontend run test:syncfusion-license
  fi
}

run_storybook() {
  NODE_OPTIONS=--max-old-space-size=4096 npm --prefix frontend run build-storybook
}

run_playwright() { npm run test:ui:angular:docker; }

enabled "${FRONTEND_BUILD:-false}" && ci_parallel_start frontend-build run_build
enabled "${FRONTEND_UNIT:-false}" && ci_parallel_start frontend-unit run_unit
if enabled "${FRONTEND_ARCHITECTURE:-false}" || enabled "${FRONTEND_LICENSE_GUARD:-false}"; then
  ci_parallel_start frontend-static run_static
fi
enabled "${FRONTEND_STORYBOOK:-false}" && ci_parallel_start frontend-storybook run_storybook
enabled "${FRONTEND_PLAYWRIGHT:-false}" && ci_parallel_start frontend-playwright run_playwright

ci_parallel_wait
