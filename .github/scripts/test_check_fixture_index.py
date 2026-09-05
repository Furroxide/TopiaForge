#!/usr/bin/env python3
"""Tests for the gamemode-contract fixture index audit."""

from __future__ import annotations

import importlib.util
import json
import tempfile
import unittest
from pathlib import Path

AUDIT = Path(__file__).with_name("check_fixture_index.py")
AUDIT_SPEC = importlib.util.spec_from_file_location("fixture_index_audit", AUDIT)
assert AUDIT_SPEC is not None and AUDIT_SPEC.loader is not None
AUDIT_MODULE = importlib.util.module_from_spec(AUDIT_SPEC)
AUDIT_SPEC.loader.exec_module(AUDIT_MODULE)


def _case(**overrides) -> dict:
    case = {
        "id": "a-case",
        "channel": "serialization",
        "kind": "launch-intent-hostile",
        "summary": "A case that exists only to exercise the index audit.",
        "intent": {},
        "operations": {"csharp": "read-intent", "dart": "write-intent"},
        "expect": {
            "csharp": {"outcome": "reject", "errorCodes": ["worldLaunch.gamemodeId"]},
            "dart": {"outcome": "reject", "errorCodes": ["writer-cannot-emit"]},
        },
    }
    case.update(overrides)
    return case


class FixtureIndexAuditTests(unittest.TestCase):
    def setUp(self) -> None:
        self._temp = tempfile.TemporaryDirectory()
        self.addCleanup(self._temp.cleanup)
        root = Path(self._temp.name)
        fixtures = root / "tests" / "fixtures" / "gamemode-v6"
        (fixtures / "serialization" / "launch-intent").mkdir(parents=True)

        self._restore = (
            AUDIT_MODULE.ROOT,
            AUDIT_MODULE.FIXTURE_ROOT,
            AUDIT_MODULE.INDEX,
        )
        AUDIT_MODULE.ROOT = root
        AUDIT_MODULE.FIXTURE_ROOT = fixtures
        AUDIT_MODULE.INDEX = fixtures / "index.json"
        self.addCleanup(self._restore_module)

        self.fixtures = fixtures
        self.cases = fixtures / "serialization" / "launch-intent"

    def _restore_module(self) -> None:
        AUDIT_MODULE.ROOT, AUDIT_MODULE.FIXTURE_ROOT, AUDIT_MODULE.INDEX = self._restore

    def _write(self, name: str, case: dict) -> None:
        directory = self.fixtures / "serialization" / "manifest" if str(case.get("kind", "")).startswith("manifest-") else self.cases
        directory.mkdir(parents=True, exist_ok=True)
        (directory / name).write_text(json.dumps(case, indent=2), encoding="utf-8")

    def _expect_failure(self, fragment: str) -> None:
        with self.assertRaises(AUDIT_MODULE.FixtureIndexError) as raised:
            AUDIT_MODULE.build_index()
        self.assertIn(fragment, str(raised.exception))

    def test_write_then_check_round_trips(self) -> None:
        self._write("a-case.json", _case())
        self.assertEqual(AUDIT_MODULE.main(["--write"]), 0)
        self.assertEqual(AUDIT_MODULE.main([]), 0)
        index = json.loads(AUDIT_MODULE.INDEX.read_text(encoding="utf-8"))
        self.assertEqual(index["channelRunners"], {"serialization": ["csharp", "dart"]})
        self.assertEqual(
            index["cases"],
            [
                {
                    "id": "a-case",
                    "channel": "serialization",
                    "kind": "launch-intent-hostile",
                    "path": "serialization/launch-intent/a-case.json",
                }
            ],
        )

    def test_a_fixture_added_without_regenerating_fails_the_check(self) -> None:
        self._write("a-case.json", _case())
        self.assertEqual(AUDIT_MODULE.main(["--write"]), 0)
        self._write("b-case.json", _case(id="b-case"))
        self.assertEqual(AUDIT_MODULE.main([]), 1)

    def test_a_missing_index_fails_rather_than_being_created(self) -> None:
        self._write("a-case.json", _case())
        self.assertEqual(AUDIT_MODULE.main([]), 1)

    def test_an_empty_tree_is_not_a_pass(self) -> None:
        self._expect_failure("No fixtures were found")

    def test_id_must_equal_the_file_stem(self) -> None:
        self._write("named-one-thing.json", _case(id="named-another"))
        self._expect_failure("file stem")

    def test_channel_must_match_the_directory(self) -> None:
        self._write("a-case.json", _case(channel="resolution"))
        self._expect_failure("The directory decides the channel")

    def test_an_unknown_kind_is_rejected_rather_than_indexed(self) -> None:
        self._write("a-case.json", _case(kind="launch-intent-imaginary"))
        self._expect_failure("does not define")

    def test_every_obliged_runner_must_state_an_outcome(self) -> None:
        self._write(
            "a-case.json",
            _case(expect={"csharp": {"outcome": "accept"}}),
        )
        self._expect_failure("obliges")

    def test_an_unobliged_runner_cannot_be_added(self) -> None:
        expect = _case()["expect"]
        expect["rust"] = {"outcome": "accept"}
        self._write("a-case.json", _case(expect=expect))
        self._expect_failure("obliges")

    def test_ids_are_unique_across_the_tree(self) -> None:
        nested = self.cases / "nested"
        nested.mkdir()
        self._write("a-case.json", _case())
        (nested / "a-case.json").write_text(
            json.dumps(_case(), indent=2), encoding="utf-8"
        )
        self._expect_failure("reuses id")

    def test_unknown_channel_is_not_silently_ignored(self) -> None:
        unknown = self.fixtures / "serializaton"
        unknown.mkdir()
        (unknown / "a-case.json").write_text(json.dumps(_case()), encoding="utf-8")
        self._expect_failure("outside a known channel")

    def test_root_level_fixture_is_not_silently_ignored(self) -> None:
        (self.fixtures / "a-case.json").write_text(json.dumps(_case()), encoding="utf-8")
        self._expect_failure("outside a known channel")

    def test_nested_index_json_is_a_case_not_an_exclusion(self) -> None:
        self._write("index.json", {})
        self._expect_failure("missing a string")

    def test_manifest_requires_schema_outcome(self) -> None:
        self._write("a-case.json", _case(kind="manifest-rejects"))
        self._expect_failure("requires schemaOutcome")

    def test_manifest_cannot_have_different_codes(self) -> None:
        self._write("a-case.json", _case(kind="manifest-rejects", schemaOutcome="reject"))
        self._expect_failure("divergent same-operation")

    def test_manifest_acceptance_requires_normalization(self) -> None:
        self._write("a-case.json", _case(kind="manifest-accepts", schemaOutcome="accept",
            expect={"csharp": {"outcome": "accept"}, "dart": {"outcome": "accept"}}))
        self._expect_failure("requires a structured normalized")

    def test_non_json_file_is_not_silently_ignored(self) -> None:
        (self.cases / "notes.txt").write_text("unexecuted fixture", encoding="utf-8")
        self._expect_failure("unexpected non-JSON")

    def test_uppercase_extension_is_not_silently_ignored(self) -> None:
        self._write("a-case.JSON", _case())
        self._expect_failure("unexpected non-JSON")

    def test_case_cannot_be_in_another_kind_directory(self) -> None:
        directory = self.fixtures / "serialization" / "manifest"
        directory.mkdir()
        (directory / "a-case.json").write_text(json.dumps(_case()), encoding="utf-8")
        self._expect_failure("misplaced")

    def test_expectations_must_be_objects(self) -> None:
        self._write("a-case.json", _case(expect=[]))
        self._expect_failure("expectation object")

    def test_missing_runner_outcome_is_not_silently_indexed(self) -> None:
        self._write("a-case.json", _case(expect={"csharp": {}, "dart": {}}))
        self._expect_failure("accept/reject outcome")

    def test_reject_requires_nonempty_codes(self) -> None:
        self._write("a-case.json", _case(expect={"csharp": {"outcome": "reject"}, "dart": {"outcome": "reject"}}))
        self._expect_failure("nonempty errorCodes")

    def _resolution(self, **overrides) -> dict:
        reason = {"code": "worldNotDeclared", "subject": "base.mod.world", "subjectVersion": ""}
        expected = {"outcome": "reject", "blocks": [reason]}
        case = {"id": "a-case", "channel": "resolution", "kind": "launch-resolution",
            "summary": "A pure resolution closure test", "profile": {"packages": []}, "request": {},
            "expect": {"csharp": expected, "dart": json.loads(json.dumps(expected))}}
        case.update(overrides)
        path = self.fixtures / "resolution"
        path.mkdir(exist_ok=True)
        (path / "a-case.json").write_text(json.dumps(case), encoding="utf-8")
        return case

    def test_resolution_requires_full_reason_tuples(self) -> None:
        self._resolution(expect={runner: {"outcome": "reject", "blocks": ["worldNotDeclared"]} for runner in ["csharp", "dart"]})
        self._expect_failure("closed string block tuples")

    def test_resolution_reasons_are_ordered_and_unique(self) -> None:
        reason = {"code": "worldNotDeclared", "subject": "base.mod.world", "subjectVersion": ""}
        self._resolution(expect={runner: {"outcome": "reject", "blocks": [reason, reason]} for runner in ["csharp", "dart"]})
        self._expect_failure("ordinal sorted unique blocks")

    def test_resolution_acceptance_requires_normalization(self) -> None:
        self._resolution(expect={runner: {"outcome": "accept"} for runner in ["csharp", "dart"]})
        self._expect_failure("structured normalized")

    def test_resolution_requires_package_manifest_expectations(self) -> None:
        self._resolution(profile={"packages": [{"id": "base.mod", "version": "1.0.0", "manifest": {}}]})
        self._expect_failure("schemaOutcome and validation")

    def test_resolution_rejects_nonobject_package_validation(self) -> None:
        self._resolution(profile={"packages": [{"schemaOutcome": "accept", "validation": []}]})
        self._expect_failure("schemaOutcome and validation")

    def test_resolution_rejects_missing_semantic_codes(self) -> None:
        self._resolution(profile={"packages": [{"schemaOutcome": "accept", "validation": {"outcome": "reject"}}]})
        self._expect_failure("exact sorted manifest validation codes")

    def test_resolution_cannot_accept_invalid_manifest_inputs(self) -> None:
        self._resolution(profile={"packages": [{"schemaOutcome": "reject", "validation": {"outcome": "accept"}}]},
            expect={runner: {"outcome": "accept", "normalized": {}} for runner in ["csharp", "dart"]})
        self._expect_failure("invalid input manifests")

    def test_resolution_cannot_exempt_same_operation_parity(self) -> None:
        self._resolution(divergenceReason="Intentionally different language rules")
        self._expect_failure("same-operation parity")

    def test_malformed_json_names_the_file(self) -> None:
        (self.cases / "a-case.json").write_text("{not json", encoding="utf-8")
        self._expect_failure("a-case.json is not valid JSON")


if __name__ == "__main__":
    unittest.main()
