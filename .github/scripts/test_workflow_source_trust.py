"""Regression checks for executable workflow source and transitive cache trust."""
from pathlib import Path
import os
import re
import shutil
import subprocess
import textwrap
import unittest

ROOT = Path(__file__).resolve().parents[2]
WORKFLOWS = ROOT / ".github" / "workflows"


def read_workflow(name):
    return (WORKFLOWS / name).read_text(encoding="utf-8-sig")


def checkout_steps(source):
    return [
        step for step in re.split(r"(?m)(?=      - name:)", source)
        if re.search(r"(?m)^        uses: actions/checkout@", step)
    ]


class ExecutableSourceTests(unittest.TestCase):
    def test_release_packages_cannot_select_an_arbitrary_revision(self):
        for name in ("release-package-build.yml", "flutter-launcher-builds.yml"):
            source = read_workflow(name)
            self.assertNotIn("source_sha", source, name)
            steps = checkout_steps(source)
            self.assertTrue(steps, name)
            for step in steps:
                self.assertRegex(step, r"(?m)^          ref: \$\{\{ github\.sha \}\}$")
                self.assertIn("persist-credentials: false", step)
        self.assertNotIn("source_sha", read_workflow("release-dry-run.yml"))

    def test_package_source_admission_precedes_every_executable_job(self):
        source = read_workflow("release-package-build.yml")
        preflight = source.split("  preflight:\n", 1)[1].split("    steps:", 1)[0]
        for guard in (
            "github.repository == 'Furroxide/TopiaForge'",
            "github.event_name == 'push'",
            "github.event_name == 'workflow_dispatch'",
            "github.ref == 'refs/heads/main'",
            "startsWith(github.ref, 'refs/heads/release/')",
        ):
            self.assertIn(guard, preflight)
        launcher = source.split("  build-launchers:\n", 1)[1].split("  preflight:", 1)[0]
        self.assertIn("needs: preflight", launcher)
        for job in ("canonical-ecosystem", "macos-cli-x64", "build"):
            body = source.split(f"  {job}:\n", 1)[1].split("    steps:", 1)[0]
            self.assertIn("preflight", body)

    def test_pages_separates_dispatch_and_workflow_run_revisions(self):
        source = read_workflow("deploy-pages.yml")
        steps = checkout_steps(source)
        self.assertEqual(len(steps), 2)
        run, dispatch = steps
        self.assertIn("if: github.event_name == 'workflow_run'", run)
        self.assertIn("ref: refs/heads/main", run)
        self.assertIn("repository: ${{ github.repository }}", run)
        self.assertNotIn("head_sha", run)
        self.assertIn("if: github.event_name == 'workflow_dispatch'", dispatch)
        self.assertIn("ref: ${{ github.sha }}", dispatch)
        self.assertNotIn("head_sha", dispatch)
        self.assertIn("github.event.workflow_run.head_repository.full_name == github.repository", source)
        self.assertLess(source.index("name: Require a trusted Pages source"),
                        source.index("name: Checkout protected main for completed CI"))
        self.assertIn(".prerelease == false", source)
        self.assertIn(".immutable == true", source)

    def _pages_ci_admission(self):
        source = read_workflow("deploy-pages.yml")
        steps = re.split(r"(?m)(?=      - name:)", source)
        checkout_index = next(i for i, step in enumerate(steps)
                              if "name: Checkout protected main for completed CI" in step)
        admission = steps[checkout_index + 1]
        self.assertIn("name: Require completed CI to match protected main", admission)
        self.assertIn("if: github.event_name == 'workflow_run'", admission)
        self.assertIn("EXPECTED_CI_SHA: ${{ github.event.workflow_run.head_sha }}", admission)
        self.assertIn('[[ "$EXPECTED_CI_SHA" =~ ^[0-9a-f]{40}$ ]]', admission)
        self.assertIn('test "$(git --no-replace-objects rev-parse HEAD)" = "$EXPECTED_CI_SHA"', admission)
        self.assertLess(source.index("name: Require completed CI to match protected main"),
                        source.index("uses: ./.github/actions/setup-flutter"))
        return textwrap.dedent(admission.split("        run: |\n", 1)[1]).strip()

    def test_pages_checks_exact_ci_head_before_repository_code(self):
        self._pages_ci_admission()

    def test_pages_rejects_stale_and_malformed_completed_ci_heads(self):
        # Completion order can differ from commit order. A stale completed run
        # must never publish old docs after protected main has already advanced.
        admission = self._pages_ci_admission()
        bash = shutil.which("bash")
        if os.name == "nt":
            # Windows' System32 bash launches WSL, whose path and argv handling
            # is different. Run this portable shell guard with Git for Windows.
            git = shutil.which("git")
            self.assertIsNotNone(git)
            directory = Path(git).parent
            bash = next((str(path) for path in (
                directory / "bash.exe", directory.parent / "bin" / "bash.exe",
            ) if path.is_file()), None)
        self.assertIsNotNone(bash)
        protected = "1" * 40
        script = (
            'git() { test "$*" = "--no-replace-objects rev-parse HEAD" || return 71; '
            f'printf "%s\\n" "{protected}"; }}\n'
            + admission
        )
        for expected, passed in [
            (protected, True), ("2" * 40, False), ("A" * 40, False),
            ("", False), (protected + "\n", False), (protected[:39], False),
            ("$(exit 0)", False),
        ]:
            with self.subTest(expected=expected):
                environment = {"EXPECTED_CI_SHA": expected}
                if os.name == "nt":
                    environment["SystemRoot"] = os.environ["SystemRoot"]
                result = subprocess.run(
                    [bash, "--noprofile", "--norc", "-c", script],
                    env=environment, capture_output=True, text=True,
                    timeout=10, check=False,
                )
                self.assertEqual(result.returncode == 0, passed, result.stderr)


if __name__ == "__main__":
    unittest.main()
