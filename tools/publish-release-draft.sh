#!/usr/bin/env bash
set -euo pipefail

usage() {
  echo "Usage: publish-release-draft.sh <owner/repo> <tag> <target-sha> <title> <notes-file> <assets-dir> <prerelease> [draft|publish]" >&2
  exit 64
}

[[ $# -ge 7 && $# -le 8 ]] || usage
repository=$1
tag=$2
target_sha=$3
title=$4
notes_file=$5
assets_dir=$6
prerelease=$7
mode=${8:-draft}
script_dir=$(CDPATH='' cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
repository_root=$(CDPATH='' cd -- "$script_dir/.." && pwd)
governance_policy="$repository_root/.github/repository-governance.json"
release_policy="$repository_root/release/release-policy.json"

[[ $repository =~ ^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$ ]] || usage
[[ $tag =~ ^v[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?$ ]] || usage
[[ $target_sha =~ ^[0-9a-f]{40,64}$ ]] || usage
[[ $prerelease == true || $prerelease == false ]] || usage
[[ $mode == draft || $mode == publish ]] || usage
[[ -n ${GH_TOKEN:-} ]] || {
  echo "GH_TOKEN is required for release reconciliation." >&2
  exit 1
}
if [[ $mode == publish && -z ${TOPIAFORGE_GOVERNANCE_AUDIT_TOKEN:-} ]]; then
  echo "TOPIAFORGE_GOVERNANCE_AUDIT_TOKEN is required for immutable-release verification." >&2
  exit 1
fi
tag_is_prerelease=false
[[ $tag == *-* ]] && tag_is_prerelease=true
[[ $prerelease == "$tag_is_prerelease" ]] || {
  echo "Prerelease flag $prerelease does not match release tag $tag." >&2
  exit 1
}
[[ -f $notes_file && -s $notes_file ]] || {
  echo "Release notes are missing or empty: $notes_file" >&2
  exit 1
}
[[ -d $assets_dir && ! -L $assets_dir ]] || {
  echo "Assets directory must be a real directory: $assets_dir" >&2
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
declare -A workflow_generated_assets
while IFS= read -r generated_name; do
  generated_name=${generated_name%$'\r'}
  workflow_generated_assets[$generated_name]=1
done < <(jq -r '.[]' <<<"$workflow_generated_json")

# The handoff asset set follows artifactPolicy rather than a hard-coded platform
# list, which stops this script demanding a Linux archive the Windows-only RC1
# build never produces. Restoring Linux takes more than an artifactPolicy edit:
# release_policy.dart pins platformArchives to {TopiaForge-windows-x64.zip} and
# release_handoff_contract.dart pins targetPlatforms to {windows-x64}. Both must
# be lifted with the policy. See P0-LINUX-01 in docs/LaunchBlockers.md.
required_handoff_assets=(
  release-handoff-v1.json
  release-handoff-v1.json.p7s
)
while IFS= read -r policy_archive; do
  bundle_target=${policy_archive#TopiaForge-}
  bundle_target=${bundle_target%.zip}
  required_handoff_assets+=(
    "$policy_archive"
    "release-platform-bundle-v1-$bundle_target.json"
  )
done < <(jq -er '
  .artifactPolicy.platformArchives |
  select(type == "array" and length > 0) | sort | .[]
' "$release_policy")
for required in "${required_handoff_assets[@]}"; do
  [[ -f $assets_dir/$required && -s $assets_dir/$required ]] || {
    echo "Required local handoff asset is missing: $required" >&2
    exit 1
  }
done
if [[ $mode == publish ]]; then
  for required in \
    release-bom.json \
    release-sbom.spdx.json \
    topiaforge-update-v1.json \
    topiaforge-update-v1.json.sig \
    SHA256SUMS; do
    [[ -f $assets_dir/$required && -s $assets_dir/$required ]] || {
      echo "Required final release metadata is missing: $required" >&2
      exit 1
    }
  done
  jq -e \
    '.distributable == true and
     (.blockingReasons | type == "array" and length == 0)' \
    "$assets_dir/release-bom.json" >/dev/null || {
    echo "Release BOM is non-distributable or has unresolved blocking reasons." >&2
    exit 1
  }
  (
    cd "$assets_dir"
    sha256sum --check --strict SHA256SUMS
  )
fi

mapfile -d '' local_paths < <(
  find "$assets_dir" -mindepth 1 -maxdepth 1 -type f -print0 | sort -z
)
[[ ${#local_paths[@]} -ge ${#required_handoff_assets[@]} ]] || {
  echo "No complete release handoff was staged." >&2
  exit 1
}
if find "$assets_dir" -mindepth 1 -maxdepth 1 ! -type f -print -quit | grep -q .; then
  echo "Release staging contains a directory, link, or special entry." >&2
  exit 1
fi

declare -A local_sha local_size local_path
for path in "${local_paths[@]}"; do
  name=$(basename "$path")
  [[ $name =~ ^[A-Za-z0-9][A-Za-z0-9._+-]*$ ]] || {
    echo "Unsafe release asset name: $name" >&2
    exit 1
  }
  [[ -z ${local_path[$name]+x} ]] || {
    echo "Duplicate release asset basename: $name" >&2
    exit 1
  }
  local_path[$name]=$path
  local_sha[$name]=$(sha256sum "$path" | awk '{print $1}')
  local_size[$name]=$(wc -c <"$path" | tr -d ' ')
done
if [[ $mode == publish ]]; then
  expected_checksum_names=$(mktemp)
  actual_checksum_names=$(mktemp)
  for name in "${!local_path[@]}"; do
    [[ $name == SHA256SUMS ]] || printf '%s\n' "$name"
  done | sort >"$expected_checksum_names"
  sed -nE \
    's/^[0-9a-f]{64} [ *]([A-Za-z0-9][A-Za-z0-9._+-]*)$/\1/p' \
    "$assets_dir/SHA256SUMS" | sort >"$actual_checksum_names"
  if ! diff -u "$expected_checksum_names" "$actual_checksum_names"; then
    rm -f "$expected_checksum_names" "$actual_checksum_names"
    echo "SHA256SUMS does not cover the exact final release asset set." >&2
    exit 1
  fi
  rm -f "$expected_checksum_names" "$actual_checksum_names"
fi

gh_api() {
  gh api \
    -H 'Accept: application/vnd.github+json' \
    -H 'X-GitHub-Api-Version: 2026-03-10' \
    "$@"
}

verify_release_fields() {
  local file=$1
  local notes=$2
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
    "$file" >/dev/null || {
    echo "Release metadata or pinned staging author differs from the reviewed candidate." >&2
    exit 1
  }
}

verify_asset_uploaders() {
  local file=$1
  jq -e \
    --arg mode "$mode" \
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
      all(.[];
        .name as $name |
        if (
          $mode == "publish" and
          ($generated | index($name)) != null
        ) then workflow_uploader
        else staging_uploader
        end
      )
    ' "$file" >/dev/null || {
    echo "Release asset inventory contains an untrusted uploader." >&2
    exit 1
  }
}

verify_immutable_setting() {
  local immutable
  local immutable_token=${TOPIAFORGE_GOVERNANCE_AUDIT_TOKEN:-$GH_TOKEN}
  immutable=$(GH_TOKEN="$immutable_token" \
    gh_api "repos/$repository/immutable-releases") || {
    echo "Repository release immutability must be enabled before publication." >&2
    exit 1
  }
  jq -e '.enabled == true' <<<"$immutable" >/dev/null || {
    echo "Repository release immutability must be enabled before publication." >&2
    exit 1
  }
}

reconcile_assets() {
  local release_id=$1
  local allow_mutation=$2
  local assets_file
  local -A present
  assets_file=$(mktemp)
  gh_api "repos/$repository/releases/$release_id/assets?per_page=100" >"$assets_file"
  jq -e 'type == "array" and length < 100' "$assets_file" >/dev/null || {
    rm -f "$assets_file"
    echo "Release asset inventory is invalid or exceeds the supported bound." >&2
    exit 1
  }
  verify_asset_uploaders "$assets_file"
  while IFS= read -r encoded; do
    asset=$(tr -d '\r' <<<"$encoded" | base64 --decode)
    name=$(jq -er '.name' <<<"$asset")
    state=$(jq -er '.state' <<<"$asset")
    id=$(jq -er '.id | select(type == "number" and . > 0)' <<<"$asset")
    [[ $name =~ ^[A-Za-z0-9][A-Za-z0-9._+-]*$ ]] || {
      rm -f "$assets_file"
      echo "Release contains an unsafe asset name: $name" >&2
      exit 1
    }
    [[ -n ${local_path[$name]+x} ]] || {
      rm -f "$assets_file"
      echo "Release contains unexpected asset: $name" >&2
      exit 1
    }
    [[ -z ${present[$name]+x} ]] || {
      rm -f "$assets_file"
      echo "Release contains duplicate asset: $name" >&2
      exit 1
    }
    if [[ $state == starter && $allow_mutation == true ]]; then
      if [[ $mode == draft ||
            -n ${workflow_generated_assets[$name]+x} ]]; then
        gh_api --method DELETE "repos/$repository/releases/assets/$id"
        continue
      fi
      rm -f "$assets_file"
      echo "Protected publication refuses to delete an incomplete admin-staged asset: $name" >&2
      exit 1
    fi
    digest=$(jq -r '.digest // ""' <<<"$asset")
    size=$(jq -r '.size' <<<"$asset")
    [[ $state == uploaded &&
       $digest == "sha256:${local_sha[$name]}" &&
       $size == "${local_size[$name]}" ]] || {
      rm -f "$assets_file"
      echo "Release asset differs from local immutable bytes: $name" >&2
      exit 1
    }
    present[$name]=1
  done < <(jq -r '.[] | @base64' "$assets_file")
  rm -f "$assets_file"

  if [[ $mode == publish ]]; then
    for name in "${!local_path[@]}"; do
      if [[ -z ${present[$name]+x} &&
            -z ${workflow_generated_assets[$name]+x} ]]; then
        echo "Protected publication is missing admin-staged asset: $name" >&2
        exit 1
      fi
    done
  fi
  for name in "${!local_path[@]}"; do
    if [[ -z ${present[$name]+x} ]]; then
      [[ $allow_mutation == true ]] || {
        echo "Published release is missing expected asset: $name" >&2
        exit 1
      }
      gh release upload "$tag" "${local_path[$name]}" --repo "$repository"
    fi
  done

  assets_file=$(mktemp)
  gh_api "repos/$repository/releases/$release_id/assets?per_page=100" >"$assets_file"
  verify_asset_uploaders "$assets_file"
  [[ $(jq 'length' "$assets_file") -eq ${#local_paths[@]} ]] || {
    rm -f "$assets_file"
    echo "Final release asset count is incomplete." >&2
    exit 1
  }
  while IFS= read -r encoded; do
    asset=$(tr -d '\r' <<<"$encoded" | base64 --decode)
    name=$(jq -er '.name' <<<"$asset")
    state=$(jq -er '.state' <<<"$asset")
    digest=$(jq -r '.digest // ""' <<<"$asset")
    size=$(jq -r '.size' <<<"$asset")
    [[ -n ${local_sha[$name]+x} &&
       $state == uploaded &&
       $digest == "sha256:${local_sha[$name]}" &&
       $size == "${local_size[$name]}" ]] || {
      rm -f "$assets_file"
      echo "Final release verification failed for $name." >&2
      exit 1
    }
  done < <(jq -r '.[] | @base64' "$assets_file")
  rm -f "$assets_file"
}

# A GitHub release POST creates the tag when it is absent. Prove that the
# protected, annotated remote tag already exists at the reviewed commit before
# making any release API call.
"$script_dir/verify-release-tag.sh" "$tag" "$target_sha" origin >/dev/null

notes=$(<"$notes_file")
release_file=$(mktemp)
trap 'rm -f "$release_file"' EXIT
if gh_api "repos/$repository/releases/tags/$tag" >"$release_file" 2>/dev/null; then
  :
elif [[ $mode == draft ]]; then
  repository_access=$(gh_api "repos/$repository")
  jq -e '.permissions.admin == true' <<<"$repository_access" >/dev/null || {
    echo "Creating a release draft requires repository-admin authentication." >&2
    exit 1
  }
  payload=$(jq -n \
    --arg tag "$tag" \
    --arg name "$title" \
    --arg body "$notes" \
    --argjson prerelease "$prerelease" \
    '{tag_name:$tag,name:$name,body:$body,draft:true,prerelease:$prerelease}')
  gh_api --method POST "repos/$repository/releases" --input - \
    <<<"$payload" >"$release_file"
else
  echo "The admin-staged draft for $tag does not exist." >&2
  exit 1
fi

verify_release_fields "$release_file" "$notes"
release_id=$(jq -er '.id | select(type == "number" and . > 0)' "$release_file")
draft=$(jq -r '.draft' "$release_file")

if [[ $mode == draft && $draft != true ]]; then
  echo "Refusing to mutate already-published release $tag." >&2
  exit 1
fi
if [[ $mode == publish ]]; then
  verify_immutable_setting
fi

"$script_dir/verify-release-tag.sh" "$tag" "$target_sha" origin >/dev/null
reconcile_assets "$release_id" "$draft"
"$script_dir/verify-release-tag.sh" "$tag" "$target_sha" origin >/dev/null

if [[ $mode == draft ]]; then
  echo "Prepared verified admin-staged draft release $tag at $target_sha."
  echo "No tag was created and the draft was not published."
  exit 0
fi

if [[ $draft == true ]]; then
  # GitHub does not support conditional requests for this unsafe PATCH. Narrow
  # the unavoidable server-side race to this final transition by re-reading the
  # draft metadata and every asset after all verification/attestation work.
  gh_api "repos/$repository/releases/tags/$tag" >"$release_file"
  verify_release_fields "$release_file" "$notes"
  [[ $(jq -er '.id' "$release_file") == "$release_id" &&
     $(jq -er '.draft' "$release_file") == true ]] || {
    echo "The release draft changed before the publication transition." >&2
    exit 1
  }
  "$script_dir/verify-release-tag.sh" "$tag" "$target_sha" origin >/dev/null
  reconcile_assets "$release_id" false
  jq -n '{draft:false}' |
    gh_api --method PATCH "repos/$repository/releases/$release_id" --input - \
      >"$release_file"
fi

# Fetch fresh state after the single publication transition. When immutable
# releases are enabled, this also proves GitHub locked the asset set and tag.
gh_api "repos/$repository/releases/tags/$tag" >"$release_file"
verify_release_fields "$release_file" "$notes"
jq -e \
  '.draft == false and
   .immutable == true and
   (.published_at | type == "string" and length > 0)' \
  "$release_file" >/dev/null || {
  echo "GitHub did not publish $tag as an immutable release." >&2
  exit 1
}
"$script_dir/verify-release-tag.sh" "$tag" "$target_sha" origin >/dev/null
reconcile_assets "$release_id" false

echo "Verified immutable published release $tag at $target_sha."
