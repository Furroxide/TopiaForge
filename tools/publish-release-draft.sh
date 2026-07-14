#!/usr/bin/env bash
set -euo pipefail

usage() {
  echo "Usage: publish-release-draft.sh <owner/repo> <tag> <target-sha> <title> <notes-file> <assets-dir>" >&2
  exit 64
}

[[ $# -eq 6 ]] || usage
repository=$1
tag=$2
target_sha=$3
title=$4
notes_file=$5
assets_dir=$6
script_dir=$(CDPATH='' cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)

[[ $repository =~ ^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$ ]] || usage
[[ $tag =~ ^v[0-9]+\.[0-9]+\.[0-9]+([.-][0-9A-Za-z.-]+)?$ ]] || usage
[[ $target_sha =~ ^[0-9a-f]{40,64}$ ]] || usage
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

# A GitHub release POST creates the tag when it is absent. Prove that the
# protected, annotated remote tag already exists at the reviewed commit before
# making any release API call.
"$script_dir/verify-release-tag.sh" "$tag" "$target_sha" origin

for required in release-bom.json release-sbom.spdx.json SHA256SUMS; do
  [[ -f $assets_dir/$required && -s $assets_dir/$required ]] || {
    echo "Required release metadata is missing: $required" >&2
    exit 1
  }
done
jq -e '.distributable == true and (.blockingReasons | type == "array" and length == 0)' \
  "$assets_dir/release-bom.json" >/dev/null || {
  echo "Release BOM is non-distributable or has unresolved blocking reasons." >&2
  exit 1
}
(
  cd "$assets_dir"
  sha256sum --check --strict SHA256SUMS
)

mapfile -d '' local_paths < <(
  find "$assets_dir" -mindepth 1 -maxdepth 1 -type f -print0 | sort -z
)
[[ ${#local_paths[@]} -gt 3 ]] || {
  echo "No release payload assets were staged." >&2
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
  local_size[$name]=$(wc -c < "$path" | tr -d ' ')
done

notes=$(<"$notes_file")
release_file=$(mktemp)
assets_file=$(mktemp)
trap 'rm -f "$release_file" "$assets_file"' EXIT

if gh api "repos/$repository/releases/tags/$tag" >"$release_file" 2>/dev/null; then
  [[ $(jq -r '.draft' "$release_file") == true ]] || {
    echo "Refusing to mutate already-published release $tag." >&2
    exit 1
  }
else
  payload=$(jq -n \
    --arg tag "$tag" \
    --arg name "$title" \
    --arg body "$notes" \
    '{tag_name:$tag,name:$name,body:$body,draft:true,prerelease:false}')
  gh api --method POST "repos/$repository/releases" --input - \
    <<<"$payload" >"$release_file"
fi

[[ $(jq -r '.draft' "$release_file") == true ]] || exit 1
[[ $(jq -r '.tag_name' "$release_file") == "$tag" ]] || {
  echo "Draft tag differs from the reviewed catalog." >&2
  exit 1
}
# Re-fetch after draft reconciliation so a tag change cannot be hidden by a
# stale local ref. Protected-tag rules are the repository-side mutation guard.
"$script_dir/verify-release-tag.sh" "$tag" "$target_sha" origin
[[ $(jq -r '.name' "$release_file") == "$title" ]] || {
  echo "Draft title differs from the reviewed catalog." >&2
  exit 1
}
[[ $(jq -r '.body' "$release_file") == "$notes" ]] || {
  echo "Draft notes differ from the reviewed catalog." >&2
  exit 1
}
release_id=$(jq -r '.id' "$release_file")

gh api "repos/$repository/releases/$release_id/assets?per_page=100" >"$assets_file"
while IFS= read -r encoded; do
  asset=$(base64 --decode <<<"$encoded")
  name=$(jq -r '.name' <<<"$asset")
  state=$(jq -r '.state' <<<"$asset")
  id=$(jq -r '.id' <<<"$asset")
  [[ -n ${local_path[$name]+x} ]] || {
    echo "Draft contains unexpected asset: $name" >&2
    exit 1
  }
  if [[ $state == starter ]]; then
    gh api --method DELETE "repos/$repository/releases/assets/$id"
    continue
  fi
  digest=$(jq -r '.digest // ""' <<<"$asset")
  size=$(jq -r '.size' <<<"$asset")
  [[ $state == uploaded && $digest == "sha256:${local_sha[$name]}" && $size == "${local_size[$name]}" ]] || {
    echo "Draft asset differs from local immutable bytes: $name" >&2
    exit 1
  }
  unset 'local_path[$name]'
done < <(jq -r '.[] | @base64' "$assets_file")

for name in "${!local_path[@]}"; do
  gh release upload "$tag" "${local_path[$name]}"
done

gh api "repos/$repository/releases/$release_id/assets?per_page=100" >"$assets_file"
[[ $(jq 'length' "$assets_file") -eq ${#local_paths[@]} ]] || {
  echo "Final draft asset count is incomplete." >&2
  exit 1
}
while IFS= read -r encoded; do
  asset=$(base64 --decode <<<"$encoded")
  name=$(jq -r '.name' <<<"$asset")
  state=$(jq -r '.state' <<<"$asset")
  digest=$(jq -r '.digest // ""' <<<"$asset")
  size=$(jq -r '.size' <<<"$asset")
  [[ -n ${local_sha[$name]+x} && $state == uploaded && $digest == "sha256:${local_sha[$name]}" && $size == "${local_size[$name]}" ]] || {
    echo "Final draft verification failed for $name." >&2
    exit 1
  }
done < <(jq -r '.[] | @base64' "$assets_file")

echo "Prepared verified draft release $tag at $target_sha."
echo "No tag was created and the draft was not published."
