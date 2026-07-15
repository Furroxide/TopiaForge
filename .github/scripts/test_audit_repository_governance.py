#!/usr/bin/env python3
"""Tests for the read-only repository governance audit."""

from __future__ import annotations

import copy
import importlib.util
import json
import unittest
from pathlib import Path
from typing import Any


SCRIPT = Path(__file__).with_name("audit_repository_governance.py")
POLICY_PATH = SCRIPT.parents[1] / "repository-governance.json"
SPEC = importlib.util.spec_from_file_location("audit_repository_governance", SCRIPT)
assert SPEC and SPEC.loader
AUDIT = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(AUDIT)


def load_policy() -> dict[str, Any]:
    return json.loads(POLICY_PATH.read_text(encoding="utf-8"))


def ruleset_fixture(expected: dict[str, Any], integration_id: int) -> dict[str, Any]:
    rules: list[dict[str, Any]] = []
    for rule_type in expected["rule_types"]:
        rule: dict[str, Any] = {"type": rule_type}
        if rule_type == "pull_request":
            rule["parameters"] = copy.deepcopy(expected["pull_request"])
            rule["parameters"]["required_reviewers"] = []
        elif rule_type == "required_status_checks":
            status_policy = expected["required_status_checks"]
            rule["parameters"] = {
                "strict_required_status_checks_policy": status_policy[
                    "strict_required_status_checks_policy"
                ],
                "do_not_enforce_on_create": status_policy["do_not_enforce_on_create"],
                "required_status_checks": [
                    {"context": context, "integration_id": integration_id}
                    for context in status_policy["contexts"]
                ],
            }
        elif rule_type == "code_scanning":
            rule["parameters"] = {
                "code_scanning_tools": [copy.deepcopy(expected["code_scanning"])]
            }
        rules.append(rule)
    return {
        "name": expected["name"],
        "target": expected["target"],
        "enforcement": expected["enforcement"],
        "bypass_actors": copy.deepcopy(expected["bypass_actors"]),
        "conditions": {
            "ref_name": {"include": copy.deepcopy(expected["ref_includes"]), "exclude": []}
        },
        "rules": rules,
    }


def environment_fixture(expected: dict[str, Any]) -> dict[str, Any]:
    return {
        "name": expected["name"],
        "can_admins_bypass": expected["can_admins_bypass"],
        "deployment_branch_policy": {
            "protected_branches": expected["protected_branches"],
            "custom_branch_policies": expected["custom_branch_policies"],
        },
        "protection_rules": [
            {
                "type": "required_reviewers",
                "prevent_self_review": expected["prevent_self_review"],
                "reviewers": [
                    {"type": "User", "reviewer": {"id": reviewer_id}}
                    for reviewer_id in expected["reviewer_ids"]
                ],
            }
        ],
        "branch_policies": copy.deepcopy(expected["branch_policies"]),
    }


def compliant_snapshot(policy: dict[str, Any]) -> dict[str, Any]:
    repository = {
        "full_name": policy["repository_full_name"],
        **copy.deepcopy(policy["repository_settings"]),
        "security_and_analysis": {
            name: {"status": status}
            for name, status in policy["security_and_analysis"].items()
        },
    }
    return {
        "repository": repository,
        "rulesets": [
            ruleset_fixture(ruleset, policy["github_actions_integration_id"])
            for ruleset in policy["rulesets"]
        ],
        "actions": copy.deepcopy(policy["actions"]),
        "environments": {
            environment["name"]: environment_fixture(environment)
            for environment in policy["environments"]
        },
        "security": copy.deepcopy(policy["security"]),
        "immutable_releases": copy.deepcopy(policy["immutable_releases"]),
    }


class RepositoryGovernanceAuditTests(unittest.TestCase):
    def setUp(self) -> None:
        self.policy = load_policy()
        self.snapshot = compliant_snapshot(self.policy)

    def test_compliant_snapshot_passes(self) -> None:
        self.assertEqual([], AUDIT.evaluate_snapshot(self.snapshot, self.policy))

    def test_repository_snapshot_drops_temporary_clone_credentials(self) -> None:
        scrubbed = AUDIT.scrub_repository_data(
            {
                "full_name": "furroxide/TopiaForge",
                "default_branch": "main",
                "temp_clone_token": "must-not-be-snapshotted",
            }
        )

        self.assertEqual(
            {"full_name": "furroxide/TopiaForge", "default_branch": "main"},
            scrubbed,
        )

    def test_always_bypass_and_stale_context_are_reported(self) -> None:
        main = next(
            ruleset for ruleset in self.snapshot["rulesets"] if ruleset["name"] == "protect-main"
        )
        main["bypass_actors"] = [
            {
                "actor_id": 5,
                "actor_type": "RepositoryRole",
                "bypass_mode": "always",
            }
        ]
        statuses = next(
            rule for rule in main["rules"] if rule["type"] == "required_status_checks"
        )
        statuses["parameters"]["required_status_checks"][0]["context"] = (
            "Template obsolete matrix leaf"
        )

        failures = AUDIT.evaluate_snapshot(self.snapshot, self.policy)

        self.assertTrue(any("protect-main.bypass_actors" in failure for failure in failures))
        self.assertTrue(
            any("protect-main.required_status_checks.contexts" in failure for failure in failures)
        )

    def test_wrong_environment_ref_and_actions_policy_are_reported(self) -> None:
        self.snapshot["actions"]["permissions"]["sha_pinning_required"] = False
        release = self.snapshot["environments"]["release"]
        release["branch_policies"] = [{"name": "main", "type": "branch"}]

        failures = AUDIT.evaluate_snapshot(self.snapshot, self.policy)

        self.assertTrue(any("sha_pinning_required" in failure for failure in failures))
        self.assertTrue(any("environment release.branch_policies" in failure for failure in failures))

    def test_obsolete_active_ruleset_is_reported(self) -> None:
        self.snapshot["rulesets"].append(
            {"name": "protected-release-flow", "enforcement": "active", "rules": []}
        )

        failures = AUDIT.evaluate_snapshot(self.snapshot, self.policy)

        self.assertTrue(any("obsolete ruleset is still active" in failure for failure in failures))

    def test_policy_has_exact_common_contexts_and_no_branch_flow_bypass(self) -> None:
        common = {
            "Required / PR policy",
            "Required / CI validation",
            "Required / Unity source validation",
            "Required / Registry validation",
            "Required / Dependency review",
        }
        branch_flow = {
            ruleset["name"]: ruleset
            for ruleset in self.policy["rulesets"]
            if ruleset["name"]
            in {"protect-main", "protect-dev", "protect-release-branches"}
        }

        self.assertEqual(
            {"protect-main", "protect-dev", "protect-release-branches"},
            set(branch_flow),
        )
        for ruleset in branch_flow.values():
            self.assertEqual([], ruleset["bypass_actors"])
            self.assertTrue(common.issubset(ruleset["required_status_checks"]["contexts"]))
        self.assertIn(
            "Required / Release packages",
            branch_flow["protect-main"]["required_status_checks"]["contexts"],
        )


if __name__ == "__main__":
    unittest.main()
