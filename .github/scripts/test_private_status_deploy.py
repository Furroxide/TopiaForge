"""Check that the private Status source cannot bypass the public deploy gate."""

import os
from pathlib import Path
import json
import re
import shutil
import subprocess
import tempfile
import textwrap
import unittest


WORKFLOW = (Path(__file__).resolve().parents[1] / "workflows" /
            "deploy-private-status-source.yml").read_text(encoding="utf-8")
POLICY = (Path(__file__).resolve().parents[1] / "workflows" /
          "pr-policy.yml").read_text(encoding="utf-8")
CI = (Path(__file__).resolve().parents[1] / "workflows" /
      "ci.yml").read_text(encoding="utf-8")


def jobs():
    parts = re.split(r"(?m)^  ([a-z][a-z0-9-]*):\n", WORKFLOW.split("\njobs:\n", 1)[1])
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


class PrivateStatusDeployTests(unittest.TestCase):
    def test_scoped_main_branch_exception_keeps_other_branches_blocked(self):
        step = POLICY.split("      - name: Validate title and branch routing\n", 1)[1]
        script = textwrap.dedent(step.split("        run: |\n", 1)[1].split("\n      - name:", 1)[0]).strip()
        bash = git_bash()
        self.assertIsNotNone(bash)
        for head, head_repo, accepted in [
            ("cloudflare/deploy-host-main", "Furroxide/TopiaForge", True),
            ("release/0.2.0", "Furroxide/TopiaForge", True),
            ("unrelated/main-change", "Furroxide/TopiaForge", False),
            ("cloudflare/deploy-host-main", "fork/TopiaForge", False),
        ]:
            with self.subTest(head=head, head_repo=head_repo):
                variables = {
                    "AUTHOR_LOGIN": "Furroxide", "BASE_REF": "main",
                    "BASE_REPOSITORY": "Furroxide/TopiaForge", "HEAD_REF": head,
                    "HEAD_REPOSITORY": head_repo,
                    "PR_TITLE": "ci: host protected Cloudflare deploys from private Status",
                }
                if os.name == "nt":
                    variables["SystemRoot"] = os.environ["SystemRoot"]
                result = subprocess.run(
                    [bash, "--noprofile", "--norc", "-c", script], env=variables,
                    capture_output=True, text=True, timeout=10, check=False,
                )
                self.assertEqual(result.returncode == 0, accepted, result.stderr)

        scope = POLICY.split("      - name: Limit the Cloudflare deploy-host pull request\n", 1)[1]
        scope = scope.split("\n      - name:", 1)[0]
        self.assertIn("git diff --name-only -z --no-renames", scope)
        self.assertIn(".github/workflows/deploy-private-status-source.yml", scope)
        self.assertIn("docs/internal/CloudflareDeploymentHost.md", scope)
        self.assertIn("test -f .github/workflows/deploy-private-status-source.yml", scope)
        self.assertIn("github.head_ref != 'cloudflare/deploy-host-main'", POLICY)
        self.assertEqual(CI.count("PINNED_STABLE_BASELINE:"), 3)
        self.assertEqual(CI.count("github.head_ref == 'cloudflare/deploy-host-main'"), 3)
        self.assertIn("python3 .github/scripts/test_private_status_deploy.py", CI)

    def test_scoped_main_branch_rejects_client_file_changes(self):
        step = POLICY.split("      - name: Limit the Cloudflare deploy-host pull request\n", 1)[1]
        script = textwrap.dedent(step.split("        run: |\n", 1)[1].split("\n      - name:", 1)[0]).strip()
        bash = git_bash()
        self.assertIsNotNone(bash)
        fake_git = (
            'git() { if [[ "$1" == fetch ]]; then return 0; fi; '
            'if [[ "$1" == diff ]]; then printf "%s\\0" "$SCOPED_PATH"; return 0; fi; return 1; }\n'
        )
        for path, accepted in [
            (".github/workflows/deploy-private-status-source.yml", True),
            ("src/TopiaForge.Client/Changed.cs", False),
        ]:
            with self.subTest(path=path), tempfile.TemporaryDirectory() as folder:
                workflow = Path(folder) / ".github/workflows/deploy-private-status-source.yml"
                workflow.parent.mkdir(parents=True)
                workflow.touch()
                variables = {"BASE_SHA": "a" * 40, "SCOPED_PATH": path}
                if os.name == "nt":
                    variables["SystemRoot"] = os.environ["SystemRoot"]
                result = subprocess.run(
                    [bash, "--noprofile", "--norc", "-c", fake_git + script],
                    env=variables, cwd=folder, capture_output=True, text=True,
                    timeout=10, check=False,
                )
                self.assertEqual(result.returncode == 0, accepted, result.stderr)

    def test_branch_policy_accepts_only_existing_staging_and_production_rules(self):
        preflight = jobs()["preflight"]
        expression = re.search(
            r'jq -e --arg name "\$DEPLOYMENT_ENVIRONMENT" \'(.*?)\' '
            r'<<<"\$branch_policy" >/dev/null', preflight, re.DOTALL,
        )
        self.assertIsNotNone(expression)
        jq = shutil.which("jq")
        self.assertIsNotNone(jq)
        cases = [
            ("control-plane-staging", ["dev", "main"], True),
            ("control-plane-staging", ["main"], False),
            ("control-plane-staging", ["dev", "main", "release/*"], False),
            ("control-plane-production", ["main"], True),
            ("control-plane-production", ["dev", "main"], False),
        ]
        for environment, names, accepted in cases:
            with self.subTest(environment=environment, names=names):
                response = {
                    "total_count": len(names),
                    "branch_policies": [{"name": name, "type": "branch"} for name in names],
                }
                result = subprocess.run(
                    [jq, "-e", "--arg", "name", environment, expression.group(1)],
                    input=json.dumps(response), capture_output=True, text=True,
                    timeout=10, check=False,
                )
                self.assertEqual(result.returncode == 0, accepted, result.stderr)

    def test_dispatch_rejects_untrusted_target_revision_and_branch(self):
        preflight = jobs()["preflight"]
        step = preflight.split("      - name: Require main and a reviewed Status commit\n", 1)[1]
        script = textwrap.dedent(step.split("        run: |\n", 1)[1].split("\n      - name:", 1)[0]).strip()
        bash = git_bash()
        self.assertIsNotNone(bash)
        valid_sha = "a" * 40
        cases = [
            ("status", valid_sha, "refs/heads/main", "control-plane-production", "", True),
            ("control-plane-staging", valid_sha, "refs/heads/main", "control-plane-staging", "staging", True),
            ("control-plane-production", valid_sha, "refs/heads/main", "control-plane-production", "production", True),
            ("status", valid_sha, "refs/heads/dev", "", "", False),
            ("status", "a" * 39, "refs/heads/main", "", "", False),
            ("status", "A" * 40, "refs/heads/main", "", "", False),
            ("unknown", valid_sha, "refs/heads/main", "", "", False),
        ]
        for target, sha, ref, environment, wrangler, accepted in cases:
            with self.subTest(target=target, sha=sha, ref=ref), tempfile.TemporaryDirectory() as folder:
                output = Path(folder) / "output"
                output.touch()
                variables = {
                    "TARGET": target, "SOURCE_SHA": sha, "GITHUB_REF": ref,
                    "GITHUB_OUTPUT": str(output),
                }
                if os.name == "nt":
                    variables["SystemRoot"] = os.environ["SystemRoot"]
                result = subprocess.run(
                    [bash, "--noprofile", "--norc", "-c", script], env=variables,
                    capture_output=True, text=True, timeout=10, check=False,
                )
                self.assertEqual(result.returncode == 0, accepted, result.stderr)
                if accepted:
                    self.assertEqual(output.read_text(encoding="utf-8").splitlines(), [
                        f"deployment_environment={environment}", f"wrangler_environment={wrangler}",
                    ])

    def test_private_source_and_exact_sql_are_checked_before_execution(self):
        preflight, deploy = jobs()["preflight"], jobs()["deploy"]
        self.assertIn("github.repository == 'Furroxide/TopiaForge'", preflight)
        self.assertNotIn("secrets.", preflight)
        self.assertNotRegex(preflight, r"(?m)^    environment:")
        self.assertIn("    needs: preflight\n", deploy)
        self.assertIn("name: ${{ needs.preflight.outputs.deployment_environment }}", deploy)
        self.assertIn("repository: Furroxide/TopiaForge-Status", deploy)
        self.assertIn("ref: ${{ inputs.source_sha }}", deploy)
        self.assertIn("persist-credentials: false", deploy)
        self.assertIn("gh api repos/Furroxide/TopiaForge-Status/commits/main --jq .sha", deploy)
        self.assertIn("sha256sum migrations/0003_public_activity.sql", deploy)
        self.assertIn("SELECT name FROM d1_migrations ORDER BY id", deploy)
        self.assertLess(deploy.index("SELECT name FROM d1_migrations ORDER BY id"),
                        deploy.index("wrangler d1 migrations apply DB --remote"))
        self.assertLess(deploy.index("Require the checked-out revision to be Status main"),
                        deploy.index("Test and dry-run the status page"))
        self.assertLess(deploy.index("Require the exact approved status migration"),
                        deploy.index("Apply reviewed status migrations and deploy"))


if __name__ == "__main__":
    unittest.main()
