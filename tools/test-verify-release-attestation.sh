#!/usr/bin/env bash
set -euo pipefail

script_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
temp_root=$(mktemp -d)
trap 'rm -rf -- "$temp_root"' EXIT

predicate="$temp_root/predicate.json"
subjects="$temp_root/subjects.json"
exact="$temp_root/exact.json"

cat >"$predicate" <<'JSON'
{"role":"verifier","schemaVersion":1}
JSON
cat >"$subjects" <<'JSON'
[
  {
    "name": "TopiaForge-linux-x64.zip",
    "digest": {
      "sha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
    }
  },
  {
    "name": "release-handoff-v1.json",
    "digest": {
      "sha256": "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"
    }
  }
]
JSON
cat >"$exact" <<'JSON'
[
  {
    "verificationResult": {
      "statement": {
        "predicate": {
          "role": "verifier",
          "schemaVersion": 1
        },
        "subject": [
          {
            "name": "release-handoff-v1.json",
            "digest": {
              "sha256": "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"
            }
          },
          {
            "name": "TopiaForge-linux-x64.zip",
            "digest": {
              "sha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
            }
          }
        ]
      }
    }
  }
]
JSON

bash "$script_dir/verify-release-attestation.sh" \
  "$exact" "$predicate" "$subjects"

expect_failure() {
  local label=$1
  local candidate=$2
  if bash "$script_dir/verify-release-attestation.sh" \
    "$candidate" "$predicate" "$subjects"; then
    echo "expected failure: $label" >&2
    exit 1
  fi
}

jq \
  '.[0].verificationResult.statement.subject[0].digest.sha256 =
    "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc"' \
  "$exact" >"$temp_root/wrong-digest.json"
expect_failure "wrong subject digest" "$temp_root/wrong-digest.json"

jq \
  '.[0].verificationResult.statement.subject += [{
    name: "unexpected.zip",
    digest: {
      sha256:
        "dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd"
    }
  }]' \
  "$exact" >"$temp_root/extra-subject.json"
expect_failure "extra subject" "$temp_root/extra-subject.json"

jq \
  '.[0].verificationResult.statement.predicate.schemaVersion = 2' \
  "$exact" >"$temp_root/wrong-predicate.json"
expect_failure "wrong predicate" "$temp_root/wrong-predicate.json"

echo "release attestation subject verification tests passed"
