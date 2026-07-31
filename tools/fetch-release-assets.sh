#!/usr/bin/env bash
set -euo pipefail

usage() {
  echo "Usage: fetch-release-assets.sh <owner/repo> <tag> <target-sha> <title> <notes-file> <destination> <prerelease>" >&2
  exit 64
}

[[ $# -eq 7 ]] || usage
repository=$1
tag=$2
target_sha=$3
title=$4
notes_file=$5
destination=$6
prerelease=$7
script_dir=$(CDPATH='' cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
repository_root=$(CDPATH='' cd -- "$script_dir/.." && pwd)
governance_policy="$repository_root/.github/repository-governance.json"
release_policy="$repository_root/release/release-policy.json"

[[ $repository =~ ^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$ ]] || usage
[[ $tag =~ ^v[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?$ ]] || usage
[[ $target_sha =~ ^[0-9a-f]{40,64}$ ]] || usage
[[ $prerelease == true || $prerelease == false ]] || usage
[[ -n ${GH_TOKEN:-} ]] || {
  echo "GH_TOKEN is required to fetch release assets." >&2
  exit 1
}
[[ -f $notes_file && -s $notes_file ]] || {
  echo "Release notes are missing or empty: $notes_file" >&2
  exit 1
}
for command in gh git jq sha256sum; do
  command -v "$command" >/dev/null || {
    echo "$command is required." >&2
    exit 1
  }
done
for policy_file in "$governance_policy" "$release_policy"; do
  [[ -f $policy_file && ! -L $policy_file ]] || {
    echo "Required release authority policy is missing or unsafe: $policy_file" >&2
    exit 1
  }
done
jq -e '
  .schema_version == 2 and
  .release_staging_principal == {
    login: "furroxide",
    actor_id: 221987073,
    type: "User"
  } and
  .release_workflow_principal == {
    login: "github-actions[bot]",
    actor_id: 41898282,
    type: "Bot"
  } and
  .github_actions_integration_id == 15368
' "$governance_policy" >/dev/null || {
  echo "Release mutation principals do not match the pinned governance contract." >&2
  exit 1
}
workflow_generated_json=$(jq -ec '
  .artifactPolicy.generatedMetadata |
  select(
    type == "array" and
    length > 0 and
    length == (unique | length) and
    all(.[];
      type == "string" and
      test("^[A-Za-z0-9][-A-Za-z0-9._+]*$")
    )
  )
' "$release_policy") || {
  echo "Workflow-generated release metadata policy is invalid." >&2
  exit 1
}

if [[ -e $destination && ! -d $destination ]] || [[ -L $destination ]]; then
  echo "Release destination must be a real directory: $destination" >&2
  exit 1
fi
mkdir -p "$destination"
if find "$destination" -mindepth 1 -maxdepth 1 -print -quit | grep -q .; then
  echo "Release destination must be empty: $destination" >&2
  exit 1
fi

gh_api() {
  gh api \
    -H 'Accept: application/vnd.github+json' \
    -H 'X-GitHub-Api-Version: 2026-03-10' \
    "$@"
}

verify_asset_uploaders() {
  local file=$1
  local release_state=$2
  jq -e \
    --arg release_state "$release_state" \
    --argjson generated "$workflow_generated_json" \
    '
      def staging_uploader:
        .uploader.login == "furroxide" and
        .uploader.id == 221987073 and
        .uploader.type == "User" and
        (
          (has("performed_via_github_app") | not) or
          .performed_via_github_app == null
        );
      def workflow_uploader:
        .uploader.login == "github-actions[bot]" and
        .uploader.id == 41898282 and
        .uploader.type == "Bot" and
        (
          (has("performed_via_github_app") | not) or
          .performed_via_github_app == null or
          (
            (.performed_via_github_app | type) == "object" and
            .performed_via_github_app.id == 15368
          )
        );
      type == "array" and
      ($release_state == "draft" or $release_state == "published") and
      all(.[];
        .name as $name |
        if ($generated | index($name)) != null then workflow_uploader
        else staging_uploader
        end
      )
    ' "$file" >/dev/null || {
    echo "Release asset inventory contains an untrusted uploader." >&2
    exit 1
  }
}

"$script_dir/verify-release-tag.sh" "$tag" "$target_sha" origin >/dev/null

release_file=$(mktemp)
assets_file=$(mktemp)
trap 'rm -f "$release_file" "$assets_file"' EXIT
gh_api "repos/$repository/releases/tags/$tag" >"$release_file" || {
  echo "The admin-staged release for $tag does not exist." >&2
  exit 1
}

notes=$(<"$notes_file")
jq -e \
  --arg tag "$tag" \
  --arg title "$title" \
  --arg body "$notes" \
  --argjson prerelease "$prerelease" \
  '.tag_name == $tag and
   .name == $title and
   .body == $body and
   .prerelease == $prerelease and
   (.draft | type == "boolean") and
   .author.login == "furroxide" and
   .author.id == 221987073 and
   .author.type == "User"' \
  "$release_file" >/dev/null || {
  echo "The staged release metadata or pinned author does not match the reviewed candidate." >&2
  exit 1
}

draft=$(jq -r '.draft' "$release_file")
release_state=draft
if [[ $draft == false ]]; then
  release_state=published
  jq -e \
    '.immutable == true and
     (.published_at | type == "string" and length > 0)' \
    "$release_file" >/dev/null || {
    echo "Published release $tag is not immutable." >&2
    exit 1
  }
fi
release_id=$(jq -er '.id | select(type == "number" and . > 0)' "$release_file")
gh_api "repos/$repository/releases/$release_id/assets?per_page=100" >"$assets_file"
jq -e 'type == "array" and length > 0 and length < 100' \
  "$assets_file" >/dev/null || {
  echo "Release $tag has an invalid or unsupported asset count." >&2
  exit 1
}
verify_asset_uploaders "$assets_file" "$release_state"

declare -A seen_names
starter_count=0
downloaded_count=0
while IFS= read -r encoded; do
  asset=$(tr -d '\r' <<<"$encoded" | base64 --decode)
  name=$(jq -er '.name' <<<"$asset")
  id=$(jq -er '.id | select(type == "number" and . > 0)' <<<"$asset")
  state=$(jq -er '.state' <<<"$asset")
  [[ $name =~ ^[A-Za-z0-9][A-Za-z0-9._+-]*$ ]] || {
    echo "Release contains an unsafe asset name: $name" >&2
    exit 1
  }
  [[ -z ${seen_names[$name]+x} ]] || {
    echo "Release contains duplicate asset name: $name" >&2
    exit 1
  }
  seen_names[$name]=1
  if [[ $state == starter && $draft == true ]]; then
    starter_count=$((starter_count + 1))
    continue
  fi
  [[ $state == uploaded ]] || {
    echo "Release asset $name is not completely uploaded." >&2
    exit 1
  }
  digest=$(jq -er '.digest' <<<"$asset")
  size=$(jq -er '.size | select(type == "number" and . > 0)' <<<"$asset")
  [[ $digest =~ ^sha256:([0-9a-f]{64})$ ]] || {
    echo "Release asset $name has no valid SHA-256 digest." >&2
    exit 1
  }
  path="$destination/$name"
  gh api \
    -H 'Accept: application/octet-stream' \
    -H 'X-GitHub-Api-Version: 2026-03-10' \
    "repos/$repository/releases/assets/$id" >"$path"
  [[ -f $path && ! -L $path ]] || {
    echo "Downloaded release asset is not a regular file: $name" >&2
    exit 1
  }
  actual_size=$(wc -c <"$path" | tr -d ' ')
  actual_sha=$(sha256sum "$path" | awk '{print $1}')
  [[ $actual_size == "$size" && "sha256:$actual_sha" == "$digest" ]] || {
    echo "Downloaded release asset differs from GitHub metadata: $name" >&2
    exit 1
  }
  downloaded_count=$((downloaded_count + 1))
done < <(jq -r '.[] | @base64' "$assets_file")

[[ $downloaded_count -gt 0 ]] || {
  echo "Release $tag contains no complete assets." >&2
  exit 1
}
"$script_dir/verify-release-tag.sh" "$tag" "$target_sha" origin >/dev/null

if [[ -n ${GITHUB_OUTPUT:-} ]]; then
  {
    echo "release_id=$release_id"
    echo "draft=$draft"
    echo "published=$([[ $draft == false ]] && echo true || echo false)"
    echo "downloaded_asset_count=$downloaded_count"
    echo "starter_asset_count=$starter_count"
  } >>"$GITHUB_OUTPUT"
fi

echo "Fetched $downloaded_count exact assets from the admin-staged $tag release."
