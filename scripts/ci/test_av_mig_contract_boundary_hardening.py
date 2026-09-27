#!/usr/bin/env python3
"""Regression mutations for AV-MIG-02 runtime-contract hardening."""

from __future__ import annotations

import os
import tempfile
import unittest
from pathlib import Path

import test_av_mig_contract_boundary as baseline
import verify_av_mig_contract_boundary as verifier


class RuntimeContractMutationTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        cls.policy = verifier.load_policy(baseline.POLICY_PATH)
        cls._previous_define_constants = os.environ.get("AV_MIG_CSHARP_DEFINE_CONSTANTS")
        os.environ["AV_MIG_CSHARP_DEFINE_CONSTANTS"] = baseline.TEST_DEFINE_CONSTANTS

    @classmethod
    def tearDownClass(cls) -> None:
        if cls._previous_define_constants is None:
            os.environ.pop("AV_MIG_CSHARP_DEFINE_CONSTANTS", None)
        else:
            os.environ["AV_MIG_CSHARP_DEFINE_CONSTANTS"] = cls._previous_define_constants

    def make_fixture(self):
        temp = tempfile.TemporaryDirectory()
        root = Path(temp.name)
        baseline.write_source_fixture(root, self.policy)
        document = baseline.build_document(self.policy)
        return temp, root, document

    def first_signalr_signature(self) -> dict[str, object]:
        signalr = self.policy["nonOpenApiContracts"]["signalR"]
        signature = signalr["clientMethodSignatures"][0]
        assert isinstance(signature, dict)
        return signature

    def test_unlisted_anonymous_openapi_operation_fails(self) -> None:
        temp, root, document = self.make_fixture()
        self.addCleanup(temp.cleanup)
        document["paths"]["/api/unlisted-anonymous"] = {
            "get": {
                "responses": {"200": {"description": "fixture"}},
                "security": [],
            }
        }
        with self.assertRaisesRegex(verifier.BoundaryViolation, "not explicitly allowlisted"):
            verifier.verify_boundary(document, self.policy, root)

    def test_hub_allow_anonymous_attribute_fails(self) -> None:
        temp, root, document = self.make_fixture()
        self.addCleanup(temp.cleanup)
        hub = root / "src/Coglatas.Web/Realtime/AppHub.cs"
        source = hub.read_text(encoding="utf-8")
        hub.write_text(
            source.replace("[Authorize]\n", "[Authorize]\n[AllowAnonymous]\n", 1),
            encoding="utf-8",
        )
        with self.assertRaisesRegex(verifier.BoundaryViolation, "must not allow"):
            verifier.verify_boundary(document, self.policy, root)

    def test_hub_endpoint_allow_anonymous_fails(self) -> None:
        temp, root, document = self.make_fixture()
        self.addCleanup(temp.cleanup)
        program = root / "src/Coglatas.Web/Program.cs"
        source = program.read_text(encoding="utf-8")
        program.write_text(
            source.replace('app.MapHub<AppHub>("/hubs/app");', 'app.MapHub<AppHub>("/hubs/app").AllowAnonymous();'),
            encoding="utf-8",
        )
        with self.assertRaisesRegex(verifier.BoundaryViolation, "direct unchained app.MapHub"):
            verifier.verify_boundary(document, self.policy, root)

    def test_hub_mapping_through_anonymous_group_fails(self) -> None:
        temp, root, document = self.make_fixture()
        self.addCleanup(temp.cleanup)
        program = root / "src/Coglatas.Web/Program.cs"
        signalr = self.policy["nonOpenApiContracts"]["signalR"]
        path = signalr["path"]
        assert isinstance(path, str)
        program.write_text(
            'var anonymousHubGroup = app.MapGroup("").AllowAnonymous();\n'
            f'anonymousHubGroup.MapHub<AppHub>("{path}");\n',
            encoding="utf-8",
        )
        with self.assertRaisesRegex(verifier.BoundaryViolation, "direct unchained app.MapHub"):
            verifier.verify_boundary(document, self.policy, root)

    def test_hub_endpoint_builder_later_allow_anonymous_fails(self) -> None:
        temp, root, document = self.make_fixture()
        self.addCleanup(temp.cleanup)
        program = root / "src/Coglatas.Web/Program.cs"
        signalr = self.policy["nonOpenApiContracts"]["signalR"]
        path = signalr["path"]
        assert isinstance(path, str)
        program.write_text(
            f'var hubEndpoint = app.MapHub<AppHub>("{path}");\n'
            "hubEndpoint.AllowAnonymous();\n",
            encoding="utf-8",
        )
        with self.assertRaisesRegex(verifier.BoundaryViolation, "direct unchained app.MapHub"):
            verifier.verify_boundary(document, self.policy, root)

    def test_hub_method_public_name_drift_fails(self) -> None:
        temp, root, document = self.make_fixture()
        self.addCleanup(temp.cleanup)
        hub = root / "src/Coglatas.Web/Realtime/AppHub.cs"
        source = hub.read_text(encoding="utf-8")
        signature = self.first_signalr_signature()
        name = signature["name"]
        assert isinstance(name, str)
        hub.write_text(
            source.replace(
                f"    public {signature['returnType']} {name}(",
                f'    [HubMethodName("{name}V2")]\n    public {signature["returnType"]} {name}(',
                1,
            ),
            encoding="utf-8",
        )
        with self.assertRaisesRegex(verifier.BoundaryViolation, "callable public method is missing"):
            verifier.verify_boundary(document, self.policy, root)

    def test_static_hub_method_fails(self) -> None:
        temp, root, document = self.make_fixture()
        self.addCleanup(temp.cleanup)
        hub = root / "src/Coglatas.Web/Realtime/AppHub.cs"
        source = hub.read_text(encoding="utf-8")
        signature = self.first_signalr_signature()
        name = signature["name"]
        return_type = signature["returnType"]
        assert isinstance(name, str)
        assert isinstance(return_type, str)
        hub.write_text(
            source.replace(
                f"    public {return_type} {name}(",
                f"    public static {return_type} {name}(",
                1,
            ),
            encoding="utf-8",
        )
        with self.assertRaisesRegex(verifier.BoundaryViolation, "callable public method is missing"):
            verifier.verify_boundary(document, self.policy, root)

    def test_public_name_overload_fails(self) -> None:
        temp, root, document = self.make_fixture()
        self.addCleanup(temp.cleanup)
        hub = root / "src/Coglatas.Web/Realtime/AppHub.cs"
        source = hub.read_text(encoding="utf-8")
        signature = self.first_signalr_signature()
        name = signature["name"]
        return_type = signature["returnType"]
        assert isinstance(name, str)
        assert isinstance(return_type, str)
        overload = (
            f"    public {return_type} {name}(Guid duplicate) => "
            'Task.FromResult(new HubSubscriptionResult(true, "ok"));\n'
        )
        body, closing = source.rsplit("}\n", 1)
        hub.write_text(body + overload + "}\n" + closing, encoding="utf-8")
        with self.assertRaisesRegex(verifier.BoundaryViolation, "must be unique"):
            verifier.verify_boundary(document, self.policy, root)


if __name__ == "__main__":
    unittest.main()
