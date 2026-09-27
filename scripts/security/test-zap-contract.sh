#!/usr/bin/env bash
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$root"

plan="scripts/security/zap-automation.yaml"
policy="scripts/security/zap-policy.json"
runner="scripts/security/zap-runner.sh"
processor="scripts/security/process-zap-report.py"
required_active_rule_ids="6,40003,40008,40012,40014,40018,40022,90020"

test_fail() {
  printf 'SEC-06 contract test failed: %s\n' "$*" >&2
  exit 1
}

expect_failure_contains() {
  local expected=$1
  shift
  local output status
  set +e
  output="$("$@" 2>&1)"
  status=$?
  set -e
  (( status != 0 )) || test_fail "command unexpectedly succeeded; expected rejection containing '$expected'"
  [[ "$output" == *"$expected"* ]] ||
    test_fail "expected rejection containing '$expected', got: $output"
}

validate_automation_plan() {
  local candidate=$1
  ruby - "$candidate" "$required_active_rule_ids" <<'RUBY'
require "yaml"

path = ARGV.fetch(0)
required_active_rule_ids = ARGV.fetch(1).split(",")

def fail!(message)
  warn message
  exit 1
end

def only_job(jobs, type)
  matches = jobs.select { |job| job.is_a?(Hash) && job["type"] == type }
  fail!("Automation plan must contain exactly one #{type} job") unless matches.length == 1
  matches.first
end

def hash_array(value, message)
  fail!(message) unless value.is_a?(Array) && value.all? { |item| item.is_a?(Hash) }
  value
end

def require_blocking_stats_test(tests, statistic, operator, value, message)
  matches = tests.select { |test| test["statistic"].to_s == statistic }
  fail!(message) unless matches.length == 1

  test = matches.first
  unless test["type"] == "stats" &&
         test["operator"] == operator &&
         test["value"] == value &&
         test["onFail"] == "error"
    fail!(
      "#{message}: expected type=stats statistic=#{statistic.inspect} " \
      "operator=#{operator.inspect} value=#{value.inspect} onFail=error"
    )
  end
end

begin
  document = YAML.safe_load(File.read(path), aliases: false)
rescue Psych::Exception => e
  fail!("Automation plan YAML is invalid: #{e.message}")
end
fail!("Automation plan root must be a mapping") unless document.is_a?(Hash)
env = document["env"]
fail!("Automation plan env must be a mapping") unless env.is_a?(Hash)
contexts = hash_array(env["contexts"], "Automation plan contexts must be an array of mappings")
fail!("Automation plan must contain exactly one SEC-06 context") unless contexts.length == 1
context = contexts.first
required_auth_exclusions = [
  "${COGLATAS_SECURITY_ZAP_TARGET_REGEX}/api/auth/logout(?:[/?#].*)?$",
  "${COGLATAS_SECURITY_ZAP_TARGET_REGEX}/api/auth/change-password(?:[/?#].*)?$",
]
exclude_paths = context["excludePaths"]
unless exclude_paths.is_a?(Array) && (required_auth_exclusions - exclude_paths).empty?
  fail!("SEC-06 context must exclude session-mutating auth routes before OpenAPI import")
end
jobs = hash_array(document["jobs"], "Automation plan jobs must be an array of mappings")

openapi = only_job(jobs, "openapi")
openapi_tests = hash_array(openapi["tests"], "OpenAPI job tests must be an array of mappings")
require_blocking_stats_test(
  openapi_tests,
  "openapi.urls.added",
  ">",
  0,
  "Automation plan OpenAPI coverage invariant is missing or non-blocking"
)

requestor = only_job(jobs, "requestor")
requests = hash_array(requestor["requests"], "requestor requests must be an array of mappings")
required_probe_headers = [
  "X-Tenant-Slug:${COGLATAS_SECURITY_ZAP_TENANT}",
  "Cookie:${COGLATAS_SECURITY_ZAP_COOKIE}",
  "X-CSRF-Token:${COGLATAS_SECURITY_ZAP_CSRF_TOKEN}",
]
unless requests.any? { |request|
  request["url"] == "${COGLATAS_SECURITY_ZAP_TARGET}/api/announcements/audiences" &&
    request["responseCode"] == 200 &&
    request["headers"].is_a?(Array) &&
    (required_probe_headers - request["headers"]).empty?
}
  fail!("Automation plan authenticated request/response probe is missing or lacks explicit auth headers")
end

unless jobs.any? { |job| job["type"] == "passiveScan-wait" }
  fail!("Automation plan passiveScan-wait invariant is missing")
end

policy_job = only_job(jobs, "activeScan-policy")
policy_definition = policy_job["policyDefinition"]
fail!("Automation plan activeScan policyDefinition is missing") unless policy_definition.is_a?(Hash)
default_threshold = policy_definition["defaultThreshold"]
unless default_threshold == "Off"
  fail!("Automation plan defaultThreshold must remain the string Off")
end
rules = hash_array(policy_definition["rules"], "Automation plan activeScan rules must be an array of mappings")
plan_rule_ids = rules.map { |rule| rule["id"] }.compact.map(&:to_s)
unless plan_rule_ids.length == required_active_rule_ids.length &&
       plan_rule_ids.sort == required_active_rule_ids.sort
  fail!(
    "Automation plan rules must exactly match the independent SEC-06 required-rule set: " \
    "expected=#{required_active_rule_ids} actual=#{plan_rule_ids}"
  )
end

active_scan = only_job(jobs, "activeScan")
active_parameters = active_scan["parameters"]
unless active_parameters.is_a?(Hash) && active_parameters["scanHeadersAllRequests"] == true
  fail!("Active scan must scan headers on all requests so required parameter-oriented rules receive work")
end
active_tests = hash_array(active_scan["tests"], "activeScan tests must be an array of mappings")
statistics = active_tests.map { |test| test["statistic"] }.compact.map(&:to_s)
fail!("Active scan forced-stop invariant is missing") unless statistics.include?("stats.ascan.stopped")
require_blocking_stats_test(
  active_tests,
  "stats.ascan.stopped",
  "==",
  0,
  "Active scan forced-stop invariant is missing or non-blocking"
)
required_active_rule_ids.each do |rule_id|
  started = "stats.ascan.#{rule_id}.started"
  skipped = "stats.ascan.#{rule_id}.skipped"
  [started, skipped].each do |statistic|
    fail!("Required active-rule completion invariant missing: #{statistic}") unless statistics.include?(statistic)
  end
  require_blocking_stats_test(
    active_tests,
    started,
    ">=",
    1,
    "Required active-rule started invariant missing or non-blocking: #{started}"
  )
  require_blocking_stats_test(
    active_tests,
    skipped,
    "==",
    0,
    "Required active-rule skipped invariant missing or non-blocking: #{skipped}"
  )
  obsolete = "stats.ascan.#{rule_id}.time"
  fail!("Obsolete per-rule time invariant must be absent: #{obsolete}") if statistics.include?(obsolete)
end

report = only_job(jobs, "report")
unless report["parameters"].is_a?(Hash) && report["parameters"]["template"] == "traditional-json"
  fail!("Automation plan report template must remain traditional-json")
end

exit_status = only_job(jobs, "exitStatus")
exit_parameters = exit_status["parameters"]
unless exit_parameters.is_a?(Hash) &&
       exit_parameters["errorLevel"] == "High" &&
       exit_parameters["warnExitValue"] == 1
  fail!("Automation plan exitStatus blocking invariant is missing")
end

replacer = only_job(jobs, "replacer")
replacer_rules = hash_array(replacer["rules"], "replacer rules must be an array of mappings")
replacements = replacer_rules.map { |rule| rule["replacementString"] }.compact
%w[${COGLATAS_SECURITY_ZAP_COOKIE} ${COGLATAS_SECURITY_ZAP_CSRF_TOKEN}].each do |replacement|
  fail!("Automation plan replacer invariant missing: #{replacement}") unless replacements.include?(replacement)
end
RUBY
}

comment_out_exact_lines() {
  local source=$1 destination=$2 needle=$3
  python3 - "$source" "$destination" "$needle" <<'PY'
from pathlib import Path
import sys

source = Path(sys.argv[1])
destination = Path(sys.argv[2])
needle = sys.argv[3]
matched = 0
rendered = []
for line in source.read_text(encoding="utf-8").splitlines(keepends=True):
    newline = "\n" if line.endswith("\n") else ""
    body = line[:-1] if newline else line
    if body.strip() == needle:
        indent = body[: len(body) - len(body.lstrip())]
        rendered.append(f"{indent}# {body.lstrip()}{newline}")
        matched += 1
    else:
        rendered.append(line)
if matched == 0:
    raise SystemExit(f"fixture source line not found: {needle}")
destination.write_text("".join(rendered), encoding="utf-8")
PY
}

for path in "$plan" "$policy" "$runner" "$processor"; do
  [[ -f "$path" ]] || test_fail "missing $path"
done

bash -n "$runner"
python3 -m py_compile "$processor"
command -v ruby >/dev/null 2>&1 || test_fail "Ruby is required to parse the SEC-06 Automation Framework YAML"

tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT

# The real ZAP job parser leaves job placeholders untouched. Verify that the
# private rendered plan is valid YAML and preserves escaped credential values.
export COGLATAS_SECURITY_ZAP_TARGET='http://app:8080'
export COGLATAS_SECURITY_ZAP_TARGET_REGEX='http://app:8080'
export COGLATAS_SECURITY_ZAP_TENANT='security-alpha'
export COGLATAS_SECURITY_ZAP_COOKIE='session=contract-"quoted"'
export COGLATAS_SECURITY_ZAP_CSRF_TOKEN='contract-token'
export COGLATAS_SECURITY_ZAP_REPORT_DIR='/state'
export COGLATAS_SECURITY_ZAP_REPORT_FILE='zap-contract.json'
python3 scripts/security/render-zap-plan.py "$plan" "$tmp/private-plan.yaml"
python3 - "$tmp/private-plan.yaml" <<'PY'
import os
import stat
import sys
from pathlib import Path
path = Path(sys.argv[1])
assert stat.S_IMODE(path.stat().st_mode) == 0o600
assert b"${COGLATAS_SECURITY_ZAP_" not in path.read_bytes()
PY
ruby - "$tmp/private-plan.yaml" <<'RUBY'
require "yaml"
plan = YAML.safe_load(File.read(ARGV.fetch(0)), aliases: false)
replacer = plan.fetch("jobs").find { |job| job["type"] == "replacer" }
requestor = plan.fetch("jobs").find { |job| job["type"] == "requestor" }
probe_headers = requestor.fetch("requests").first.fetch("headers")
abort "SEC-06 rendered cookie changed" unless replacer.fetch("rules")[1]["replacementString"] == ENV.fetch("COGLATAS_SECURITY_ZAP_COOKIE")
abort "SEC-06 rendered URL changed" unless replacer.fetch("rules")[0]["url"] == "http://app:8080/.*"
abort "SEC-06 requestor cookie header changed" unless probe_headers.include?("Cookie:#{ENV.fetch("COGLATAS_SECURITY_ZAP_COOKIE")}")
abort "SEC-06 requestor tenant header changed" unless probe_headers.include?("X-Tenant-Slug:#{ENV.fetch("COGLATAS_SECURITY_ZAP_TENANT")}")
abort "SEC-06 requestor CSRF header changed" unless probe_headers.include?("X-CSRF-Token:#{ENV.fetch("COGLATAS_SECURITY_ZAP_CSRF_TOKEN")}")
RUBY
unset COGLATAS_SECURITY_ZAP_TARGET COGLATAS_SECURITY_ZAP_TARGET_REGEX COGLATAS_SECURITY_ZAP_TENANT
unset COGLATAS_SECURITY_ZAP_COOKIE COGLATAS_SECURITY_ZAP_CSRF_TOKEN
unset COGLATAS_SECURITY_ZAP_REPORT_DIR COGLATAS_SECURITY_ZAP_REPORT_FILE

python3 - "$policy" "$required_active_rule_ids" <<'PY'
import json
import re
import sys
from pathlib import Path
policy = json.loads(Path(sys.argv[1]).read_text(encoding="utf-8"))
required_active_rule_ids = tuple(sys.argv[2].split(","))
scanner = policy["scanner"]
image = scanner["image"]
if scanner["version"] != "2.17.0":
    raise SystemExit("ZAP version must remain explicitly pinned")
if not re.search(r"@sha256:[0-9a-f]{64}$", image):
    raise SystemExit("ZAP image must be digest pinned")
if ":latest" in image or image.endswith(":stable"):
    raise SystemExit("moving ZAP image tags are forbidden")
required = {"automation", "openapi", "pscan", "pscanrules", "ascanrules", "reports", "replacer"}
if not required.issubset(set(scanner["requiredAddons"])):
    raise SystemExit("required ZAP add-on inventory is incomplete")
active_rules = policy.get("activeRules")
if not isinstance(active_rules, list) or not all(
    isinstance(rule, dict) and "id" in rule for rule in active_rules
):
    raise SystemExit("SEC-06 activeRules must be an array of rule objects with ids")
active_rule_ids = tuple(str(rule["id"]) for rule in active_rules)
if (
    len(active_rule_ids) != len(required_active_rule_ids)
    or set(active_rule_ids) != set(required_active_rule_ids)
):
    raise SystemExit(
        "SEC-06 activeRules must exactly match the independent required-rule set: "
        f"expected={required_active_rule_ids} actual={active_rule_ids}"
    )
if policy["roles"] != ["alpha-owner", "alpha-restricted", "beta-owner"]:
    raise SystemExit("SEC-06 role matrix drifted")
blocking = policy["blockingPolicy"]
if blocking["high"] != "block" or blocking["medium"] != "report":
    raise SystemExit("High must block while Medium remains visible/report-only")
if blocking.get("zeroOpenApiCoverage") != "block":
    raise SystemExit("zero OpenAPI/authenticated coverage must remain blocking")
coverage = policy.get("coveragePolicy", {}).get("zeroOpenApiCoverage")
expected_probe = {
    "method": "GET",
    "path": "/api/announcements/audiences",
    "expectedStatus": 200,
}
if not isinstance(coverage, dict) or coverage.get("openApiStatistic") != "openapi.urls.added > 0":
    raise SystemExit("SEC-06 coverage policy must retain the OpenAPI import signal")
if coverage.get("authenticatedRequestResponsePerRole") != expected_probe:
    raise SystemExit("SEC-06 coverage policy must require the protected per-role request/response probe")
PY

validate_automation_plan "$plan"

plan_fixture_index=0
expect_plan_comment_rejected() {
  local needle=$1
  plan_fixture_index=$((plan_fixture_index + 1))
  local mutant="$tmp/plan-comment-required-${plan_fixture_index}.yaml"
  comment_out_exact_lines "$plan" "$mutant" "$needle"
  if validate_automation_plan "$mutant" >/dev/null 2>&1; then
    test_fail "comment-only Automation Framework invariant was accepted: $needle"
  fi
}

for invariant in \
  '- "${COGLATAS_SECURITY_ZAP_TARGET_REGEX}/api/auth/logout(?:[/?#].*)?$"' \
  '- "${COGLATAS_SECURITY_ZAP_TARGET_REGEX}/api/auth/change-password(?:[/?#].*)?$"' \
  '- type: openapi' \
  'statistic: openapi.urls.added' \
  'operator: ">"' \
  '- type: requestor' \
  'url: "${COGLATAS_SECURITY_ZAP_TARGET}/api/announcements/audiences"' \
  '- "X-Tenant-Slug:${COGLATAS_SECURITY_ZAP_TENANT}"' \
  '- "Cookie:${COGLATAS_SECURITY_ZAP_COOKIE}"' \
  '- "X-CSRF-Token:${COGLATAS_SECURITY_ZAP_CSRF_TOKEN}"' \
  'responseCode: 200' \
  '- type: passiveScan-wait' \
  '- type: activeScan-policy' \
  'defaultThreshold: "Off"' \
  '- type: activeScan' \
  'scanHeadersAllRequests: true' \
  'statistic: stats.ascan.stopped' \
  'template: traditional-json' \
  '- type: exitStatus' \
  'errorLevel: High' \
  'warnExitValue: 1' \
  'replacementString: "${COGLATAS_SECURITY_ZAP_COOKIE}"' \
  'replacementString: "${COGLATAS_SECURITY_ZAP_CSRF_TOKEN}"'
do
  expect_plan_comment_rejected "$invariant"
done

IFS=',' read -r -a required_active_rules <<< "$required_active_rule_ids"
for rule_id in "${required_active_rules[@]}"; do
  expect_plan_comment_rejected "- id: $rule_id"
  expect_plan_comment_rejected "statistic: stats.ascan.${rule_id}.started"
  expect_plan_comment_rejected "statistic: stats.ascan.${rule_id}.skipped"

  comment_only_obsolete="$tmp/plan-comment-obsolete-${rule_id}.yaml"
  cp "$plan" "$comment_only_obsolete"
  printf '\n# statistic: stats.ascan.%s.time\n' "$rule_id" >> "$comment_only_obsolete"
  validate_automation_plan "$comment_only_obsolete" >/dev/null 2>&1 ||
    test_fail "comment-only obsolete per-rule time marker affected parsed validation for rule $rule_id"
done

comment_only_report="$tmp/plan-comment-report-template.yaml"
cp "$plan" "$comment_only_report"
printf '\n# template: traditional-json-plus\n' >> "$comment_only_report"
validate_automation_plan "$comment_only_report" >/dev/null 2>&1 ||
  test_fail "comment-only request/response-bearing report template affected parsed validation"

runner_export_pattern='^[[:space:]]*export[[:space:]]+COGLATAS_SECURITY_ZAP_FORBIDDEN_VALUES="[$]forbidden_json"[[:space:]]*$'
runner_cookie_pair_pattern='^[[:space:]]*values[.]add[(]f"[{]name[}]=[{]value[}]"[)][[:space:]]*$'
runner_unset_pattern='^[[:space:]]*unset[[:space:]]+COGLATAS_SECURITY_ZAP_FORBIDDEN_VALUES[[:space:]]*$'
runner_container_name_pattern='^[[:space:]]*container_name="sec06-zap-[$][{]role[}]-[$][$]"[[:space:]]*$'
runner_docker_name_pattern='^[[:space:]]*--name[[:space:]]+"[$]container_name"[[:space:]]+\\[[:space:]]*$'
runner_cleanup_pattern='^[[:space:]]*docker[[:space:]]+rm[[:space:]]+-f[[:space:]]+"[$]container_name"[[:space:]]+>/dev/null[[:space:]]+2>&1[[:space:]]+\|\|[[:space:]]+true[[:space:]]*$'
runner_forbidden_container_pattern='^[[:space:]]*-e[[:space:]]+COGLATAS_SECURITY_ZAP_FORBIDDEN_VALUES([[:space:]\\]|$)'

grep -Eq -- "$runner_export_pattern" "$runner" || test_fail "full forbidden-value set is not exported for host-side redaction"
grep -Eq -- "$runner_cookie_pair_pattern" "$runner" || test_fail "cookie name=value pairs are missing from the forbidden-value set"
grep -Eq -- "$runner_unset_pattern" "$runner" || test_fail "host-side forbidden-value set is not cleared after each role"
! grep -Eq -- "$runner_forbidden_container_pattern" "$runner" || test_fail "forbidden-value set must not be passed into the ZAP container"
grep -Eq -- "$runner_container_name_pattern" "$runner" || test_fail "ZAP role container lacks a deterministic cleanup name"
grep -Eq -- "$runner_docker_name_pattern" "$runner" || test_fail "named ZAP role container is not wired into docker run"
grep -Eq -- "$runner_cleanup_pattern" "$runner" || test_fail "non-zero ZAP exit does not force-remove its named container"

runner_fixture_index=0
expect_runner_comment_rejected() {
  local needle=$1 pattern=$2
  runner_fixture_index=$((runner_fixture_index + 1))
  local mutant="$tmp/runner-comment-required-${runner_fixture_index}.sh"
  comment_out_exact_lines "$runner" "$mutant" "$needle"
  if grep -Eq -- "$pattern" "$mutant"; then
    test_fail "comment-only runner safeguard was accepted: $needle"
  fi
}

expect_runner_comment_rejected 'export COGLATAS_SECURITY_ZAP_FORBIDDEN_VALUES="$forbidden_json"' "$runner_export_pattern"
expect_runner_comment_rejected 'values.add(f"{name}={value}")' "$runner_cookie_pair_pattern"
expect_runner_comment_rejected 'unset COGLATAS_SECURITY_ZAP_FORBIDDEN_VALUES' "$runner_unset_pattern"
expect_runner_comment_rejected 'container_name="sec06-zap-${role}-$$"' "$runner_container_name_pattern"
expect_runner_comment_rejected '--name "$container_name" \' "$runner_docker_name_pattern"
expect_runner_comment_rejected 'docker rm -f "$container_name" >/dev/null 2>&1 || true' "$runner_cleanup_pattern"

runner_comment_forbidden="$tmp/runner-comment-forbidden-env.sh"
cp "$runner" "$runner_comment_forbidden"
printf '%s\n' '# -e COGLATAS_SECURITY_ZAP_FORBIDDEN_VALUES \' >> "$runner_comment_forbidden"
! grep -Eq -- "$runner_forbidden_container_pattern" "$runner_comment_forbidden" ||
  test_fail "comment-only forbidden-value container argument was treated as executable"

printf '{}\n' > "$tmp/openapi.json"
cp "$plan" "$tmp/plan.yaml"
cp "$policy" "$tmp/policy.json"
addon_sha="$(printf 'immutable-addon-inventory' | sha256sum | awk '{print $1}')"
readonly TEST_SCANNER_IMAGE='zaproxy/zap-stable:2.17.0@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa'
readonly TEST_FORBIDDEN_VALUES='["synthetic-secret","synthetic-cookie","synthetic-csrf"]'

cat > "$tmp/medium.json" <<'JSON'
{
  "@version": "2.17.0",
  "site": [
    {
      "@name": "http://app:8080",
      "alerts": [
        {
          "pluginid": "10021",
          "name": "Example Medium Passive Alert",
          "riskcode": "2",
          "riskdesc": "Medium (Medium)",
          "confidence": "2",
          "count": "1",
          "instances": [
            {
              "uri": "http://app:8080/api/tasks?q=synthetic-secret",
              "method": "GET",
              "param": "q",
              "attack": "synthetic-secret",
              "evidence": "synthetic-cookie"
            }
          ]
        }
      ]
    }
  ]
}
JSON

: > "$tmp/empty-plan.yaml"
expect_failure_contains \
  "required input is empty: $tmp/empty-plan.yaml" \
  env -u COGLATAS_SECURITY_ZAP_ALLOW_UNSANITIZED COGLATAS_SECURITY_ZAP_FORBIDDEN_VALUES="$TEST_FORBIDDEN_VALUES" \
  python3 "$processor" \
    --raw-report "$tmp/medium.json" \
    --output "$tmp/empty-plan-safe.json" \
    --metadata "$tmp/empty-plan-meta.json" \
    --role alpha-restricted \
    --target http://app:8080 \
    --scanner-version 2.17.0 \
    --scanner-image "$TEST_SCANNER_IMAGE" \
    --scanner-exit 0 \
    --contract "$tmp/openapi.json" \
    --automation-plan "$tmp/empty-plan.yaml" \
    --policy "$tmp/policy.json" \
    --addon-list-sha256 "$addon_sha"
[[ ! -e "$tmp/empty-plan-safe.json" && ! -e "$tmp/empty-plan-meta.json" ]] ||
  test_fail "empty automation plan wrote evidence"

: > "$tmp/empty-policy.json"
expect_failure_contains \
  "required input is empty: $tmp/empty-policy.json" \
  env -u COGLATAS_SECURITY_ZAP_ALLOW_UNSANITIZED COGLATAS_SECURITY_ZAP_FORBIDDEN_VALUES="$TEST_FORBIDDEN_VALUES" \
  python3 "$processor" \
    --raw-report "$tmp/medium.json" \
    --output "$tmp/empty-policy-safe.json" \
    --metadata "$tmp/empty-policy-meta.json" \
    --role alpha-restricted \
    --target http://app:8080 \
    --scanner-version 2.17.0 \
    --scanner-image "$TEST_SCANNER_IMAGE" \
    --scanner-exit 0 \
    --contract "$tmp/openapi.json" \
    --automation-plan "$tmp/plan.yaml" \
    --policy "$tmp/empty-policy.json" \
    --addon-list-sha256 "$addon_sha"
[[ ! -e "$tmp/empty-policy-safe.json" && ! -e "$tmp/empty-policy-meta.json" ]] ||
  test_fail "empty policy wrote evidence"

expect_failure_contains \
  'COGLATAS_SECURITY_ZAP_FORBIDDEN_VALUES is not set' \
  env -u COGLATAS_SECURITY_ZAP_FORBIDDEN_VALUES -u COGLATAS_SECURITY_ZAP_ALLOW_UNSANITIZED \
  python3 "$processor" \
    --raw-report "$tmp/medium.json" \
    --output "$tmp/missing-forbidden-safe.json" \
    --metadata "$tmp/missing-forbidden-meta.json" \
    --role alpha-restricted \
    --target http://app:8080 \
    --scanner-version 2.17.0 \
    --scanner-image "$TEST_SCANNER_IMAGE" \
    --scanner-exit 0 \
    --contract "$tmp/openapi.json" \
    --automation-plan "$tmp/plan.yaml" \
    --policy "$tmp/policy.json" \
    --addon-list-sha256 "$addon_sha"
[[ ! -e "$tmp/missing-forbidden-safe.json" && ! -e "$tmp/missing-forbidden-meta.json" ]] ||
  test_fail "missing forbidden-value set wrote evidence"

expect_failure_contains \
  'COGLATAS_SECURITY_ZAP_FORBIDDEN_VALUES contains no non-empty values' \
  env -u COGLATAS_SECURITY_ZAP_ALLOW_UNSANITIZED COGLATAS_SECURITY_ZAP_FORBIDDEN_VALUES='[]' \
  python3 "$processor" \
    --raw-report "$tmp/medium.json" \
    --output "$tmp/empty-forbidden-safe.json" \
    --metadata "$tmp/empty-forbidden-meta.json" \
    --role alpha-restricted \
    --target http://app:8080 \
    --scanner-version 2.17.0 \
    --scanner-image "$TEST_SCANNER_IMAGE" \
    --scanner-exit 0 \
    --contract "$tmp/openapi.json" \
    --automation-plan "$tmp/plan.yaml" \
    --policy "$tmp/policy.json" \
    --addon-list-sha256 "$addon_sha"
[[ ! -e "$tmp/empty-forbidden-safe.json" && ! -e "$tmp/empty-forbidden-meta.json" ]] ||
  test_fail "empty forbidden-value set wrote evidence without explicit opt-out"

env -u COGLATAS_SECURITY_ZAP_FORBIDDEN_VALUES COGLATAS_SECURITY_ZAP_ALLOW_UNSANITIZED=1 \
python3 "$processor" \
  --raw-report "$tmp/medium.json" \
  --output "$tmp/optout-safe.json" \
  --metadata "$tmp/optout-meta.json" \
  --role alpha-restricted \
  --target http://app:8080 \
  --scanner-version 2.17.0 \
  --scanner-image "$TEST_SCANNER_IMAGE" \
  --scanner-exit 0 \
  --contract "$tmp/openapi.json" \
  --automation-plan "$tmp/plan.yaml" \
  --policy "$tmp/policy.json" \
  --addon-list-sha256 "$addon_sha" >/dev/null
grep -Fq '"forbiddenValueCount": 0' "$tmp/optout-safe.json" || test_fail "opt-out evidence did not record zero forbidden values"
grep -Fq '"unsanitizedAllowed": true' "$tmp/optout-safe.json" || test_fail "opt-out evidence did not record the explicit decision"

env -u COGLATAS_SECURITY_ZAP_ALLOW_UNSANITIZED COGLATAS_SECURITY_ZAP_FORBIDDEN_VALUES="$TEST_FORBIDDEN_VALUES" \
python3 "$processor" \
  --raw-report "$tmp/medium.json" \
  --output "$tmp/medium-safe.json" \
  --metadata "$tmp/medium-meta.json" \
  --role alpha-restricted \
  --target http://app:8080 \
  --scanner-version 2.17.0 \
  --scanner-image "$TEST_SCANNER_IMAGE" \
  --scanner-exit 0 \
  --contract "$tmp/openapi.json" \
  --automation-plan "$tmp/plan.yaml" \
  --policy "$tmp/policy.json" \
  --addon-list-sha256 "$addon_sha"

grep -Fq '"Medium": 1' "$tmp/medium-safe.json" || test_fail "Medium alert is not visible in sanitized evidence"
grep -Fq '"forbiddenValueCount": 3' "$tmp/medium-safe.json" || test_fail "sanitization evidence did not record the forbidden-value count"
grep -Fq '"unsanitizedAllowed": false' "$tmp/medium-safe.json" || test_fail "normal SEC-06 evidence incorrectly recorded sanitization opt-out"
if grep -Fq 'synthetic-secret' "$tmp/medium-safe.json" || grep -Fq 'synthetic-cookie' "$tmp/medium-safe.json"; then
  test_fail "sanitized report persisted attack/evidence/session material"
fi
grep -Fq '"queryParameterNames"' "$tmp/medium-safe.json" || test_fail "sanitizer did not retain safe location metadata"

python3 - "$tmp/medium.json" "$tmp/encoded-secret.json" <<'PY'
import json
import sys
from pathlib import Path
doc = json.loads(Path(sys.argv[1]).read_text())
doc["site"][0]["alerts"][0]["instances"][0]["uri"] = "http://app:8080/api/synthetic%2Ftoken"
Path(sys.argv[2]).write_text(json.dumps(doc), encoding="utf-8")
PY
expect_failure_contains \
  'sanitized evidence still contains ephemeral authentication material' \
  env -u COGLATAS_SECURITY_ZAP_ALLOW_UNSANITIZED COGLATAS_SECURITY_ZAP_FORBIDDEN_VALUES='["synthetic/token"]' \
  python3 "$processor" \
    --raw-report "$tmp/encoded-secret.json" \
    --output "$tmp/encoded-secret-safe.json" \
    --metadata "$tmp/encoded-secret-meta.json" \
    --role alpha-restricted \
    --target http://app:8080 \
    --scanner-version 2.17.0 \
    --scanner-image "$TEST_SCANNER_IMAGE" \
    --scanner-exit 0 \
    --contract "$tmp/openapi.json" \
    --automation-plan "$tmp/plan.yaml" \
    --policy "$tmp/policy.json" \
    --addon-list-sha256 "$addon_sha"
[[ ! -e "$tmp/encoded-secret-safe.json" && ! -e "$tmp/encoded-secret-meta.json" ]] ||
  test_fail "URL-encoded forbidden path wrote sanitized evidence"

python3 - "$tmp/medium.json" "$tmp/unknown-risk.json" <<'PY'
import json
import sys
from pathlib import Path
doc = json.loads(Path(sys.argv[1]).read_text())
alert = doc["site"][0]["alerts"][0]
alert["riskcode"] = "unexpected"
alert["riskdesc"] = "Unexpected"
Path(sys.argv[2]).write_text(json.dumps(doc), encoding="utf-8")
PY
expect_failure_contains \
  'unrecognized risk classification' \
  env -u COGLATAS_SECURITY_ZAP_ALLOW_UNSANITIZED COGLATAS_SECURITY_ZAP_FORBIDDEN_VALUES="$TEST_FORBIDDEN_VALUES" \
  python3 "$processor" \
    --raw-report "$tmp/unknown-risk.json" \
    --output "$tmp/unknown-risk-safe.json" \
    --metadata "$tmp/unknown-risk-meta.json" \
    --role alpha-restricted \
    --target http://app:8080 \
    --scanner-version 2.17.0 \
    --scanner-image "$TEST_SCANNER_IMAGE" \
    --scanner-exit 0 \
    --contract "$tmp/openapi.json" \
    --automation-plan "$tmp/plan.yaml" \
    --policy "$tmp/policy.json" \
    --addon-list-sha256 "$addon_sha"

printf '{"site": []}\n' > "$tmp/empty-sites.json"
expect_failure_contains \
  'scanner exited successfully without scanned-site coverage' \
  env -u COGLATAS_SECURITY_ZAP_ALLOW_UNSANITIZED COGLATAS_SECURITY_ZAP_FORBIDDEN_VALUES="$TEST_FORBIDDEN_VALUES" \
  python3 "$processor" \
    --raw-report "$tmp/empty-sites.json" \
    --output "$tmp/empty-sites-safe.json" \
    --metadata "$tmp/empty-sites-meta.json" \
    --role alpha-restricted \
    --target http://app:8080 \
    --scanner-version 2.17.0 \
    --scanner-image "$TEST_SCANNER_IMAGE" \
    --scanner-exit 0 \
    --contract "$tmp/openapi.json" \
    --automation-plan "$tmp/plan.yaml" \
    --policy "$tmp/policy.json" \
    --addon-list-sha256 "$addon_sha"

python3 - "$tmp/medium.json" "$tmp/high.json" <<'PY'
import json
import sys
from pathlib import Path
doc = json.loads(Path(sys.argv[1]).read_text())
alert = doc["site"][0]["alerts"][0]
alert["pluginid"] = "40018"
alert["name"] = "SQL Injection"
alert["riskcode"] = "3"
alert["riskdesc"] = "High (Medium)"
Path(sys.argv[2]).write_text(json.dumps(doc), encoding="utf-8")
PY

expect_failure_contains \
  'High findings are blocking' \
  env -u COGLATAS_SECURITY_ZAP_ALLOW_UNSANITIZED COGLATAS_SECURITY_ZAP_FORBIDDEN_VALUES="$TEST_FORBIDDEN_VALUES" \
  python3 "$processor" \
    --raw-report "$tmp/high.json" \
    --output "$tmp/high-safe.json" \
    --metadata "$tmp/high-meta.json" \
    --role alpha-owner \
    --target http://app:8080 \
    --scanner-version 2.17.0 \
    --scanner-image "$TEST_SCANNER_IMAGE" \
    --scanner-exit 1 \
    --contract "$tmp/openapi.json" \
    --automation-plan "$tmp/plan.yaml" \
    --policy "$tmp/policy.json" \
    --addon-list-sha256 "$addon_sha"
[[ -s "$tmp/high-safe.json" ]] || test_fail "blocking High finding did not leave sanitized evidence"
grep -Fq '"blockingHighAlerts": 1' "$tmp/high-safe.json" || test_fail "High blocker count missing"

expect_failure_contains \
  'ZAP failure/timeout cannot be green' \
  env -u COGLATAS_SECURITY_ZAP_ALLOW_UNSANITIZED COGLATAS_SECURITY_ZAP_FORBIDDEN_VALUES="$TEST_FORBIDDEN_VALUES" \
  python3 "$processor" \
    --raw-report "$tmp/medium.json" \
    --output "$tmp/timeout-safe.json" \
    --metadata "$tmp/timeout-meta.json" \
    --role beta-owner \
    --target http://app:8080 \
    --scanner-version 2.17.0 \
    --scanner-image "$TEST_SCANNER_IMAGE" \
    --scanner-exit 124 \
    --contract "$tmp/openapi.json" \
    --automation-plan "$tmp/plan.yaml" \
    --policy "$tmp/policy.json" \
    --addon-list-sha256 "$addon_sha"

python3 - "$tmp/medium.json" "$tmp/cross-origin.json" <<'PY'
import json
import sys
from pathlib import Path
doc = json.loads(Path(sys.argv[1]).read_text())
doc["site"][0]["alerts"][0]["instances"][0]["uri"] = "https://public.example.com/leak"
Path(sys.argv[2]).write_text(json.dumps(doc), encoding="utf-8")
PY
expect_failure_contains \
  'cross-origin alert evidence observed' \
  env -u COGLATAS_SECURITY_ZAP_ALLOW_UNSANITIZED COGLATAS_SECURITY_ZAP_FORBIDDEN_VALUES="$TEST_FORBIDDEN_VALUES" \
  python3 "$processor" \
    --raw-report "$tmp/cross-origin.json" \
    --output "$tmp/cross-safe.json" \
    --metadata "$tmp/cross-meta.json" \
    --role alpha-owner \
    --target http://app:8080 \
    --scanner-version 2.17.0 \
    --scanner-image "$TEST_SCANNER_IMAGE" \
    --scanner-exit 0 \
    --contract "$tmp/openapi.json" \
    --automation-plan "$tmp/plan.yaml" \
    --policy "$tmp/policy.json" \
    --addon-list-sha256 "$addon_sha"

# The shared SEC-03 boundary must reject public targets before Docker or
# authenticated traffic can be reached.
# shellcheck source=scripts/security/scanner-harness.sh
source scripts/security/scanner-harness.sh
# shellcheck source=scripts/security/zap-runner.sh
source scripts/security/zap-runner.sh
export ASPNETCORE_ENVIRONMENT=Test
export COGLATAS_SECURITY_CI_FIXTURE_ENABLED=true
export COGLATAS_SECURITY_CI_PASSWORD='contract-test-password'
export SECURITY_SCAN_TRANSPORT_KIND=compose
export SECURITY_SCAN_TARGET='https://production.example.com'
set +e
target_rejection="$(security_zap_require_target 2>&1)"
target_status=$?
set -e
(( target_status != 0 )) || test_fail "public target passed SEC-06 target preflight"
[[ "$target_rejection" == *'SEC-03 target rejected before network access'* ]] ||
  test_fail "unexpected public-target rejection: $target_rejection"

# SEC-06 is intentionally narrower than the shared local-target allowlist: even
# an otherwise allowed localhost origin must be rejected unless it is the
# transport-bound SEC-02 Compose service alias.
export SECURITY_SCAN_TARGET='http://localhost'
set +e
target_rejection="$(security_zap_require_target 2>&1)"
target_status=$?
set -e
(( target_status != 0 )) || test_fail "non-Compose local target passed SEC-06 target preflight"
[[ "$target_rejection" == *'required SEC-06 runtime accepts only the SEC-02 Compose service origin'* ]] ||
  test_fail "unexpected SEC-06 target-preflight rejection: $target_rejection"

# A non-internal scanner network must block the matrix before toolchain
# preparation or any role scan can run. Stub Docker narrowly to the two network
# inspect calls used by security_zap_require_internal_network.
set +e
internal_network_rejection="$(
  (
    docker() {
      if [[ "$1" == "network" && "$2" == "inspect" && "$3" == "sec06-denied-network" ]]; then
        printf '[{"Internal":false}]\n'
        return 0
      fi
      printf 'UNEXPECTED_DOCKER_CALL %s\n' "$*" >&2
      return 99
    }
    security_zap_require_contract() { return 0; }
    security_zap_require_target() { return 0; }
    security_zap_verify_toolchain() { printf 'ZAP_PREP_CALLED\n' >&2; return 0; }
    security_zap_run_role() { printf 'ZAP_ROLE_CALLED\n' >&2; return 0; }
    security_zap_run_matrix sec06-denied-network "$tmp" app-container
  ) 2>&1
)"
internal_network_status=$?
set -e
(( internal_network_status != 0 )) || test_fail "non-internal SEC-06 scanner network was accepted"
[[ "$internal_network_rejection" == *'SEC-06 scanner network must be Docker internal=true'* ]] ||
  test_fail "unexpected internal-network rejection: $internal_network_rejection"
[[ "$internal_network_rejection" != *'ZAP_PREP_CALLED'* && "$internal_network_rejection" != *'ZAP_ROLE_CALLED'* ]] ||
  test_fail "SEC-06 continued to ZAP preparation after rejecting a non-internal network"

export COGLATAS_SECURITY_ZAP_FORBIDDEN_VALUES='["cookie-value-123","session=cookie-value-123","csrf-value-123"]'
redacted="$(printf '%s\n' 'cookie-value-123 session=cookie-value-123 csrf-value-123 safe-marker' | security_zap_redact_stream)"
for value in cookie-value-123 session=cookie-value-123 csrf-value-123; do
  [[ "$redacted" != *"$value"* ]] || test_fail "stream redaction leaked forbidden value '$value'"
done
[[ "$redacted" == *'safe-marker'* ]] || test_fail "stream redaction removed safe log content"
unset COGLATAS_SECURITY_ZAP_FORBIDDEN_VALUES

# Auth/context loss must block before the runner can prepare or launch ZAP.
auth_fetch_called=0
security_scan_verify_context() { return 1; }
security_scan_fetch_csrf() { auth_fetch_called=1; return 0; }
if security_zap_run_role alpha-owner unused-network "$tmp" >/dev/null 2>&1; then
  test_fail "failed authenticated context was incorrectly accepted"
fi
[[ "$auth_fetch_called" == 0 ]] || test_fail "runner continued after authenticated context failure"

printf 'SEC-06 ZAP contract tests passed.\n'
