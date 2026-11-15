#!/usr/bin/env python3
"""Admit only the reviewed file scope for a same-repository main governance PR."""

from __future__ import annotations

import argparse
import subprocess
import sys


BRANCH = "maintenance/main-governance"
ALLOWED_PATHS = frozenset(
    {
        ".github/repository-governance.json",
        ".github/scripts/audit_repository_governance.py",
        ".github/scripts/check_main_governance_scope.py",
        ".github/scripts/test_audit_repository_governance.py",
        ".github/scripts/test_main_governance_scope.py",
        ".github/scripts/test_release_package_source.py",
        ".github/workflows/ci.yml",
        ".github/workflows/flutter-launcher-builds.yml",
        ".github/workflows/pr-policy.yml",
        ".github/workflows/release-dry-run.yml",
        ".github/workflows/release-package-build.yml",
        "docs/RepositoryGovernance.md",
    }
)


def failures(
    base_ref: str,
    head_ref: str,
    base_repository: str,
    head_repository: str,
    changed_paths: list[str],
) -> list[str]:
    errors: list[str] = []
    if base_ref != "main" or head_ref != BRANCH:
        errors.append("Governance maintenance requires its exact branch and main base.")
    if head_repository.casefold() != base_repository.casefold():
        errors.append("Governance maintenance requires a same-repository pull request.")
    if not changed_paths:
        errors.append("Governance maintenance requires at least one changed file.")
    for path in changed_paths:
        if path not in ALLOWED_PATHS:
            errors.append(f"Out-of-scope governance file: {path}")
    return errors


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--base-ref", required=True)
    parser.add_argument("--head-ref", required=True)
    parser.add_argument("--base-repository", required=True)
    parser.add_argument("--head-repository", required=True)
    parser.add_argument("--base-sha", required=True)
    args = parser.parse_args()
    result = subprocess.run(
        ["git", "diff", "--name-only", "-z", "--no-renames", args.base_sha, "HEAD"],
        check=True,
        stdout=subprocess.PIPE,
    )
    changed = [
        path.decode("utf-8", errors="surrogateescape")
        for path in result.stdout.split(b"\0")
        if path
    ]
    errors = failures(
        args.base_ref,
        args.head_ref,
        args.base_repository,
        args.head_repository,
        changed,
    )
    for error in errors:
        print(f"::error::{error}", file=sys.stderr)
    return 1 if errors else 0


if __name__ == "__main__":
    raise SystemExit(main())
