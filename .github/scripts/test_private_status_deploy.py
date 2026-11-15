"""Check that the private Status source cannot bypass the public deploy gate."""

import os
from pathlib import Path
import json
import re
import shutil
import subprocess
import sys
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

    def test_status_migration_guard_rejects_unapproved_history(self):
        deploy = jobs()["deploy"]
        expression = re.search(
            r"(?ms)^          jq -e '\n(.*?)\n          ' <<<\"\$migration_rows\" >/dev/null$",
            deploy,
        )
        self.assertIsNotNone(expression)
        jq = shutil.which("jq")
        self.assertIsNotNone(jq)
        old = ["0001_status.sql", "0002_claim_owners.sql"]
        approved = old + ["0003_public_activity.sql"]
        for names, success, accepted in [
            (old, True, True),
            (approved, True, True),
            (old[:1], True, False),
            (list(reversed(old)), True, False),
            (approved + ["0004_unreviewed.sql"], True, False),
            (old, False, False),
        ]:
            with self.subTest(names=names, success=success):
                response = [{"success": success, "results": [{"name": name} for name in names]}]
                result = subprocess.run(
                    [jq, "-e", expression.group(1)], input=json.dumps(response),
                    capture_output=True, text=True, timeout=10, check=False,
                )
                self.assertEqual(result.returncode == 0, accepted, result.stderr)

    def test_control_plane_secrets_release_only_through_protected_deploy(self):
        deploy = jobs()["deploy"]
        step = deploy.split("      - name: Deploy the control plane\n", 1)[1]
        script = textwrap.dedent(step.split("          python3 - \"$secrets_file\" <<'PY'\n", 1)[1]
                                 .split("\n          PY", 1)[0])
        self.assertIn('trap \'rm -f -- "$secrets_file"\' EXIT', step)
        self.assertIn('wrangler deploy --config wrangler.deploy.toml --env "$WRANGLER_ENVIRONMENT" --secrets-file "$secrets_file"', step)
        self.assertLess(step.index("python3 - \"$secrets_file\""), step.index("wrangler deploy --config"))

        good = {
            "TARGET": "control-plane-production",
            "CLOUDFLARE_ACCOUNT_ID": "a" * 32,
            "SERVICE_KEY": "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8",
            "ADMIN_TOKEN": "b" * 40,
            "TURN_KEY_ID": "c" * 32,
            "TURN_KEY_API_TOKEN": "d" * 64,
            "ANALYTICS_API_TOKEN": "e" * 40,
        }

        def run(variables):
            with tempfile.TemporaryDirectory() as folder:
                output = Path(folder) / "secrets.json"
                env = os.environ.copy()
                env.update(variables)
                result = subprocess.run(
                    [sys.executable, "-c", script, str(output)], env=env,
                    capture_output=True, text=True, timeout=10, check=False,
                )
                data = json.loads(output.read_text(encoding="utf-8")) if output.exists() else None
                if output.exists() and os.name != "nt":
                    self.assertEqual(output.stat().st_mode & 0o777, 0o600)
                return result, data

        staging = dict(good, TARGET="control-plane-staging")
        result, data = run(staging)
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(data, {"ANALYTICS_ACCOUNT_ID": good["CLOUDFLARE_ACCOUNT_ID"],
                                "ANALYTICS_API_TOKEN": good["ANALYTICS_API_TOKEN"]})
        result, data = run(good)
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(data, {"ANALYTICS_ACCOUNT_ID": good["CLOUDFLARE_ACCOUNT_ID"],
                                **{name: good[name] for name in (
                                    "SERVICE_KEY", "ADMIN_TOKEN", "TURN_KEY_ID",
                                    "TURN_KEY_API_TOKEN", "ANALYTICS_API_TOKEN")}})
        alternate = dict(good, TURN_KEY_ID="Key_Alpha-7",
                         TURN_KEY_API_TOKEN="turn+token/opaque==",
                         ANALYTICS_API_TOKEN="analytics+read/opaque==")
        result, data = run(alternate)
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(data["TURN_KEY_ID"], alternate["TURN_KEY_ID"])
        self.assertEqual(data["TURN_KEY_API_TOKEN"], alternate["TURN_KEY_API_TOKEN"])
        for name, invalid in (
            ("CLOUDFLARE_ACCOUNT_ID", "wrong"),
            ("SERVICE_KEY", "short"),
            ("ADMIN_TOKEN", "short"),
            ("TURN_KEY_ID", "wrong"),
            ("TURN_KEY_API_TOKEN", "wrong"),
            ("ANALYTICS_API_TOKEN", ""),
        ):
            with self.subTest(name=name):
                result, data = run(dict(good, **{name: invalid}))
                self.assertNotEqual(result.returncode, 0)
                self.assertIsNone(data)
                self.assertNotIn(good["SERVICE_KEY"], result.stderr)
                self.assertNotIn(good["TURN_KEY_API_TOKEN"], result.stderr)

    def test_status_staging_ingest_token_uses_protected_deploy(self):
        deploy = jobs()["deploy"]
        step = deploy.split("      - name: Apply reviewed status migrations and deploy\n", 1)[1]
        script = textwrap.dedent(step.split("          python3 - \"$secrets_file\" <<'PY'\n", 1)[1]
                                 .split("\n          PY", 1)[0])
        self.assertIn('wrangler deploy --config wrangler.deploy.toml --secrets-file "$secrets_file"', step)
        self.assertIn('trap \'rm -f -- "$secrets_file"\' EXIT', step)
        for token, accepted in (("a" * 48, True), ("short", False), ("a" * 40 + "\n", False)):
            with self.subTest(accepted=accepted, length=len(token)), tempfile.TemporaryDirectory() as folder:
                output = Path(folder) / "status-secrets.json"
                env = os.environ.copy()
                env["STATUS_INGEST_TOKEN_STAGING"] = token
                result = subprocess.run(
                    [sys.executable, "-c", script, str(output)], env=env,
                    capture_output=True, text=True, timeout=10, check=False,
                )
                self.assertEqual(result.returncode == 0, accepted, result.stderr)
                self.assertEqual(output.exists(), accepted)
                if accepted:
                    self.assertEqual(json.loads(output.read_text(encoding="utf-8")),
                                     {"INGEST_TOKEN_STAGING": token})
                    if os.name != "nt":
                        self.assertEqual(output.stat().st_mode & 0o777, 0o600)
                self.assertNotIn(token, result.stderr)

    def test_production_public_smoke_rejects_enabled_features(self):
        smoke = jobs()["deploy"].split("      - name: Smoke-test public routes after domain cutover\n", 1)[1]
        base = re.search(r"predicate='([^']+)'", smoke)
        production = re.search(r'predicate="\$predicate and ([^"]+)"', smoke)
        self.assertIsNotNone(base)
        self.assertIsNotNone(production)
        jq = shutil.which("jq")
        if jq is None:
            self.skipTest("jq is required to execute the public smoke predicate")
        predicate = f"{base.group(1)} and {production.group(1)}"
        disabled = {name: False for name in ("hosting", "invites", "signaling", "relay")}
        for features, accepted in (
            (disabled, True),
            (dict(disabled, relay=True), False),
            (dict(disabled, hosting=True), False),
            ({"hosting": False, "invites": False, "signaling": False}, False),
        ):
            with self.subTest(features=features):
                response = {"schema": "topiaforge.control-plane-status/1", "api": 1,
                            "features": features}
                result = subprocess.run(
                    [jq, "-e", predicate], input=json.dumps(response),
                    capture_output=True, text=True, timeout=10, check=False,
                )
                self.assertEqual(result.returncode == 0, accepted, result.stderr)


if __name__ == "__main__":
    unittest.main()
