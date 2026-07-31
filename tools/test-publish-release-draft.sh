#!/usr/bin/env bash
set -euo pipefail

script_dir=$(CDPATH='' cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
publisher="$script_dir/publish-release-draft.sh"
fetcher="$script_dir/fetch-release-assets.sh"
fake_gh="$script_dir/test-support/fake-gh-release.sh"
temp_root=$(mktemp -d)
trap 'rm -rf "$temp_root"' EXIT

git init --quiet --bare "$temp_root/remote.git"
git init --quiet "$temp_root/work"
git -C "$temp_root/work" config user.name "TopiaForge Release Test"
git -C "$temp_root/work" config user.email "release-test@example.invalid"
git -C "$temp_root/work" remote add origin "$temp_root/remote.git"
git -C "$temp_root/work" commit --quiet --allow-empty -m first
wrong_sha=$(git -C "$temp_root/work" rev-parse HEAD)
git -C "$temp_root/work" commit --quiet --allow-empty -m release
target_sha=$(git -C "$temp_root/work" rev-parse HEAD)
git -C "$temp_root/work" push --quiet origin HEAD:refs/heads/main
git -C "$temp_root/work" tag -a v1.0.0-rc.1 "$target_sha" -m v1.0.0-rc.1
git -C "$temp_root/work" tag -a v1.0.1-rc.1 "$wrong_sha" -m v1.0.1-rc.1
git -C "$temp_root/work" tag -a v1.0.2 "$target_sha" -m v1.0.2
git -C "$temp_root/work" push --quiet origin \
  refs/tags/v1.0.0-rc.1 refs/tags/v1.0.1-rc.1 refs/tags/v1.0.2

mkdir -p "$temp_root/bin" "$temp_root/assets" "$temp_root/state"
cp "$fake_gh" "$temp_root/bin/gh"
printf 'release notes\n' >"$temp_root/notes.md"
printf '{"distributable":true,"blockingReasons":[]}\n' \
  >"$temp_root/assets/release-bom.json"
printf '{"spdxVersion":"SPDX-2.3"}\n' \
  >"$temp_root/assets/release-sbom.spdx.json"
printf '{"version":"1.0.0-rc.1"}\n' \
  >"$temp_root/assets/topiaforge-update-v1.json"
printf 'update-signature\n' \
  >"$temp_root/assets/topiaforge-update-v1.json.sig"
for platform in windows-x64 linux-x64; do
  printf '{"schema":"release-platform-bundle-v1","platform":"%s"}\n' \
    "$platform" \
    >"$temp_root/assets/release-platform-bundle-v1-$platform.json"
done
printf '{"schema":"release-handoff-v1"}\n' \
  >"$temp_root/assets/release-handoff-v1.json"
printf 'detached-cms-signature\n' \
  >"$temp_root/assets/release-handoff-v1.json.p7s"
printf 'windows archive\n' >"$temp_root/assets/TopiaForge-windows-x64.zip"
printf 'linux archive\n' >"$temp_root/assets/TopiaForge-linux-x64.zip"
printf 'canonical mod\n' >"$temp_root/assets/example.topiaforgemod"
checksum_temp="$temp_root/SHA256SUMS.tmp"
(
  cd "$temp_root/assets"
  find . -mindepth 1 -maxdepth 1 -type f ! -name SHA256SUMS \
    -printf '%f\n' | sort |
    while IFS= read -r name; do
      sha256sum "$name"
    done
) >"$checksum_temp"
mv "$checksum_temp" "$temp_root/assets/SHA256SUMS"
expected_asset_count=$(
  find "$temp_root/assets" -mindepth 1 -maxdepth 1 -type f | wc -l |
    tr -d ' '
)

export PATH="$temp_root/bin:$PATH"
export GH_TOKEN=test-token
export TOPIAFORGE_GOVERNANCE_AUDIT_TOKEN=test-governance-token
export FAKE_GH_STATE="$temp_root/state"
export FAKE_GH_CALLER_ADMIN=true
export FAKE_GH_IMMUTABLE_ENABLED=true
export FAKE_GH_PUBLISHED_IMMUTABLE=true

run_publisher() {
  run_publisher_with_flag "$1" "$2" true "${3:-draft}"
}
run_publisher_with_flag() {
  local tag=$1
  local sha=$2
  local prerelease=$3
  local mode=${4:-draft}
  (
    cd "$temp_root/work"
    "$publisher" owner/repo "$tag" "$sha" "TopiaForge ${tag#v}" \
      "$temp_root/notes.md" "$temp_root/assets" "$prerelease" "$mode"
  )
}
run_fetcher() {
  local destination=$1
  (
    cd "$temp_root/work"
    bash "$fetcher" owner/repo v1.0.0-rc.1 "$target_sha" \
      "TopiaForge 1.0.0-rc.1" "$temp_root/notes.md" "$destination" true
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
    '{
      id:1,
      tag_name:"v1.0.0-rc.1",
      name:"TopiaForge 1.0.0-rc.1",
      body:$body,
      draft:true,
      prerelease:true,
      immutable:false,
      published_at:null,
      author:{login:"furroxide",id:221987073,type:"User"}
    }' >"$FAKE_GH_STATE/release.json"
}
write_publish_uploader_fixture() {
  local source=$1
  local destination=$2
  local generated
  generated=$(jq -c '.artifactPolicy.generatedMetadata' \
    "$script_dir/../release/release-policy.json")
  jq \
    --argjson generated "$generated" \
    'map(
      . as $asset |
      if ($generated | index($asset.name)) != null then
        .uploader = {
          login:"github-actions[bot]",
          id:41898282,
          type:"Bot"
        } |
        .performed_via_github_app = {id:15368}
      else
        .
      end
    )' "$source" >"$destination"
}

# New draft: the POST omits target_commitish, so GitHub cannot synthesize a tag.
must_fail run_publisher_with_flag v1.0.0-rc.1 "$target_sha" false
run_publisher v1.0.0-rc.1 "$target_sha" >/dev/null
test "$(<"$FAKE_GH_STATE/uploads")" = "$expected_asset_count"
jq -e \
  '.draft == true and
   .immutable == false and
   .prerelease == true and
   .tag_name == "v1.0.0-rc.1" and
   (.target_commitish | not)' \
  "$FAKE_GH_STATE/release.json" >/dev/null

# This broad fixture deliberately includes generated metadata during admin
# staging. The fetcher must reject those names until the pinned Actions bot
# owns them; a human uploader is never valid generated-metadata provenance.
mkdir "$temp_root/human-generated-draft-fetch"
must_fail run_fetcher "$temp_root/human-generated-draft-fetch"

# A release from any mutable login or actor ID is rejected by both paths.
jq '.author.login="other-admin"' "$FAKE_GH_STATE/release.json" \
  >"$FAKE_GH_STATE/release.tmp"
mv "$FAKE_GH_STATE/release.tmp" "$FAKE_GH_STATE/release.json"
must_fail run_publisher v1.0.0-rc.1 "$target_sha"
mkdir "$temp_root/non-admin-fetch"
must_fail run_fetcher "$temp_root/non-admin-fetch"
reset_matching_release
jq '.author.id=999' "$FAKE_GH_STATE/release.json" \
  >"$FAKE_GH_STATE/release.tmp"
mv "$FAKE_GH_STATE/release.tmp" "$FAKE_GH_STATE/release.json"
must_fail run_publisher v1.0.0-rc.1 "$target_sha"
reset_matching_release

# Every admin-staged asset is bound to the pinned login, actor ID, and type.
cp "$FAKE_GH_STATE/assets.json" "$FAKE_GH_STATE/assets.identity-good.json"
jq '.[0].uploader.id=999' "$FAKE_GH_STATE/assets.identity-good.json" \
  >"$FAKE_GH_STATE/assets.json"
must_fail run_publisher v1.0.0-rc.1 "$target_sha"
mkdir "$temp_root/wrong-uploader-fetch"
must_fail run_fetcher "$temp_root/wrong-uploader-fetch"
cp "$FAKE_GH_STATE/assets.identity-good.json" "$FAKE_GH_STATE/assets.json"
jq '.[0].uploader.type="Bot"' "$FAKE_GH_STATE/assets.identity-good.json" \
  >"$FAKE_GH_STATE/assets.json"
must_fail run_publisher v1.0.0-rc.1 "$target_sha"
cp "$FAKE_GH_STATE/assets.identity-good.json" "$FAKE_GH_STATE/assets.json"

if [[ ${TOPIAFORGE_RELEASE_AUTHORITY_TEST_ONLY:-false} == true ]]; then
  write_publish_uploader_fixture \
    "$FAKE_GH_STATE/assets.identity-good.json" \
    "$FAKE_GH_STATE/assets.authority-publish.json"
  reset_matching_release
  jq '
    map(
      if .name == "release-bom.json" then .uploader.id=999
      else .
      end
    )
  ' "$FAKE_GH_STATE/assets.authority-publish.json" \
    >"$FAKE_GH_STATE/assets.json"
  must_fail run_publisher v1.0.0-rc.1 "$target_sha" publish
  reset_matching_release
  jq '
    map(
      if .name == "release-bom.json" then
        .performed_via_github_app.id=999
      else .
      end
    )
  ' "$FAKE_GH_STATE/assets.authority-publish.json" \
    >"$FAKE_GH_STATE/assets.json"
  must_fail run_publisher v1.0.0-rc.1 "$target_sha" publish
  reset_matching_release
  jq '[.[] | select(.name != "TopiaForge-windows-x64.zip")]' \
    "$FAKE_GH_STATE/assets.authority-publish.json" \
    >"$FAKE_GH_STATE/assets.json"
  uploads_before_missing_admin=$(<"$FAKE_GH_STATE/uploads")
  must_fail run_publisher v1.0.0-rc.1 "$target_sha" publish
  test "$(<"$FAKE_GH_STATE/uploads")" = "$uploads_before_missing_admin"
  reset_matching_release
  jq '
    map(
      if .name == "TopiaForge-windows-x64.zip" then
        .state="starter" | .digest=null | .size=0
      else .
      end
    )
  ' "$FAKE_GH_STATE/assets.authority-publish.json" \
    >"$FAKE_GH_STATE/assets.json"
  uploads_before_admin_starter=$(<"$FAKE_GH_STATE/uploads")
  must_fail run_publisher v1.0.0-rc.1 "$target_sha" publish
  test "$(<"$FAKE_GH_STATE/uploads")" = "$uploads_before_admin_starter"
  jq -e '
    any(.[];
      .name == "TopiaForge-windows-x64.zip" and
      .state == "starter"
    )
  ' "$FAKE_GH_STATE/assets.json" >/dev/null
  reset_matching_release
  cp "$FAKE_GH_STATE/assets.authority-publish.json" \
    "$FAKE_GH_STATE/assets.json"
  missing_generated_id=$(jq -er \
    '.[] | select(.name == "release-bom.json") | .id' \
    "$FAKE_GH_STATE/assets.json")
  jq '[.[] | select(.name != "release-bom.json")]' \
    "$FAKE_GH_STATE/assets.json" >"$FAKE_GH_STATE/assets.tmp"
  mv "$FAKE_GH_STATE/assets.tmp" "$FAKE_GH_STATE/assets.json"
  rm -f "$FAKE_GH_STATE/asset-content/$missing_generated_id"
  export FAKE_GH_UPLOAD_PRINCIPAL=workflow
  run_publisher v1.0.0-rc.1 "$target_sha" publish >/dev/null
  unset FAKE_GH_UPLOAD_PRINCIPAL
  jq '
    map(
      if .name == "release-bom.json" then .uploader.type="User"
      else .
      end
    )
  ' "$FAKE_GH_STATE/assets.authority-publish.json" \
    >"$FAKE_GH_STATE/assets.json"
  mkdir "$temp_root/authority-wrong-workflow-fetch"
  must_fail run_fetcher "$temp_root/authority-wrong-workflow-fetch"
  echo "Release author and uploader authority regression tests passed."
  exit 0
fi

# Exact draft rerun is a no-op.
run_publisher v1.0.0-rc.1 "$target_sha" >/dev/null
test "$(<"$FAKE_GH_STATE/uploads")" = "$expected_asset_count"

# A partial starter upload is deleted and resumed with the exact local bytes.
jq '.[0] |= (.state="starter" | .digest=null | .size=0)' \
  "$FAKE_GH_STATE/assets.json" >"$FAKE_GH_STATE/assets.tmp"
mv "$FAKE_GH_STATE/assets.tmp" "$FAKE_GH_STATE/assets.json"
run_publisher v1.0.0-rc.1 "$target_sha" >/dev/null
test "$(<"$FAKE_GH_STATE/uploads")" = "$((expected_asset_count + 1))"
jq -e 'all(.[]; .state == "uploaded")' \
  "$FAKE_GH_STATE/assets.json" >/dev/null

# Existing bytes and names must match exactly.
cp "$FAKE_GH_STATE/assets.json" "$FAKE_GH_STATE/assets.good.json"
jq '.[0].digest="sha256:ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff"' \
  "$FAKE_GH_STATE/assets.good.json" >"$FAKE_GH_STATE/assets.json"
must_fail run_publisher v1.0.0-rc.1 "$target_sha"
cp "$FAKE_GH_STATE/assets.good.json" "$FAKE_GH_STATE/assets.json"
jq '. + [{
  id:999,
  name:"unexpected.zip",
  state:"uploaded",
  digest:"sha256:0000000000000000000000000000000000000000000000000000000000000000",
  size:1
}]' "$FAKE_GH_STATE/assets.good.json" >"$FAKE_GH_STATE/assets.json"
must_fail run_publisher v1.0.0-rc.1 "$target_sha"
cp "$FAKE_GH_STATE/assets.good.json" "$FAKE_GH_STATE/assets.json"

# Candidate and release metadata mismatches fail before publication.
reset_matching_release
jq '.prerelease=false' "$FAKE_GH_STATE/release.json" \
  >"$FAKE_GH_STATE/release.tmp"
mv "$FAKE_GH_STATE/release.tmp" "$FAKE_GH_STATE/release.json"
must_fail run_publisher v1.0.0-rc.1 "$target_sha"

reset_matching_release
jq '.name="Wrong title"' "$FAKE_GH_STATE/release.json" \
  >"$FAKE_GH_STATE/release.tmp"
mv "$FAKE_GH_STATE/release.tmp" "$FAKE_GH_STATE/release.json"
must_fail run_publisher v1.0.0-rc.1 "$target_sha"

reset_matching_release
jq '.body="Wrong notes"' "$FAKE_GH_STATE/release.json" \
  >"$FAKE_GH_STATE/release.tmp"
mv "$FAKE_GH_STATE/release.tmp" "$FAKE_GH_STATE/release.json"
must_fail run_publisher v1.0.0-rc.1 "$target_sha"

# Both a wrong target argument and an annotated tag at another commit fail.
reset_matching_release
must_fail run_publisher v1.0.0-rc.1 "$wrong_sha"
must_fail run_publisher v1.0.1-rc.1 "$target_sha"

# Final checksums must cover every public asset other than the checksum file.
cp "$temp_root/assets/SHA256SUMS" "$temp_root/SHA256SUMS.good"
grep -v 'release-handoff-v1.json$' \
  "$temp_root/SHA256SUMS.good" >"$temp_root/assets/SHA256SUMS"
must_fail run_publisher v1.0.0-rc.1 "$target_sha" publish
mv "$temp_root/SHA256SUMS.good" "$temp_root/assets/SHA256SUMS"

# Publication is a single transition, requires immutable releases, and reruns
# verify the exact already-published bytes without uploading or patching.
reset_matching_release
write_publish_uploader_fixture \
  "$FAKE_GH_STATE/assets.good.json" \
  "$FAKE_GH_STATE/assets.publish-good.json"
cp "$FAKE_GH_STATE/assets.publish-good.json" "$FAKE_GH_STATE/assets.json"

# A finalizer may upload every generated metadata asset and then stop before
# the draft:false transition. The next run must accept the pinned Actions
# uploader on that still-draft inventory, fetch the exact bytes, and publish
# without replacing any asset.
mkdir "$temp_root/stranded-finalizer-fetch"
run_fetcher "$temp_root/stranded-finalizer-fetch" >/dev/null
uploads_before_stranded_resume=$(<"$FAKE_GH_STATE/uploads")
run_publisher v1.0.0-rc.1 "$target_sha" publish >/dev/null
jq -e \
  '.draft == false and
   .immutable == true and
   .published_at != null' \
  "$FAKE_GH_STATE/release.json" >/dev/null
test "$(<"$FAKE_GH_STATE/uploads")" = "$uploads_before_stranded_resume"
reset_matching_release
cp "$FAKE_GH_STATE/assets.publish-good.json" "$FAKE_GH_STATE/assets.json"

# The same recovery path repairs an incomplete Actions-owned generated asset,
# while continuing to reject starter state for every admin-staged asset.
jq '
  map(
    if .name == "release-bom.json" then
      .state="starter" | .digest=null | .size=0
    else .
    end
  )
' "$FAKE_GH_STATE/assets.publish-good.json" >"$FAKE_GH_STATE/assets.json"
mkdir "$temp_root/stranded-finalizer-starter-fetch"
run_fetcher "$temp_root/stranded-finalizer-starter-fetch" >/dev/null
uploads_before_stranded_starter=$(<"$FAKE_GH_STATE/uploads")
export FAKE_GH_UPLOAD_PRINCIPAL=workflow
run_publisher v1.0.0-rc.1 "$target_sha" publish >/dev/null
unset FAKE_GH_UPLOAD_PRINCIPAL
test "$(<"$FAKE_GH_STATE/uploads")" = \
  "$((uploads_before_stranded_starter + 1))"
jq -e \
  '.draft == false and
   .immutable == true and
   .published_at != null' \
  "$FAKE_GH_STATE/release.json" >/dev/null
reset_matching_release
cp "$FAKE_GH_STATE/assets.publish-good.json" "$FAKE_GH_STATE/assets.json"

rm -f "$FAKE_GH_STATE/asset-get-count"
export FAKE_GH_MUTATE_ASSETS_ON_GET=3
must_fail run_publisher v1.0.0-rc.1 "$target_sha" publish
unset FAKE_GH_MUTATE_ASSETS_ON_GET
cp "$FAKE_GH_STATE/assets.publish-good.json" "$FAKE_GH_STATE/assets.json"
rm -f "$FAKE_GH_STATE/asset-get-count"

# Workflow-generated metadata requires the stable Actions bot and, when
# exposed by the API, the pinned GitHub Actions integration.
reset_matching_release
jq '
  map(
    if .name == "release-bom.json" then .uploader.id=999
    else .
    end
  )
' "$FAKE_GH_STATE/assets.publish-good.json" >"$FAKE_GH_STATE/assets.json"
must_fail run_publisher v1.0.0-rc.1 "$target_sha" publish
mkdir "$temp_root/stranded-wrong-actions-actor-fetch"
must_fail run_fetcher "$temp_root/stranded-wrong-actions-actor-fetch"
reset_matching_release
jq '
  map(
    if .name == "release-bom.json" then
      .performed_via_github_app.id=999
    else .
    end
  )
' "$FAKE_GH_STATE/assets.publish-good.json" >"$FAKE_GH_STATE/assets.json"
must_fail run_publisher v1.0.0-rc.1 "$target_sha" publish
mkdir "$temp_root/stranded-wrong-actions-app-fetch"
must_fail run_fetcher "$temp_root/stranded-wrong-actions-app-fetch"
cp "$FAKE_GH_STATE/assets.publish-good.json" "$FAKE_GH_STATE/assets.json"
reset_matching_release
uploads_before_publish=$(<"$FAKE_GH_STATE/uploads")
run_publisher v1.0.0-rc.1 "$target_sha" publish >/dev/null
jq -e \
  '.draft == false and
   .immutable == true and
   .published_at != null' \
  "$FAKE_GH_STATE/release.json" >/dev/null
test "$(<"$FAKE_GH_STATE/immutable-token")" = \
  "$TOPIAFORGE_GOVERNANCE_AUDIT_TOKEN"
test "$(<"$FAKE_GH_STATE/uploads")" = "$uploads_before_publish"

# Published fetches retain the split uploader policy for public metadata.
mkdir "$temp_root/published-fetch"
run_fetcher "$temp_root/published-fetch" >/dev/null
jq '
  map(
    if .name == "release-bom.json" then .uploader.type="User"
    else .
    end
  )
' "$FAKE_GH_STATE/assets.publish-good.json" >"$FAKE_GH_STATE/assets.json"
mkdir "$temp_root/wrong-workflow-uploader-fetch"
must_fail run_fetcher "$temp_root/wrong-workflow-uploader-fetch"
cp "$FAKE_GH_STATE/assets.publish-good.json" "$FAKE_GH_STATE/assets.json"

run_publisher v1.0.0-rc.1 "$target_sha" publish >/dev/null
test "$(<"$FAKE_GH_STATE/uploads")" = "$uploads_before_publish"
must_fail run_publisher v1.0.0-rc.1 "$target_sha"

# Published-byte drift and non-immutable publication both fail closed.
jq '.[0].size += 1' "$FAKE_GH_STATE/assets.json" \
  >"$FAKE_GH_STATE/assets.tmp"
mv "$FAKE_GH_STATE/assets.tmp" "$FAKE_GH_STATE/assets.json"
must_fail run_publisher v1.0.0-rc.1 "$target_sha" publish
cp "$FAKE_GH_STATE/assets.publish-good.json" "$FAKE_GH_STATE/assets.json"

reset_matching_release
export FAKE_GH_IMMUTABLE_ENABLED=false
must_fail run_publisher v1.0.0-rc.1 "$target_sha" publish
jq -e '.draft == true' "$FAKE_GH_STATE/release.json" >/dev/null
export FAKE_GH_IMMUTABLE_ENABLED=true

reset_matching_release
export FAKE_GH_PUBLISHED_IMMUTABLE=false
must_fail run_publisher v1.0.0-rc.1 "$target_sha" publish
export FAKE_GH_PUBLISHED_IMMUTABLE=true

# Stable releases retain an explicit false prerelease state.
rm -f \
  "$FAKE_GH_STATE/release.json" \
  "$FAKE_GH_STATE/assets.json" \
  "$FAKE_GH_STATE/uploads"
rm -rf "$FAKE_GH_STATE/asset-content"
run_publisher_with_flag v1.0.2 "$target_sha" false >/dev/null
jq -e \
  '.draft == true and
   .prerelease == false and
   .tag_name == "v1.0.2"' \
  "$FAKE_GH_STATE/release.json" >/dev/null

echo "Release staging, finalization, and immutable-rerun regression tests passed."
