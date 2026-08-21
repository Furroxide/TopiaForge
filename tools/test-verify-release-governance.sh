#!/usr/bin/env bash
set -euo pipefail

script_dir=$(CDPATH='' cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
verifier="$script_dir/verify-release-governance.sh"
temp_root=$(mktemp -d)
trap 'rm -rf "$temp_root"' EXIT
mkdir -p "$temp_root/bin"

cat >"$temp_root/bin/gh" <<'FAKE_GH'
#!/usr/bin/env bash
set -euo pipefail
[[ ${1:-} == api ]] || exit 64
endpoint=${*: -1}
case "$endpoint" in
  repos/furroxide/TopiaForge/immutable-releases)
    jq -nc \
      --argjson enabled "${FAKE_IMMUTABLE_ENABLED:-true}" \
      '{enabled:$enabled}'
    ;;
  users/furroxide)
    jq -nc \
      --arg login "${FAKE_REVIEWER_LOGIN:-furroxide}" \
      --argjson id "${FAKE_REVIEWER_ID:-221987073}" \
      '{login:$login,id:$id,type:"User"}'
    ;;
  repos/furroxide/TopiaForge/environments/release)
    jq -nc \
      --arg login "${FAKE_ENV_REVIEWER_LOGIN:-furroxide}" \
      --argjson id "${FAKE_ENV_REVIEWER_ID:-221987073}" \
      --argjson bypass "${FAKE_ADMINS_BYPASS:-false}" \
      --argjson self_review "${FAKE_PREVENT_SELF_REVIEW:-false}" \
      '{
        name:"release",
        can_admins_bypass:$bypass,
        deployment_branch_policy:{
          protected_branches:false,
          custom_branch_policies:true
        },
        protection_rules:[{
          type:"required_reviewers",
          prevent_self_review:$self_review,
          reviewers:[{
            type:"User",
            reviewer:{login:$login,id:$id,type:"User"}
          }]
        }]
      }'
    ;;
  repos/furroxide/TopiaForge/environments/release/deployment-branch-policies\?per_page=100)
    jq -nc \
      --arg name "${FAKE_TAG_POLICY:-v*}" \
      '{total_count:1,branch_policies:[{name:$name,type:"tag"}]}'
    ;;
  repos/furroxide/TopiaForge/rulesets\?includes_parents=false\&per_page=100)
    if [[ ${FAKE_RULESET_MODE:-} == missing ]]; then
      printf '[{"id":1,"name":"release-branch-lifecycle"},{"id":2,"name":"version-tag-creation"}]\n'
    else
      printf '[{"id":1,"name":"release-branch-lifecycle"},{"id":2,"name":"version-tag-creation"},{"id":3,"name":"version-tag-immutability"}]\n'
    fi
    ;;
  repos/furroxide/TopiaForge/rulesets/1)
    enforcement=active
    [[ ${FAKE_RULESET_MODE:-} != inactive-release ]] ||
      enforcement=evaluate
    jq -nc \
      --arg enforcement "$enforcement" \
      '{
        name:"release-branch-lifecycle",
        target:"branch",
        enforcement:$enforcement,
        conditions:{ref_name:{include:["refs/heads/release/*"],exclude:[]}},
        bypass_actors:[{
          actor_id:5,
          actor_type:"RepositoryRole",
          bypass_mode:"always"
        }],
        rules:[{type:"creation"},{type:"deletion"}]
      }'
    ;;
  repos/furroxide/TopiaForge/rulesets/2)
    jq -nc \
      '{
        name:"version-tag-creation",
        target:"tag",
        enforcement:"active",
        conditions:{ref_name:{include:["refs/tags/v*"],exclude:[]}},
        bypass_actors:[{
          actor_id:5,
          actor_type:"RepositoryRole",
          bypass_mode:"always"
        }],
        rules:[{type:"creation"}]
      }'
    ;;
  repos/furroxide/TopiaForge/rulesets/3)
    enforcement=active
    ref='refs/tags/v*'
    rules='[{"type":"deletion"},{"type":"update"}]'
    bypass='[]'
    case "${FAKE_RULESET_MODE:-}" in
      inactive) enforcement=evaluate ;;
      wrong-ref) ref='refs/tags/*' ;;
      mutable) rules='[{"type":"deletion"}]' ;;
      bypass) bypass='[{"actor_id":5,"actor_type":"RepositoryRole","bypass_mode":"always"}]' ;;
    esac
    jq -nc \
      --arg enforcement "$enforcement" \
      --arg ref "$ref" \
      --argjson rules "$rules" \
      --argjson bypass "$bypass" \
      '{
        name:"version-tag-immutability",
        target:"tag",
        enforcement:$enforcement,
        conditions:{ref_name:{include:[$ref],exclude:[]}},
        bypass_actors:$bypass,
        rules:$rules
      }'
    ;;
  *)
    echo "Unexpected fake gh request: $endpoint" >&2
    exit 1
    ;;
esac
FAKE_GH
chmod +x "$temp_root/bin/gh"

export PATH="$temp_root/bin:$PATH"
export TOPIAFORGE_GH_CLI="$temp_root/bin/gh"

must_fail() {
  if "$@" >/dev/null 2>&1; then
    echo "Expected governance verification to fail: $*" >&2
    exit 1
  fi
}

"$verifier" furroxide/TopiaForge >/dev/null

export FAKE_IMMUTABLE_ENABLED=false
must_fail "$verifier" furroxide/TopiaForge
unset FAKE_IMMUTABLE_ENABLED

export FAKE_ADMINS_BYPASS=true
must_fail "$verifier" furroxide/TopiaForge
unset FAKE_ADMINS_BYPASS

export FAKE_ENV_REVIEWER_LOGIN=someone-else
must_fail "$verifier" furroxide/TopiaForge
unset FAKE_ENV_REVIEWER_LOGIN

export FAKE_PREVENT_SELF_REVIEW=true
must_fail "$verifier" furroxide/TopiaForge
unset FAKE_PREVENT_SELF_REVIEW

export FAKE_TAG_POLICY='release-*'
must_fail "$verifier" furroxide/TopiaForge
unset FAKE_TAG_POLICY

for mode in missing inactive wrong-ref mutable bypass inactive-release; do
  export FAKE_RULESET_MODE=$mode
  must_fail "$verifier" furroxide/TopiaForge
done
unset FAKE_RULESET_MODE

echo "Release governance verifier regression tests passed."
