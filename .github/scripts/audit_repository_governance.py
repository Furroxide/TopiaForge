#!/usr/bin/env python3
"""Capture and verify TopiaForge's GitHub governance without mutating it."""

from __future__ import annotations

import argparse
import json
import re
import subprocess
import sys
from datetime import datetime, timezone
from pathlib import Path
from typing import Any
from urllib.parse import quote


ROOT = Path(__file__).resolve().parents[2]
DEFAULT_POLICY = ROOT / ".github" / "repository-governance.json"
API_VERSION = "2026-03-10"
REPOSITORY_SNAPSHOT_FIELDS = {
    "allow_auto_merge",
    "allow_merge_commit",
    "allow_rebase_merge",
    "allow_squash_merge",
    "allow_update_branch",
    "default_branch",
    "delete_branch_on_merge",
    "full_name",
    "has_issues",
    "has_projects",
    "has_wiki",
    "merge_commit_message",
    "merge_commit_title",
    "security_and_analysis",
    "squash_merge_commit_message",
    "squash_merge_commit_title",
    "web_commit_signoff_required",
}


class AuditError(RuntimeError):
    """The read-only audit could not collect or parse required evidence."""


class GitHubClient:
    """Minimal read-only wrapper around the authenticated GitHub CLI."""

    def _request(self, path: str) -> subprocess.CompletedProcess[str]:
        try:
            return subprocess.run(
                [
                    "gh",
                    "api",
                    path,
                    "-H",
                    "Accept: application/vnd.github+json",
                    "-H",
                    f"X-GitHub-Api-Version: {API_VERSION}",
                ],
                check=False,
                stdout=subprocess.PIPE,
                stderr=subprocess.PIPE,
                text=True,
            )
        except FileNotFoundError as error:
            raise AuditError("The governance audit requires the `gh` CLI.") from error

    @staticmethod
    def _status(result: subprocess.CompletedProcess[str]) -> int | None:
        match = re.search(r"\(HTTP ([0-9]{3})\)", result.stderr)
        return int(match.group(1)) if match else None

    def get_json(
        self, path: str, *, tolerate_statuses: set[int] | None = None
    ) -> Any:
        result = self._request(path)
        tolerated = tolerate_statuses or set()
        status = self._status(result)
        if result.returncode != 0:
            if status in tolerated:
                return None
            detail = result.stderr.strip() or f"exit {result.returncode}"
            raise AuditError(f"GitHub GET {path} failed: {detail}")
        if not result.stdout.strip():
            return {}
        try:
            return json.loads(result.stdout)
        except json.JSONDecodeError as error:
            raise AuditError(f"GitHub GET {path} returned invalid JSON: {error}") from error

    def enabled_probe(self, path: str) -> bool:
        result = self._request(path)
        if result.returncode == 0:
            return True
        if self._status(result) == 404:
            return False
        detail = result.stderr.strip() or f"exit {result.returncode}"
        raise AuditError(f"GitHub GET {path} failed: {detail}")


def read_json(path: Path) -> dict[str, Any]:
    try:
        value = json.loads(path.read_text(encoding="utf-8"))
    except OSError as error:
        raise AuditError(f"Cannot read {path}: {error}") from error
    except json.JSONDecodeError as error:
        raise AuditError(f"Invalid JSON in {path}: {error}") from error
    if not isinstance(value, dict):
        raise AuditError(f"Expected a JSON object in {path}.")
    return value


def validate_repository_name(repository: str) -> None:
    if not re.fullmatch(r"[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+", repository):
        raise AuditError(f"Invalid GitHub repository name: {repository!r}")


def scrub_repository_data(repository: dict[str, Any]) -> dict[str, Any]:
    """Retain only governance fields; never persist temporary clone credentials."""

    return {
        key: value
        for key, value in repository.items()
        if key in REPOSITORY_SNAPSHOT_FIELDS
    }


def collect_snapshot(repository: str, client: GitHubClient | None = None) -> dict[str, Any]:
    """Read every API surface used by the desired-state assertions."""

    validate_repository_name(repository)
    github = client or GitHubClient()
    base = f"/repos/{repository}"

    repository_data = scrub_repository_data(github.get_json(base))
    ruleset_summaries = github.get_json(
        f"{base}/rulesets?includes_parents=false&per_page=100"
    )
    rulesets = [
        github.get_json(f"{base}/rulesets/{summary['id']}")
        for summary in ruleset_summaries
    ]

    action_permissions = github.get_json(f"{base}/actions/permissions")
    selected_actions = None
    if action_permissions.get("allowed_actions") == "selected":
        selected_actions = github.get_json(
            f"{base}/actions/permissions/selected-actions"
        )

    environment_listing = github.get_json(f"{base}/environments?per_page=100")
    environments: dict[str, Any] = {}
    for summary in environment_listing.get("environments", []):
        name = summary["name"]
        encoded_name = quote(name, safe="")
        environment = github.get_json(f"{base}/environments/{encoded_name}")
        deployment_policy = environment.get("deployment_branch_policy") or {}
        branch_policies: list[dict[str, Any]] = []
        if deployment_policy.get("custom_branch_policies"):
            policies = github.get_json(
                f"{base}/environments/{encoded_name}/deployment-branch-policies?per_page=100"
            )
            branch_policies = policies.get("branch_policies", [])
        environment["branch_policies"] = branch_policies
        environments[name] = environment

    automated_security_fixes = github.get_json(f"{base}/automated-security-fixes")

    return {
        "snapshot_schema_version": 1,
        "captured_at": datetime.now(timezone.utc).isoformat(),
        "api_version": API_VERSION,
        "repository": repository_data,
        "rulesets": rulesets,
        "actions": {
            "permissions": action_permissions,
            "selected_actions": selected_actions,
            "workflow_permissions": github.get_json(
                f"{base}/actions/permissions/workflow"
            ),
            "fork_pr_contributor_approval": github.get_json(
                f"{base}/actions/permissions/fork-pr-contributor-approval"
            ),
        },
        "environments": environments,
        "security": {
            "dependabot_alerts": github.enabled_probe(f"{base}/vulnerability-alerts"),
            "automated_security_updates": bool(
                automated_security_fixes.get("enabled")
            ),
            "private_vulnerability_reporting": bool(
                github.get_json(f"{base}/private-vulnerability-reporting").get(
                    "enabled"
                )
            ),
            "codeql_default_setup": github.get_json(
                f"{base}/code-scanning/default-setup", tolerate_statuses={404}
            )
            or {"state": "not-configured"},
        },
        "immutable_releases": github.get_json(f"{base}/immutable-releases"),
    }


def add_mismatch(
    failures: list[str], label: str, actual: Any, expected: Any
) -> None:
    if actual != expected:
        failures.append(f"{label}: expected {expected!r}, found {actual!r}")


def compare_mapping(
    failures: list[str], label: str, actual: Any, expected: dict[str, Any]
) -> None:
    if not isinstance(actual, dict):
        failures.append(f"{label}: expected an object, found {type(actual).__name__}")
        return
    for key, expected_value in expected.items():
        actual_value = actual.get(key)
        child_label = f"{label}.{key}"
        if isinstance(expected_value, dict):
            compare_mapping(failures, child_label, actual_value, expected_value)
        elif isinstance(expected_value, list):
            if not isinstance(actual_value, list):
                failures.append(
                    f"{child_label}: expected a list, found {type(actual_value).__name__}"
                )
            else:
                add_mismatch(
                    failures,
                    child_label,
                    sorted(actual_value, key=repr),
                    sorted(expected_value, key=repr),
                )
        else:
            add_mismatch(failures, child_label, actual_value, expected_value)


def rule_by_type(ruleset: dict[str, Any], rule_type: str) -> dict[str, Any] | None:
    matches = [rule for rule in ruleset.get("rules", []) if rule.get("type") == rule_type]
    if len(matches) != 1:
        return None
    return matches[0]


def normalized_bypass_actors(ruleset: dict[str, Any]) -> list[dict[str, Any]]:
    keys = ("actor_id", "actor_type", "bypass_mode")
    return sorted(
        [{key: actor.get(key) for key in keys} for actor in ruleset.get("bypass_actors", [])],
        key=lambda actor: (str(actor["actor_type"]), str(actor["actor_id"])),
    )


def check_rulesets(
    failures: list[str], snapshot: dict[str, Any], policy: dict[str, Any]
) -> None:
    actual_rulesets = snapshot.get("rulesets")
    if not isinstance(actual_rulesets, list):
        failures.append("rulesets: missing or not a list")
        return

    by_name = {ruleset.get("name"): ruleset for ruleset in actual_rulesets}
    for forbidden_name in policy.get("forbidden_rulesets", []):
        forbidden = by_name.get(forbidden_name)
        if forbidden and forbidden.get("enforcement") == "active":
            failures.append(f"ruleset {forbidden_name}: obsolete ruleset is still active")

    integration_id = policy["github_actions_integration_id"]
    for expected in policy.get("rulesets", []):
        name = expected["name"]
        label = f"ruleset {name}"
        actual = by_name.get(name)
        if not actual:
            failures.append(f"{label}: missing")
            continue

        add_mismatch(failures, f"{label}.target", actual.get("target"), expected["target"])
        add_mismatch(
            failures,
            f"{label}.enforcement",
            actual.get("enforcement"),
            expected["enforcement"],
        )
        actual_includes = (
            actual.get("conditions", {}).get("ref_name", {}).get("include", [])
        )
        add_mismatch(
            failures,
            f"{label}.ref_includes",
            sorted(actual_includes),
            sorted(expected["ref_includes"]),
        )
        actual_excludes = (
            actual.get("conditions", {}).get("ref_name", {}).get("exclude", [])
        )
        add_mismatch(failures, f"{label}.ref_excludes", actual_excludes, [])
        add_mismatch(
            failures,
            f"{label}.bypass_actors",
            normalized_bypass_actors(actual),
            sorted(
                expected.get("bypass_actors", []),
                key=lambda actor: (str(actor["actor_type"]), str(actor["actor_id"])),
            ),
        )

        actual_rule_types = sorted(rule.get("type") for rule in actual.get("rules", []))
        expected_rule_types = sorted(expected["rule_types"])
        add_mismatch(
            failures, f"{label}.rule_types", actual_rule_types, expected_rule_types
        )

        if "pull_request" in expected:
            rule = rule_by_type(actual, "pull_request")
            if not rule:
                failures.append(f"{label}.pull_request: missing or duplicated")
            else:
                parameters = rule.get("parameters", {})
                compare_mapping(
                    failures,
                    f"{label}.pull_request",
                    parameters,
                    expected["pull_request"],
                )

        if "required_status_checks" in expected:
            rule = rule_by_type(actual, "required_status_checks")
            if not rule:
                failures.append(f"{label}.required_status_checks: missing or duplicated")
            else:
                parameters = rule.get("parameters", {})
                status_policy = expected["required_status_checks"]
                for key in (
                    "strict_required_status_checks_policy",
                    "do_not_enforce_on_create",
                ):
                    add_mismatch(
                        failures,
                        f"{label}.required_status_checks.{key}",
                        parameters.get(key),
                        status_policy[key],
                    )
                actual_checks = sorted(
                    (
                        check.get("context"),
                        check.get("integration_id"),
                    )
                    for check in parameters.get("required_status_checks", [])
                )
                expected_checks = sorted(
                    (context, integration_id) for context in status_policy["contexts"]
                )
                add_mismatch(
                    failures,
                    f"{label}.required_status_checks.contexts",
                    actual_checks,
                    expected_checks,
                )

        if "code_scanning" in expected:
            rule = rule_by_type(actual, "code_scanning")
            if not rule:
                failures.append(f"{label}.code_scanning: missing or duplicated")
            else:
                tools = rule.get("parameters", {}).get("code_scanning_tools", [])
                add_mismatch(
                    failures,
                    f"{label}.code_scanning.tools",
                    tools,
                    [expected["code_scanning"]],
                )


def reviewer_rule(environment: dict[str, Any]) -> dict[str, Any] | None:
    rules = [
        rule
        for rule in environment.get("protection_rules", [])
        if rule.get("type") == "required_reviewers"
    ]
    return rules[0] if len(rules) == 1 else None


def check_environments(
    failures: list[str], snapshot: dict[str, Any], policy: dict[str, Any]
) -> None:
    actual_environments = snapshot.get("environments")
    if not isinstance(actual_environments, dict):
        failures.append("environments: missing or not an object")
        return

    for expected in policy.get("environments", []):
        name = expected["name"]
        label = f"environment {name}"
        actual = actual_environments.get(name)
        if not actual:
            failures.append(f"{label}: missing")
            continue

        add_mismatch(
            failures,
            f"{label}.can_admins_bypass",
            actual.get("can_admins_bypass"),
            expected["can_admins_bypass"],
        )
        deployment_policy = actual.get("deployment_branch_policy") or {}
        add_mismatch(
            failures,
            f"{label}.protected_branches",
            deployment_policy.get("protected_branches"),
            expected["protected_branches"],
        )
        add_mismatch(
            failures,
            f"{label}.custom_branch_policies",
            deployment_policy.get("custom_branch_policies"),
            expected["custom_branch_policies"],
        )

        required_reviewers = reviewer_rule(actual)
        if not required_reviewers:
            failures.append(f"{label}.required_reviewers: missing or duplicated")
        else:
            actual_reviewer_ids = sorted(
                reviewer.get("reviewer", {}).get("id")
                for reviewer in required_reviewers.get("reviewers", [])
            )
            add_mismatch(
                failures,
                f"{label}.reviewer_ids",
                actual_reviewer_ids,
                sorted(expected["reviewer_ids"]),
            )
            add_mismatch(
                failures,
                f"{label}.prevent_self_review",
                required_reviewers.get("prevent_self_review"),
                expected["prevent_self_review"],
            )

        actual_policies = sorted(
            (branch_policy.get("name"), branch_policy.get("type"))
            for branch_policy in actual.get("branch_policies", [])
        )
        expected_policies = sorted(
            (branch_policy["name"], branch_policy["type"])
            for branch_policy in expected["branch_policies"]
        )
        add_mismatch(
            failures,
            f"{label}.branch_policies",
            actual_policies,
            expected_policies,
        )


def evaluate_snapshot(snapshot: dict[str, Any], policy: dict[str, Any]) -> list[str]:
    """Return every desired-state mismatch in a collected or offline snapshot."""

    failures: list[str] = []
    repository = snapshot.get("repository")
    if not isinstance(repository, dict):
        failures.append("repository: missing or not an object")
        repository = {}

    add_mismatch(
        failures,
        "repository.full_name",
        repository.get("full_name"),
        policy["repository_full_name"],
    )
    compare_mapping(
        failures,
        "repository",
        repository,
        policy.get("repository_settings", {}),
    )
    actual_security_and_analysis = repository.get("security_and_analysis") or {}
    expected_security_and_analysis = {
        key: {"status": status}
        for key, status in policy.get("security_and_analysis", {}).items()
    }
    compare_mapping(
        failures,
        "repository.security_and_analysis",
        actual_security_and_analysis,
        expected_security_and_analysis,
    )

    compare_mapping(
        failures,
        "actions",
        snapshot.get("actions"),
        policy.get("actions", {}),
    )
    compare_mapping(
        failures,
        "security",
        snapshot.get("security"),
        policy.get("security", {}),
    )
    compare_mapping(
        failures,
        "immutable_releases",
        snapshot.get("immutable_releases"),
        policy.get("immutable_releases", {}),
    )
    check_rulesets(failures, snapshot, policy)
    check_environments(failures, snapshot, policy)
    return failures


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        description="Read and verify TopiaForge GitHub repository governance."
    )
    parser.add_argument(
        "--policy",
        type=Path,
        default=DEFAULT_POLICY,
        help="desired-state JSON (default: .github/repository-governance.json)",
    )
    subparsers = parser.add_subparsers(dest="command", required=True)
    snapshot_parser = subparsers.add_parser(
        "snapshot", help="print a read-only live governance snapshot as JSON"
    )
    snapshot_parser.add_argument(
        "--repo", help="owner/repository; defaults to the policy repository"
    )
    check_parser = subparsers.add_parser(
        "check", help="verify live governance or an offline snapshot"
    )
    check_parser.add_argument(
        "--repo", help="owner/repository; defaults to the policy repository"
    )
    check_parser.add_argument(
        "--snapshot",
        type=Path,
        help="check a previously captured snapshot instead of calling GitHub",
    )
    return parser


def main() -> int:
    args = build_parser().parse_args()
    try:
        policy = read_json(args.policy)
        repository = args.repo or policy["repository_full_name"]
        if args.command == "snapshot":
            snapshot = collect_snapshot(repository)
            print(json.dumps(snapshot, indent=2, sort_keys=True))
            return 0

        snapshot = (
            read_json(args.snapshot)
            if args.snapshot is not None
            else collect_snapshot(repository)
        )
        failures = evaluate_snapshot(snapshot, policy)
    except (AuditError, KeyError, TypeError) as error:
        print(f"Governance audit error: {error}", file=sys.stderr)
        return 2

    if failures:
        print("Repository governance does not match desired state:", file=sys.stderr)
        for failure in failures:
            print(f"- {failure}", file=sys.stderr)
        return 1

    print(f"Repository governance matches desired state for {repository}.")
    for manual_control in policy.get("manual_controls", []):
        print(f"MANUAL: {manual_control}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
