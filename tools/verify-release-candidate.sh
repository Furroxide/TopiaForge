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
release_pr_number=$(jq -er '.number' <<<"$release_pr")
release_head_sha=$(jq -er '.head.sha' <<<"$release_pr")
read -r -a target_commit_line <<<"$(
  git rev-list --parents -n 1 "$target_sha"
)"
[[ ${#target_commit_line[@]} -eq 3 &&
   ${target_commit_line[0]} == "$target_sha" &&
   ${target_commit_line[2]} == "$release_head_sha" ]] || {
  echo "Release target $target_sha is not the exact merge of checked head $release_head_sha." >&2
  exit 1
}
release_head_type=$(git cat-file -t "$release_head_sha" 2>/dev/null || true)
[[ $release_head_type == commit ]] || {
  echo "Release PR head $release_head_sha is not available as a commit." >&2
  exit 1
}
target_tree=$(git rev-parse --verify "$target_sha^{tree}")
release_head_tree=$(git rev-parse --verify "$release_head_sha^{tree}")
[[ $target_tree == "$release_head_tree" ]] || {
  echo "Release target tree differs from the hosted-check release head." >&2
  exit 1
}

declare -A required_check_workflow=(
  ["Required / PR policy"]=".github/workflows/pr-policy.yml"
  ["Required / CI validation"]=".github/workflows/ci.yml"
  ["Required / Unity source validation"]=".github/workflows/unity-pr-artifacts.yml"
  ["Required / Registry validation"]=".github/workflows/validate-registry.yml"
  ["Required / Dependency review"]=".github/workflows/dependency-review.yml"
  ["Required / Release packages"]=".github/workflows/release-dry-run.yml"
)
declare -A required_check_workflow_name=(
  ["Required / PR policy"]="PR policy"
  ["Required / CI validation"]="CI"
  ["Required / Unity source validation"]="Unity Source Validation"
  ["Required / Registry validation"]="Validate registry entries"
  ["Required / Dependency review"]="Dependency review"
  ["Required / Release packages"]="Release package dry run"
)
declare -A required_check_event=(
  ["Required / PR policy"]="pull_request"
  ["Required / CI validation"]="pull_request"
  ["Required / Unity source validation"]="pull_request"
  ["Required / Registry validation"]="pull_request"
  ["Required / Dependency review"]="pull_request"
  ["Required / Release packages"]="push"
)
required_hosted_checks=(
  "Required / PR policy"
  "Required / CI validation"
  "Required / Unity source validation"
  "Required / Registry validation"
  "Required / Dependency review"
  "Required / Release packages"
)
checks_payload=$(
  gh_api \
    "repos/$repository/commits/$release_head_sha/check-runs?filter=all&per_page=100"
)

require_check() {
  local sha=$1
  local name=$2
  local expected_workflow=${required_check_workflow[$name]}
  local expected_workflow_name=${required_check_workflow_name[$name]}
  local expected_event=${required_check_event[$name]}
  local expected_branch="release/$version"
  local latest
  local details_url
  local run_id
  local job_id
  local run
  local run_attempt
  local workflow_id
  local workflow
  local attempt_jobs

  latest=$(jq -ec \
    --arg name "$name" \
    --arg sha "$sha" \
    '[.check_runs[] | select(
      .name == $name and
      .head_sha == $sha and
      .app.id == 15368
    )] | sort_by(.id) | last // empty' <<<"$checks_payload") || {
    echo "Missing GitHub Actions check '$name' on $sha." >&2
    exit 1
  }
  jq -e \
    '.status == "completed" and .conclusion == "success"' \
    <<<"$latest" >/dev/null || {
    echo "Newest GitHub Actions check '$name' on $sha is not successful." >&2
    exit 1
  }

  details_url=$(jq -er '.details_url | select(type == "string")' <<<"$latest") || {
    echo "GitHub Actions check '$name' has no job provenance URL." >&2
    exit 1
  }
  if [[ $details_url =~ ^https://github\.com/${repository}/actions/runs/([1-9][0-9]*)/job/([1-9][0-9]*)$ ]]; then
    run_id=${BASH_REMATCH[1]}
    job_id=${BASH_REMATCH[2]}
  else
    echo "GitHub Actions check '$name' has an unexpected provenance URL." >&2
    exit 1
  fi

  run=$(gh_api "repos/$repository/actions/runs/$run_id")
  jq -e \
    --arg repository "$repository" \
    --arg workflow "$expected_workflow" \
    --arg event "$expected_event" \
    --arg sha "$sha" \
    --arg branch "$expected_branch" \
    --argjson run_id "$run_id" \
    '.id == $run_id and
     .repository.full_name == $repository and
     .head_repository.full_name == $repository and
     .path == $workflow and
     .event == $event and
     .head_sha == $sha and
     .head_branch == $branch and
     .status == "completed" and
     .conclusion == "success" and
     (.workflow_id | type == "number") and
     (.run_attempt | type == "number" and . >= 1)' \
    <<<"$run" >/dev/null || {
    echo "GitHub Actions run provenance is invalid for '$name'." >&2
    exit 1
  }
  run_attempt=$(jq -er '.run_attempt' <<<"$run")
  workflow_id=$(jq -er '.workflow_id' <<<"$run")

  if [[ $expected_event == pull_request ]]; then
    jq -e \
      --arg repository "$repository" \
      --arg branch "$expected_branch" \
      --arg sha "$sha" \
      --argjson release_pr_number "$release_pr_number" \
      '(.pull_requests | type == "array" and length == 1) and
       .pull_requests[0].number == $release_pr_number and
       .pull_requests[0].url ==
         ("https://api.github.com/repos/" + $repository + "/pulls/" +
           ($release_pr_number | tostring)) and
       .pull_requests[0].head.ref == $branch and
       .pull_requests[0].head.sha == $sha and
       .pull_requests[0].head.repo.url ==
         ("https://api.github.com/repos/" + $repository) and
       .pull_requests[0].base.ref == "main" and
       .pull_requests[0].base.repo.url ==
         ("https://api.github.com/repos/" + $repository)' \
      <<<"$run" >/dev/null || {
      echo "GitHub Actions run '$name' is not associated with release PR #$release_pr_number targeting main." >&2
      exit 1
    }
  fi

  workflow=$(gh_api "repos/$repository/actions/workflows/$workflow_id")
  jq -e \
    --arg repository "$repository" \
    --arg path "$expected_workflow" \
    --arg name "$expected_workflow_name" \
    --argjson workflow_id "$workflow_id" \
    '.id == $workflow_id and
     .path == $path and
     .name == $name and
     .state == "active" and
     .html_url == ("https://github.com/" + $repository + "/actions/workflows/" +
       ($path | split("/") | last))' \
    <<<"$workflow" >/dev/null || {
    echo "GitHub Actions workflow identity is invalid for '$name'." >&2
    exit 1
  }

  attempt_jobs=$(
    gh_api \
      "repos/$repository/actions/runs/$run_id/attempts/$run_attempt/jobs?per_page=100"
  )
  jq -e \
    --arg name "$name" \
    --arg sha "$sha" \
    --arg details_url "$details_url" \
    --argjson job_id "$job_id" \
    --argjson run_id "$run_id" \
    --argjson run_attempt "$run_attempt" \
    '[.jobs[] | select(.id == $job_id)] |
     length == 1 and
     .[0].name == $name and
     .[0].run_id == $run_id and
     .[0].run_attempt == $run_attempt and
     .[0].head_sha == $sha and
     .[0].html_url == $details_url and
     .[0].status == "completed" and
     .[0].conclusion == "success"' \
    <<<"$attempt_jobs" >/dev/null || {
    echo "GitHub Actions check '$name' is not from the successful current run attempt." >&2
    exit 1
  }
}

for check_name in "${required_hosted_checks[@]}"; do
  require_check "$release_head_sha" "$check_name"
done

if [[ -n ${GITHUB_OUTPUT:-} ]]; then
  {
    echo "release_pr_number=$release_pr_number"
    echo "release_head_sha=$release_head_sha"
  } >>"$GITHUB_OUTPUT"
fi

echo "Verified release $tag at $target_sha with the exact release PR and hosted branch checks."
