#!/usr/bin/env bash
# SEC-06 authenticated OWASP ZAP API DAST runner.
# Source this file after SEC-03 security_scan_preflight. It reuses the harness
# sessions and is intentionally runnable only against the isolated Compose app.

ZAP_VERSION="2.17.0"
ZAP_PLATFORM="linux/amd64"
ZAP_IMAGE="zaproxy/zap-stable:2.17.0@sha256:781a2bdaea47324e7bab583e2263f21d257b0aee61ed51521a5be45f5f5081ef"
ZAP_CONTRACT="artifacts/openapi/coglatas-openapi.json"
ZAP_AUTOMATION_PLAN="scripts/security/zap-automation.yaml"
ZAP_POLICY="scripts/security/zap-policy.json"

security_zap_fail() {
  printf 'SEC-06 ZAP failed: %s\n' "$*" >&2
  return 1
}

security_zap_require_contract() {
  [[ -f "$ZAP_CONTRACT" ]] || security_zap_fail "authoritative SEC-01 OpenAPI artifact is missing" || return 1
  [[ -f "$ZAP_AUTOMATION_PLAN" ]] || security_zap_fail "repository-owned Automation Framework plan is missing" || return 1
  [[ -f "$ZAP_POLICY" ]] || security_zap_fail "repository-owned ZAP policy manifest is missing" || return 1
  python3 scripts/ci/verify-openapi.py "$ZAP_CONTRACT" ||
    security_zap_fail "authoritative SEC-01 OpenAPI artifact failed re-verification" || return 1
  return 0
}

security_zap_roles() {
  # alpha-restricted is the lower-privilege representative requested by SEC-06.
  # SEC-05 separately owns exhaustive actor x authorization semantics.
  printf '%s\n' alpha-owner alpha-restricted beta-owner || return 1
  return 0
}

security_zap_require_target() {
  local target=${SECURITY_SCAN_TARGET:-}
  local host normalized
  security_scan_require_boundary || return 1
  normalized="$(security_scan_validate_target "$target")" || return 1
  [[ "$normalized" == "$target" ]] ||
    security_zap_fail "SEC-06 target must already be normalized by SEC-03" || return 1
  host="$(security_scan_target_host "$target")" || return 1
  [[ "$host" == "app" ]] ||
    security_zap_fail "required SEC-06 runtime accepts only the SEC-02 Compose service origin" || return 1
  security_scan_require_transport_binding "$target" || return 1
}

security_zap_require_internal_network() {
  local network=$1 app_container=$2
  [[ -n "$network" && -n "$app_container" ]] ||
    security_zap_fail "internal scanner network and app container are required" || return 1
  docker network inspect "$network" >/dev/null 2>&1 ||
    security_zap_fail "scanner network '$network' does not exist" || return 1

  docker network inspect "$network" | python3 -c '
import json
import sys
document = json.load(sys.stdin)
if len(document) != 1 or document[0].get("Internal") is not True:
    raise SystemExit("SEC-06 scanner network must be Docker internal=true")
' || return 1

  # Network aliases are endpoint settings on the container. `docker network
  # inspect` exposes attached container IDs/addresses but does not reliably
  # expose the endpoint Aliases array, so validate the alias from container
  # NetworkSettings instead of treating a missing field as a topology failure.
  docker inspect "$app_container" --format '{{json .NetworkSettings.Networks}}' | python3 -c '
import json
import sys
network = sys.argv[1]
networks = json.load(sys.stdin)
endpoint = networks.get(network)
if not isinstance(endpoint, dict):
    raise SystemExit("SEC-02 app is not attached to the SEC-06 internal scanner network")
aliases = endpoint.get("Aliases") or []
if "app" not in aliases:
    raise SystemExit("SEC-02 app must have the app alias on the SEC-06 internal scanner network")
' "$network" || return 1
  return 0
}

security_zap_target_regex() {
  python3 - "$SECURITY_SCAN_TARGET" <<'PY' || return 1
import re
import sys
print(re.escape(sys.argv[1]))
PY
  return 0
}

security_zap_cookie_header() {
  local role=$1 jar
  jar="$(security_scan_host_path "${role}.cookies")" || return 1
  [[ -s "$jar" ]] || security_zap_fail "SEC-03 cookie jar is missing for '$role'" || return 1
  python3 - "$jar" <<'PY'
from pathlib import Path
import sys
cookies = []
for raw in Path(sys.argv[1]).read_text(encoding="utf-8").splitlines():
    if raw.startswith("#HttpOnly_"):
        raw = raw[len("#HttpOnly_"):]
    elif not raw or raw.startswith("#"):
        continue
    parts = raw.split("\t")
    if len(parts) < 7:
        raise SystemExit("invalid SEC-03 Netscape cookie jar")
    name, value = parts[5], parts[6]
    if name and value:
        cookies.append((name, value))
if not cookies:
    raise SystemExit("SEC-03 cookie jar contains no session cookies")
print("; ".join(f"{name}={value}" for name, value in cookies))
PY
}

security_zap_forbidden_values_json() {
  local role=$1 cookie_header=$2 csrf=$3 jar
  jar="$(security_scan_host_path "${role}.cookies")" || return 1
  SECURITY_ZAP_COOKIE_HEADER="$cookie_header" \
  SECURITY_ZAP_CSRF="$csrf" \
  SECURITY_ZAP_FIXTURE_PASSWORD="${COGLATAS_SECURITY_CI_PASSWORD:-}" \
    python3 - "$jar" <<'PY' || return 1
from pathlib import Path
import json
import os
import sys
values = {
    os.environ.get("SECURITY_ZAP_COOKIE_HEADER", ""),
    os.environ.get("SECURITY_ZAP_CSRF", ""),
    os.environ.get("SECURITY_ZAP_FIXTURE_PASSWORD", ""),
}
for raw in Path(sys.argv[1]).read_text(encoding="utf-8").splitlines():
    if raw.startswith("#HttpOnly_"):
        raw = raw[len("#HttpOnly_"):]
    elif not raw or raw.startswith("#"):
        continue
    parts = raw.split("\t")
    if len(parts) >= 7 and parts[5] and parts[6]:
        name, value = parts[5], parts[6]
        values.add(value)
        values.add(f"{name}={value}")
print(json.dumps(sorted(value for value in values if value), separators=(",", ":")))
PY
  return 0
}

security_zap_redact_stream() {
  # SEC-03 already redacts common header forms. Consume the same complete
  # forbidden-value set used by the evidence sanitizer so raw cookie values,
  # cookie pairs, serialized Cookie headers, CSRF tokens, and fixture secrets
  # cannot survive non-canonical ZAP/add-on log output.
  security_scan_redact_stream | python3 -c '
import json
import os
import sys
text = sys.stdin.read()
raw = os.environ.get("COGLATAS_SECURITY_ZAP_FORBIDDEN_VALUES", "")
values = json.loads(raw) if raw else []
if not isinstance(values, list) or not all(isinstance(value, str) for value in values):
    raise SystemExit("COGLATAS_SECURITY_ZAP_FORBIDDEN_VALUES must be a JSON array of strings")
for value in values:
    if value:
        text = text.replace(value, "[REDACTED]")
sys.stdout.write(text)
' || return 1
  return 0
}

security_zap_verify_toolchain() {
  local version addon_list required addon_hash attempt
  for attempt in 1 2 3; do
    if docker pull --platform "$ZAP_PLATFORM" "$ZAP_IMAGE" >/dev/null; then
      break
    fi
    if (( attempt == 3 )); then
      security_zap_fail "failed to pull the immutable ZAP image after 3 attempts"
      return 1
    fi
    sleep $((attempt * 5))
  done

  version="$(docker run --rm --platform "$ZAP_PLATFORM" --entrypoint /zap/zap.sh "$ZAP_IMAGE" -cmd -silent -version 2>&1 | tr -d '\r')" ||
    security_zap_fail "pinned image could not report its ZAP version" || return 1
  [[ "$version" == *"$ZAP_VERSION"* ]] ||
    security_zap_fail "pinned image did not report ZAP $ZAP_VERSION" || return 1

  addon_list="$(docker run --rm --platform "$ZAP_PLATFORM" --entrypoint /zap/zap.sh "$ZAP_IMAGE" -cmd -silent -addonlist 2>&1 | tr -d '\r')" ||
    security_zap_fail "pinned image could not enumerate its add-ons" || return 1
  for required in automation openapi pscan pscanrules ascanrules reports replacer; do
    grep -Eiq "(^|[^[:alnum:]_-])${required}([^[:alnum:]_-]|$)" <<<"$addon_list" ||
      security_zap_fail "immutable image is missing required add-on '$required'" || return 1
  done
  addon_hash="$(printf '%s' "$addon_list" | sha256sum | cut -d ' ' -f1)"
  [[ "$addon_hash" =~ ^[0-9a-f]{64}$ ]] || return 1
  SECURITY_ZAP_ADDON_LIST_SHA256="$addon_hash"
  export SECURITY_ZAP_ADDON_LIST_SHA256
}

security_zap_run_role() {
  local role=$1 network=$2 mount_root=$3
  local tenant cookie_header target_regex raw_host report_name output metadata
  local forbidden_json status process_status role_timeout container_name plan_host plan_name

  security_scan_verify_context "$role" ||
    security_zap_fail "SEC-03 authenticated context verification failed for '$role'" || return 1
  security_scan_fetch_csrf "$role" ||
    security_zap_fail "SEC-03 CSRF refresh failed for '$role'" || return 1

  tenant="$(security_scan_role_tenant "$role")" || return 1
  cookie_header="$(security_zap_cookie_header "$role")" || return 1
  target_regex="$(security_zap_target_regex)" || return 1
  forbidden_json="$(security_zap_forbidden_values_json "$role" "$cookie_header" "$SECURITY_SCAN_CSRF_TOKEN")" || return 1

  report_name="zap-${role}-raw.json"
  raw_host="$(security_scan_host_path "$report_name")"
  output="artifacts/security/zap/${role}.json"
  metadata="artifacts/security/zap/${role}.metadata.json"
  rm -f -- "$raw_host" "$output" "$metadata"
  mkdir -p artifacts/security/zap

  export COGLATAS_SECURITY_ZAP_TARGET="$SECURITY_SCAN_TARGET"
  export COGLATAS_SECURITY_ZAP_TARGET_REGEX="$target_regex"
  export COGLATAS_SECURITY_ZAP_TENANT="$tenant"
  export COGLATAS_SECURITY_ZAP_COOKIE="$cookie_header"
  export COGLATAS_SECURITY_ZAP_CSRF_TOKEN="$SECURITY_SCAN_CSRF_TOKEN"
  # The host state directory is mounted into the scanner container at /state.
  # Keep raw_host as the host-side path used by report processing, but direct
  # the in-container Automation Framework report job to its writable mount.
  export COGLATAS_SECURITY_ZAP_REPORT_DIR="/state"
  export COGLATAS_SECURITY_ZAP_REPORT_FILE="$report_name"
  export COGLATAS_SECURITY_ZAP_FORBIDDEN_VALUES="$forbidden_json"

  plan_name="zap-${role}-plan.yaml"
  plan_host="${SECURITY_SCAN_STATE_DIR}/$plan_name"
  python3 scripts/security/render-zap-plan.py "$ZAP_AUTOMATION_PLAN" "$plan_host" || return 1

  role_timeout="${COGLATAS_SECURITY_ZAP_ROLE_TIMEOUT:-15m}"
  container_name="sec06-zap-${role}-$$"
  printf 'SEC-06 ZAP: role=%s target=%s policy=sec06-strict-api timeout=%s\n' \
    "$role" "$SECURITY_SCAN_TARGET" "$role_timeout"

  set +e
  timeout --signal=TERM --kill-after=30s "$role_timeout" \
    docker run --rm \
      --name "$container_name" \
      --platform "$ZAP_PLATFORM" \
      --user "$(id -u):$(id -g)" \
      --network "$network" \
      --read-only \
      --cap-drop ALL \
      --security-opt no-new-privileges \
      --tmpfs /tmp:rw,nosuid,nodev,size=768m \
      --workdir /work \
      -e HOME=/tmp \
      -e COGLATAS_SECURITY_ZAP_PLAN="/state/$plan_name" \
      -v "$PWD:/work:ro" \
      -v "$mount_root:/state" \
      --entrypoint /bin/bash \
      "$ZAP_IMAGE" \
      -lc 'mkdir -p /tmp/zap-home && exec /zap/zap.sh -cmd -silent -dir /tmp/zap-home -autorun "$COGLATAS_SECURITY_ZAP_PLAN"' \
      2>&1 | security_zap_redact_stream
  status=${PIPESTATUS[0]}
  if (( status != 0 )); then
    docker rm -f "$container_name" >/dev/null 2>&1 || true
  fi
  set -e
  rm -f -- "$plan_host"

  set +e
  python3 scripts/security/process-zap-report.py \
    --raw-report "$raw_host" \
    --output "$output" \
    --metadata "$metadata" \
    --role "$role" \
    --target "$SECURITY_SCAN_TARGET" \
    --scanner-version "$ZAP_VERSION" \
    --scanner-image "$ZAP_IMAGE" \
    --scanner-exit "$status" \
    --contract "$ZAP_CONTRACT" \
    --automation-plan "$ZAP_AUTOMATION_PLAN" \
    --policy "$ZAP_POLICY" \
    --addon-list-sha256 "$SECURITY_ZAP_ADDON_LIST_SHA256"
  process_status=$?
  set -e

  unset COGLATAS_SECURITY_ZAP_TARGET COGLATAS_SECURITY_ZAP_TARGET_REGEX COGLATAS_SECURITY_ZAP_TENANT
  unset COGLATAS_SECURITY_ZAP_COOKIE COGLATAS_SECURITY_ZAP_CSRF_TOKEN
  unset COGLATAS_SECURITY_ZAP_REPORT_DIR COGLATAS_SECURITY_ZAP_REPORT_FILE
  unset COGLATAS_SECURITY_ZAP_FORBIDDEN_VALUES

  (( process_status == 0 )) || return "$process_status"
  (( status == 0 )) || security_zap_fail "role '$role' scanner exited $status" || return 1

  # A successful ZAP process is not meaningful evidence if it destroyed or lost
  # its authenticated session during scanning.
  security_scan_verify_context "$role" ||
    security_zap_fail "role '$role' lost meaningful authenticated coverage" || return 1
  security_scan_health ||
    security_zap_fail "application became unhealthy after role '$role'" || return 1

  if [[ -n "${GITHUB_STEP_SUMMARY:-}" ]]; then
    {
      printf '### SEC-06 OWASP ZAP — %s\n\n' "$role"
      printf -- '- Tool: `OWASP ZAP %s` / `%s`\n' "$ZAP_VERSION" "$ZAP_IMAGE"
      printf -- '- OpenAPI SHA-256: `%s`\n' "$(sha256sum "$ZAP_CONTRACT" | cut -d ' ' -f1)"
      printf -- '- Automation plan SHA-256: `%s`\n' "$(sha256sum "$ZAP_AUTOMATION_PLAN" | cut -d ' ' -f1)"
      printf -- '- Policy SHA-256: `%s`\n' "$(sha256sum "$ZAP_POLICY" | cut -d ' ' -f1)"
      printf -- '- Add-on inventory SHA-256: `%s`\n' "$SECURITY_ZAP_ADDON_LIST_SHA256"
      printf -- '- Sanitized report: `%s`\n\n' "$output"
    } >> "$GITHUB_STEP_SUMMARY"
  fi
}

security_zap_run_matrix() {
  local network=$1 mount_root=$2 app_container=$3 role
  local stage_file="artifacts/security/zap/preflight-stage.txt"

  rm -rf artifacts/security/zap
  mkdir -p artifacts/security/zap
  printf 'stage=init\n' > "$stage_file"

  security_scan_require_no_xtrace || return 1
  printf 'stage=contract\n' > "$stage_file"
  security_zap_require_contract || return 1
  printf 'stage=target\n' > "$stage_file"
  security_zap_require_target || return 1
  printf 'stage=internal-network\n' > "$stage_file"
  security_zap_require_internal_network "$network" "$app_container" || return 1
  printf 'stage=toolchain\n' > "$stage_file"
  security_zap_verify_toolchain || return 1
  printf 'stage=role-scan\n' > "$stage_file"

  while IFS= read -r role <&3; do
    [[ -n "$role" ]] || continue
    security_zap_run_role "$role" "$network" "$mount_root" || return 1
  done 3< <(security_zap_roles)

  printf 'SEC-06 OWASP ZAP authenticated API DAST passed: roles=%s image=%s\n' \
    "$(security_zap_roles | paste -sd, -)" "$ZAP_IMAGE"
}
