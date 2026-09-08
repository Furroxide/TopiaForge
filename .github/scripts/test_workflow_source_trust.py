"""Regression checks for executable workflow source and transitive cache trust."""
from pathlib import Path
import re
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
        self.assertIn("ref: ${{ github.event.workflow_run.head_sha }}", run)
        self.assertIn("if: github.event_name == 'workflow_dispatch'", dispatch)
        self.assertIn("ref: ${{ github.sha }}", dispatch)
        self.assertNotIn("head_sha", dispatch)
        self.assertIn("github.event.workflow_run.head_repository.full_name == github.repository", source)
        self.assertLess(source.index("name: Require a trusted Pages source"),
                        source.index("name: Checkout exact"))
        self.assertIn(".prerelease == false", source)
        self.assertIn(".immutable == true", source)


if __name__ == "__main__":
    unittest.main()
