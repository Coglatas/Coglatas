#!/usr/bin/env python3
"""Apply only enumerated, source-verified Qodana decisions; otherwise stay read-only."""
from __future__ import annotations

import collections
import hashlib
import json
import os
from pathlib import Path
import sys
import time
import urllib.error
import urllib.parse
import urllib.request

OLD_PREFIX = 'Automated Qodana false-positive triage on 2026-10-02.'
NEW_PREFIX = 'Qodana individual review batch 2: '


def require(condition: bool, message: str) -> None:
    if not condition:
        raise ValueError(message)


def expand(manifest: dict) -> list[dict]:
    require(manifest['schema_version'] == 1, 'Unsupported review schema')
    require(manifest['tool'] == 'QDNETC', 'Only QDNETC is in scope')
    decisions = []
    for group in manifest['groups']:
        evidence = manifest['evidence_catalog'][group['evidence_key']]
        for number, member, line in group['alerts']:
            symbol = group['type'] + '.' + member
            decisions.append(dict(number=number, rule=group['rule'], path=group['path'],
                line=line, message=f"Positional property '{symbol}' is never accessed (except in implicit Equals/ToString implementations)",
                symbol=symbol, reason=evidence['reason'], evidence=evidence['evidence'], target='dismissed'))
    decisions.extend(dict(item, target='dismissed') for item in manifest['individual'])
    decisions.extend(dict(item, target='open') for item in manifest['reopen_for_review'])
    numbers = [item['number'] for item in decisions]
    require(0 < len(numbers) <= 150, 'Review batch outside safe size')
    require(all(type(n) is int and n > 0 for n in numbers), 'Invalid alert ID')
    require(len(numbers) == len(set(numbers)), 'Duplicate alert decisions')
    for item in decisions:
        require(item['path'] in manifest['source_sha256'], 'Declaration not source-guarded')
        require(item['reason'] and item['evidence'], 'Missing individual review evidence')
        for evidence in item['evidence']:
            require(evidence.rsplit(':', 1)[0] in manifest['source_sha256'], 'Evidence not source-guarded')
    return sorted(decisions, key=lambda item: item['number'])


def validate_sources(manifest: dict, source: Path) -> None:
    root = source.resolve()
    for name, expected in manifest['source_sha256'].items():
        path = (root / name).resolve()
        require(path.is_relative_to(root), 'Source path escapes checkout')
        require(hashlib.sha256(path.read_bytes()).hexdigest() == expected, 'Reviewed source changed: ' + name)


def validate_alert(manifest: dict, item: dict, alert: dict) -> None:
    instance = alert['most_recent_instance']
    location = instance['location']
    require(alert['number'] == item['number'], 'Alert ID mismatch')
    require(alert['tool']['name'] == manifest['tool'], 'Unreviewed tool')
    require(alert['rule']['id'] == item['rule'], 'Rule changed')
    require(instance['ref'] == 'refs/heads/main', 'Unreviewed ref')
    require(instance['category'] == manifest['category'], 'Unreviewed analysis category')
    require(instance['commit_sha'] in {manifest['analysis_sha'], manifest['reviewed_source_sha']}, 'Unreviewed analysis revision')
    require(location['path'] == item['path'] and location['start_line'] == item['line'], 'Location changed')
    require(instance['message']['text'] == item['message'], 'Symbol or diagnostic changed')
    if item['target'] == 'open' and alert['state'] == 'dismissed':
        require(alert['dismissed_reason'] == 'false positive', 'Do not overwrite another disposition')
        require((alert.get('dismissed_comment') or '').startswith(OLD_PREFIX), 'Not our previous blanket dismissal')
        require((alert.get('dismissed_by') or {}).get('login') == 'github-actions[bot]', 'Not our previous actor')


class Api:
    def __init__(self, repository: str) -> None:
        self.base = os.environ['GITHUB_API_URL'] + '/repos/' + repository
        self.headers = {'Accept': 'application/vnd.github+json',
            'Authorization': 'Bearer ' + os.environ['GH_TOKEN'],
            'X-GitHub-Api-Version': '2022-11-28', 'User-Agent': 'Coglatas-Reviewed-Qodana-Triage'}

    def request(self, path: str, body: dict | None = None) -> dict | list:
        data = None if body is None else json.dumps(body).encode()
        headers = dict(self.headers)
        if data is not None:
            headers['Content-Type'] = 'application/json'
        for attempt in range(4):
            req = urllib.request.Request(self.base + path, data=data, headers=headers,
                                         method='GET' if body is None else 'PATCH')
            try:
                with urllib.request.urlopen(req, timeout=60) as response:
                    return json.load(response)
            except urllib.error.HTTPError as error:
                if error.code not in (429, 500, 502, 503, 504) or attempt == 3:
                    raise
                time.sleep(min(120, max(2 ** attempt, int(error.headers.get('Retry-After', '1')))))
        raise RuntimeError('API retry budget exceeded')


def snapshot(api: Api) -> dict[int, dict]:
    result = {}
    for state in ('open', 'dismissed'):
        for page in range(1, 101):
            query = urllib.parse.urlencode(dict(state=state, ref='refs/heads/main', per_page=100, page=page))
            batch = api.request('/code-scanning/alerts?' + query)
            for alert in batch:
                result[alert['number']] = alert
            if len(batch) < 100:
                break
        else:
            raise RuntimeError('Incomplete alert pagination')
    return result


def apply_decisions(manifest: dict, decisions: list[dict], api: Api, receipt: dict, delay=time.sleep) -> None:
    # Validate the complete explicit batch before the first mutation.
    preflight = {item['number']: api.request('/code-scanning/alerts/' + str(item['number'])) for item in decisions}
    for item in decisions:
        validate_alert(manifest, item, preflight[item['number']])
    require(api.request('/git/ref/heads/main')['object']['sha'] == manifest['reviewed_source_sha'], 'Main moved after review')
    for item in decisions:
        number = item['number']
        endpoint = '/code-scanning/alerts/' + str(number)
        current = api.request(endpoint)
        validate_alert(manifest, item, current)
        entry = dict(item, before_state=current['state'], outcome='unchanged')
        receipt['decisions'].append(entry)
        # Never replace a human dismissal, a fixed alert, or an already applied decision.
        if current['state'] == item['target'] or current['state'] == 'fixed':
            entry['after_state'] = current['state']
            continue
        require(current['state'] == ('open' if item['target'] == 'dismissed' else 'dismissed'), 'Unexpected state')
        entry['previous_dismissed_comment'] = current.get('dismissed_comment')
        body = {'state': item['target']}
        if item['target'] == 'dismissed':
            reason = item['reason'][:155].rsplit(' ', 1)[0]
            body.update(dismissed_reason='false positive', dismissed_comment=(NEW_PREFIX + reason +
                f". Evidence: qodana-reviewed-alerts.json #{number}; source {manifest['reviewed_source_sha'][:12]}.")[:280])
        updated = api.request(endpoint, body)
        entry['patch_returned_state'] = updated['state']
        verified = api.request(endpoint)
        require(verified['state'] == item['target'], f'Write not confirmed for #{number}')
        if item['target'] == 'dismissed':
            require(verified['dismissed_reason'] == 'false positive', 'Dismissal reason not confirmed')
            require((verified.get('dismissed_comment') or '').startswith(NEW_PREFIX), 'Dismissal comment not confirmed')
        entry.update(outcome='verified', after_state=verified['state'],
                     dismissed_comment=verified.get('dismissed_comment'), updated_at=verified['updated_at'])
        print(f"VERIFIED #{number}: {entry['before_state']} -> {entry['after_state']}", flush=True)
        delay(1.0)


def main() -> None:
    require(len(sys.argv) == 4, 'Usage: apply-reviewed-qodana-alerts.py MANIFEST SOURCE OUT')
    manifest_path, source, out = map(Path, sys.argv[1:])
    manifest = json.loads(manifest_path.read_text())
    require(manifest['repository'] == os.environ['GITHUB_REPOSITORY'] == 'NYGsatoshi/Coglatas', 'Repository mismatch')
    event = json.loads(Path(os.environ['GITHUB_EVENT_PATH']).read_text())
    subject = (event.get('head_commit') or {}).get('message', '').splitlines()
    apply = bool(subject and subject[0] == manifest['apply_commit_subject'])
    out.mkdir(parents=True, exist_ok=True)
    receipt = dict(mode='APPLY' if apply else 'READ_ONLY', status='started',
        manifest_sha256=hashlib.sha256(manifest_path.read_bytes()).hexdigest(),
        reviewed_source_sha=manifest['reviewed_source_sha'], run_id=os.environ['GITHUB_RUN_ID'], decisions=[])
    api = Api(manifest['repository'])
    try:
        decisions = expand(manifest)
        receipt['approved_counts'] = dict(collections.Counter(d['target'] for d in decisions))
        validate_sources(manifest, source)
        if not apply:
            receipt['status'] = 'validated_read_only'
            print('Manifest and source evidence validated. No alert writes authorized for this commit.')
            return
        require(os.environ['GITHUB_REF'] == 'refs/heads/ops/qodana-code-scanning-triage-20261002', 'Unreviewed branch')
        before = snapshot(api)
        apply_decisions(manifest, decisions, api, receipt)
        after = snapshot(api)
        receipt['verified_counts'] = dict(collections.Counter(d['after_state'] for d in receipt['decisions'] if d['outcome'] == 'verified'))
        receipt['after_counts'] = dict(collections.Counter(a['tool']['name'] + ':' + a['state'] for a in after.values()))
        reviewed = {d['number'] for d in decisions}
        receipt['unlisted_state_changes'] = [n for n in set(before) & set(after) - reviewed if before[n]['state'] != after[n]['state']]
        receipt['source_still_main'] = api.request('/git/ref/heads/main')['object']['sha'] == manifest['reviewed_source_sha']
        receipt['status'] = 'complete'
        (out / 'alerts-after.json').write_text(json.dumps(list(after.values()), ensure_ascii=False))
    except Exception as error:
        receipt.update(status='failed', error=type(error).__name__ + ': ' + str(error))
        raise
    finally:
        (out / 'review-receipt.json').write_text(json.dumps(receipt, indent=2, ensure_ascii=False) + '\n')
        (out / 'review-manifest.json').write_bytes(manifest_path.read_bytes())
        print(json.dumps({k: v for k, v in receipt.items() if k != 'decisions'}, indent=2))
        with open(os.environ['GITHUB_STEP_SUMMARY'], 'a') as summary:
            summary.write('## Individual Qodana review\n\n```json\n' + json.dumps({k:v for k,v in receipt.items() if k != 'decisions'}, indent=2) + '\n```\n')


if __name__ == '__main__':
    main()
