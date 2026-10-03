from __future__ import annotations

import copy
import hashlib
import json
import sys
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / 'scripts/performance'))
import api_k6
import compare
from test_performance_comparator import fingerprint, HEAD, policy


def raw_result():
    return {'schemaVersion': 1, 'warmupSamplesExcluded': True, 'authFailures': 0, 'healthFailures': 0,
            'scenarios': [{'scenario': s['id'], 'requestCount': 20, 'errorCount': 0, 'timeoutCount': 0,
                           'p50': 100, 'p95': 150, 'p99': 180, 'durationSeconds': 2}
                          for s in api_k6.load_contract()['scenarios']]}


def run(sha=HEAD):
    contract = api_k6.load_contract()
    fp = fingerprint()
    fp['commitSha'] = sha
    fp['fixture']['profile'] = contract['profile']['fixture']
    fp['k6Version'] = contract['k6Version']
    return {'schemaVersion': 1, 'headSha': sha, 'fingerprint': fp,
            'contractHash': hashlib.sha256((ROOT / 'performance/api-k6.json').read_bytes()).hexdigest(),
            'profile': contract['profile'], 'measurements': api_k6.normalize(raw_result(), contract)}


class ApiNormalizationTests(unittest.TestCase):
    def setUp(self):
        self.contract = api_k6.load_contract()
        self.raw = raw_result()

    def test_same_output_is_deterministic(self):
        self.assertEqual(api_k6.normalize(self.raw, self.contract), api_k6.normalize(copy.deepcopy(self.raw), self.contract))

    def test_protected_fields_are_not_preserved(self):
        self.raw['cookie'] = 'protected-cookie'
        self.raw['scenarios'][0]['body'] = 'protected-response-body'
        text = json.dumps(api_k6.normalize(self.raw, self.contract))
        self.assertNotIn('protected', text)
        self.assertNotIn('body', text)
        self.assertNotIn('cookie', text)

    def test_auth_and_health_failures_never_report_green(self):
        for key in ('authFailures', 'healthFailures'):
            with self.subTest(key=key):
                raw = copy.deepcopy(self.raw)
                raw[key] = 1
                with self.assertRaises(api_k6.PerformanceContractError): api_k6.normalize(raw, self.contract)

    def test_warmup_included_is_rejected(self):
        self.raw['warmupSamplesExcluded'] = False
        with self.assertRaises(api_k6.PerformanceContractError): api_k6.normalize(self.raw, self.contract)

    def test_missing_duplicate_partial_or_empty_scenario_is_rejected(self):
        for variant in (self.raw['scenarios'][:-1], self.raw['scenarios'][:-1] + [self.raw['scenarios'][0]], []):
            with self.subTest(variant=len(variant)):
                self.raw['scenarios'] = variant
                with self.assertRaises(api_k6.PerformanceContractError): api_k6.normalize(self.raw, self.contract)

    def test_insufficient_requests_and_invalid_numbers_are_rejected(self):
        for key, value in (('requestCount', 19), ('requestCount', True), ('p95', float('nan')),
                           ('durationSeconds', 0), ('errorCount', 21), ('timeoutCount', 1), ('p99', 1)):
            with self.subTest(key=key, value=value):
                raw = raw_result()
                raw['scenarios'][0][key] = value
                with self.assertRaises(api_k6.PerformanceContractError): api_k6.normalize(raw, self.contract)


class ApiGateTests(unittest.TestCase):
    def setUp(self):
        self.current = run()
        self.base = run(api_k6.load_contract()['baseline']['sha'])

    def fast(self):
        return api_k6.evaluate([self.current], [], 'fast')

    def regression(self):
        return api_k6.evaluate([copy.deepcopy(self.current) for _ in range(5)],
                               [copy.deepcopy(self.base) for _ in range(5)], 'regression')

    def test_valid_fast_gate_needs_no_fabricated_baseline(self):
        result = self.fast()
        self.assertEqual('pass', result['decision'])
        self.assertIsNone(result['baselineSha'])
        self.assertEqual(78, len(result['results']))

    def test_slow_controlled_measurement_fails_hard_ceiling(self):
        self.current['measurements']['workspace.list']['api.latency.p95_ms'] = 2001
        result = self.fast()
        self.assertEqual('fail', result['decision'])
        self.assertIn('hard-ceiling-exceeded', {r['reasonCode'] for r in result['results']})

    def test_injected_500_and_timeout_fail_strict_budget(self):
        for metric in ('api.error_rate', 'api.timeout_rate'):
            self.current = run()
            self.current['measurements']['workspace.list'][metric] = 1 / 20
            self.assertEqual('fail', self.fast()['decision'])

    def test_main_compares_multiple_runs(self):
        result = self.regression()
        self.assertEqual('pass', result['decision'])
        self.assertEqual(self.base['headSha'], result['baselineSha'])
        self.assertTrue(all(r['sampleCount'] == 5 for r in result['results']))

    def test_main_relative_latency_regression_is_blocking(self):
        self.current['measurements']['workspace.list']['api.latency.p95_ms'] = 500
        self.assertEqual('fail', self.regression()['decision'])

    def test_main_throughput_decrease_is_blocking(self):
        self.current['measurements']['workspace.list']['api.throughput_rps'] = 5
        self.assertEqual('fail', self.regression()['decision'])

    def test_main_missing_baseline_is_not_green(self):
        with self.assertRaises(api_k6.PerformanceContractError): api_k6.evaluate([self.current] * 5, [], 'regression')

    def test_missing_scenario_or_metric_is_not_green(self):
        del self.current['measurements']['workspace.list']
        with self.assertRaises(KeyError): self.fast()

    def test_main_fingerprint_or_tool_mismatch_is_not_green(self):
        for kind in ('tool', 'environment', 'head', 'contract'):
            self.base = run(api_k6.load_contract()['baseline']['sha'])
            if kind == 'tool': self.base['fingerprint']['k6Version'] = 'latest'
            if kind == 'environment': self.base['fingerprint']['runner']['cpuCount'] += 1
            if kind == 'head': self.base['headSha'] = HEAD
            if kind == 'contract': self.base['contractHash'] = 'bad'
            with self.assertRaises(api_k6.PerformanceContractError): self.regression()

    def test_main_high_variance_is_not_green(self):
        runs = [copy.deepcopy(self.current) for _ in range(5)]
        for item, value in zip(runs, [100, 200, 300, 400, 500]):
            item['measurements']['workspace.list']['api.latency.p95_ms'] = value
        result = api_k6.evaluate(runs, [self.base] * 5, 'regression')
        self.assertIn('unstable', {r['decision'] for r in result['results']})

    def test_single_failed_run_cannot_disappear_in_median(self):
        runs = [copy.deepcopy(self.current) for _ in range(5)]
        runs[0]['measurements']['workspace.list']['api.error_rate'] = 0.05
        result = api_k6.evaluate(runs, [self.base] * 5, 'regression')
        self.assertEqual('fail', result['decision'])

    def test_result_key_parity(self):
        schema = json.loads((ROOT / 'performance/performance-result.schema.json').read_text())
        for result in self.fast()['results']:
            self.assertEqual(set(schema['required']), set(result))

    def test_routing(self):
        for path in ('src/Coglatas.Web/Program.cs', 'performance/api-k6.json', 'global.json',
                     '.github/workflows/ci.yml', 'infra/compose/performance/pr.yml'):
            self.assertTrue(api_k6.relevant_path(path), path)
        for path in ('docs/ROADMAP.md', 'frontend/src/app/app.ts', 'LICENSE'):
            self.assertFalse(api_k6.relevant_path(path), path)

    def test_stable_gate_rejects_required_missing_skipped_cancelled(self):
        source = (ROOT / '.github/workflows/performance-api.yml').read_text()
        self.assertIn('name: performance-fast', source)
        self.assertIn('if: always()', source)
        self.assertIn('true) test "$BENCHMARK_RESULT" == success', source)
        self.assertNotIn('continue-on-error', source)
        self.assertNotIn('${{ secrets.', source)


class ApiGovernanceTests(unittest.TestCase):
    def test_relaxation_and_baseline_changes_require_review_ledger(self):
        from test_performance_comparator import baseline_updates
        contract = api_k6.load_contract()
        original = api_k6.governance_budgets(contract)
        ledger = json.loads((ROOT / 'performance/baseline-updates.json').read_text())
        for change in ('ceiling', 'maxIncreasePercent', 'minimumAbsoluteIncrease', 'maxDecreasePercent', 'sha'):
            modified = copy.deepcopy(contract)
            if change == 'sha': modified['baseline']['sha'] = '3' * 40
            elif change == 'maxDecreasePercent': modified['metrics']['api.throughput_rps'][change] += 1
            else: modified['metrics']['api.latency.p95_ms'][change] += 1
            with self.assertRaises(baseline_updates.BaselineUpdateError):
                baseline_updates.validate_transition(original, api_k6.governance_budgets(modified), ledger, head_sha=HEAD)

    def test_head_cannot_become_own_baseline(self):
        from test_performance_comparator import baseline_updates
        contract = api_k6.load_contract()
        contract['baseline']['sha'] = HEAD
        with self.assertRaises(baseline_updates.BaselineUpdateError):
            baseline_updates.validate_transition({'schemaVersion': 1, 'budgets': []}, api_k6.governance_budgets(contract),
                                                json.loads((ROOT / 'performance/baseline-updates.json').read_text()), head_sha=HEAD)
