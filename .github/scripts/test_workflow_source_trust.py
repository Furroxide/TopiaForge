"""Regression checks for executable workflow source and transitive cache trust."""
from pathlib import Path
import os
import re
import shutil
import subprocess
import tempfile
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


def workflow_jobs(source):
    """Split a workflow into its top-level jobs, keyed by job id."""
    jobs = source.split("\njobs:\n", 1)[1]
    parts = re.split(r"(?m)^  ([a-z][a-z0-9-]*):\n", jobs)
    return {parts[index]: parts[index + 1] for index in range(1, len(parts), 2)}


def git_bash():
    bash = shutil.which("bash")
    if os.name == "nt":
        git = shutil.which("git")
        if git is None:
            return None
        directory = Path(git).parent
        bash = next((str(path) for path in (
            directory / "bash.exe", directory.parent / "bin" / "bash.exe",
        ) if path.is_file()), None)
    return bash


class ControlPlaneDeployTests(unittest.TestCase):
    """The control-plane deployment is manual, preflighted and confined to one job."""

    def setUp(self):
        self.source = read_workflow("control-plane.yml")
        self.jobs = workflow_jobs(self.source)

    def test_deployment_runs_only_on_a_manual_dispatch(self):
        triggers = self.source.split("\non:\n", 1)[1].split("\npermissions:", 1)[0]
        self.assertIn("  workflow_dispatch:\n", triggers)
        self.assertNotIn("pull_request_target", self.source)
        self.assertNotIn("workflow_run", triggers)
        self.assertIn("    if: github.event_name == 'workflow_dispatch'\n", self.jobs["deploy"])
        self.assertIn("github.event_name == 'workflow_dispatch'", self.jobs["preflight"])
        self.assertEqual(set(self.jobs), {"test", "preflight", "deploy"})

    def test_only_the_deploy_job_names_an_environment_or_reads_secrets(self):
        for name, body in self.jobs.items():
            with self.subTest(job=name):
                if name == "deploy":
                    self.assertIn("    environment:\n      name: ${{ inputs.environment }}\n", body)
                    self.assertIn("${{ secrets.CLOUDFLARE_API_TOKEN }}", body)
                    continue
                self.assertNotRegex(body, r"(?m)^    environment:")
                self.assertNotIn("secrets.", body)

    def test_every_deployment_waits_for_the_preflight_and_the_tests(self):
        self.assertIn("    needs: [preflight, test]\n", self.jobs["deploy"])
        preflight = self.jobs["preflight"]
        self.assertIn("github.repository == 'Furroxide/TopiaForge'", preflight)
        self.assertIn(".can_admins_bypass == false", preflight)
        self.assertIn('select(.type == "required_reviewers")', preflight)
        self.assertIn(".deployment_branch_policy.custom_branch_policies == true", preflight)
        self.assertLess(self.source.index("  preflight:\n"), self.source.index("  deploy:\n"))

    def test_deploy_uses_the_exact_dispatched_revision_and_no_cache(self):
        steps = checkout_steps(self.jobs["deploy"])
        self.assertEqual(len(steps), 1)
        self.assertRegex(steps[0], r"(?m)^          ref: \$\{\{ github\.sha \}\}$")
        for step in checkout_steps(self.source):
            self.assertIn("persist-credentials: false", step)
        self.assertNotIn("cache:", self.jobs["deploy"])
        self.assertIn("npm ci --ignore-scripts", self.jobs["deploy"])

    def test_targets_accept_only_their_source_branches(self):
        preflight = self.jobs["preflight"]
        step = preflight.split("      - name: Require a trusted source branch for the target\n", 1)[1]
        script = textwrap.dedent(step.split("        run: |\n", 1)[1].split("\n      - name:", 1)[0]).strip()
        bash = git_bash()
        self.assertIsNotNone(bash)
        for target, ref, passed in [
            ("control-plane-staging", "refs/heads/dev", True),
            ("control-plane-staging", "refs/heads/main", True),
            ("control-plane-staging", "refs/heads/feature", False),
            ("control-plane-staging", "refs/tags/v1.0.0", False),
            ("control-plane-production", "refs/heads/main", True),
            ("control-plane-production", "refs/heads/dev", False),
            ("control-plane-production", "refs/heads/main-copy", False),
            ("control-plane-preview", "refs/heads/main", False),
        ]:
            with self.subTest(target=target, ref=ref), tempfile.TemporaryDirectory() as folder:
                output = Path(folder) / "output"
                output.write_text("", encoding="utf-8")
                environment = {"TARGET": target, "SOURCE_REF": ref, "GITHUB_OUTPUT": str(output)}
                if os.name == "nt":
                    environment["SystemRoot"] = os.environ["SystemRoot"]
                result = subprocess.run(
                    [bash, "--noprofile", "--norc", "-c", script],
                    env=environment, capture_output=True, text=True,
                    timeout=10, check=False,
                )
                self.assertEqual(result.returncode == 0, passed, result.stderr)
                if passed:
                    expected = "staging" if target.endswith("staging") else "production"
                    self.assertEqual(output.read_text(encoding="utf-8").strip(), f"wrangler_environment={expected}")


if __name__ == "__main__":
    unittest.main()
