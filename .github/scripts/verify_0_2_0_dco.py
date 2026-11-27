"""Verify the exact 0.2.0 candidate from a protected checkout using Git objects."""
from hashlib import sha256
from pathlib import Path
import re
import subprocess
import sys
import tempfile


ATTESTATION_SHA256 = "6a412e3684842edf26a03759ed1de77909271a784f3602a0dd2b4a68b5bd5e47"
REPOSITORY = "Furroxide/TopiaForge"
SIGNING_NAMESPACE = "topiaforge-release-dco"
SIGNING_PRINCIPAL = "Furroxide"
ATTESTATION = ".github/release-dco/0.2.0-attestation.txt"
ROUTES = {("main", "release/0.2.0"), ("dev", "sync/main-v0.2.0")}
PRERELEASE_ROUTE = ("main", "release/0.2.0-rc.1")
PRERELEASE_ATTESTATION = ".github/release-dco/0.2.0-rc.1-attestation.txt"
PRERELEASE_SHA256 = "51e5f60e584e03cfec2910c8af71ea4553596731847e9497550f9cc0f1dff79a"
SIGNOFF = re.compile(rb"Signed-off-by:[ \t].+<[^<>\s]+@[^<>\s]+>[ \t]*", re.IGNORECASE)


def git(*args):
    return subprocess.check_output(["git", *args], stderr=subprocess.PIPE, timeout=30)


def candidate_blob(head, path):
    object_name = f"{head}:{path}"
    if not 0 < int(git("cat-file", "-s", object_name)) <= 8192:
        raise ValueError("Invalid attestation input size")
    return git("cat-file", "blob", object_name)


def unsigned_commits(base, head):
    records = git("log", "-z", "--format=%H%x00%B", f"{base}..{head}").split(b"\0")
    fields = iter(records[:-1])
    return [
        commit.decode("ascii")
        for commit, message in zip(fields, fields, strict=True)
        if not any(SIGNOFF.fullmatch(line) for line in message.splitlines())
    ]


def attestation_spec(base_ref, head_ref):
    route = (base_ref, head_ref)
    if route == PRERELEASE_ROUTE:
        return PRERELEASE_ATTESTATION, PRERELEASE_SHA256
    if route in ROUTES:
        return ATTESTATION, ATTESTATION_SHA256
    return None


def verify(base_repository, head_repository, base_ref, head_ref, missing, payload, signature, allowed_signers):
    if base_repository != REPOSITORY or head_repository != REPOSITORY:
        return "Historical DCO certification belongs to Furroxide/TopiaForge."
    spec = attestation_spec(base_ref, head_ref)
    if spec is None:
        return "Historical DCO certification applies only to the declared 0.2.0 routes."
    _, digest = spec
    if sha256(payload).hexdigest() != digest:
        return "Historical DCO attestation does not match the reviewed bytes."
    heading = f"[{head_ref} -> {base_ref}]\n"
    certified = payload.decode("ascii").split(heading, 1)[1].split("\n\n", 1)[0].splitlines()
    if sorted(missing) != certified:
        return "Unsigned commits differ from the complete certified list for this route."

    try:
        with tempfile.TemporaryDirectory() as temporary:
            signature_file = Path(temporary) / "attestation.sig"
            signature_file.write_bytes(signature)
            result = subprocess.run(
                [
                    "ssh-keygen", "-Y", "verify",
                    "-f", str(allowed_signers), "-I", SIGNING_PRINCIPAL,
                    "-n", SIGNING_NAMESPACE, "-s", str(signature_file),
                ],
                input=payload, capture_output=True, timeout=10, check=False,
            )
    except (OSError, subprocess.TimeoutExpired):
        return "Historical DCO attestation or signature could not be verified."
    if result.returncode:
        return "Historical DCO attestation requires a valid release-maintainer signature."
    return None


def main(args):
    allowed_signers, base_repository, head_repository, base_ref, head_ref, base, head = args
    missing = []
    try:
        if not all(re.fullmatch(r"[a-f0-9]{40}", oid) for oid in (base, head)):
            raise ValueError("Expected complete Git commit IDs")
        git("rev-parse", "--verify", f"{base}^{{commit}}")
        git("rev-parse", "--verify", f"{head}^{{commit}}")
        missing = unsigned_commits(base, head)
        spec = attestation_spec(base_ref, head_ref)
        if spec is None:
            raise ValueError("Unrecognized attestation route")
        attestation, _ = spec
        failure = verify(
            base_repository, head_repository, base_ref, head_ref, missing,
            candidate_blob(head, attestation), candidate_blob(head, attestation + ".sig"),
            allowed_signers,
        )
    except (ValueError, OSError, subprocess.SubprocessError):
        failure = "Cannot read the exact release attestation and signature from the candidate commit."
    if failure:
        for commit in missing:
            print(f"::error::Commit {commit} is missing a valid Signed-off-by trailer.")
        print(f"::error::{failure}")
        return 1
    print(f"Verified the signed historical DCO certification for base {base}, head {head}, "
          f"and {len(missing)} incoming commits.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
