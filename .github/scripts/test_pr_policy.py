"""Execute the PR policy shell bodies with deterministic, offline Git responses."""
from pathlib import Path
import os
import shutil
import subprocess
import sys
import tempfile
import textwrap
import unittest
from unittest.mock import patch

import verify_0_2_0_dco as historical_dco

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
      case "$2" in
        "$BASE_SHA:.github/scripts/verify_0_2_0_dco.py")
          printf '%s\n' 'import os, sys' 'from pathlib import Path' \
            'assert Path(sys.argv[1]).read_text() == "trusted base key\n"' \
            'assert sys.argv[2:] == ["Furroxide/TopiaForge", "Furroxide/TopiaForge", "main", os.environ["HEAD_REF"], "b"*40, "a"*40]' \
            'print("protected verifier used")'
          return ;;
        "$BASE_SHA:.github/release-dco/allowed-signers")
          [[ "$BASE_SIGNER_FAIL" != true ]] || return 93
          printf '%s\n' 'trusted base key'
          return ;;
      esac
      [[ "$2 $3" == '-s --format=%B' ]] || return 91
      case "$4" in
        incoming-one) printf '%s\n' "$FIRST_MESSAGE" ;;
        incoming-two)
          [[ "$SHOW_FAIL" != true ]] || return 92
          printf '%s\n' "$SECOND_MESSAGE" ;;
        *) printf '%s\n' 'test: unsigned historical base commit' ;;
      esac ;;
    *) return 99 ;;
  esac
}
jq() { printf '%s\n' "$PRODUCT_VERSION"; }
python3() { "$PYTHON_EXE" "$@"; }
'''
        signed = "test: signed\n\nSigned-off-by: Test Contributor <test@example.invalid>"
        environment = dict(os.environ, BASE_SHA="b" * 40, HEAD_SHA="a" * 40,
                           FETCH_FAIL="false", MISSING_BASE="false", MISSING_HEAD="false",
                           LIST_FAIL="false", SHOW_FAIL="false", NO_COMMITS="false",
                           PRODUCT_VERSION="0.2.0", BASE_REPOSITORY="Furroxide/TopiaForge",
                           BASE_SIGNER_FAIL="false",
                           HEAD_REPOSITORY="Furroxide/TopiaForge", BASE_REF="main",
                           HEAD_REF="release/0.2.0", FIRST_MESSAGE=signed, SECOND_MESSAGE=signed,
                           PYTHON_EXE=Path(sys.executable).as_posix())
        environment.update(values)
        return subprocess.run([bash, "--noprofile", "--norc", "-c", mocks + step_body(name)],
                              env=environment, cwd=ROOT, capture_output=True, text=True, timeout=10)

    def dco(self, **values):
        values.setdefault("HEAD_REF", "work/policy-fixture")
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

    def test_commit_message_failure_fails_closed(self):
        self.assertNotEqual(self.dco(SHOW_FAIL="true").returncode, 0)

    def test_scoped_unsigned_route_loads_the_verifier_and_key_from_the_base(self):
        for branch in ("release/0.2.0", "release/0.2.0-rc.1"):
            with self.subTest(branch=branch):
                context = dict(HEAD_REF=branch, SECOND_MESSAGE="test: unsigned")
                result = self.dco(**context)
                self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
                self.assertIn("protected verifier used", result.stdout)
                self.assertNotEqual(self.dco(**context, BASE_SIGNER_FAIL="true").returncode, 0)
                fork = self.dco(**context, HEAD_REPOSITORY="fork/TopiaForge")
                self.assertNotEqual(fork.returncode, 0)
                self.assertNotIn("protected verifier used", fork.stdout)

    def test_empty_commit_range_is_valid(self):
        self.assertEqual(self.dco(NO_COMMITS="true").returncode, 0)

    def test_future_release_source_must_match_current_product(self):
        name = "Validate release source against the product version"
        self.assertEqual(self.run_policy(name).returncode, 0)
        self.assertEqual(self.run_policy(name, HEAD_REF="release/0.2.0-rc.1", PRODUCT_VERSION="0.2.0-rc.1").returncode, 0)
        for branch in ("release/0.1.0-rc.1", "release/0.2.0-rc.1", "release/0.3.0", "dev"):
            self.assertNotEqual(self.run_policy(name, HEAD_REF=branch).returncode, 0)
        self.assertNotEqual(self.run_policy(name, PRODUCT_VERSION="invalid").returncode, 0)


class HistoricalDcoTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.temp = tempfile.TemporaryDirectory()
        cls.addClassCleanup(cls.temp.cleanup)
        cls.root = Path(cls.temp.name)
        cls.release = [f"{number:040x}" for number in range(1, 49)]
        cls.sync = [f"{number:040x}" for number in range(49, 57)]
        cls.contents = (
            "[release/0.2.0 -> main]\n" + "\n".join(cls.release) + "\n\n"
            "[sync/main-v0.2.0 -> dev]\n" + "\n".join(cls.sync) + "\n"
        ).encode("ascii")
        cls.digest = historical_dco.sha256(cls.contents).hexdigest()
        cls.signer = cls.root / "fixture-signing-key"
        subprocess.run(["ssh-keygen", "-q", "-t", "ed25519", "-N", "", "-f", str(cls.signer)],
                       capture_output=True, check=True, timeout=10)
        cls.signed = cls.root / "fixture-attestation.txt"
        cls.signed.write_bytes(cls.contents)
        subprocess.run(
            ["ssh-keygen", "-Y", "sign", "-f", str(cls.signer),
             "-n", historical_dco.SIGNING_NAMESPACE, str(cls.signed)],
            capture_output=True, check=True, timeout=10,
        )
        cls.signature = cls.signed.with_suffix(".txt.sig").read_bytes()
        public = cls.signer.with_suffix(".pub").read_text(encoding="utf-8").split()
        cls.fixture_signers = cls.root / "allowed-signers"
        cls.fixture_signers.write_text(
            f'{historical_dco.SIGNING_PRINCIPAL} {" ".join(public[:2])}\n', encoding="utf-8",
        )

    def verify(self, commits, *, base="main", head="release/0.2.0", repository="Furroxide/TopiaForge",
               payload=None, signature=True, allowed=True):
        with patch.object(historical_dco, "ATTESTATION_SHA256", self.digest):
            return historical_dco.verify(
                "Furroxide/TopiaForge", repository, base, head, commits,
                self.contents if payload is None else payload,
                self.signature if signature is True else signature,
                self.fixture_signers if allowed else ROOT / ".github/release-dco/allowed-signers",
            )

    def test_exact_complete_release_and_sync_lists_are_accepted_with_valid_signature(self):
        self.assertEqual(len(self.release), 48)
        self.assertEqual(len(self.sync), 8)
        self.assertIsNone(self.verify(list(reversed(self.release))))
        self.assertIsNone(self.verify(self.sync, base="dev", head="sync/main-v0.2.0"))

    def test_fork_and_other_release_or_sync_routes_cannot_use_certification(self):
        self.assertIsNotNone(self.verify(self.release, repository="some-fork/TopiaForge"))
        self.assertIsNotNone(self.verify(self.release, head="release/0.3.0"))
        self.assertIsNotNone(self.verify(self.sync, base="dev", head="sync/main-v0.3.0"))

    def test_prerelease_requires_its_own_exact_route_and_signature(self):
        prerelease = "release/0.2.0-rc.1"
        signed = self.root / "fixture-prerelease.txt"
        payload = (f"[{prerelease} -> main]\n" + "\n".join(self.release) + "\n").encode("ascii")
        signed.write_bytes(payload)
        subprocess.run(
            ["ssh-keygen", "-Y", "sign", "-f", str(self.signer),
             "-n", historical_dco.SIGNING_NAMESPACE, str(signed)],
            capture_output=True, check=True, timeout=10,
        )
        signature = signed.with_suffix(".txt.sig").read_bytes()
        with patch.object(historical_dco, "PRERELEASE_SHA256", historical_dco.sha256(payload).hexdigest()):
            self.assertIsNone(self.verify(self.release, head=prerelease, payload=payload, signature=signature))
            self.assertIn("reviewed bytes", self.verify(self.release, head=prerelease))
            self.assertIn("reviewed bytes", self.verify(self.release, payload=payload, signature=signature))
            self.assertIn("valid release-maintainer signature", self.verify(self.release, head=prerelease, payload=payload))
            self.assertIn("complete certified list", self.verify(
                self.release[:-1], head=prerelease, payload=payload, signature=signature,
            ))
            self.assertIsNotNone(self.verify(
                self.release, base="dev", head=prerelease, payload=payload, signature=signature,
            ))

    def test_prerelease_verifier_reads_the_distinct_candidate_blobs(self):
        base, head = "a" * 40, "b" * 40
        path = ".github/release-dco/0.2.0-rc.1-attestation.txt"
        with patch.object(historical_dco, "git"), \
                patch.object(historical_dco, "unsigned_commits", return_value=self.release), \
                patch.object(historical_dco, "candidate_blob", side_effect=[b"payload", b"signature"]) as blobs, \
                patch.object(historical_dco, "verify", return_value=None):
            self.assertEqual(historical_dco.main([
                str(self.fixture_signers), "Furroxide/TopiaForge", "Furroxide/TopiaForge",
                "main", "release/0.2.0-rc.1", base, head,
            ]), 0)
        self.assertEqual([call.args for call in blobs.call_args_list], [(head, path), (head, f"{path}.sig")])

    def test_new_missing_commit_and_partial_lists_are_rejected(self):
        for invalid in (self.release + ["f" * 40], self.release[:-1], self.sync, self.release + self.release[:1]):
            self.assertIn("complete certified list", self.verify(invalid))

    def test_empty_corrupt_or_untrusted_signatures_are_rejected(self):
        self.assertIn("valid release-maintainer signature", self.verify(self.release, signature=b""))
        self.assertIn("valid release-maintainer signature", self.verify(self.release, signature=b"invalid\n"))
        self.assertIn("valid release-maintainer signature", self.verify(self.release, allowed=False))

    def test_changed_attestation_is_rejected(self):
        self.assertIn("reviewed bytes", self.verify(self.release, payload=self.contents + b"\n"))

    def test_candidate_attestation_is_read_as_a_bounded_git_blob(self):
        name = f'{"a" * 40}:{historical_dco.ATTESTATION}'
        with patch.object(historical_dco, "git", side_effect=[b"12\n", b"fixture data"]) as call:
            self.assertEqual(historical_dco.candidate_blob("a" * 40, historical_dco.ATTESTATION), b"fixture data")
            self.assertEqual(call.call_args_list[0].args, ("cat-file", "-s", name))
            self.assertEqual(call.call_args_list[1].args, ("cat-file", "blob", name))
        with patch.object(historical_dco, "git", return_value=b"9000\n") as call:
            with self.assertRaises(ValueError):
                historical_dco.candidate_blob("a" * 40, historical_dco.ATTESTATION)
            self.assertEqual(call.call_count, 1)


if __name__ == "__main__":
    unittest.main()
