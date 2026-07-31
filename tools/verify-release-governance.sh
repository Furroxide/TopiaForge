#!/usr/bin/env bash
set -euo pipefail

usage() {
  echo "Usage: verify-release-governance.sh <owner/repo>" >&2
  exit 64
}

[[ $# -eq 1 ]] || usage
repository=$1
[[ $repository =~ ^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$ ]] || usage

script_dir=$(CDPATH='' cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
repository_root=$(git -C "$script_dir/.." rev-parse --show-toplevel)
policy_path="$repository_root/.github/repository-governance.json"
gh_command=${TOPIAFORGE_GH_CLI:-gh}

for command in git jq; do
  command -v "$command" >/dev/null || {
    echo "$command is required for release governance verification." >&2
    exit 1
  }
done
[[ -f $policy_path && ! -L $policy_path ]] || {
  echo "Checked-in repository governance policy is missing or unsafe." >&2
  exit 1
}

gh_api() {
  "$gh_command" api \
    -H 'Accept: application/vnd.github+json' \
    -H 'X-GitHub-Api-Version: 2026-03-10' \
    "$@"
}

policy_repository=$(jq -er '.repository_full_name' "$policy_path")
[[ $policy_repository == "$repository" ]] || {
  echo "Governance policy repository does not match $repository." >&2
  exit 1
}
reviewer_login=$(jq -er \
  '.repository_administrators |
   select(type == "array" and length == 1) |
   .[0] |
   select(. == "furroxide")' "$policy_path") || {
  echo "Governance policy must name furroxide as its sole release reviewer." >&2
  exit 1
}
reviewer_id=$(jq -er \
  '[.environments[] | select(.name == "release")] |
   select(length == 1) |
   .[0].reviewer_ids |
   select(type == "array" and length == 1) |
   .[0] |
   select(type == "number")' "$policy_path") || {
  echo "Governance policy must pin exactly one release reviewer ID." >&2
  exit 1
}

immutable=$(gh_api "repos/$repository/immutable-releases")
jq -e '.enabled == true' <<<"$immutable" >/dev/null || {
  echo "GitHub immutable releases must be enabled." >&2
  exit 1
}

reviewer=$(gh_api "users/$reviewer_login")
jq -e \
  --arg login "$reviewer_login" \
  --argjson id "$reviewer_id" \
  '.login == $login and .id == $id and .type == "User"' \
  <<<"$reviewer" >/dev/null || {
  echo "The pinned release reviewer identity no longer resolves to furroxide." >&2
  exit 1
}

environment=$(gh_api "repos/$repository/environments/release")
jq -e \
  --arg login "$reviewer_login" \
  --argjson id "$reviewer_id" \
  '.name == "release" and
   .can_admins_bypass == false and
   .deployment_branch_policy.protected_branches == false and
   .deployment_branch_policy.custom_branch_policies == true and
   ([.protection_rules[]? | select(.type == "required_reviewers")] |
     length == 1 and
     .[0].prevent_self_review == false and
     (.[0].reviewers | type == "array" and length == 1) and
     .[0].reviewers[0].type == "User" and
     .[0].reviewers[0].reviewer.login == $login and
     .[0].reviewers[0].reviewer.id == $id and
     .[0].reviewers[0].reviewer.type == "User")' \
  <<<"$environment" >/dev/null || {
  echo "The protected release environment does not match the fail-closed reviewer policy." >&2
  exit 1
}

branch_policies=$(
  gh_api \
    "repos/$repository/environments/release/deployment-branch-policies?per_page=100"
)
jq -e \
  '.total_count == 1 and
   (.branch_policies | type == "array" and length == 1) and
   .branch_policies[0].name == "v*" and
   .branch_policies[0].type == "tag"' \
  <<<"$branch_policies" >/dev/null || {
  echo "The release environment must allow exactly the v* tag policy." >&2
  exit 1
}

ruleset_summaries=$(
  gh_api "repos/$repository/rulesets?includes_parents=false&per_page=100"
)
required_rulesets=(
  release-branch-lifecycle
  version-tag-creation
  version-tag-immutability
)
for ruleset_name in "${required_rulesets[@]}"; do
  expected=$(jq -ecS \
    --arg name "$ruleset_name" \
    '[.rulesets[] | select(.name == $name)] |
     select(length == 1) |
     .[0] |
     {
       name,
       target,
       enforcement,
       ref_includes: (.ref_includes | sort),
       bypass_actors: (
         (.bypass_actors // []) |
         map({
           actor_id,
           actor_type,
           bypass_mode
         }) |
         sort_by(.actor_type, .actor_id, .bypass_mode)
       ),
       rule_types: (.rule_types | sort)
     } |
     select(.enforcement == "active")' "$policy_path") || {
    echo "Checked-in governance is missing active ruleset $ruleset_name." >&2
    exit 1
  }
  ruleset_id=$(jq -er \
    --arg name "$ruleset_name" \
    '[.[] | select(.name == $name)] |
     select(length == 1) |
     .[0].id |
     select(type == "number")' <<<"$ruleset_summaries") || {
    echo "GitHub is missing the unique required ruleset $ruleset_name." >&2
    exit 1
  }
  actual=$(gh_api "repos/$repository/rulesets/$ruleset_id")
  jq -e \
    --argjson expected "$expected" \
    '{
       name,
       target,
       enforcement,
       ref_includes: ((.conditions.ref_name.include // []) | sort),
       bypass_actors: (
         (.bypass_actors // []) |
         map({
           actor_id,
           actor_type,
           bypass_mode
         }) |
         sort_by(.actor_type, .actor_id, .bypass_mode)
       ),
       rule_types: ([.rules[]?.type] | sort)
     } == $expected and
     ((.conditions.ref_name.exclude // []) | length == 0)' \
    <<<"$actual" >/dev/null || {
    echo "GitHub ruleset $ruleset_name differs from checked-in governance." >&2
    exit 1
  }
done

echo "Verified immutable releases, protected release approval, and release ref governance."
