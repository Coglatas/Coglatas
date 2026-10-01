#!/usr/bin/env python3
"""Verify a query-schema proposal against the pinned scanner without a server."""
import copy
import importlib.metadata
import json
from pathlib import Path
from urllib.parse import parse_qs, urlparse
import requests
import schemathesis
from schemathesis.core.parameters import ParameterLocation
from schemathesis.generation import GenerationMode
from schemathesis.generation.meta import CaseMetadata, ComponentInfo, GenerationInfo, PhaseInfo, TestPhase, FuzzingPhaseData
from schemathesis.specs.openapi.checks import has_only_additional_properties_in_non_body_parameters

PATTERN = "^(?:[tT][rR][uU][eE]|[fF][aA][lL][sS][eE])$"
assert importlib.metadata.version("schemathesis") == "4.25.2"
root = Path("/work/artifacts/query-contract")
document = json.loads((root / "contract.json").read_text())
captured = json.loads((root / "case.json").read_text())
meta = CaseMetadata(
    generation=GenerationInfo(time=0, mode=GenerationMode.NEGATIVE),
    components={ParameterLocation.QUERY: ComponentInfo(mode=GenerationMode.NEGATIVE)},
    phase=PhaseInfo(name=TestPhase.FUZZING, data=FuzzingPhaseData(
        description="violates additionalProperties", parameter=None,
        parameter_location=ParameterLocation.QUERY, location=None)))

def case_for(doc, query):
    schema = schemathesis.openapi.from_dict(doc)
    return schema["/api/projects"]["GET"].Case(query=copy.deepcopy(query), _meta=copy.deepcopy(meta))

query = captured["query"]
current = case_for(document, query)
assert not has_only_additional_properties_in_non_body_parameters(current)
known = {p["name"] for p in document["paths"]["/api/projects"]["get"]["parameters"]}
extras = set(query) - known
assert len(extras) == 1 and all(query[key] == [] for key in extras)
prepared = requests.Request("GET", "http://example.invalid/api/projects", params=query).prepare()
wire_names = set(parse_qs(urlparse(prepared.url).query))
assert wire_names <= known and not wire_names & extras

proposed = copy.deepcopy(document)
changed = []
for path, path_item in proposed["paths"].items():
    for method, operation in path_item.items():
        if not isinstance(operation, dict):
            continue
        for parameter in operation.get("parameters", []):
            schema = parameter.get("schema", {})
            if parameter.get("in") == "query" and schema.get("type") == "boolean":
                schema["type"] = ["boolean", "string"]
                schema["pattern"] = PATTERN
                changed.append({"path": path, "method": method, "name": parameter["name"]})

fixed = case_for(proposed, query)
assert has_only_additional_properties_in_non_body_parameters(fixed)
valid = 0
for value in [True, False, "true", "false", "TRUE", "False", "TrUe"]:
    candidate_query = copy.deepcopy(query)
    candidate_query["Archived"] = value
    assert has_only_additional_properties_in_non_body_parameters(case_for(proposed, candidate_query))
    valid += 1

rejected = 0
for value in ["not-bool", "", "null", "1", "truejunk", None, 1, []]:
    candidate_query = copy.deepcopy(query)
    candidate_query["Archived"] = value
    assert not has_only_additional_properties_in_non_body_parameters(case_for(proposed, candidate_query)), repr(value)
    rejected += 1
bad_integer = copy.deepcopy(query)
bad_integer["Page"] = "invalid-page"
assert not has_only_additional_properties_in_non_body_parameters(case_for(proposed, bad_integer))
rejected += 1

# The proposal changes only the declared inline query boolean type and pattern.
expected = copy.deepcopy(document)
for change in changed:
    parameter = next(p for p in expected["paths"][change["path"]][change["method"]]["parameters"]
                     if p["name"] == change["name"] and p["in"] == "query")
    parameter["schema"]["type"] = ["boolean", "string"]
    parameter["schema"]["pattern"] = PATTERN
assert expected == proposed
proof = {"version": "4.25.2", "current_guard": False, "proposed_guard": True,
         "additional_query_key_absent_on_wire": True, "valid_cases": valid,
         "rejected_cases": rejected, "changed_query_parameters": changed,
         "scope": "proposal simulation only; runtime code and scanner checks unchanged"}
(root / "proposal-proof.json").write_text(json.dumps(proof, indent=2) + "\n")
print("QUERY_PROPOSAL_PROOF " + json.dumps(proof), flush=True)
