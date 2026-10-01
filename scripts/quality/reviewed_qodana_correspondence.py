"""Validate explicit retained-finding correspondences against immutable source."""
import base64
import collections
import hashlib
import re
import reconcile_qodana as q

ENTRY_FIELDS = {"before_identity", "after_identity", "before_location", "after_location",
                "before_blob", "after_blob", "before_declaration", "after_declaration", "packet", "rationale"}
MANIFEST_FIELDS = {"version", "baseline_sha", "head_sha", "baseline_sarif_sha256",
                   "head_sarif_sha256", "correspondences"}


def read_source(path, revision, expected_blob, token):
    if (not re.fullmatch(r"src/[A-Za-z0-9_./-]+", path) or ".." in path.split("/")
            or not re.fullmatch(r"[0-9a-f]{40}", revision)
            or not re.fullmatch(r"[0-9a-f]{40}", expected_blob)):
        raise ValueError("Unsafe or mutable source binding")
    response = q.api("contents/" + path + "?ref=" + revision, token)
    if (response.get("type") != "file" or response.get("path") != path
            or response.get("sha") != expected_blob or response.get("encoding") != "base64"):
        raise ValueError("Source path/revision/blob mismatch")
    data = base64.b64decode(response["content"], validate=False)
    if len(data) > 1024 * 1024:
        raise ValueError("Source exceeds correspondence size limit")
    actual_blob = hashlib.sha1(b"blob " + str(len(data)).encode() + b"\0" + data).hexdigest()
    if actual_blob != expected_blob:
        raise ValueError("Downloaded source blob bytes mismatch")
    return data.decode("utf-8-sig")


def verify_source_anchor(source, physical_location, declaration):
    region = physical_location["region"]
    if region["startLine"] != region["endLine"]:
        raise ValueError("Correspondence requires one exact declaration line")
    lines = source.splitlines()
    line_number = region["startLine"]
    if type(line_number) is not int or not 1 <= line_number <= len(lines):
        raise ValueError("Source region outside bound file")
    line = lines[line_number - 1]
    if line != declaration:
        raise ValueError("Reviewed declaration does not match bound source")
    start, end = region["startColumn"], region["endColumn"]
    if type(start) is not int or type(end) is not int or not 1 <= start < end:
        raise ValueError("Invalid correspondence columns")
    encoded = line.encode("utf-16-le")
    if 2 * (end - 1) > len(encoded):
        raise ValueError("Source region outside declaration")
    text = encoded[2 * (start - 1):2 * (end - 1)].decode("utf-16-le")
    if not text or text != region["snippet"]["text"]:
        raise ValueError("Retained source token does not match exact region")


def validate_correspondences(manifest, baseline, head, removed_indices,
                             baseline_provenance, head_provenance, completed_packets, source_reader):
    if set(manifest) != MANIFEST_FIELDS or manifest["version"] != 1:
        raise ValueError("Invalid correspondence manifest")
    for prefix, provenance in [("baseline", baseline_provenance), ("head", head_provenance)]:
        if (manifest[prefix + "_sha"] != provenance["sha"]
                or manifest[prefix + "_sarif_sha256"] != provenance["sarif_sha256"]):
            raise ValueError("Correspondence SARIF/revision binding mismatch")
    entries = manifest["correspondences"]
    if not isinstance(entries, list) or not entries or len(entries) > 30:
        raise ValueError("Require bounded explicit correspondence entries")
    retained = [r for i, r in enumerate(baseline) if i not in removed_indices]
    before_count = collections.Counter(q.identity(r) for r in retained)
    after_count = collections.Counter(q.identity(r) for r in head)
    before_results = {q.identity(r): r for r in retained}
    after_results = {q.identity(r): r for r in head}
    mapping = {}
    destinations = set()
    source_cache = {}
    for entry in entries:
        if set(entry) != ENTRY_FIELDS or entry["packet"] not in completed_packets or not entry["rationale"]:
            raise ValueError("Invalid or unlanded correspondence entry")
        before, after = entry["before_identity"], entry["after_identity"]
        if (not isinstance(before, list) or not isinstance(after, list) or len(before) != 4 or len(after) != 4
                or before[0:2] != after[0:2] or before[3] != after[3]
                or set(before[2]) != {"equalIndicator/v1"} or set(after[2]) != {"equalIndicator/v1"}
                or before[2] == after[2]
                or any(not re.fullmatch(r"[0-9A-F]{64}", item["equalIndicator/v1"]) for item in [before[2], after[2]])):
            raise ValueError("Only explicit equalIndicator fingerprint relocation is permitted")
        old = q.json.dumps(before, sort_keys=True, separators=(",", ":"))
        new = q.json.dumps(after, sort_keys=True, separators=(",", ":"))
        if (old in mapping or new in destinations or before_count[old] != 1 or after_count[new] != 1
                or after_count[old] != 0 or before_count[new] != 0):
            raise ValueError("Correspondence duplicate, removed, absent or colliding identity")
        old_region, new_region = entry["before_location"]["region"], entry["after_location"]["region"]
        for field in ["startLine", "endLine", "startColumn", "endColumn", "snippet"]:
            if old_region[field] != new_region[field]:
                raise ValueError("Retained source token/location changed")
        for prefix, results, key, provenance in [
                ("before", before_results, old, baseline_provenance), ("after", after_results, new, head_provenance)]:
            physical = results[key]["locations"][0]["physicalLocation"]
            if physical != entry[prefix + "_location"] or physical["artifactLocation"]["uri"] != before[1]:
                raise ValueError("Actual finding location differs from reviewed correspondence")
            binding = (before[1], provenance["sha"], entry[prefix + "_blob"])
            if binding not in source_cache:
                source_cache[binding] = source_reader(*binding)
            verify_source_anchor(source_cache[binding], physical, entry[prefix + "_declaration"])
        mapping[old] = new
        destinations.add(new)
    return mapping
