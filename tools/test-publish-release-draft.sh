#!/usr/bin/env bash
set -euo pipefail

script_dir=$(CDPATH='' cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
publisher="$script_dir/publish-release-draft.sh"
fake_gh="$script_dir/test-support/fake-gh-release.sh"
temp_root=$(mktemp -d)
trap 'rm -rf "$temp_root"' EXIT

git init --quiet --bare "$temp_root/remote.git"
git init --quiet "$temp_root/work"
git -C "$temp_root/work" config user.name "Robotopia Release Test"
git -C "$temp_root/work" config user.email "release-test@example.invalid"
git -C "$temp_root/work" remote add origin "$temp_root/remote.git"
git -C "$temp_root/work" commit --quiet --allow-empty -m first
wrong_sha=$(git -C "$temp_root/work" rev-parse HEAD)
git -C "$temp_root/work" commit --quiet --allow-empty -m release
target_sha=$(git -C "$temp_root/work" rev-parse HEAD)
git -C "$temp_root/work" push --quiet origin HEAD:refs/heads/main
git -C "$temp_root/work" tag -a v1.0.0 "$target_sha" -m v1.0.0
git -C "$temp_root/work" tag -a v1.0.1 "$wrong_sha" -m v1.0.1
git -C "$temp_root/work" push --quiet origin refs/tags/v1.0.0 refs/tags/v1.0.1

mkdir -p "$temp_root/bin" "$temp_root/assets" "$temp_root/state"
ln -s "$fake_gh" "$temp_root/bin/gh"
printf 'release notes\n' >"$temp_root/notes.md"
printf '{"distributable":true,"blockingReasons":[]}\n' >"$temp_root/assets/release-bom.json"
printf '{"spdxVersion":"SPDX-2.3"}\n' >"$temp_root/assets/release-sbom.spdx.json"
printf 'payload\n' >"$temp_root/assets/QuantumWorks-linux-x64.zip"
(
  cd "$temp_root/assets"
  sha256sum release-bom.json release-sbom.spdx.json QuantumWorks-linux-x64.zip >SHA256SUMS
)

export PATH="$temp_root/bin:$PATH"
export FAKE_GH_STATE="$temp_root/state"
run_publisher() {
  (
    cd "$temp_root/work"
    "$publisher" owner/repo "$@" "QuantumWorks 1.0.0" \
      "$temp_root/notes.md" "$temp_root/assets"
  )
}
must_fail() {
  if "$@" >/dev/null 2>&1; then
    echo "Expected command to fail: $*" >&2
    exit 1
  fi
}
reset_matching_release() {
  jq -n \
    --arg body "$(<"$temp_root/notes.md")" \
    '{id:1,tag_name:"v1.0.0",name:"QuantumWorks 1.0.0",body:$body,draft:true,prerelease:false}' \
    >"$FAKE_GH_STATE/release.json"
}

# New draft: the POST omits target_commitish, so GitHub cannot synthesize a tag.
run_publisher v1.0.0 "$target_sha" >/dev/null
test "$(<"$FAKE_GH_STATE/uploads")" = 4
jq -e '.draft == true and .tag_name == "v1.0.0" and (.target_commitish | not)' \
  "$FAKE_GH_STATE/release.json" >/dev/null

# Exact rerun is a no-op.
run_publisher v1.0.0 "$target_sha" >/dev/null
test "$(<"$FAKE_GH_STATE/uploads")" = 4

# A partial starter upload is deleted and resumed with the exact local bytes.
jq '.[0] |= (.state="starter" | .digest=null | .size=0)' \
  "$FAKE_GH_STATE/assets.json" >"$FAKE_GH_STATE/assets.tmp"
mv "$FAKE_GH_STATE/assets.tmp" "$FAKE_GH_STATE/assets.json"
run_publisher v1.0.0 "$target_sha" >/dev/null
test "$(<"$FAKE_GH_STATE/uploads")" = 5
jq -e 'all(.[]; .state == "uploaded")' "$FAKE_GH_STATE/assets.json" >/dev/null

# Existing immutable bytes, names, and publication state must match exactly.
cp "$FAKE_GH_STATE/assets.json" "$FAKE_GH_STATE/assets.good.json"
jq '.[0].digest="sha256:ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff"' \
  "$FAKE_GH_STATE/assets.good.json" >"$FAKE_GH_STATE/assets.json"
must_fail run_publisher v1.0.0 "$target_sha"
cp "$FAKE_GH_STATE/assets.good.json" "$FAKE_GH_STATE/assets.json"
jq '. + [{id:999,name:"unexpected.zip",state:"uploaded",digest:"sha256:00",size:1}]' \
  "$FAKE_GH_STATE/assets.good.json" >"$FAKE_GH_STATE/assets.json"
must_fail run_publisher v1.0.0 "$target_sha"
cp "$FAKE_GH_STATE/assets.good.json" "$FAKE_GH_STATE/assets.json"

reset_matching_release
jq '.draft=false' "$FAKE_GH_STATE/release.json" >"$FAKE_GH_STATE/release.tmp"
mv "$FAKE_GH_STATE/release.tmp" "$FAKE_GH_STATE/release.json"
must_fail run_publisher v1.0.0 "$target_sha"

reset_matching_release
jq '.name="Wrong title"' "$FAKE_GH_STATE/release.json" >"$FAKE_GH_STATE/release.tmp"
mv "$FAKE_GH_STATE/release.tmp" "$FAKE_GH_STATE/release.json"
must_fail run_publisher v1.0.0 "$target_sha"

reset_matching_release
jq '.body="Wrong notes"' "$FAKE_GH_STATE/release.json" >"$FAKE_GH_STATE/release.tmp"
mv "$FAKE_GH_STATE/release.tmp" "$FAKE_GH_STATE/release.json"
must_fail run_publisher v1.0.0 "$target_sha"

# Both a wrong target argument and an annotated tag at another commit fail.
reset_matching_release
must_fail run_publisher v1.0.0 "$wrong_sha"
must_fail run_publisher v1.0.1 "$target_sha"

echo "Release draft rerun regression tests passed."
