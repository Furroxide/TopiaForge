"""Tests for the narrowly scoped main governance pull-request route."""

import importlib.util
from pathlib import Path
import subprocess
import unittest
from unittest.mock import patch


SCRIPT = Path(__file__).with_name("check_main_governance_scope.py")
SPEC = importlib.util.spec_from_file_location("check_main_governance_scope", SCRIPT)
assert SPEC and SPEC.loader
SCOPE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(SCOPE)


class MainGovernanceScopeTests(unittest.TestCase):
    def errors(self, *, base="main", head="maintenance/main-governance",
               base_repo="Furroxide/TopiaForge", head_repo="Furroxide/TopiaForge",
               paths=None, sidecar_blobs=None, dco_blobs=None):
        return SCOPE.failures(
            base, head, base_repo, head_repo,
            [".github/workflows/release-dry-run.yml"] if paths is None else paths,
            sidecar_blobs, dco_blobs,
        )

    def test_exact_same_repository_branch_and_allowed_file_pass(self):
        self.assertEqual([], self.errors())

    def test_fork_is_rejected(self):
        self.assertTrue(self.errors(head_repo="someone/TopiaForge"))

    def test_out_of_scope_file_is_rejected(self):
        self.assertTrue(self.errors(paths=["services/control-plane/src/index.ts"]))

    def test_other_branch_and_empty_diff_are_rejected(self):
        self.assertTrue(self.errors(head="maintenance/other"))
        self.assertTrue(self.errors(paths=[]))

    def test_exact_sidecar_backport_preserves_the_rest_of_the_scope(self):
        blobs = SCOPE.SIDECAR_BACKPORT
        paths = list(blobs)
        self.assertEqual([], self.errors(paths=paths, sidecar_blobs=blobs))
        self.assertTrue(self.errors(paths=paths[:1], sidecar_blobs=blobs))
        self.assertTrue(self.errors(paths=paths + ["tools/ugc-automerge-sidecar/index.mjs"],
                                    sidecar_blobs=blobs))
        self.assertTrue(self.errors(paths=paths, sidecar_blobs=blobs, head_repo="fork/TopiaForge"))

    def test_sidecar_backport_rejects_other_base_or_head_content(self):
        path = "tools/ugc-automerge-sidecar/package-lock.json"
        blobs = SCOPE.SIDECAR_BACKPORT
        old, new = blobs[path]
        for pair in (("other", new), (old, "other"), (new, "other"), (old, "")):
            with self.subTest(pair=pair):
                modified = {**blobs, path: pair}
                self.assertTrue(self.errors(paths=list(blobs), sidecar_blobs=modified))

    def test_backport_requires_a_regular_non_executable_file(self):
        path = "tools/ugc-automerge-sidecar/package.json"
        old, _ = SCOPE.SIDECAR_BACKPORT[path]
        for metadata, expected in (("100644 blob", old), ("100755 blob", ""),
                                   ("120000 blob", ""), ("040000 tree", "")):
            with self.subTest(metadata=metadata):
                entry = subprocess.CompletedProcess([], 0, f"{metadata} {old}\t{path}\n", "")
                with patch.object(SCOPE.subprocess, "run", return_value=entry):
                    self.assertEqual(expected, SCOPE.blob_at("HEAD", path))

    def test_only_the_complete_exact_dco_bootstrap_is_allowed(self):
        blobs = SCOPE.DCO_TRUST_BOOTSTRAP
        paths = list(blobs)
        self.assertEqual([], self.errors(paths=paths, dco_blobs=blobs))
        self.assertTrue(self.errors(paths=paths[:-1], dco_blobs=blobs))
        self.assertTrue(self.errors(paths=paths + [".github/release-dco/0.2.0-attestation.txt"], dco_blobs=blobs))
        substituted_key = {**blobs, ".github/release-dco/allowed-signers": ("", "wrong key")}
        self.assertTrue(self.errors(paths=paths, dco_blobs=substituted_key))

    def test_prerelease_dco_scope_admits_only_the_reviewed_verifier_and_test_transition(self):
        blobs = SCOPE.DCO_PRERELEASE_TRUST
        paths = list(blobs)
        self.assertEqual([], self.errors(paths=paths, dco_blobs=blobs))
        self.assertTrue(self.errors(paths=paths[:1], dco_blobs=blobs))
        self.assertTrue(self.errors(paths=paths + [".github/release-dco/allowed-signers"], dco_blobs=blobs))
        self.assertTrue(self.errors(paths=paths + [".github/release-dco/0.2.0-rc.1-attestation.txt"], dco_blobs=blobs))
        for path, (old, new) in blobs.items():
            with self.subTest(path=path):
                self.assertTrue(self.errors(paths=paths, dco_blobs={**blobs, path: ("", new)}))
                self.assertTrue(self.errors(paths=paths, dco_blobs={**blobs, path: (old, "wrong blob")}))

    def test_workflow_runs_this_check(self):
        policy = (SCRIPT.parents[1] / "workflows" / "pr-policy.yml").read_text(
            encoding="utf-8"
        )
        self.assertIn("python3 .github/scripts/check_main_governance_scope.py", policy)


if __name__ == "__main__":
    unittest.main()
