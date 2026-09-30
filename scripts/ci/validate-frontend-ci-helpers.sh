#!/usr/bin/env bash
set -Eeuo pipefail

python3 -m py_compile \
  scripts/ci/verify-mvp-a-required-a11y-tests.py \
  scripts/ci/verify-mvp-a-final-checks.py
bash -n scripts/ci/mvp-a-authz-boundary-acceptance.sh
bash -n scripts/ci/run-mvp-a-authz-boundary-acceptance.sh

python3 - <<'PY'
from pathlib import Path

source = Path("scripts/ci/mvp-a-authz-boundary-acceptance.sh").read_text(encoding="utf-8")
start_marker = "node --input-type=module <<'NODE'\n"
start = source.index(start_marker) + len(start_marker)
end = source.rindex("\nNODE\n")
Path("/tmp/mvp-a-authz-boundary-acceptance.mjs").write_text(
    source[start:end], encoding="utf-8"
)
PY

node --check /tmp/mvp-a-authz-boundary-acceptance.mjs
python3 scripts/ci/verify-mvp-a-required-a11y-tests.py
