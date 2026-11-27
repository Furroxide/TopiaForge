"""Regression checks for the required main package build's source boundary."""

from pathlib import Path
import re
import unittest


WORKFLOWS = Path(__file__).resolve().parents[1] / "workflows"


def workflow(name: str) -> str:
    return (WORKFLOWS / name).read_text(encoding="utf-8")


def job(source: str, name: str) -> str:
    match = re.search(
        rf"(?ms)^  {re.escape(name)}:\n(.*?)(?=^  [a-z][a-z0-9-]*:\n|\Z)",
        source,
    )
    if match is None:
        raise AssertionError(f"Missing job: {name}")
    return match.group(1)


class ReleasePackageSourceTests(unittest.TestCase):
    def test_main_pr_runs_the_existing_required_check(self) -> None:
        caller = workflow("release-dry-run.yml")
        self.assertRegex(caller, r"(?m)^  pull_request:\n    branches:\n      - main$")
        self.assertIn("github.event.pull_request.head.repo.full_name == github.repository", caller)
        self.assertNotIn("source_sha", caller)
        self.assertNotIn("secrets: inherit", caller)
        required = job(caller, "required-release-packages")
        self.assertIn("name: Required / Release packages", required)
        self.assertIn("if: always()", required)
        self.assertIn("needs: build-packages", required)
        self.assertIn('run: test "$RESULT" = success', required)

    def test_every_package_job_uses_the_admitted_caller_commit(self) -> None:
        package = workflow("release-package-build.yml")
        launcher = workflow("flutter-launcher-builds.yml")
        self.assertNotIn("source_sha", package)
        self.assertNotIn("source_sha", launcher)
        preflight = job(package, "preflight")
        for guard in (
            "github.repository == 'Furroxide/TopiaForge'",
            "github.event_name == 'push'",
            "github.event_name == 'workflow_dispatch'",
            "github.event_name == 'pull_request'",
            "github.base_ref == 'main'",
            "github.event.pull_request.head.repo.full_name == github.repository",
        ):
            self.assertIn(guard, preflight)
        for name in ("build-launchers", "canonical-ecosystem", "macos-cli-x64", "build"):
            self.assertIn("preflight", job(package, name), name)
        for source in (package, launcher):
            checkouts = re.findall(
                r"(?m)^        uses: actions/checkout@[^\n]+\n        with:\n"
                r"(.*?)(?=^      - name:|^  [a-z][a-z0-9-]*:|\Z)",
                source,
                flags=re.DOTALL | re.MULTILINE,
            )
            self.assertTrue(checkouts)
            for checkout in checkouts:
                self.assertIn("ref: ${{ github.sha }}", checkout)
                self.assertIn("persist-credentials: false", checkout)

    def test_package_jobs_cannot_publish_managed_ref_caches(self) -> None:
        package = workflow("release-package-build.yml")
        self.assertNotRegex(package, r"uses: actions/cache@")
        self.assertEqual(2, package.count("uses: actions/cache/restore@"))

    def test_release_prs_keep_latest_managed_ref_requirement(self) -> None:
        package = workflow("release-package-build.yml")
        baselines = re.findall(r"(?m)^          PINNED_STABLE_BASELINE: (.+)$", package)
        self.assertEqual(3, len(baselines))
        for expression in baselines:
            self.assertIn("github.head_ref == 'maintenance/main-governance'", expression)
            self.assertIn("github.head_ref == 'cloudflare/deploy-host-main'", expression)
            self.assertNotIn("github.base_ref == 'main' }}", expression)
        ci_baselines = re.findall(
            r"(?m)^          PINNED_STABLE_BASELINE: (.+)$", workflow("ci.yml")
        )
        self.assertEqual(4, len(ci_baselines))
        for expression in ci_baselines:
            self.assertIn("github.head_ref == 'maintenance/main-governance'", expression)
            self.assertNotIn("github.head_ref == 'release/", expression)


if __name__ == "__main__":
    unittest.main()
