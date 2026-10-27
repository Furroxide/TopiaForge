"""Keep the parallel CI graph fail-closed, including its Pages completion path."""
import ast
import os
from pathlib import Path
import re
import shutil
import subprocess
import textwrap
import unittest

ROOT = Path(__file__).resolve().parents[2]
RESULTS = {
    "CSHARP": "csharp-tests",
    "CSHARP_FORMAT": "csharp-format",
    "DART": "dart-tests",
    "FLUTTER": "flutter-tests",
    "REPOSITORY": "repository-audits",
    "RELEASE_HELPERS_WINDOWS": "release-helpers-windows",
    "DOCS": "docs",
    "TEMPLATES": "template-matrix",
}


def jobs(source):
    parts = re.split(r"(?m)^  ([a-z][a-z0-9-]*):\n", source.split("\njobs:\n", 1)[1])
    return dict(zip(parts[1::2], parts[2::2]))


def step(body, name):
    parts = re.split(r"(?m)^      - name: (.+)\n", body)
    return dict(zip(parts[1::2], parts[2::2]))[name]


def run_script(body):
    return textwrap.dedent(body.split("        run: |\n", 1)[1]).strip()


def condition(body, context):
    """Evaluate the small boolean expression subset used by this job's admission.

    Parse to an AST rather than eval repository text. Unknown syntax fails closed.
    This tests the real expression's grouping, not just the presence of guard text.
    """
    expression = body.split("    if: >-\n", 1)[1].split("    runs-on:", 1)[0]
    expression = " ".join(expression.split()).replace("&&", " and ").replace("||", " or ")
    expression = re.sub(r"!(?!=)", "not ", expression)

    def evaluate(node):
        if isinstance(node, ast.Constant) and isinstance(node.value, (str, bool)):
            return node.value
        if isinstance(node, ast.Attribute):
            return context[ast.unparse(node)]
        if isinstance(node, ast.BoolOp):
            values = [bool(evaluate(value)) for value in node.values]
            if isinstance(node.op, ast.And):
                return all(values)
            if isinstance(node.op, ast.Or):
                return any(values)
        if isinstance(node, ast.UnaryOp) and isinstance(node.op, ast.Not):
            return not evaluate(node.operand)
        if isinstance(node, ast.Compare) and len(node.ops) == 1:
            left, right = evaluate(node.left), evaluate(node.comparators[0])
            if isinstance(node.ops[0], ast.Eq):
                return left == right
            if isinstance(node.ops[0], ast.NotEq):
                return left != right
        if (isinstance(node, ast.Call) and isinstance(node.func, ast.Name)
                and node.func.id == "startsWith" and len(node.args) == 2 and not node.keywords):
            return evaluate(node.args[0]).startswith(evaluate(node.args[1]))
        raise AssertionError(f"Unsupported admission expression: {ast.dump(node)}")

    return evaluate(ast.parse(expression, mode="eval").body)


class CiValidationTests(unittest.TestCase):
    def setUp(self):
        self.source = (ROOT / ".github/workflows/ci.yml").read_text(encoding="utf-8")
        self.jobs = jobs(self.source)
        self.required = self.jobs["required-ci"]

    def test_required_audits_execute_this_suite_unconditionally(self):
        audit = step(self.jobs["repository-audits"], "Test required CI and Pages completion gates")
        self.assertEqual(audit.strip(), "run: python3 .github/scripts/test_ci_validation.py")

    def test_every_validation_job_is_required(self):
        self.assertEqual(set(self.jobs), {*RESULTS.values(), "required-ci"})
        header = self.required.split("    steps:", 1)[0]
        needs = re.findall(r"(?m)^      - ([a-z][a-z0-9-]*)$", header)
        self.assertCountEqual(needs, RESULTS.values())
        self.assertIn("    name: Required / CI validation\n", header)
        self.assertIn("    if: always()\n", header)
        for name, body in self.jobs.items():
            with self.subTest(job=name):
                self.assertNotIn("continue-on-error:", body)
                if name != "required-ci":
                    self.assertNotRegex(body.split("    steps:", 1)[0], r"(?m)^    if:")

    def test_result_bindings_cover_each_dependency(self):
        enforcement = step(self.required, "Enforce all validation jobs")
        environment = enforcement.split("        env:\n", 1)[1].split("        run:", 1)[0]
        bindings = re.findall(r"(?m)^          ([A-Z_]+): \$\{\{ needs\.([a-z-]+)\.result \}\}$", environment)
        self.assertCountEqual(bindings, RESULTS.items())
        self.assertNotIn("        if:", enforcement)

    def test_enforcement_rejects_every_non_success_result(self):
        script = run_script(step(self.required, "Enforce all validation jobs"))
        bash = shutil.which("bash")
        if os.name == "nt":
            git = Path(shutil.which("git"))
            bash = next((str(path) for path in (git.parent / "bash.exe",
                git.parent.parent / "bin/bash.exe") if path.is_file()), None)
        self.assertIsNotNone(bash)
        cases = [(None, "success")]
        cases += [(key, result) for key in RESULTS
                  for result in ("failure", "cancelled", "skipped", "", "unknown", "success ")]
        for key, result in cases:
            with self.subTest(dependency=key, result=result):
                environment = {key: "success" for key in RESULTS}
                if os.name == "nt":
                    environment["SystemRoot"] = os.environ["SystemRoot"]
                if key is not None:
                    environment[key] = result
                completed = subprocess.run([bash, "--noprofile", "--norc", "-c", script],
                    env=environment, capture_output=True, text=True, timeout=10, check=False)
                self.assertEqual(completed.returncode == 0, key is None, completed.stderr)

    def test_formatting_keeps_the_exact_command_and_reference_setup(self):
        formatting = self.jobs["csharp-format"]
        build = self.jobs["csharp-tests"]
        for name in ("Checkout", "Set up .NET", "Install managed-reference archive dependency",
                     "Resolve Robotopia refs cache key", "Cache Robotopia managed refs",
                     "Restore Robotopia managed refs"):
            with self.subTest(step=name):
                self.assertEqual(step(formatting, name).strip(), step(build, name).strip())
        self.assertEqual(run_script(step(formatting, "Verify formatting")),
            "dotnet restore TopiaForge.slnx\ndotnet format TopiaForge.slnx --verify-no-changes --no-restore")
        self.assertNotIn("        if:", step(formatting, "Verify formatting"))
        self.assertIn("    runs-on: ubuntu-latest\n", formatting)
        self.assertNotIn("    needs:", formatting)
        self.assertIn("        run: dotnet restore TopiaForge.slnx\n", step(build, "Restore solution"))

    def test_parallel_jobs_keep_read_only_source_and_cache_provenance(self):
        self.assertIn("\npermissions:\n  contents: read\n", self.source)
        for name in ("docs", "csharp-format", "csharp-tests"):
            body = self.jobs[name]
            with self.subTest(job=name):
                self.assertNotIn("    permissions:", body)
                self.assertNotIn("secrets.", body)
                checkout = step(body, "Checkout")
                self.assertIn("          persist-credentials: false\n", checkout)
                self.assertNotIn("          ref:", checkout)
                self.assertNotIn("          repository:", checkout)
                cache = step(body, "Cache Robotopia managed refs")
                self.assertIn("          key: ${{ steps.refs-cache.outputs.key }}\n", cache)
                self.assertIn("          path: ${{ runner.temp }}/robotopia-managed-refs-cache\n", cache)
                self.assertNotIn("restore-keys:", cache)
                self.assertIn("--source public --cache-key-only", step(body, "Resolve Robotopia refs cache key"))
                self.assertIn("--source public --require-latest", step(body, "Restore Robotopia managed refs"))
        self.assertNotIn("    needs:", self.jobs["docs"])

    def test_all_scaffolds_still_use_the_current_run_payload(self):
        templates = self.jobs["template-matrix"]
        self.assertIn("    needs: csharp-tests\n", templates)
        self.assertIn("      fail-fast: false\n", templates)
        self.assertIn("        template: [minimal, gameplay, gamemode, service, ui, asset, world, multiplayer]\n", templates)
        download = step(templates, "Download extracted-release SDK acceptance payload")
        self.assertIn("          name: topiaforge-sdk-acceptance\n", download)
        self.assertNotIn("run-id:", download)
        self.assertNotIn("repository:", download)
        self.assertNotIn("github-token:", download)


class PagesCompletionTests(unittest.TestCase):
    def setUp(self):
        self.source = (ROOT / ".github/workflows/deploy-pages.yml").read_text(encoding="utf-8")
        self.jobs = jobs(self.source)
        self.context = {
            "github.event_name": "workflow_run", "github.repository": "example/project",
            "github.ref": "refs/heads/main", "github.event.workflow_run.conclusion": "success",
            "github.event.workflow_run.event": "push", "github.event.workflow_run.head_branch": "main",
            "github.event.workflow_run.head_repository.full_name": "example/project",
        }

    def test_completed_ci_requires_every_admission_condition(self):
        triggers = self.source.split("\non:\n", 1)[1].split("\npermissions:", 1)[0]
        self.assertIn("  workflow_run:\n    workflows: [CI]\n    types: [completed]\n", triggers)
        build = self.jobs["build-pages"]
        self.assertTrue(condition(build, self.context))
        cases = [("github.event.workflow_run.conclusion", value) for value in
                 ("failure", "cancelled", "skipped", "neutral", "timed_out", "action_required", "")]
        cases += [("github.event.workflow_run.event", "pull_request"),
                  ("github.event.workflow_run.event", "workflow_dispatch"),
                  ("github.event.workflow_run.head_branch", "dev"),
                  ("github.event.workflow_run.head_repository.full_name", "example/fork"),
                  ("github.event_name", "pull_request"), ("github.event_name", "release")]
        for key, value in cases:
            with self.subTest(field=key, value=value):
                self.assertFalse(condition(build, {**self.context, key: value}))

    def test_dispatch_remains_separate_and_deploy_requires_a_successful_build(self):
        for ref, allowed in (("refs/heads/main", True), ("refs/tags/v1.2.3", True),
                             ("refs/heads/dev", False), ("refs/heads/feature", False),
                             ("refs/tags/unsafe", False)):
            with self.subTest(ref=ref):
                self.assertEqual(condition(self.jobs["build-pages"], {
                    **self.context, "github.event_name": "workflow_dispatch", "github.ref": ref,
                    "github.event.workflow_run.conclusion": "failure"}), allowed)
        # No status override: the deploy job inherits Actions' success() dependency guard.
        deploy = self.jobs["deploy"]
        self.assertIn("    needs: build-pages\n", deploy)
        self.assertNotIn("    if:", deploy)
        self.assertNotIn("continue-on-error:", self.source)
        self.assertNotIn("actions/checkout@", deploy)


if __name__ == "__main__":
    unittest.main()
