"""Tests for the narrowly scoped main governance pull-request route."""

import importlib.util
from pathlib import Path
import unittest


SCRIPT = Path(__file__).with_name("check_main_governance_scope.py")
SPEC = importlib.util.spec_from_file_location("check_main_governance_scope", SCRIPT)
assert SPEC and SPEC.loader
SCOPE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(SCOPE)


class MainGovernanceScopeTests(unittest.TestCase):
    def errors(self, *, base="main", head="maintenance/main-governance",
               base_repo="Furroxide/TopiaForge", head_repo="Furroxide/TopiaForge",
               paths=None):
        return SCOPE.failures(
            base, head, base_repo, head_repo,
            [".github/workflows/release-dry-run.yml"] if paths is None else paths,
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

    def test_workflow_runs_this_check(self):
        policy = (SCRIPT.parents[1] / "workflows" / "pr-policy.yml").read_text(
            encoding="utf-8"
        )
        self.assertIn("python3 .github/scripts/check_main_governance_scope.py", policy)


if __name__ == "__main__":
    unittest.main()
