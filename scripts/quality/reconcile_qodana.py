#!/usr/bin/env python3
"""Reconcile frozen R2 identities with a successful immutable main analysis."""
from __future__ import annotations

import collections
import hashlib
import io
import json
import os
from pathlib import Path
import re
import sys
import urllib.error
import urllib.parse
import urllib.request
import zipfile

REPOSITORY = "NYGsatoshi/Coglatas"
AUDIT_SHA = "9ff983078f2ddf85f21e4e16e0c9b7d6b1a403f6"
AUDIT_SARIF_SHA256 = "95a0556a7cedce7b9d4d6a6a49af9af710ffd2c414289d946b84ae4f6ff0bb4c"
ORIGINAL_SHA = "64db0f5aaf8b4283360f4c6d7934c8ed89256024"
ORIGINAL_SARIF_SHA256 = "6f1b59fe7bbcccdd97534e063e2f7205d3e722958f962664cac2352e7b81091b"
PACKET_RANGES = {
    "P01": "2182-2190,2433,2438", "P02": "73-78,2191",
    "P03": "84-90,2421", "P04": "68-72,79-83,91-93",
    "P05": "271,2201-2205", "P06": "2420,2426",
    "P07": "140,150,153,163,172-173,175,182,188-190",
    "P08": "158-160,186", "P09": "94,161,164,192",
    "P10": "146-147,2434-2435,2437", "P11": "2467-2477",
    "P12": "143,2200", "P13": "137-138",
    "P14": "148,151-152,154-157,162,165-171,174,176-181,183-185,187",
    "P15": "2398,2423",
}
PACKET_COUNTS = dict(zip(PACKET_RANGES, [11, 7, 8, 13, 6, 2, 11, 4, 4, 5, 11, 2, 2, 26, 2]))
MAX_BYTES = 32 * 1024 * 1024


def digest(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def indices(packet: str) -> set[int]:
    result = set()
    for part in PACKET_RANGES[packet].split(","):
        bounds = part.split("-")
        start, end = int(bounds[0]), int(bounds[-1])
        result.update(range(start, end + 1))
    if len(result) != PACKET_COUNTS[packet]:
        raise ValueError("Frozen packet index/count mismatch")
    return result


def validate_selection(selection: dict) -> None:
    if set(selection) != {"version", "repository", "baseline", "head", "identity_anchor", "completed_packets"}:
        raise ValueError("Unexpected reconciliation selection fields")
    if selection["version"] != 1 or selection["repository"] != REPOSITORY:
        raise ValueError("Unsupported reconciliation repository/version")
    packets = selection["completed_packets"]
    if not isinstance(packets, list) or not packets or len(set(packets)) != len(packets):
        raise ValueError("Require distinct completed packets")
    if any(p not in PACKET_RANGES for p in packets):
        raise ValueError("Unsupported packet")
    for revision in [selection["baseline"], selection["head"], selection["identity_anchor"]]:
        if set(revision) != {"sha", "run_id", "artifact_id", "zip_sha256"}:
            raise ValueError("Unexpected revision fields")
        if not re.fullmatch(r"[0-9a-f]{40}", revision["sha"]):
            raise ValueError("Require immutable full source revisions")
        if not re.fullmatch(r"[0-9a-f]{64}", revision["zip_sha256"]):
            raise ValueError("Require pinned artifact digest")
        if any(type(revision[key]) is not int or revision[key] < 1 for key in ["run_id", "artifact_id"]):
            raise ValueError("Require positive workflow/artifact identifiers")
    if selection["baseline"]["sha"] != AUDIT_SHA:
        raise ValueError("Frozen audited revision changed")
    if selection["identity_anchor"]["sha"] != ORIGINAL_SHA:
        raise ValueError("Frozen original identity anchor changed")
    assigned = [i for packet in PACKET_RANGES for i in indices(packet)]
    if len(assigned) != 114 or len(set(assigned)) != 114:
        raise ValueError("Frozen packet ownership overlap")


def sarif_results(data: bytes, revision: str, expected_digest: str | None = None) -> list[dict]:
    if expected_digest and digest(data) != expected_digest:
        raise ValueError("Historical SARIF bytes differ; do not reuse frozen indices")
    sarif = json.loads(data)
    runs = sarif.get("runs")
    if not isinstance(runs, list) or len(runs) != 1:
        raise ValueError("Require one complete SARIF run")
    run = runs[0]
    provenance = run.get("versionControlProvenance", [])
    if len(provenance) != 1 or provenance[0].get("revisionId") != revision:
        raise ValueError("SARIF source revision mismatch")
    results = run.get("results")
    if not isinstance(results, list) or not results:
        raise ValueError("Missing or empty SARIF inventory")
    return results


def identity(result: dict) -> str:
    rule = result.get("ruleId")
    locations = result.get("locations")
    message = result.get("message", {}).get("text")
    if not isinstance(rule, str) or not rule or not isinstance(message, str) or not message:
        raise ValueError("Incomplete rule/message identity")
    if not isinstance(locations, list) or not locations:
        raise ValueError("Missing source location")
    artifact = locations[0].get("physicalLocation", {}).get("artifactLocation", {})
    path = artifact.get("uri")
    if not isinstance(path, str) or not path:
        raise ValueError("Missing source path")
    fingerprints = result.get("partialFingerprints", {})
    if not isinstance(fingerprints, dict) or any(not isinstance(k, str) or not isinstance(v, str)
                                               for k, v in fingerprints.items()):
        raise ValueError("Malformed partial fingerprints")
    # Source lines move during the exact edits. The frozen audit defines
    # identity by rule/path/fingerprints/message, retaining multiplicities.
    return json.dumps([rule, path, fingerprints, message], sort_keys=True, separators=(",", ":"))


def require_ordered_identity_equivalence(original: list[dict], replay: list[dict]) -> str:
    if len(original) != len(replay):
        raise ValueError("Audited replay identity count changed")
    for index, (left, right) in enumerate(zip(original, replay)):
        if identity(left) != identity(right):
            raise ValueError(f"Audited replay identity/order mismatch at index {index}")
    return digest(json.dumps([identity(result) for result in original], separators=(",", ":")).encode())


def reconcile(baseline: list[dict], head: list[dict], removed_indices: set[int]) -> dict:
    if any(type(i) is not int or i < 0 or i >= len(baseline) for i in removed_indices):
        raise ValueError("Removal index outside audited inventory")
    before = collections.Counter(identity(r) for r in baseline)
    current = collections.Counter(identity(r) for r in head)
    removed = collections.Counter(identity(baseline[i]) for i in removed_indices)
    expected = before - removed
    added, missing = current - expected, expected - current
    if added or missing:
        details = {"unexpected_added": sum(added.values()), "unexpected_missing": sum(missing.values()),
                   "added_examples": list(added)[:3], "missing_examples": list(missing)[:3]}
        raise ValueError("Unexpected identity changes: " + json.dumps(details, sort_keys=True))
    current_rules = collections.Counter(r["ruleId"] for r in head)
    removed_rules = collections.Counter(baseline[i]["ruleId"] for i in removed_indices)
    return {
        "baseline_findings": len(baseline), "head_findings": len(head),
        "measured_removals": sum(removed.values()), "retained_identities": sum(expected.values()),
        "unexpected_added": 0, "unexpected_missing": 0,
        "removed_by_rule": dict(sorted(removed_rules.items())),
        "head_rule_counts": dict(sorted(current_rules.items())),
        "retained_identity_sha256": digest(json.dumps(sorted(expected.items()), separators=(",", ":")).encode()),
    }


def diagnostic_identity_pairs(baseline: list[dict], head: list[dict], removed_indices: set[int],
                              evidence: Path) -> None:
    old_results = [r for i, r in enumerate(baseline) if i not in removed_indices]
    before = collections.Counter(identity(r) for r in old_results)
    after = collections.Counter(identity(r) for r in head)
    missing, added = before - after, after - before
    old_by_identity = {identity(r): r for r in old_results}
    new_by_identity = {identity(r): r for r in head}
    pairs = []
    for old_identity, count in missing.items():
        old_key = json.loads(old_identity)
        candidates = [new_identity for new_identity in added
                      if json.loads(new_identity)[0:2] == old_key[0:2]
                      and json.loads(new_identity)[3] == old_key[3]]
        if count == 1 and len(candidates) == 1 and added[candidates[0]] == 1:
            new_identity = candidates[0]
            pairs.append({"old_identity": old_identity, "new_identity": new_identity,
                          "baseline_location": old_by_identity[old_identity]["locations"][0],
                          "head_location": new_by_identity[new_identity]["locations"][0]})
    diagnostic = {"missing": dict(missing), "added": dict(added), "unique_same_rule_path_message_pairs": pairs}
    (evidence / "identity-drift.json").write_text(json.dumps(diagnostic, indent=2, sort_keys=True) + "\n")
    for pair in pairs[:30]:
        print("IDENTITY_DRIFT_PAIR " + json.dumps(pair, sort_keys=True), flush=True)


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None


def api(path: str, token: str) -> dict:
    request = urllib.request.Request("https://api.github.com/repos/" + REPOSITORY + "/" + path,
                                    headers={"Authorization": "Bearer " + token,
                                             "Accept": "application/vnd.github+json",
                                             "X-GitHub-Api-Version": "2022-11-28"})
    with urllib.request.build_opener(NoRedirect).open(request, timeout=30) as response:
        return json.load(response)


def artifact_bytes(artifact_id: int, token: str) -> bytes:
    request = urllib.request.Request(
        f"https://api.github.com/repos/{REPOSITORY}/actions/artifacts/{artifact_id}/zip",
        headers={"Authorization": "Bearer " + token, "Accept": "application/vnd.github+json"})
    try:
        urllib.request.build_opener(NoRedirect).open(request, timeout=30)
    except urllib.error.HTTPError as exc:
        if exc.code != 302:
            raise ValueError(f"Artifact download API failed with HTTP {exc.code}") from None
        location = exc.headers.get("Location", "")
    else:
        raise ValueError("Expected authenticated API redirect")
    parsed = urllib.parse.urlparse(location)
    if parsed.scheme != "https" or not parsed.hostname or not any(
        parsed.hostname.endswith(suffix) for suffix in [".blob.core.windows.net", ".githubusercontent.com"]
    ):
        raise ValueError("Unexpected artifact redirect host")
    # Never forward the repository token to the storage redirect.
    with urllib.request.urlopen(location, timeout=30) as response:
        data = response.read(MAX_BYTES + 1)
    if len(data) > MAX_BYTES:
        raise ValueError("Artifact ZIP exceeds limit")
    return data


def raw_sarif_from_zip(data: bytes) -> bytes:
    with zipfile.ZipFile(io.BytesIO(data)) as archive:
        roots = [entry for entry in archive.infolist() if entry.filename == "qodana.sarif.json"]
        if len(roots) != 1 or roots[0].file_size > MAX_BYTES:
            raise ValueError("Require one bounded canonical root SARIF file")
        raw = archive.read(roots[0])
        # Qodana also packages an identical report/results copy. A different
        # copy remains ambiguous and must stop; do not select by basename alone.
        mirrors = [entry for entry in archive.infolist()
                   if entry.filename != "qodana.sarif.json" and Path(entry.filename).name == "qodana.sarif.json"]
        for entry in mirrors:
            if entry.file_size > MAX_BYTES or archive.read(entry) != raw:
                raise ValueError("Conflicting or oversized packaged SARIF copy")
    return raw


def read_snapshot(revision: dict, token: str, evidence: Path, label: str) -> tuple[bytes, dict]:
    run = api(f"actions/runs/{revision['run_id']}", token)
    artifact = api(f"actions/artifacts/{revision['artifact_id']}", token)
    expected_name = "Qodana Deep Analysis" if label == "identity-anchor" else "Qodana Cloud Full Analysis"
    expected_artifact_name = "qodana-full-inventory" if label == "identity-anchor" else "qodana-cloud-full-inventory"
    if (run.get("head_sha") != revision["sha"] or run.get("head_branch") != "main"
            or run.get("status") != "completed" or run.get("conclusion") != "success"
            or run.get("name") != expected_name):
        raise ValueError("Require successful immutable main Qodana analysis run")
    if (artifact.get("expired") or artifact.get("name") != expected_artifact_name
            or artifact.get("workflow_run", {}).get("id") != revision["run_id"]
            or artifact.get("workflow_run", {}).get("head_sha") != revision["sha"]
            or artifact.get("digest") != "sha256:" + revision["zip_sha256"]):
        raise ValueError("Artifact/run/digest binding mismatch")
    data = artifact_bytes(revision["artifact_id"], token)
    if digest(data) != revision["zip_sha256"]:
        raise ValueError("Downloaded ZIP digest mismatch")
    with zipfile.ZipFile(io.BytesIO(data)) as archive:
        sarif_entries = [{"path": entry.filename, "bytes": entry.file_size}
                         for entry in archive.infolist() if "sarif" in entry.filename.lower()]
        print(label + " SARIF archive entries " + json.dumps(sarif_entries[:16], sort_keys=True), flush=True)
    raw = raw_sarif_from_zip(data)
    (evidence / f"{label}.sarif.json").write_bytes(raw)
    provenance = {**revision, "sarif_sha256": digest(raw), "run_attempt": run.get("run_attempt"),
                  "artifact_created_at": artifact.get("created_at")}
    (evidence / f"{label}-provenance.json").write_text(json.dumps(provenance, indent=2) + "\n")
    print(label + " snapshot " + json.dumps(provenance, sort_keys=True), flush=True)
    return raw, provenance


def main() -> None:
    selection = json.loads(Path(sys.argv[1]).read_text())
    validate_selection(selection)
    evidence = Path(sys.argv[2])
    evidence.mkdir(parents=True, exist_ok=True)
    (evidence / "selection.json").write_text(json.dumps(selection, indent=2) + "\n")
    token = os.environ["GH_TOKEN"]
    anchor_raw, anchor_provenance = read_snapshot(selection["identity_anchor"], token, evidence, "identity-anchor")
    original = sarif_results(anchor_raw, ORIGINAL_SHA, ORIGINAL_SARIF_SHA256)
    if len(original) != 2480:
        raise ValueError("Frozen original finding total differs")
    baseline_raw, baseline_provenance = read_snapshot(selection["baseline"], token, evidence, "baseline")
    baseline = sarif_results(baseline_raw, AUDIT_SHA)
    # The immutable R2 audit explicitly proved all original/R2 identities and
    # indices equal. Verify the original bytes, then independently verify every
    # replay identity and its position. Never relabel replay bytes as the lost
    # Cloud #107 digest or apply indices to an unverified fresh report.
    ordered_identity_sha256 = require_ordered_identity_equivalence(original, baseline)
    head_raw, head_provenance = read_snapshot(selection["head"], token, evidence, "head")
    head = sarif_results(head_raw, selection["head"]["sha"])
    removed = set().union(*(indices(packet) for packet in selection["completed_packets"]))
    diagnostic_identity_pairs(baseline, head, removed, evidence)
    proof = {**reconcile(baseline, head, removed), "completed_packets": selection["completed_packets"],
             "baseline": baseline_provenance, "head": head_provenance,
             "identity_anchor": anchor_provenance,
             "historical_cloud_sarif_sha256": AUDIT_SARIF_SHA256,
             "historical_cloud_bytes_available": digest(baseline_raw) == AUDIT_SARIF_SHA256,
             "original_replay_ordered_identity_sha256": ordered_identity_sha256,
             "identity_mapping_authority": "Issue #976 R2 audit: all 2480 original/R2 identities and indices match"}
    (evidence / "proof.json").write_text(json.dumps(proof, indent=2, sort_keys=True) + "\n")
    print("QODANA_RECONCILIATION " + json.dumps(proof, sort_keys=True), flush=True)


if __name__ == "__main__":
    try:
        main()
    except (ValueError, KeyError, OSError, zipfile.BadZipFile) as exc:
        print("STOP: " + str(exc), file=sys.stderr)
        raise SystemExit(1)
