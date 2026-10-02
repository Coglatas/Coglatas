#!/usr/bin/env python3
"""Read immutable failed-run artifacts and emit bounded public contract diagnostics."""
import io
import json
import os
import zipfile
from reconcile_qodana import api, artifact_bytes, digest, MAX_BYTES
from pathlib import Path

selection = json.loads(Path("docs/ci/query-contract-diagnostic.json").read_text())
HEAD = selection["head_sha"]
RUN = selection["run_id"]
ARTIFACTS = [(a["id"], a["name"], a["zip_sha256"]) for a in selection["artifacts"]]
evidence = Path("artifacts/query-contract")
evidence.mkdir(parents=True, exist_ok=True)
TOKEN = os.environ["GH_TOKEN"]
run = api(f"actions/runs/{RUN}", TOKEN)
if run["head_sha"] != HEAD or run["status"] != "completed" or run["conclusion"] != "failure":
    raise ValueError("Unexpected diagnostic run provenance")

def public_fields(value, path=""):
    # Allow only contract/mutation data; never emit headers, cookies, bodies, URLs or credentials.
    allowed = {"id", "case_id", "test_case_id", "method", "path", "query", "meta",
               "generation", "phase", "components", "mode", "mutations",
               "parameter_location", "description", "keyword", "status_code"}
    if isinstance(value, dict):
        for key, item in value.items():
            current = f"{path}.{key}"
            if key in allowed:
                if key in {"meta", "query", "mutations", "components", "generation", "phase"}:
                    print("CASE_FIELD " + current + " " + json.dumps(item, ensure_ascii=True)[:12000])
                elif not isinstance(item, (dict, list)):
                    print("CASE_FIELD " + current + " " + json.dumps(item, ensure_ascii=True)[:2000])
            elif isinstance(item, (dict, list)):
                public_fields(item, current)
    elif isinstance(value, list):
        for i, item in enumerate(value[:30]):
            public_fields(item, f"{path}[{i}]")

for artifact_id, name, expected_digest in ARTIFACTS:
    metadata = api(f"actions/artifacts/{artifact_id}", TOKEN)
    if (metadata.get("expired") or metadata["name"] != name
            or metadata["workflow_run"]["id"] != RUN
            or metadata["workflow_run"]["head_sha"] != HEAD
            or metadata["digest"] != "sha256:" + expected_digest):
        raise ValueError("Unexpected diagnostic artifact binding")
    data = artifact_bytes(artifact_id, TOKEN)
    if digest(data) != expected_digest:
        raise ValueError("Diagnostic ZIP digest mismatch")
    with zipfile.ZipFile(io.BytesIO(data)) as archive:
        entries = archive.infolist()
        print("ARTIFACT_ENTRIES " + json.dumps([{"path": e.filename, "bytes": e.file_size} for e in entries][:60]))
        for entry in entries:
            if entry.file_size > 200 * MAX_BYTES:
                raise ValueError("Diagnostic member exceeds bounded limit")
            if name == "openapi-3.1-contract" and entry.filename.endswith(".json"):
                document = json.loads(archive.read(entry))
                if "openapi" not in document:
                    continue
                (evidence / "contract.json").write_text(json.dumps(document))
                operation = document["paths"]["/api/projects"]["get"]
                print("PROJECTS_GET_CONTRACT " + json.dumps(operation, ensure_ascii=True)[:18000])
                for parameter in operation.get("parameters", []):
                    ref = parameter.get("$ref")
                    if ref:
                        node = document
                        for segment in ref.removeprefix("#/").split("/"):
                            node = node[segment.replace("~1", "/").replace("~0", "~")]
                        print("RESOLVED_PARAMETER " + json.dumps(node, ensure_ascii=True))
            elif name == "security-scan-reports" and entry.filename.endswith("alpha-member.ndjson"):
                hits = 0
                with archive.open(entry) as stream:
                    for raw_line in stream:
                        if len(raw_line) > 16 * MAX_BYTES:
                            raise ValueError("Diagnostic event exceeds bounded limit")
                        if b"11IkF3" in raw_line:
                            event = json.loads(raw_line)
                            print("CASE_EVENT_KEYS " + json.dumps(list(event)))
                            case = event["ScenarioFinished"]["recorder"]["cases"]["11IkF3"]["value"]
                            public_fields(case)
                            safe_case = {key: case[key] for key in ("id", "method", "path", "query", "meta")}
                            (evidence / "case.json").write_text(json.dumps(safe_case))
                            hits += 1
                            if hits >= 4:
                                break
                print("TARGET_CASE_EVENTS " + str(hits))
