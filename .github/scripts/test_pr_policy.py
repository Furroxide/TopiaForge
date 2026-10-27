"""Execute the PR policy shell bodies with deterministic, offline Git responses."""
from pathlib import Path
import os
import shutil
import subprocess
import textwrap
import unittest

ROOT = Path(__file__).resolve().parents[2]


def step_body(name):
    workflow = (ROOT / ".github/workflows/pr-policy.yml").read_text(encoding="utf-8")
    step = workflow.split("      - name: " + name + "\n", 1)[1].split("\n      - name:", 1)[0]
    body = step.split("        run: |\n", 1)[1]
    return textwrap.dedent(body.split("\n  required:", 1)[0])


class PrPolicyTests(unittest.TestCase):
    def run_policy(self, name, **values):
        bash = os.environ.get("TOPIAFORGE_BASH") or shutil.which("bash")
        self.assertIsNotNone(bash, "Git Bash or bash is required")
        # The base has unsigned historical commits. Only the exact incoming
        # range yields these two new commits; all other ranges fail the test.
        # No release tags exist, including RC1. Every unexpected Git call fails.
        mocks = r'''
git() {
  case "$1" in
    fetch)
      [[ "$FETCH_FAIL" != true && "$*" == "fetch --force --no-tags origin $BASE_SHA" ]] ;;
    rev-parse)
      [[ "$2" == --verify ]] || return 90
      case "$3" in
        "$BASE_SHA^{commit}") [[ "$MISSING_BASE" != true ]] ;;
        "$HEAD_SHA^{commit}") [[ "$MISSING_HEAD" != true ]] ;;
        *) return 1 ;;
      esac ;;
    rev-list)
      [[ "$LIST_FAIL" != true && "$*" == "rev-list --reverse $BASE_SHA..$HEAD_SHA" ]] || return 1
      [[ "$NO_COMMITS" == true ]] || printf '%s\n' incoming-one incoming-two ;;
    show)
      [[ "$2 $3" == '-s --format=%B' ]] || return 91
      case "$4" in
        incoming-one) printf '%s\n' "$FIRST_MESSAGE" ;;
        incoming-two) printf '%s\n' "$SECOND_MESSAGE" ;;
        *) printf '%s\n' 'test: unsigned historical base commit' ;;
      esac ;;
    *) return 99 ;;
  esac
}
jq() { printf '%s\n' "$PRODUCT_VERSION"; }
'''
        signed = "test: signed\n\nSigned-off-by: Test Contributor <test@example.invalid>"
        environment = dict(os.environ, BASE_SHA="b" * 40, HEAD_SHA="a" * 40,
                           FETCH_FAIL="false", MISSING_BASE="false", MISSING_HEAD="false",
                           LIST_FAIL="false", NO_COMMITS="false", PRODUCT_VERSION="0.2.0",
                           HEAD_REF="release/0.2.0", FIRST_MESSAGE=signed, SECOND_MESSAGE=signed)
        environment.update(values)
        return subprocess.run([bash, "--noprofile", "--norc", "-c", mocks + step_body(name)],
                              env=environment, cwd=ROOT, capture_output=True, text=True, timeout=10)

    def dco(self, **values):
        return self.run_policy("Enforce DCO for incoming commits", **values)

    def test_signed_incoming_range_passes_without_any_release_tag(self):
        for version in ("0.1.0-rc.1", "0.2.0", "0.3.0", "1.0.0"):
            result = self.dco(PRODUCT_VERSION=version)
            self.assertEqual(result.returncode, 0, result.stdout + result.stderr)

    def test_either_unsigned_incoming_commit_is_rejected(self):
        for key in ("FIRST_MESSAGE", "SECOND_MESSAGE"):
            result = self.dco(**{key: "test: unsigned"})
            self.assertNotEqual(result.returncode, 0)
            self.assertIn("missing a valid Signed-off-by", result.stdout)

    def test_malformed_trailers_are_rejected(self):
        for message in ("Signed-off-by: Sam", "Signed-off-by: <invalid>"):
            result = self.dco(SECOND_MESSAGE=message)
            self.assertNotEqual(result.returncode, 0)
            self.assertIn("missing a valid Signed-off-by", result.stdout)

    def test_unsigned_historical_base_is_not_checked(self):
        self.assertEqual(self.dco().returncode, 0)

    def test_unavailable_base_fails_closed(self):
        self.assertNotEqual(self.dco(MISSING_BASE="true").returncode, 0)

    def test_unavailable_head_fails_closed(self):
        self.assertNotEqual(self.dco(MISSING_HEAD="true").returncode, 0)

    def test_fetch_failure_fails_closed(self):
        self.assertNotEqual(self.dco(FETCH_FAIL="true").returncode, 0)

    def test_commit_enumeration_failure_fails_closed(self):
        self.assertNotEqual(self.dco(LIST_FAIL="true").returncode, 0)

    def test_empty_commit_range_is_valid(self):
        self.assertEqual(self.dco(NO_COMMITS="true").returncode, 0)

    def test_future_release_source_must_match_current_product(self):
        name = "Validate release source against the product version"
        self.assertEqual(self.run_policy(name).returncode, 0)
        for branch in ("release/0.1.0-rc.1", "release/0.3.0", "dev"):
            self.assertNotEqual(self.run_policy(name, HEAD_REF=branch).returncode, 0)
        self.assertNotEqual(self.run_policy(name, PRODUCT_VERSION="invalid").returncode, 0)


if __name__ == "__main__":
    unittest.main()
