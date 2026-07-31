#!/usr/bin/env python3
"""Tests for the read-only repository governance audit."""

from __future__ import annotations

import copy
import importlib.util
import json
import subprocess
import unittest
from pathlib import Path
from typing import Any
from unittest import mock


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
        "repository_administrators": copy.deepcopy(
            policy["repository_administrators"]
        ),
        "repository_collaborators": [
            {
                "login": policy["release_staging_principal"]["login"],
                "id": policy["release_staging_principal"]["actor_id"],
                "type": policy["release_staging_principal"]["type"],
                "role_name": "admin",
                "permissions": {
                    "pull": True,
                    "triage": True,
                    "push": True,
                    "maintain": True,
                    "admin": True,
                },
            }
        ],
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

    def test_collaborator_snapshot_keeps_only_identity_and_permissions(self) -> None:
        scrubbed = AUDIT.scrub_collaborator_data(
            {
                "login": "reader",
                "id": 123,
                "type": "User",
                "role_name": "read",
                "email": "must-not-be-snapshotted@example.invalid",
                "permissions": {
                    "pull": True,
                    "triage": False,
                    "push": False,
                    "maintain": False,
                    "admin": False,
                    "custom_secret": True,
                },
            }
        )

        self.assertNotIn("email", scrubbed)
        self.assertNotIn("custom_secret", scrubbed["permissions"])

    def test_json_enabled_probe_supports_json_and_legacy_404(self) -> None:
        client = AUDIT.GitHubClient()
        responses = iter(
            [
                subprocess.CompletedProcess([], 0, '{"enabled": false}', ""),
                subprocess.CompletedProcess([], 1, "", "gh: Not Found (HTTP 404)"),
                subprocess.CompletedProcess([], 0, '{"enabled": true}', ""),
            ]
        )
        client._request = lambda _path: next(responses)

        self.assertFalse(client.json_enabled_probe("/json-disabled"))
        self.assertFalse(client.json_enabled_probe("/legacy-disabled"))
        self.assertTrue(client.json_enabled_probe("/json-enabled"))

    def test_json_enabled_probe_rejects_malformed_success(self) -> None:
        client = AUDIT.GitHubClient()
        client._request = lambda _path: subprocess.CompletedProcess(
            [], 0, '{"paused": false}', ""
        )

        with self.assertRaises(AUDIT.AuditError):
            client.json_enabled_probe("/malformed")

    def test_github_client_honors_configured_cli_executable(self) -> None:
        configured_cli = r"C:\Program Files\GitHub CLI\gh.exe"
        completed = subprocess.CompletedProcess([], 0, "{}", "")

        with mock.patch.dict(
            AUDIT.os.environ,
            {"TOPIAFORGE_GH_CLI": configured_cli},
            clear=False,
        ), mock.patch.object(
            AUDIT.subprocess,
            "run",
            return_value=completed,
        ) as run:
            result = AUDIT.GitHubClient()._request("/repos/furroxide/TopiaForge")

        self.assertIs(completed, result)
        invocation = run.call_args.args[0]
        self.assertEqual(configured_cli, invocation[0])
        self.assertEqual(
            ["api", "/repos/furroxide/TopiaForge"],
            invocation[1:3],
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

    def test_only_active_privileged_environments_are_desired(self) -> None:
        environment_names = {
            environment["name"] for environment in self.policy["environments"]
        }

        self.assertEqual({"release", "github-pages"}, environment_names)
        self.assertNotIn(
            "game-ci/unity-builder@*",
            self.policy["actions"]["selected_actions"]["patterns_allowed"],
        )
        self.assertNotIn(
            "subosito/flutter-action@*",
            self.policy["actions"]["selected_actions"]["patterns_allowed"],
        )

    def test_rc1_codeql_languages_match_the_shipped_platform_scope(self) -> None:
        self.assertEqual(
            [
                "actions",
                "c-cpp",
                "csharp",
                "javascript-typescript",
            ],
            self.policy["security"]["codeql_default_setup"]["languages"],
        )

    def test_obsolete_active_ruleset_is_reported(self) -> None:
        self.snapshot["rulesets"].append(
            {"name": "protected-release-flow", "enforcement": "active", "rules": []}
        )

        failures = AUDIT.evaluate_snapshot(self.snapshot, self.policy)

        self.assertTrue(any("obsolete ruleset is still active" in failure for failure in failures))

    def test_unexpected_repository_administrator_is_reported(self) -> None:
        self.snapshot["repository_administrators"].append("unexpected-admin")

        failures = AUDIT.evaluate_snapshot(self.snapshot, self.policy)

        self.assertTrue(
            any("repository_administrators" in failure for failure in failures)
        )

    def test_write_or_maintain_collaborator_is_reported(self) -> None:
        self.snapshot["repository_collaborators"].append(
            {
                "login": "unexpected-writer",
                "id": 999,
                "type": "User",
                "role_name": "maintain",
                "permissions": {
                    "pull": True,
                    "triage": True,
                    "push": True,
                    "maintain": True,
                    "admin": False,
                },
            }
        )

        failures = AUDIT.evaluate_snapshot(self.snapshot, self.policy)

        self.assertTrue(
            any("write-capable collaborator" in failure for failure in failures)
        )

    def test_read_and_triage_collaborators_are_allowed(self) -> None:
        for role_name, triage in (("read", False), ("triage", True)):
            self.snapshot["repository_collaborators"].append(
                {
                    "login": f"{role_name}-only",
                    "id": 1000 + len(self.snapshot["repository_collaborators"]),
                    "type": "User",
                    "role_name": role_name,
                    "permissions": {
                        "pull": True,
                        "triage": triage,
                        "push": False,
                        "maintain": False,
                        "admin": False,
                    },
                }
            )

        self.assertEqual([], AUDIT.evaluate_snapshot(self.snapshot, self.policy))

    def test_unknown_collaborator_role_fails_closed(self) -> None:
        self.snapshot["repository_collaborators"].append(
            {
                "login": "custom-role-user",
                "id": 2000,
                "type": "User",
                "role_name": "custom-read-maybe",
                "permissions": {
                    "pull": True,
                    "triage": False,
                    "push": False,
                    "maintain": False,
                    "admin": False,
                },
            }
        )

        failures = AUDIT.evaluate_snapshot(self.snapshot, self.policy)

        self.assertTrue(
            any("unrecognized role" in failure for failure in failures)
        )

    def test_staging_principal_actor_id_is_immutable(self) -> None:
        self.snapshot["repository_collaborators"][0]["id"] = 999

        failures = AUDIT.evaluate_snapshot(self.snapshot, self.policy)

        self.assertTrue(
            any("release-staging principal.identity" in failure for failure in failures)
        )

    def test_policy_cannot_repin_release_mutation_principals(self) -> None:
        self.policy["release_staging_principal"]["actor_id"] = 999
        self.policy["release_workflow_principal"]["login"] = "other-bot"

        failures = AUDIT.evaluate_snapshot(self.snapshot, self.policy)

        self.assertTrue(
            any("policy.release_staging_principal" in failure for failure in failures)
        )
        self.assertTrue(
            any("policy.release_workflow_principal" in failure for failure in failures)
        )

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
