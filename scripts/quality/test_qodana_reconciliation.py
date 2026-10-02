"""Fail closed on inventory drift, stale provenance and token forwarding."""
import io
import copy
import base64
import hashlib
import json
import zipfile
import unittest
from unittest import mock
import urllib.error

import reconcile_qodana as q
import reviewed_qodana_correspondence as reviewed


def result(name="same", line=1):
    return {"ruleId": "TestRule", "message": {"text": name},
            "locations": [{"physicalLocation": {"artifactLocation": {"uri": "src/Test.cs"},
                                                "region": {"startLine": line}}}],
            "partialFingerprints": {"stable/v1": name}}


def selection():
    return {"version": 1, "repository": q.REPOSITORY,
            "baseline": {"sha": q.AUDIT_SHA, "run_id": 1, "artifact_id": 2, "zip_sha256": "a" * 64},
            "head": {"sha": "b" * 40, "run_id": 3, "artifact_id": 4, "zip_sha256": "c" * 64},
            "identity_anchor": {"sha": q.ORIGINAL_SHA, "run_id": 5, "artifact_id": 6, "zip_sha256": "d" * 64},
            "completed_packets": ["P11", "P13", "P14"]}


class ReconciliationTests(unittest.TestCase):
    def test_frozen_packets_are_disjoint_and_cover_114_fixed_results(self):
        groups = [q.indices(packet) for packet in q.PACKET_RANGES]
        self.assertEqual(sum(map(len, groups)), 114)
        self.assertEqual(len(set().union(*groups)), 114)
        self.assertEqual(len(set().union(*(q.indices(p) for p in ["P11", "P13", "P14"]))), 39)
        q.validate_selection(selection())

    def test_selection_rejects_unknown_duplicate_packet_mutable_or_invalid_revision(self):
        mutations = [
            lambda s: s.update(completed_packets=["P11", "P11"]),
            lambda s: s.update(completed_packets=["P16"]),
            lambda s: s.update(completed_packets=[]),
            lambda s: s.update(repository="other/repo"),
            lambda s: s["baseline"].update(sha="a" * 40),
            lambda s: s["identity_anchor"].update(sha="a" * 40),
            lambda s: s["head"].update(sha="main"),
            lambda s: s["head"].update(zip_sha256="missing"),
            lambda s: s["head"].update(run_id=True),
            lambda s: s.update(extra=True),
        ]
        for mutate in mutations:
            data = selection()
            mutate(data)
            with self.subTest(data=data), self.assertRaises(ValueError):
                q.validate_selection(data)

    def test_exact_removal_preserves_line_shifted_retained_identity(self):
        proof = q.reconcile([result("fixed"), result("keep", 10)], [result("keep", 99)], {0})
        self.assertEqual(proof["measured_removals"], 1)
        self.assertEqual(proof["retained_identities"], 1)
        self.assertEqual(proof["unexpected_added"], 0)

    def test_duplicate_identity_multiplicity_is_preserved(self):
        q.reconcile([result(), result(), result("fixed")], [result(), result()], {2})
        with self.assertRaises(ValueError):
            q.reconcile([result(), result(), result("fixed")], [result()], {2})

    def test_unexpected_new_or_removed_retained_findings_stop(self):
        for head in [[result("new")], [], [result("keep"), result("new")]]:
            with self.subTest(head=head), self.assertRaises(ValueError):
                q.reconcile([result("fixed"), result("keep")], head, {0})

    def test_missing_expected_removal_stops(self):
        with self.assertRaises(ValueError):
            q.reconcile([result("fixed"), result("keep")], [result("fixed"), result("keep")], {0})

    def test_fingerprint_or_message_or_path_drift_is_not_silently_normalized(self):
        for key in ["fingerprint", "message", "path"]:
            changed = result("keep")
            if key == "fingerprint":
                changed["partialFingerprints"]["stable/v1"] = "changed"
            elif key == "message":
                changed["message"]["text"] = "changed"
            else:
                changed["locations"][0]["physicalLocation"]["artifactLocation"]["uri"] = "src/Other.cs"
            with self.subTest(key=key), self.assertRaises(ValueError):
                q.reconcile([result("keep")], [changed], set())

    def test_invalid_removal_indices_stop(self):
        for index in [-1, 1, True]:
            with self.subTest(index=index), self.assertRaises(ValueError):
                q.reconcile([result()], [], {index})

    def test_malformed_identity_stops(self):
        for changed in [{}, {"ruleId": "TestRule"}, {**result(), "partialFingerprints": {"key": 3}}]:
            with self.subTest(changed=changed), self.assertRaises(ValueError):
                q.identity(changed)

    def test_sarif_requires_exact_digest_revision_one_run_and_nonempty_inventory(self):
        run = {"versionControlProvenance": [{"revisionId": q.AUDIT_SHA}], "results": [result()]}
        raw = json.dumps({"runs": [run]}).encode()
        self.assertEqual(q.sarif_results(raw, q.AUDIT_SHA, q.digest(raw)), [result()])
        for data, revision, digest in [
            (raw, q.AUDIT_SHA, "f" * 64),
            (raw, "b" * 40, q.digest(raw)),
            (json.dumps({"runs": [run, run]}).encode(), q.AUDIT_SHA, None),
            (json.dumps({"runs": [{**run, "results": []}]}).encode(), q.AUDIT_SHA, None),
        ]:
            with self.subTest(revision=revision), self.assertRaises(ValueError):
                q.sarif_results(data, revision, digest)

    def test_storage_redirect_does_not_receive_repository_token(self):
        redirect = urllib.error.HTTPError("https://api.github.com/test", 302, "Found",
                                          {"Location": "https://example.blob.core.windows.net/artifact"}, None)
        opener = mock.Mock()
        opener.open.side_effect = redirect
        with mock.patch.object(q.urllib.request, "build_opener", return_value=opener), \
                mock.patch.object(q.urllib.request, "urlopen", return_value=io.BytesIO(b"zip")) as download:
            self.assertEqual(q.artifact_bytes(1, "test-token"), b"zip")
        self.assertEqual(download.call_args.args, ("https://example.blob.core.windows.net/artifact",))
        self.assertEqual(opener.open.call_args.args[0].get_header("Authorization"), "Bearer test-token")

    def test_unexpected_storage_host_or_http_scheme_stops_before_download(self):
        for location in ["https://example.com/artifact", "http://example.blob.core.windows.net/artifact"]:
            opener = mock.Mock()
            opener.open.side_effect = urllib.error.HTTPError(
                "https://api.github.com/test", 302, "Found", {"Location": location}, None)
            with mock.patch.object(q.urllib.request, "build_opener", return_value=opener), \
                    mock.patch.object(q.urllib.request, "urlopen") as download, \
                    self.subTest(location=location), self.assertRaises(ValueError):
                q.artifact_bytes(1, "test-token")
            download.assert_not_called()

    @staticmethod
    def zip_fixture(entries):
        buffer = io.BytesIO()
        with zipfile.ZipFile(buffer, "w") as archive:
            for path, content in entries:
                archive.writestr(path, content)
        return buffer.getvalue()

    def test_canonical_root_allows_only_byte_identical_report_mirror(self):
        data = self.zip_fixture([("qodana.sarif.json", b"raw"),
                                 ("report/results/qodana.sarif.json", b"raw"),
                                 ("log/clt.original.sarif.json", b"other")])
        self.assertEqual(q.raw_sarif_from_zip(data), b"raw")

    def test_conflicting_mirror_or_missing_canonical_root_stops(self):
        for entries in [[("qodana.sarif.json", b"raw"), ("report/results/qodana.sarif.json", b"other")],
                        [("report/results/qodana.sarif.json", b"raw")], [("qodana-short.sarif.json", b"short")]]:
            with self.subTest(entries=entries), self.assertRaises(ValueError):
                q.raw_sarif_from_zip(self.zip_fixture(entries))

    def test_canonical_root_size_limit_stops_archive_bomb(self):
        data = self.zip_fixture([("qodana.sarif.json", b"oversized")])
        with mock.patch.object(q, "MAX_BYTES", 2), self.assertRaises(ValueError):
            q.raw_sarif_from_zip(data)

    def test_original_replay_requires_every_identity_and_its_exact_index(self):
        original = [result("first"), result("second")]
        q.require_ordered_identity_equivalence(original, [result("first", 100), result("second", 200)])
        for replay in [[result("first")], [result("second"), result("first")],
                       [result("first"), result("changed")]]:
            with self.subTest(replay=replay), self.assertRaises(ValueError):
                q.require_ordered_identity_equivalence(original, replay)



class ReviewedCorrespondenceTests(unittest.TestCase):
    def fixture(self):
        declaration = "public bool Flag { get; set; }"
        region = {"startLine": 1, "endLine": 1, "startColumn": 13, "endColumn": 17,
                  "snippet": {"text": "Flag"}}
        old = result("retained")
        old["partialFingerprints"] = {"equalIndicator/v1": "A" * 64}
        old["locations"][0]["physicalLocation"]["region"] = region
        new = copy.deepcopy(old)
        new["partialFingerprints"] = {"equalIndicator/v1": "B" * 64}
        entry = {"before_identity": json.loads(q.identity(old)), "after_identity": json.loads(q.identity(new)),
                 "before_location": copy.deepcopy(old["locations"][0]["physicalLocation"]),
                 "after_location": copy.deepcopy(new["locations"][0]["physicalLocation"]),
                 "before_blob": "a" * 40, "after_blob": "b" * 40,
                 "before_declaration": declaration, "after_declaration": declaration,
                 "packet": "P01", "rationale": "Reviewed unchanged declaration and context relocation."}
        baseline_provenance = {"sha": "c" * 40, "sarif_sha256": "d" * 64}
        head_provenance = {"sha": "e" * 40, "sarif_sha256": "f" * 64}
        manifest = {"version": 1, "baseline_sha": baseline_provenance["sha"], "head_sha": head_provenance["sha"],
                    "baseline_sarif_sha256": baseline_provenance["sarif_sha256"],
                    "head_sarif_sha256": head_provenance["sarif_sha256"], "correspondences": [entry]}
        return old, new, manifest, baseline_provenance, head_provenance, declaration

    def validate(self, fixture, removed=set()):
        old, new, manifest, bp, hp, declaration = fixture
        return reviewed.validate_correspondences(manifest, [old], [new], removed, bp, hp, ["P01"],
                                                  lambda path, revision, blob: declaration)

    def test_explicit_relocation_preserves_warning_and_is_not_counted_as_source_removal(self):
        fixture = self.fixture()
        mapping = self.validate(fixture)
        old, new = fixture[:2]
        proof = q.reconcile([old, result("fixed")], [new], {1}, mapping)
        self.assertEqual(proof["measured_removals"], 1)
        self.assertEqual(proof["mapped_retained_findings"], 1)
        self.assertEqual(proof["retained_identities"], 1)

    def test_correspondence_requires_actual_immutable_revision_and_raw_sarif_digest(self):
        for key in ["baseline_sha", "head_sha", "baseline_sarif_sha256", "head_sarif_sha256"]:
            fixture = self.fixture()
            fixture[2][key] = "0" * len(fixture[2][key])
            with self.subTest(key=key), self.assertRaises(ValueError):
                self.validate(fixture)

    def test_unreviewed_fingerprint_rule_path_and_message_changes_stop(self):
        for index, value in [(0, "NewRule"), (1, "src/Other.cs"), (2, {"equalIndicator/v1": "C" * 64}), (3, "new message")]:
            fixture = self.fixture()
            fixture[2]["correspondences"][0]["after_identity"][index] = value
            with self.subTest(index=index), self.assertRaises(ValueError):
                self.validate(fixture)

    def test_removed_or_absent_old_finding_cannot_be_a_correspondence(self):
        with self.assertRaises(ValueError):
            self.validate(self.fixture(), {0})

    def test_duplicate_correspondences_stop(self):
        fixture = self.fixture()
        fixture[2]["correspondences"].append(copy.deepcopy(fixture[2]["correspondences"][0]))
        with self.assertRaises(ValueError):
            self.validate(fixture)

    def test_exact_source_location_and_declaration_are_required(self):
        for change in ["line", "column", "snippet", "declaration", "actual_context"]:
            fixture = self.fixture()
            entry = fixture[2]["correspondences"][0]
            if change == "declaration":
                entry["after_declaration"] += " // changed"
            elif change == "actual_context":
                entry["after_location"]["unexpected"] = "drift"
            else:
                field = {"line": "startLine", "column": "startColumn", "snippet": "snippet"}[change]
                entry["after_location"]["region"][field] = {"text": "Other"} if field == "snippet" else 2
            with self.subTest(change=change), self.assertRaises(ValueError):
                self.validate(fixture)

    def test_unlanded_packet_and_unknown_manifest_fields_stop(self):
        for change in ["packet", "manifest", "entry"]:
            fixture = self.fixture()
            if change == "packet":
                fixture[2]["correspondences"][0]["packet"] = "P09"
            elif change == "manifest":
                fixture[2]["ignore_new"] = True
            else:
                fixture[2]["correspondences"][0]["ignore"] = True
            with self.subTest(change=change), self.assertRaises(ValueError):
                self.validate(fixture)

    def test_mapping_does_not_hide_new_findings_or_other_fingerprint_drift(self):
        fixture = self.fixture()
        mapping = self.validate(fixture)
        old, new = fixture[:2]
        with self.assertRaises(ValueError):
            q.reconcile([old], [new, result("unexpected")], set(), mapping)
        unreviewed = copy.deepcopy(new)
        unreviewed["partialFingerprints"] = {"equalIndicator/v1": "C" * 64}
        with self.assertRaises(ValueError):
            q.reconcile([old], [unreviewed], set(), mapping)

    def test_downloaded_source_requires_matching_path_blob_and_actual_git_bytes(self):
        data = b"public bool Flag { get; set; }\n"
        blob = hashlib.sha1(b"blob " + str(len(data)).encode() + b"\0" + data).hexdigest()
        response = {"type": "file", "path": "src/Test.cs", "sha": blob, "encoding": "base64",
                    "content": base64.b64encode(data).decode()}
        with mock.patch.object(q, "api", return_value=response):
            self.assertEqual(reviewed.read_source("src/Test.cs", "a" * 40, blob, "not-printed"), data.decode())
        response["content"] = base64.b64encode(b"different source").decode()
        with mock.patch.object(q, "api", return_value=response), self.assertRaises(ValueError):
            reviewed.read_source("src/Test.cs", "a" * 40, blob, "not-printed")

    def test_unsafe_path_and_mutable_source_revision_stop_before_api_request(self):
        for path, revision in [("src/../Other.cs", "a" * 40), ("src/Test.cs", "main")]:
            with mock.patch.object(q, "api") as api_mock, self.assertRaises(ValueError):
                reviewed.read_source(path, revision, "b" * 40, "not-printed")
            api_mock.assert_not_called()

if __name__ == "__main__":
    unittest.main()
