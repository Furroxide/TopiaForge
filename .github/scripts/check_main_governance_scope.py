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

# One-time sidecar security backport. Both tracked files must move from the
# exact legacy blobs to the reviewed blobs in the same pull request.
SIDECAR_BACKPORT = {
    "tools/ugc-automerge-sidecar/package.json": (
        "963db162dcb0fc6db3039d327c2ba14666098fe5",
        "890532dc2d21cef02ae9c66af90299be7a08f436",
    ),
    "tools/ugc-automerge-sidecar/package-lock.json": (
        "2fd5daa4edfe104eeeaf22c692ed8beb09908c51",
        "45daa6ad87887d89be29a801b4a3115d2bf6ead4",
    ),
}


def failures(
    base_ref: str,
    head_ref: str,
    base_repository: str,
    head_repository: str,
    changed_paths: list[str],
    sidecar_blobs: dict[str, tuple[str, str]] | None = None,
) -> list[str]:
    errors: list[str] = []
    if base_ref != "main" or head_ref != BRANCH:
        errors.append("Governance maintenance requires its exact branch and main base.")
    if head_repository.casefold() != base_repository.casefold():
        errors.append("Governance maintenance requires a same-repository pull request.")
    if not changed_paths:
        errors.append("Governance maintenance requires at least one changed file.")
    for path in changed_paths:
        if path not in ALLOWED_PATHS and path not in SIDECAR_BACKPORT:
            errors.append(f"Out-of-scope governance file: {path}")
    sidecar_paths = SIDECAR_BACKPORT.keys() & set(changed_paths)
    if sidecar_paths and (sidecar_paths != SIDECAR_BACKPORT.keys() or sidecar_blobs != SIDECAR_BACKPORT):
        errors.append("The sidecar backport requires both exact reviewed dependency changes.")
    return errors


def blob_at(ref: str, path: str) -> str:
    result = subprocess.run(
        ["git", "ls-tree", ref, "--", path],
        capture_output=True,
        text=True,
        check=False,
    )
    metadata, _, entry_path = result.stdout.rstrip("\n").partition("\t")
    prefix = "100644 blob "
    if result.returncode == 0 and entry_path == path and metadata.startswith(prefix):
        return metadata.removeprefix(prefix)
    return ""


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
    sidecar_blobs = {
        path: (blob_at(args.base_sha, path), blob_at("HEAD", path))
        for path in SIDECAR_BACKPORT.keys() & set(changed)
    }
    errors = failures(
        args.base_ref,
        args.head_ref,
        args.base_repository,
        args.head_repository,
        changed,
        sidecar_blobs,
    )
    for error in errors:
        print(f"::error::{error}", file=sys.stderr)
    return 1 if errors else 0


if __name__ == "__main__":
    raise SystemExit(main())
