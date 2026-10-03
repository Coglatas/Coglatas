#!/usr/bin/env bash
set -Eeuo pipefail

repo_root="${REPO_ROOT:-$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)}"
cd "$repo_root"

spec="${1:-artifacts/openapi/coglatas-openapi.json}"
web_project="src/Coglatas.Web/Coglatas.Web.csproj"
assembly="src/Coglatas.Web/bin/Release/net10.0/Coglatas.Web.dll"
assets="src/Coglatas.Web/obj/project.assets.json"
cache="src/Coglatas.Web/obj/Coglatas.Web.OpenApiFiles.cache"

version="$(sed -n 's/.*PackageReference Include="Microsoft.Extensions.ApiDescription.Server" Version="\([^"]*\)".*/\1/p' "$web_project" | head -n 1)"
[[ -n "$version" ]] || {
  echo "Unable to resolve Microsoft.Extensions.ApiDescription.Server version." >&2
  exit 1
}

nuget_root="${NUGET_PACKAGES:-$HOME/.nuget/packages}"
tool="$nuget_root/microsoft.extensions.apidescription.server/$version/tools/dotnet-getdocument.dll"

for required in "$assembly" "$assets" "$tool"; do
  [[ -f "$required" ]] || {
    echo "Prebuilt OpenAPI input is missing: $required" >&2
    exit 1
  }
done

mkdir -p "$(dirname "$spec")"
rm -f "$spec" "$cache"

# dotnet-getdocument starts the compiled application to discover endpoints.
# Keep that synthetic execution on the same explicit Test-only, seed-free
# boundary as the normal SEC-01 generator.
export ASPNETCORE_ENVIRONMENT=Test
export DOTNET_ENVIRONMENT=Test
export ASPNETCORE_CONTENTROOT="$repo_root/src/Coglatas.Web"
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

dotnet "$tool" \
  --assembly "$assembly" \
  --file-list "$cache" \
  --framework ".NETCoreApp,Version=v10.0" \
  --output "$(dirname "$spec")" \
  --project "Coglatas.Web" \
  --assets-file "$assets" \
  --platform "AnyCPU" \
  --file-name "$(basename "$spec" .json)" \
  --openapi-version OpenApi3_1

[[ -f "$spec" ]] || {
  echo "Prebuilt OpenAPI generation completed without producing $spec." >&2
  exit 1
}

python3 scripts/ci/verify-openapi.py "$spec"
python3 scripts/ci/verify_av_mig_contract_boundary.py "$spec"
sha256sum "$spec"
