#!/usr/bin/env python3
"""Adversarial regressions for AV-MIG-02 verifier false-pass findings."""

from __future__ import annotations

import os
import tempfile
import unittest
from pathlib import Path

import test_av_mig_contract_boundary as baseline
import verify_av_mig_contract_boundary as verifier
import verify_av_mig_contract_boundary_base as base_verifier


class AdversarialContractBoundaryTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        cls.policy = verifier.load_policy(baseline.POLICY_PATH)
        cls.previous_symbols = os.environ.get("AV_MIG_CSHARP_DEFINE_CONSTANTS")
        os.environ["AV_MIG_CSHARP_DEFINE_CONSTANTS"] = baseline.TEST_DEFINE_CONSTANTS

    @classmethod
    def tearDownClass(cls) -> None:
        if cls.previous_symbols is None:
            os.environ.pop("AV_MIG_CSHARP_DEFINE_CONSTANTS", None)
        else:
            os.environ["AV_MIG_CSHARP_DEFINE_CONSTANTS"] = cls.previous_symbols

    def make_fixture(self):
        temp = tempfile.TemporaryDirectory()
        root = Path(temp.name)
        baseline.write_source_fixture(root, self.policy)
        document = baseline.build_document(self.policy)
        return temp, root, document

    @staticmethod
    def add_unlisted_operation(document, security_marker: object | None, *, inherit: bool = False) -> None:
        operation = {"responses": {"200": {"description": "fixture"}}}
        if not inherit:
            operation["security"] = security_marker
        document["paths"]["/api/unlisted-anonymous"] = {"get": operation}

    def test_unlisted_empty_security_requirement_object_fails(self) -> None:
        temp, root, document = self.make_fixture()
        self.addCleanup(temp.cleanup)
        self.add_unlisted_operation(document, [{}])
        with self.assertRaisesRegex(verifier.BoundaryViolation, "not explicitly allowlisted"):
            verifier.verify_boundary(document, self.policy, root)

    def test_unlisted_cookie_or_anonymous_alternative_fails(self) -> None:
        temp, root, document = self.make_fixture()
        self.addCleanup(temp.cleanup)
        self.add_unlisted_operation(document, [{"CookieAuth": []}, {}])
        with self.assertRaisesRegex(verifier.BoundaryViolation, "not explicitly allowlisted"):
            verifier.verify_boundary(document, self.policy, root)

    def test_unlisted_inherited_root_anonymous_alternative_fails(self) -> None:
        temp, root, document = self.make_fixture()
        self.addCleanup(temp.cleanup)
        document["security"] = [{"CookieAuth": []}, {}]
        self.add_unlisted_operation(document, None, inherit=True)
        with self.assertRaisesRegex(verifier.BoundaryViolation, "not explicitly allowlisted"):
            verifier.verify_boundary(document, self.policy, root)

    def test_hub_explicit_alternate_authentication_scheme_fails(self) -> None:
        temp, root, document = self.make_fixture()
        self.addCleanup(temp.cleanup)
        hub = root / "src/Coglatas.Web/Realtime/AppHub.cs"
        source = hub.read_text(encoding="utf-8")
        hub.write_text(
            source.replace(
                "[Authorize]",
                '[Authorize(AuthenticationSchemes = "OtherScheme")]',
                1,
            ),
            encoding="utf-8",
        )
        with self.assertRaisesRegex(verifier.BoundaryViolation, "without arguments"):
            verifier.verify_boundary(document, self.policy, root)

    def test_raw_string_cannot_hide_real_allow_anonymous_attribute(self) -> None:
        temp, root, document = self.make_fixture()
        self.addCleanup(temp.cleanup)
        hub = root / "src/Coglatas.Web/Realtime/AppHub.cs"
        source = hub.read_text(encoding="utf-8")
        hub.write_text(
            'class Noise { private const string Marker = """x " // """; } [AllowAnonymous]\n'
            + source,
            encoding="utf-8",
        )
        with self.assertRaisesRegex(verifier.BoundaryViolation, "must not allow"):
            verifier.verify_boundary(document, self.policy, root)

    def test_raw_string_route_decoy_does_not_satisfy_mapping(self) -> None:
        temp, root, document = self.make_fixture()
        self.addCleanup(temp.cleanup)
        program = root / "src/Coglatas.Web/Program.cs"
        path = self.policy["nonOpenApiContracts"]["signalR"]["path"]
        program.write_text(
            'var decoy = """\n'
            f'app.MapHub<AppHub>("{path}");\n'
            '""";\n',
            encoding="utf-8",
        )
        with self.assertRaisesRegex(verifier.BoundaryViolation, "top-level unchained"):
            verifier.verify_boundary(document, self.policy, root)

    def test_base_verifier_ignores_raw_string_route_decoy(self) -> None:
        temp, root, _document = self.make_fixture()
        self.addCleanup(temp.cleanup)
        program = root / "src/Coglatas.Web/Program.cs"
        path = self.policy["nonOpenApiContracts"]["signalR"]["path"]
        program.write_text(
            'var decoy = """\n'
            f'app.MapHub<AppHub>("{path}");\n'
            '""";\n',
            encoding="utf-8",
        )
        with self.assertRaisesRegex(base_verifier.BoundaryViolation, "path drifted"):
            base_verifier.verify_signalr_contract(
                self.policy,
                root,
                set(baseline.TEST_DEFINE_CONSTANTS.split(";")),
            )

    def test_runtime_unreachable_mapping_does_not_satisfy_contract(self) -> None:
        temp, root, document = self.make_fixture()
        self.addCleanup(temp.cleanup)
        program = root / "src/Coglatas.Web/Program.cs"
        path = self.policy["nonOpenApiContracts"]["signalR"]["path"]
        program.write_text(
            "if (false)\n{\n"
            f'    app.MapHub<AppHub>("{path}");\n'
            "}\n",
            encoding="utf-8",
        )
        with self.assertRaisesRegex(verifier.BoundaryViolation, "top-level unchained"):
            verifier.verify_boundary(document, self.policy, root)


if __name__ == "__main__":
    unittest.main()
