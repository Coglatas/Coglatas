#!/usr/bin/env python3
"""Compile isolated mutations and inspect real application endpoint metadata."""
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile

ROOT = Path(__file__).resolve().parents[2]


def main():
    program = Path('src/Coglatas.Web/Program.cs')
    original = (ROOT / program).read_text()
    mapping = 'app.MapHub<AppHub>("/hubs/app");'
    authorization = 'builder.Services.AddAuthorization();'
    probes = [
        ('baseline', original, 0, 'runtime contract passed'),
        ('runtime-unreachable-map', original.replace(mapping, 'if (app.Configuration.GetValue<bool>("AvMigMissingRoute")) { ' + mapping + ' }'), 1, 'Expected AppHub transport'),
        ('runtime-endpoint-anonymous', original.replace(mapping, mapping[:-1] + '.AllowAnonymous();'), 1, 'runtime endpoint permits anonymous'),
        ('runtime-default-scheme-override', original.replace(authorization, authorization + '\nbuilder.Services.Configure<Microsoft.AspNetCore.Authentication.AuthenticationOptions>(options => options.DefaultAuthenticateScheme = "Unknown");'), 1, 'Default authenticate scheme drift'),
        ('runtime-permissive-default-policy', original.replace(authorization, 'builder.Services.AddAuthorization(options => options.DefaultPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder().RequireAssertion(_ => true).Build());'), 1, 'must require an authenticated user'),
        ('runtime-named-policy-scheme', original.replace(authorization, 'builder.Services.AddAuthorization(options => options.AddPolicy("Alternate", policy => policy.AddAuthenticationSchemes("Unknown").RequireAuthenticatedUser()));').replace(mapping, mapping[:-1] + '.RequireAuthorization("Alternate");'), 1, 'effective policy scheme drift'),
    ]
    results = []
    logs = ROOT / 'artifacts/av-mig/runtime-probes'
    logs.mkdir(parents=True, exist_ok=True)
    environment = dict(os.environ, ASPNETCORE_ENVIRONMENT='Test', DOTNET_ENVIRONMENT='Test',
        ConnectionStrings__DefaultConnection='Host=127.0.0.1;Port=1;Database=avmig;Username=unused;Password=unused;Timeout=1',
        Tenancy__AppMode='SaaS', Tenancy__SeedOnStartup='false', UiShell__SeedOnStartup='false',
        BrowserSmokeSeed__Enabled='false', COGLATAS_BROWSER_SMOKE_SEED_ENABLED='false',
        DemoDataset__Enabled='false', COGLATAS_DEMO_DATASET_ENABLED='false', COGLATAS_SEED_ADMIN_ENABLED='false',
        COGLATAS_BOOTSTRAP_ADMIN_EMAIL='', BootstrapAdmin__Email='')
    with tempfile.TemporaryDirectory(prefix='av-mig-runtime-') as temporary:
        root = Path(temporary)
        shutil.copytree(ROOT / 'src', root / 'src', ignore=shutil.ignore_patterns('wwwroot'))
        for name in ['global.json', 'Directory.Build.props', 'Directory.Build.targets', 'NuGet.Config', 'nuget.config']:
            if (ROOT / name).is_file(): shutil.copy2(ROOT / name, root / name)
        project = 'src/Coglatas.Web/Coglatas.Web.csproj'
        restore = subprocess.run(['dotnet', 'restore', project, '--disable-parallel', '--disable-build-servers', '-m:1', '--verbosity', 'minimal'], cwd=root, capture_output=True, text=True, timeout=300)
        if restore.returncode: raise RuntimeError('Runtime probe restore failed: ' + restore.stdout + restore.stderr)
        for name, source, expected, diagnostic in probes:
            (root / program).write_text(source)
            build = subprocess.run(['dotnet', 'build', project, '-c', 'Release', '--no-restore', '--disable-build-servers', '-m:1', '-p:UseSharedCompilation=false', '--verbosity', 'quiet'], cwd=root, capture_output=True, text=True, timeout=300)
            if build.returncode:
                (logs / (name + '.log')).write_text(build.stdout + build.stderr)
                results.append({'mutation': name, 'result': 'BUILD_FAILED', 'buildExit': build.returncode})
                print(json.dumps(results[-1]), flush=True)
                continue
            process = subprocess.run(['dotnet', 'src/Coglatas.Web/bin/Release/net10.0/Coglatas.Web.dll', '--AvMigContractVerify', 'true', '--AvMigContractPolicy', str(ROOT / 'docs/migration/avalonia/p0-api-boundary.json')], cwd=root, env=environment, capture_output=True, text=True, timeout=60)
            output = process.stdout + process.stderr
            (logs / (name + '.log')).write_text(output)
            actual = int(process.returncode != 0)
            result = 'PASS' if actual == expected and diagnostic in output else 'FALSE_PASS' if not actual and expected else 'FALSE_FAIL'
            results.append({'mutation': name, 'expectedExitClass': expected, 'actualExit': process.returncode, 'result': result})
            print(json.dumps(results[-1]), flush=True)
    (logs.parent / 'runtime-cli-mutations.json').write_text(json.dumps(results, indent=2) + '\n')
    return int(any(result['result'] != 'PASS' for result in results))


if __name__ == '__main__':
    raise SystemExit(main())
