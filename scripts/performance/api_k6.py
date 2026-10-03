#!/usr/bin/env python3
"""Sanitized, fail-closed k6 collection and PERF-03 API normalization."""
from __future__ import annotations

import argparse
import hashlib
import importlib.util
import json
import math
import os
import re
import subprocess
import sys
import tempfile
import urllib.request
from pathlib import Path

from common import PerformanceContractError, load_json, repository_root, validate_fixture_evidence, validate_target, write_json_atomic
from compare import compare_api_documents, environment_compatibility_key

ROOT = repository_root()


def load_contract() -> dict:
    contract = load_json(ROOT / 'performance/api-k6.json')
    inventory = load_json(ROOT / 'performance/scenarios.json')
    routes = {s['id']: next((v for v in s['surfaces'] if v['kind'] == 'api'), None) for s in inventory['scenarios']}
    if contract.get('schemaVersion') != 1 or contract.get('k6Image') != f"grafana/k6:{contract.get('k6Version')}":
        raise PerformanceContractError('invalid pinned k6 contract')
    profile = contract['profile']
    if profile['vus'] != 1 or not 20 <= profile['iterations'] <= 100 or profile['fixture'] not in ('small', 'medium'):
        raise PerformanceContractError('invalid bounded API profile')
    if contract['minimumRequests'] < 20 or contract['mainRuns'] < 5:
        raise PerformanceContractError('API sample policy cannot be disabled')
    if profile['warmupIterations'] != 2 or profile['maxDuration'] != '120s' or profile['requestTimeout'] != '10s':
        raise PerformanceContractError('API timeout/warm-up policy cannot be disabled')
    if not re.fullmatch(r'[0-9a-f]{40}', contract['baseline']['sha']) or contract['baseline']['sourceRef'] != 'refs/heads/main':
        raise PerformanceContractError('API baseline must identify exact reviewed main source')
    if len(contract['baseline']['reason']) < 20:
        raise PerformanceContractError('API baseline needs a reviewable rationale')
    required_metrics = {'api.latency.p50_ms', 'api.latency.p95_ms', 'api.latency.p99_ms',
                        'api.error_rate', 'api.timeout_rate', 'api.throughput_rps'}
    if set(contract['metrics']) != required_metrics:
        raise PerformanceContractError('API metric policy is incomplete')
    for metric, rule in contract['metrics'].items():
        for key, value in rule.items():
            if key == 'unit':
                continue
            if isinstance(value, bool) or not isinstance(value, (int, float)) or not math.isfinite(value) or value < 0:
                raise PerformanceContractError('API budget values must be finite and non-negative')
        if metric.endswith('_rate') and rule != {'unit': 'ratio', 'ceiling': 0}:
            raise PerformanceContractError('API errors and timeouts require a zero budget')
        if metric.startswith('api.latency.') and (rule['unit'] != 'ms' or not 0 < rule['ceiling'] <= 10000 or
                                                 not 0 < rule['maxIncreasePercent'] <= 100 or
                                                 not 0 < rule['minimumAbsoluteIncrease'] < rule['ceiling']):
            raise PerformanceContractError('API latency budget is missing or effectively disabled')
        if metric == 'api.throughput_rps' and (rule['unit'] != 'rps' or not 0 < rule['maxDecreasePercent'] < 100):
            raise PerformanceContractError('API throughput regression budget is disabled')
    ids = set()
    for scenario in contract['scenarios']:
        if scenario['id'] in ids:
            raise PerformanceContractError('duplicate required API scenario')
        ids.add(scenario['id'])
        route = routes.get(scenario['id'])
        if not route or route['method'] != scenario['method'] or re.sub(r'\{[^}]+\}', '{}', route['path']) != re.sub(r'\{[^}]+\}', '{}', scenario['path'].split('?')[0]):
            raise PerformanceContractError('API route is not backed by PERF-01 inventory')
    required_scenarios = {'auth.session-bootstrap', 'workspace.list', 'workspace.detail', 'project.list',
                          'project.detail', 'task.list', 'task.detail', 'task.my-tasks', 'project.kanban-load',
                          'project.gantt-load', 'conversation.list', 'notification.list', 'mutation.kanban-move'}
    if ids != required_scenarios:
        raise PerformanceContractError('required API coverage cannot be silently removed')
    return contract


def normalize(raw: dict, contract: dict) -> dict:
    if (raw.get('schemaVersion') != 1 or raw.get('warmupSamplesExcluded') is not True or
            raw.get('authFailures') != 0 or raw.get('healthFailures') != 0):
        raise PerformanceContractError('k6 authentication/health/warm-up contract failed')
    rows = raw.get('scenarios')
    required = {s['id'] for s in contract['scenarios']}
    if not isinstance(rows, list) or len(rows) != len(required) or {r.get('scenario') for r in rows} != required:
        raise PerformanceContractError('missing, duplicate, or unexpected required scenario')
    output = {}
    for row in rows:
        for key in ('requestCount', 'errorCount', 'timeoutCount'):
            if isinstance(row.get(key), bool) or not isinstance(row.get(key), int) or row[key] < 0:
                raise PerformanceContractError('invalid request counters')
        count = row['requestCount']
        if count != contract['profile']['iterations'] or count < contract['minimumRequests']:
            raise PerformanceContractError('insufficient or partial measured request group')
        if row['errorCount'] > count or row['timeoutCount'] > row['errorCount']:
            raise PerformanceContractError('inconsistent error counters')
        for key in ('p50', 'p95', 'p99', 'durationSeconds'):
            value = row.get(key)
            if isinstance(value, bool) or not isinstance(value, (float, int)) or not math.isfinite(value) or value <= 0:
                raise PerformanceContractError('invalid or missing numeric k6 summary')
        if not row['p50'] <= row['p95'] <= row['p99']:
            raise PerformanceContractError('invalid latency percentile order')
        # An allowlist projection prevents even unexpected fields in k6 output
        # from carrying a token, cookie, response body, URL, or fixture identity.
        output[row['scenario']] = {
            'requestCount': count, 'errorCount': row['errorCount'], 'timeoutCount': row['timeoutCount'],
            'api.latency.p50_ms': row['p50'], 'api.latency.p95_ms': row['p95'], 'api.latency.p99_ms': row['p99'],
            'api.error_rate': row['errorCount'] / count, 'api.timeout_rate': row['timeoutCount'] / count,
            'api.throughput_rps': count / row['durationSeconds'],
        }
    return output


def collect(output: Path) -> None:
    contract = load_contract()
    base = validate_target(os.environ['COGLATAS_PERFORMANCE_BASE_URL'])
    fixture = validate_fixture_evidence(load_json(Path(os.environ['COGLATAS_PERFORMANCE_FIXTURE_EVIDENCE'])), contract['profile']['fixture'])
    fingerprint = load_json(Path(os.environ['COGLATAS_PERFORMANCE_ENVIRONMENT_EVIDENCE']))
    # Private temporary files are outside all artifact directories. k6 process
    # output is captured and discarded even when the process fails.
    with tempfile.TemporaryDirectory(prefix='coglatas-k6-') as directory:
        temporary = Path(directory)
        temporary.chmod(0o700)
        config = dict(contract, identities=fixture['identities'])
        write_json_atomic(temporary / 'config.json', config)
        environment = dict(os.environ, PERF_K6_CONFIG='/work/config.json', PERF_K6_OUTPUT='/work/result.json')
        command = ['docker', 'run', '--rm', '--network', 'host', '--user', f'{os.getuid()}:{os.getgid()}',
                   '-v', f'{temporary}:/work', '-v', f'{ROOT / "scripts/performance/api-k6.js"}:/api-k6.js:ro']
        for name in ('PERF_K6_CONFIG', 'PERF_K6_OUTPUT', 'COGLATAS_PERFORMANCE_PASSWORD', 'COGLATAS_PERFORMANCE_BASE_URL'):
            command.extend(['-e', name])
        version = subprocess.run(['docker', 'run', '--rm', contract['k6Image'], 'version'], capture_output=True, timeout=120)
        if version.returncode != 0 or f"k6 v{contract['k6Version']} ".encode() not in version.stdout:
            raise PerformanceContractError('actual k6 version does not match the pinned contract')
        command.extend([contract['k6Image'], 'run', '--quiet', '/api-k6.js'])
        try:
            result = subprocess.run(command, env=environment, stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=180)
        except (OSError, subprocess.TimeoutExpired) as exc:
            raise PerformanceContractError('k6 process failed or timed out; protected output discarded') from exc
        if result.returncode != 0:
            raise PerformanceContractError('k6 process failed; protected output discarded')
        measurements = normalize(load_json(temporary / 'result.json'), contract)
    with urllib.request.urlopen(f'{base}/health/ready', timeout=10) as response:
        if response.status != 200:
            raise PerformanceContractError('application unhealthy after k6')
    fingerprint['k6Version'] = contract['k6Version']
    write_json_atomic(output, {
        'schemaVersion': 1, 'headSha': fingerprint['commitSha'], 'fingerprint': fingerprint,
        'contractHash': hashlib.sha256((ROOT / 'performance/api-k6.json').read_bytes()).hexdigest(),
        'profile': contract['profile'], 'measurements': measurements,
    })


def evaluate(current: list[dict], baseline: list[dict], mode: str) -> dict:
    contract = load_contract()
    policy = load_json(ROOT / 'performance/comparison-policy.json')
    if len(current) != (1 if mode == 'fast' else contract['mainRuns']):
        raise PerformanceContractError('missing independent current run')
    if mode == 'regression' and len(baseline) != contract['mainRuns']:
        raise PerformanceContractError('missing independent baseline run')
    reference = current[0]
    expected_contract_hash = hashlib.sha256((ROOT / 'performance/api-k6.json').read_bytes()).hexdigest()
    if reference.get('contractHash') != expected_contract_hash:
        raise PerformanceContractError('measurement does not bind to the current API contract')
    for group in (current, baseline):
        for run in group:
            if run.get('schemaVersion') != 1 or run.get('contractHash') != reference['contractHash'] or run.get('profile') != contract['profile']:
                raise PerformanceContractError('API contract/profile mismatch')
            if run['fingerprint']['commitSha'] != run['headSha']:
                raise PerformanceContractError('run SHA/fingerprint mismatch')
            if environment_compatibility_key(run['fingerprint']) != environment_compatibility_key(reference['fingerprint']):
                raise PerformanceContractError('independent runs have incompatible environments')
            if run['fingerprint'].get('k6Version') != contract['k6Version']:
                raise PerformanceContractError('run k6 version mismatch')
            if run['headSha'] != (contract['baseline']['sha'] if group is baseline else reference['headSha']):
                raise PerformanceContractError('run SHA mismatch')
    results = []
    scenarios = []
    for scenario in contract['scenarios']:
        sid = scenario['id']
        rows = [run['measurements'][sid] for run in current]
        scenarios.append({'scenario': sid, 'profile': contract['profile'], 'requestCount': sum(r['requestCount'] for r in rows),
                          'errorCount': sum(r['errorCount'] for r in rows), 'timeoutCount': sum(r['timeoutCount'] for r in rows)})
        for metric, budget in contract['metrics'].items():
            measurement = {
                'schemaVersion': 1, 'scenario': sid, 'metric': metric, 'unit': budget['unit'],
                'headSha': reference['headSha'], 'mode': mode, 'samples': [r[metric] for r in rows],
                'requestCounts': [r['requestCount'] for r in rows],
                'measurementEnvelope': {'warmupSamplesExcluded': True, 'environmentStable': True,
                                        'benchmarkExitCode': 0, 'timedOut': False},
            }
            measured_baseline = None
            if baseline:
                b_rows = [run['measurements'][sid] for run in baseline]
                measured_baseline = {
                    'baselineSha': contract['baseline']['sha'], 'sourceRef': 'refs/heads/main', 'approved': True,
                    'scenario': sid, 'metric': metric, 'unit': budget['unit'],
                    'samples': [r[metric] for r in b_rows], 'requestCounts': [r['requestCount'] for r in b_rows],
                    'environmentCompatibilityKey': environment_compatibility_key(baseline[0]['fingerprint']),
                    'fixtureHash': baseline[0]['fingerprint']['fixture']['hash'],
                    'fixtureVersion': baseline[0]['fingerprint']['fixture']['version'], 'k6Version': contract['k6Version'],
                }
            results.append(compare_api_documents(measurement, measured_baseline, reference['fingerprint'], contract, policy))
    return {'schemaVersion': 1, 'headSha': reference['headSha'], 'mode': mode,
            'baselineSha': contract['baseline']['sha'] if baseline else None, 'scenarios': scenarios,
            'results': results, 'decision': 'pass' if all(r['decision'] == 'pass' for r in results) else 'fail'}


def governance_budgets(contract: dict) -> dict:
    budgets = []
    for scenario in contract['scenarios']:
        for metric, rule in contract['metrics'].items():
            for field in ('ceiling', 'maxIncreasePercent', 'maxDecreasePercent', 'minimumAbsoluteIncrease'):
                if field in rule:
                    budgets.append({
                        'id': f"perf04.{scenario['id']}.{metric}.{field}", 'scenarioId': scenario['id'],
                        'metric': metric, 'gate': 'hard-ceiling', 'baseline': contract['baseline'],
                        'limit': {'value': rule[field]},
                    })
    return {'schemaVersion': 1, 'budgets': budgets}


def validate_governance(base_ref: str) -> None:
    # Reuse PERF-03's reviewed update ledger and transition validator for the
    # API supplement. Changing a latency noise floor also relaxes a budget.
    spec = importlib.util.spec_from_file_location('api_baseline_governance', ROOT / 'scripts/ci/verify-performance-baseline-updates.py')
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    old = subprocess.run(['git', 'show', f'{base_ref}:performance/api-k6.json'], capture_output=True, text=True)
    if old.returncode:
        exists = subprocess.run(['git', 'cat-file', '-e', f'{base_ref}^{{commit}}'], capture_output=True)
        if exists.returncode:
            raise PerformanceContractError('API governance base revision is unavailable')
        old_budgets = {'schemaVersion': 1, 'budgets': []}
    else:
        old_budgets = governance_budgets(json.loads(old.stdout))
    new_budgets = governance_budgets(load_contract())
    module.validate_main_ancestry(ROOT, new_budgets, base_ref)
    summary = module.validate_transition(old_budgets, new_budgets,
                                         load_json(ROOT / 'performance/baseline-updates.json'),
                                         head_sha=subprocess.run(['git', 'rev-parse', 'HEAD'], check=True, capture_output=True, text=True).stdout.strip())
    print('PERF-04 baseline/budget governance valid: ' + json.dumps(summary, sort_keys=True))


def relevant_path(path: str) -> bool:
    return (path.startswith(('src/', 'tests/Coglatas.Tests/', 'scripts/performance/', 'performance/',
                             'infra/compose/performance/', '.github/workflows/')) or
            path in ('global.json', 'Dockerfile', 'NuGet.config', 'package-lock.json') or
            path.startswith(('Directory.Build.', 'Directory.Packages.', 'tests/ci/test_performance_api',
                             'tests/ci/performance-api-k6')))


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument('command', choices=('collect', 'evaluate', 'validate', 'route', 'governance'))
    parser.add_argument('--base')
    parser.add_argument('--current', nargs='+', type=Path)
    parser.add_argument('--baseline', nargs='+', type=Path, default=[])
    parser.add_argument('--mode', choices=('fast', 'regression'), default='fast')
    parser.add_argument('--output', type=Path)
    args = parser.parse_args()
    try:
        if args.command == 'route':
            changed = subprocess.run(['git', 'diff', '--name-only', args.base, 'HEAD'], check=True, capture_output=True, text=True).stdout.splitlines()
            relevant = any(relevant_path(p) for p in changed)
            print('true' if relevant else 'false')
        elif args.command == 'governance':
            validate_governance(args.base)
        elif args.command == 'validate':
            load_contract()
            print('PERF-04 pinned API contract valid')
        elif args.command == 'collect':
            collect(args.output)
        else:
            result = evaluate([load_json(p) for p in args.current], [load_json(p) for p in args.baseline], args.mode)
            write_json_atomic(args.output, result)
            lines = ['| Scenario | Metric | Current | Baseline | Delta | Decision |', '| --- | --- | ---: | ---: | ---: | --- |']
            for r in result['results']:
                lines.append(f"| {r['scenario']} | {r['metric']} | {r['currentValue']} | {r['baselineValue']} | {r['relativeDelta']} | {r['decision']} |")
            summary = '\n'.join(lines) + '\n'
            args.output.with_suffix('.md').write_text(summary, encoding='utf-8')
            if os.environ.get('GITHUB_STEP_SUMMARY'):
                with open(os.environ['GITHUB_STEP_SUMMARY'], 'a', encoding='utf-8') as handle:
                    handle.write(summary)
            return 0 if result['decision'] == 'pass' else 1
        return 0
    except (PerformanceContractError, KeyError, OSError, ValueError, TypeError):
        # Do not echo input data, subprocess output, HTTP errors or response bodies.
        print('PERF-04 failed: missing, invalid, unhealthy, or failed benchmark evidence', file=sys.stderr)
        return 2


if __name__ == '__main__':
    raise SystemExit(main())
