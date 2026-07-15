#!/usr/bin/env bash
set -euo pipefail

usage() {
  echo "Usage: verify-release-candidate.sh <owner/repo> <tag> <version> <target-sha>" >&2
  exit 64
}

[[ $# -eq 4 ]] || usage
repository=$1
tag=$2
version=$3
target_sha=$4

[[ $repository =~ ^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$ ]] || usage
[[ $version =~ ^[0-9]+\.[0-9]+\.[0-9]+([.-][0-9A-Za-z.-]+)?$ ]] || usage
[[ $tag == "v$version" ]] || {
  echo "Release tag '$tag' does not match catalog version '$version'." >&2
  exit 1
}
[[ $target_sha =~ ^[0-9a-f]{40,64}$ ]] || usage
[[ -n ${GH_TOKEN:-} ]] || {
  echo "GH_TOKEN is required for release evidence verification." >&2
  exit 1
}
for command in gh git jq; do
  command -v "$command" >/dev/null || {
    echo "$command is required." >&2
    exit 1
  }
done

gh_api() {
  gh api \
    -H 'Accept: application/vnd.github+json' \
    -H 'X-GitHub-Api-Version: 2026-03-10' \
    "$@"
}

tag_ref="refs/tags/$tag"
[[ $(git cat-file -t "$tag_ref" 2>/dev/null || true) == tag ]] || {
  echo "Release tag $tag must be an annotated tag object." >&2
  exit 1
}
[[ $(git rev-parse --verify "$tag_ref^{}") == "$target_sha" ]] || {
  echo "Release tag $tag does not peel to approved commit $target_sha." >&2
  exit 1
}
[[ $(git cat-file -t "$target_sha" 2>/dev/null || true) == commit ]] || {
  echo "Release target $target_sha is not a commit." >&2
  exit 1
}

GIT_TERMINAL_PROMPT=0 git -c credential.helper= fetch --quiet --no-tags \
  origin refs/heads/main:refs/remotes/origin/main
main_sha=$(git rev-parse --verify refs/remotes/origin/main)
[[ $main_sha == "$target_sha" ]] || {
  echo "Release target $target_sha is not the current protected main tip $main_sha." >&2
  exit 1
}

ref_payload=$(gh_api "repos/$repository/git/ref/tags/$tag")
tag_object_sha=$(jq -er \
  'select(.object.type == "tag") | .object.sha' <<<"$ref_payload") || {
  echo "GitHub does not report $tag as an annotated tag." >&2
  exit 1
}
tag_payload=$(gh_api "repos/$repository/git/tags/$tag_object_sha")
jq -e \
  --arg tag "$tag" \
  --arg target "$target_sha" \
  '.tag == $tag and
   .object.type == "commit" and
   .object.sha == $target and
   .verification.verified == true and
   .verification.reason == "valid"' \
  <<<"$tag_payload" >/dev/null || {
  echo "GitHub did not verify a valid signature and exact target for $tag." >&2
  exit 1
}

pulls=$(gh_api "repos/$repository/commits/$target_sha/pulls?per_page=100")
release_pr=$(jq -ec \
  --arg repository "$repository" \
  --arg head "release/$version" \
  --arg target "$target_sha" \
  '[.[] | select(
    .base.ref == "main" and
    .head.repo.full_name == $repository and
    .head.ref == $head and
    .merged_at != null and
    .merge_commit_sha == $target
  )] | select(length == 1) | .[0]' <<<"$pulls") || {
  echo "Expected exactly one merged release/$version pull request for $target_sha." >&2
  exit 1
}
release_head_sha=$(jq -er '.head.sha' <<<"$release_pr")

require_check() {
  local sha=$1
  local name=$2
  local checks
  checks=$(gh_api "repos/$repository/commits/$sha/check-runs?per_page=100")
  jq -e \
    --arg name "$name" \
    --arg sha "$sha" \
    'any(.check_runs[];
      .name == $name and
      .head_sha == $sha and
      .status == "completed" and
      .conclusion == "success" and
      .app.id == 15368)' <<<"$checks" >/dev/null || {
    echo "Missing successful GitHub Actions check '$name' on $sha." >&2
    exit 1
  }
}

require_check "$release_head_sha" "Required / Release packages"
require_check "$target_sha" "Required / Unity validation"
require_check "$target_sha" "Required / Game SDK acceptance"

if [[ -n ${GITHUB_OUTPUT:-} ]]; then
  release_pr_number=$(jq -er '.number' <<<"$release_pr")
  {
    echo "release_pr_number=$release_pr_number"
    echo "release_head_sha=$release_head_sha"
  } >>"$GITHUB_OUTPUT"
fi

echo "Verified release $tag at $target_sha with exact release, Unity, and live-game evidence."
