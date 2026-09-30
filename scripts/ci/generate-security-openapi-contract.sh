#!/usr/bin/env bash
set -Eeuo pipefail

repo_root="${REPO_ROOT:-$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)}"
cd "$repo_root"

spec="${1:-artifacts/openapi/coglatas-openapi.json}"
scratch_parent="${RUNNER_TEMP:-${TMPDIR:-/tmp}}"
determinism_runs="${COGLATAS_SECURITY_OPENAPI_DETERMINISM_RUNS:-2}"
selftests="${COGLATAS_SECURITY_AVMIG_SELFTESTS:-1}"
case "$determinism_runs" in
  1|2) ;;
  *)
    echo "COGLATAS_SECURITY_OPENAPI_DETERMINISM_RUNS must be 1 or 2." >&2
    exit 2
    ;;
esac
case "$selftests" in
  0|1) ;;
  *)
    echo "COGLATAS_SECURITY_AVMIG_SELFTESTS must be 0 or 1." >&2
    exit 2
    ;;
esac
mkdir -p "$(dirname "$spec")"
first="$(mktemp "$scratch_parent/coglatas-openapi.first.XXXXXX.json")"
trap 'rm -f "$first"' EXIT

# Contract generation executes the application through dotnet-getdocument, so it
# must carry the same explicit Test-only activation boundary as SEC-02/SEC-03.
# Never inherit a caller's Production environment for this synthetic contract
# generation path.
export ASPNETCORE_ENVIRONMENT=Test
export DOTNET_ENVIRONMENT=Test

# Contract generation is a read-only build-time operation. Fail closed if the
# application accidentally tries to use a database or any ordinary seed path.
export ConnectionStrings__DefaultConnection="Host=127.0.0.1;Port=1;Database=sec01_openapi;Username=unused;Password=unused;Timeout=1;Command Timeout=1"
export Tenancy__AppMode=SaaS
export Tenancy__SeedOnStartup=false
export UiShell__SeedOnStartup=false
export BrowserSmokeSeed__Enabled=false
export COGLATAS_BROWSER_SMOKE_SEED_ENABLED=false
export DemoDataset__Enabled=false
export COGLATAS_DEMO_DATASET_ENABLED=false
export COGLATAS_SEED_ADMIN_ENABLED=false
export COGLATAS_BOOTSTRAP_ADMIN_EMAIL=""
export BootstrapAdmin__Email=""

generate_openapi() {
  # The SDK tracks this cache rather than the JSON as the target output.
  rm -f src/Coglatas.Web/obj/Coglatas.Web.OpenApiFiles.cache
  dotnet build src/Coglatas.Web/Coglatas.Web.csproj \
    --configuration Release \
    --no-restore \
    --no-incremental \
    --disable-build-servers \
    -m:1 \
    -p:GenerateSecurityOpenApiContract=true
}

# Mutation tests prove the verifier itself fails closed before it is trusted as
# a CI boundary. Fast PRs may defer these expensive verifier/tooling self-tests;
# the real generated contract is still verified below on every routed SEC-01 PR.
if [[ "$selftests" == "1" ]]; then
  python3 scripts/ci/test_av_mig_contract_boundary.py
  python3 scripts/ci/test_av_mig_contract_boundary_hardening.py
  python3 scripts/ci/test_av_mig_contract_boundary_adversarial.py
  python3 scripts/ci/test_av_mig_production_cli.py
else
  echo "AV-MIG verifier mutation self-tests deferred by fast PR routing."
fi

# The mutation harness may intentionally use a synthetic symbol set. Production
# verification must always resolve the effective Release/net10.0 symbols from
# MSBuild, never trust an ambient caller override.
unset AV_MIG_CSHARP_DEFINE_CONSTANTS

rm -f "$spec"
generate_openapi
python3 scripts/ci/verify-openapi.py "$spec"
python3 scripts/ci/verify_av_mig_contract_boundary.py "$spec"

if [[ "$determinism_runs" == "2" ]]; then
  cp "$spec" "$first"
  rm "$spec"
  generate_openapi
  python3 scripts/ci/verify-openapi.py "$spec"
  python3 scripts/ci/verify_av_mig_contract_boundary.py "$spec"
  if ! cmp --silent "$first" "$spec"; then
    echo "SEC-01 OpenAPI output is not deterministic across repeated builds." >&2
    diff -u "$first" "$spec" || true
    exit 1
  fi
else
  echo "SEC-01 deterministic repeat generation deferred by fast PR mode."
fi

dotnet src/Coglatas.Web/bin/Release/net10.0/Coglatas.Web.dll \
  --AvMigContractVerify true \
  --AvMigContractPolicy "$repo_root/docs/migration/avalonia/p0-api-boundary.json"

if [[ "$selftests" == "1" ]]; then
  python3 scripts/ci/test_av_mig_runtime_cli.py
else
  echo "AV-MIG runtime mutation self-tests deferred by fast PR routing."
fi
sha256sum "$spec"
