#!/usr/bin/env python3
"""Run adversarial probes through the production CLI over actual repository sources."""
import copy
import json
from pathlib import Path
import re
import shutil
import subprocess
import sys
import tempfile
import test_av_mig_contract_boundary as fixtures

ROOT = Path(__file__).resolve().parents[2]


def main():
    policy_path = Path('docs/migration/avalonia/p0-api-boundary.json')
    policy = json.loads((ROOT / policy_path).read_text())
    document = fixtures.build_document(policy)
    program_path, hub_path = 'src/Coglatas.Web/Program.cs', 'src/Coglatas.Web/Realtime/AppHub.cs'
    originals = {p: (ROOT / p).read_text() for p in (program_path, hub_path)}
    mapping = 'app.MapHub<AppHub>("/hubs/app");'
    assert mapping in originals[program_path]
    probes = [('baseline', None, {}, 0, 'verification passed')]

    def add(name, mutation, error='OpenAPI'):
        probes.append((name, mutation, {}, 1, error))

    def source(name, path, before, after, error):
        assert before in originals[path], name
        probes.append((name, None, {path: originals[path].replace(before, after, 1)}, 1, error))

    def unlisted(security, inherited=False):
        def apply(doc, _):
            operation = {'responses': {'200': {'description': 'probe'}}}
            if inherited: doc['security'] = security
            else: operation['security'] = security
            doc['paths']['/api/unregistered-probe'] = {'get': operation}
        return apply

    for name, security in [('empty', []), ('empty-object', [{}]), ('cookie-or-anonymous', [{'CookieAuth': []}, {}]), ('anonymous-or-cookie', [{}, {'CookieAuth': []}]), ('unknown-or-anonymous', [{'Unknown': []}, {}])]:
        add('unregistered-' + name, unlisted(security))
        add('inherited-' + name, unlisted(security, True))
    add('operation-override-root-protected', lambda d, p: (d.update(security=[{'CookieAuth': []}]), unlisted([{}])(d, p)))
    required = next(e for e in policy['requiredOperations'] if not e['anonymous'])
    def operation(doc): return doc['paths'][required['path']][required['method'].lower()]
    for name, security in [('empty', []), ('anonymous-or', [{'CookieAuth': []}, {}]), ('unknown', [{'Unknown': []}]), ('and-added', [{'CookieAuth': [], 'Other': []}])]:
        add('required-' + name, lambda d, p, value=security: operation(d).update(security=value), 'protected operation')
    add('required-security-deletion', lambda d, p: operation(d).pop('security'), 'protected operation')
    add('required-inherited-fallback', lambda d, p: (d.update(security=[{'CookieAuth': []}]), operation(d).pop('security')), 'protected operation')
    def alternate(doc, _):
        doc['components']['securitySchemes']['Other'] = {'type': 'http', 'scheme': 'bearer'}
        operation(doc)['security'] = [{'Other': []}]
    add('required-registered-alternate', alternate, 'protected operation')
    def both_deleted(doc, pol):
        pol['requiredOperations'] = [e for e in pol['requiredOperations'] if e['id'] != required['id']]
        del doc['paths'][required['path']][required['method'].lower()]
    add('policy-and-openapi-simultaneous-deletion', both_deleted, 'canonical P0')
    source('hub-anonymous', hub_path, '[Authorize]', '[Authorize]\n[AllowAnonymous]', 'must not allow')
    for scheme in ['Other', 'Unknown', 'Cookies,Other']:
        source('hub-scheme-' + scheme, hub_path, '[Authorize]', f'[Authorize(AuthenticationSchemes = "{scheme}")]', 'without arguments')
    source('default-scheme-change', program_path, '.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)', '.AddAuthentication("Other")', 'default authentication')
    source('endpoint-anonymous', program_path, mapping, mapping[:-1] + '.AllowAnonymous();', 'AppHub path drifted')
    source('missing-route', program_path, mapping, '', 'AppHub path drifted')
    source('route-drift', program_path, mapping, mapping.replace('/hubs/app', '/hubs/changed'), 'AppHub path drifted')
    for name, decoy in [('normal', 'var decoy = "app.MapHub<AppHub>(\\"/hubs/app\\");";'), ('verbatim', 'var decoy = @"app.MapHub<AppHub>(""/hubs/app"");";'), ('interpolated', 'var decoy = $"app.MapHub<AppHub>(\\"/hubs/app\\");";'), ('interpolated-verbatim', 'var decoy = $@"app.MapHub<AppHub>(""/hubs/app"");";'), ('raw', 'var decoy = """\n' + mapping + '\n""";'), ('interpolated-raw', 'var decoy = $$"""\n' + mapping + '\n""";')]:
        source('only-map-in-' + name, program_path, mapping, decoy, 'AppHub path drifted')
    for name, replacement in [('inactive', '#if false\n' + mapping + '\n#endif'), ('unreachable', 'if (false) { ' + mapping + ' }'), ('unbraced-unreachable', 'if (false) ' + mapping)]:
        source(name + '-mapping', program_path, mapping, replacement, 'AppHub path drifted')
    match = re.search(r'public\s+(?:async\s+)?Task<HubSubscriptionResult>\s+SubscribeUser\([^)]*\)', originals[hub_path])
    assert match
    signature = match.group()
    for name, replacement in [('delete', signature.replace('SubscribeUser', 'RemovedSubscribeUser')), ('private', signature.replace('public', 'private')), ('static', signature.replace('public', 'public static')), ('generic', signature.replace('SubscribeUser(', 'SubscribeUser<T>(')), ('parameter', signature.replace('()', '(int unexpected)')), ('return', signature.replace('Task<HubSubscriptionResult>', 'Task<int>')), ('auth', '[Authorize(AuthenticationSchemes = "Other")]\n' + signature), ('nested-decoy', 'public sealed class NestedDecoy { ' + signature + ' => throw new System.NotImplementedException(); }\n' + signature.replace('public', 'private'))]:
        source('method-' + name, hub_path, signature, replacement, 'method')
    source('auth-in-decoy-class', hub_path, '[Authorize]', '[Authorize] public sealed class DecoyHub {}', 'must require')
    alias = 'using HubAuth = Microsoft.AspNetCore.Authorization.AuthorizeAttribute;\n' + originals[hub_path].replace('[Authorize]', '[HubAuth]')
    probes.append(('real-authorize-alias', None, {hub_path: alias}, 0, 'verification passed'))
    results = []
    with tempfile.TemporaryDirectory(prefix='av-mig-cli-') as temporary:
        root = Path(temporary)
        shutil.copytree(ROOT / 'src', root / 'src', ignore=shutil.ignore_patterns('bin', 'obj', 'wwwroot'))
        for name in ['global.json', 'Directory.Build.props', 'Directory.Build.targets']:
            if (ROOT / name).is_file(): shutil.copy2(ROOT / name, root / name)
        manifest = root / 'docs/migration/avalonia/p0-operation-inventory.json'
        manifest.parent.mkdir(parents=True)
        shutil.copy2(ROOT / manifest.relative_to(root), manifest)
        spec = root / 'openapi.json'
        for name, mutate, sources, expected, diagnostic in probes:
            for path, content in originals.items(): (root / path).write_text(sources.get(path, content))
            doc, pol = copy.deepcopy(document), copy.deepcopy(policy)
            if mutate: mutate(doc, pol)
            spec.write_text(json.dumps(doc)); (root / policy_path).write_text(json.dumps(pol))
            process = subprocess.run([sys.executable, str(ROOT / 'scripts/ci/verify_av_mig_contract_boundary.py'), str(spec), '--repo-root', str(root), '--policy', str(root / policy_path)], capture_output=True, text=True, timeout=180)
            output = process.stdout + process.stderr
            status = 'PASS' if process.returncode == expected and diagnostic in output else 'FALSE_PASS' if process.returncode == 0 and expected else 'FALSE_FAIL'
            results.append({'mutation': name, 'expectedExit': expected, 'actualExit': process.returncode, 'result': status})
            print(json.dumps(results[-1]), flush=True)
            if status != 'PASS': print(output, file=sys.stderr)
    report = ROOT / 'artifacts/av-mig/production-cli-mutations.json'
    report.parent.mkdir(parents=True, exist_ok=True)
    report.write_text(json.dumps(results, indent=2) + '\n')
    return int(any(r['result'] != 'PASS' for r in results))


if __name__ == '__main__':
    raise SystemExit(main())
