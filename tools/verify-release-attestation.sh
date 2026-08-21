#!/usr/bin/env bash
set -euo pipefail

if (($# != 3)); then
  echo \
    "usage: verify-release-attestation.sh <verification-results.json> <predicate.json> <subjects.json>" \
    >&2
  exit 64
fi

results_json=$1
expected_predicate=$2
expected_subjects=$3

command -v jq >/dev/null 2>&1 || {
  echo "jq is required." >&2
  exit 1
}
for input in "$results_json" "$expected_predicate" "$expected_subjects"; do
  test -s "$input" || {
    echo "Required attestation verification input is missing: $input" >&2
    exit 1
  }
done

jq -e '
  type == "array" and
  length > 0 and
  all(.[];
    type == "object" and
    (.name | type == "string") and
    (.name | test("^[A-Za-z0-9][A-Za-z0-9._+-]*$")) and
    (.digest | keys == ["sha256"]) and
    (.digest.sha256 | test("^[0-9a-f]{64}$"))
  ) and
  ([.[].name] | unique | length) == length
' "$expected_subjects" >/dev/null

jq -e \
  --slurpfile expectedPredicate "$expected_predicate" \
  --slurpfile expectedSubjects "$expected_subjects" \
  'any(.[];
    .verificationResult.statement.predicate == $expectedPredicate[0] and
    (
      .verificationResult.statement.subject | sort_by(.name)
    ) == $expectedSubjects[0]
  )' "$results_json" >/dev/null
