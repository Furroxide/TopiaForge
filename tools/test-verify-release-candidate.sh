#!/usr/bin/env bash
set -euo pipefail

script_dir=$(CDPATH='' cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
verifier="$script_dir/verify-release-candidate.sh"
temp_root=$(mktemp -d)
trap 'rm -rf "$temp_root"' EXIT

git init --quiet --bare "$temp_root/remote.git"
git init --quiet "$temp_root/source"
git -C "$temp_root/source" config user.name "TopiaForge Release Test"
git -C "$temp_root/source" config user.email "release-test@example.invalid"
git -C "$temp_root/source" config core.autocrlf false
git -C "$temp_root/source" remote add origin "$temp_root/remote.git"
git -C "$temp_root/source" commit --quiet --allow-empty -m first
first_sha=$(git -C "$temp_root/source" rev-parse HEAD)
git -C "$temp_root/source" switch --quiet -c release/1.0.0
printf 'checked release bytes\n' >"$temp_root/source/release.txt"
git -C "$temp_root/source" add release.txt
git -C "$temp_root/source" commit --quiet -m 'release head'
release_head_sha=$(git -C "$temp_root/source" rev-parse HEAD)
git -C "$temp_root/source" switch --quiet -c main "$first_sha"
git -C "$temp_root/source" merge --quiet --no-ff release/1.0.0 -m second
target_sha=$(git -C "$temp_root/source" rev-parse HEAD)
git -C "$temp_root/source" push --quiet origin HEAD:refs/heads/main
git -C "$temp_root/source" tag -a v1.0.0 "$target_sha" -m v1.0.0

mkdir -p "$temp_root/bin"
cat >"$temp_root/bin/gh" <<'FAKE_GH'
#!/usr/bin/env bash
set -euo pipefail
[[ ${1:-} == api ]] || exit 64
url=${*: -1}

metadata_for_run() {
  local run_id=$1
  case "$run_id" in
    1010)
      check_name='Required / PR policy'
      workflow_id=301
      workflow_path='.github/workflows/pr-policy.yml'
      workflow_name='PR policy'
      event=pull_request
      ;;
    1020)
      check_name='Required / CI validation'
      workflow_id=302
      workflow_path='.github/workflows/ci.yml'
      workflow_name='CI'
      event=pull_request
      ;;
    1030)
      check_name='Required / Unity source validation'
      workflow_id=303
      workflow_path='.github/workflows/unity-pr-artifacts.yml'
      workflow_name='Unity Source Validation'
      event=pull_request
      ;;
    1040)
      check_name='Required / Registry validation'
      workflow_id=304
      workflow_path='.github/workflows/validate-registry.yml'
      workflow_name='Validate registry entries'
      event=pull_request
      ;;
    1050)
      check_name='Required / Dependency review'
      workflow_id=305
      workflow_path='.github/workflows/dependency-review.yml'
      workflow_name='Dependency review'
      event=pull_request
      ;;
    1060|1070)
      check_name='Required / Release packages'
      workflow_id=306
      workflow_path='.github/workflows/release-dry-run.yml'
      workflow_name='Release package dry run'
      event=push
      ;;
    *)
      echo "Unexpected fake workflow run: $run_id" >&2
      exit 1
      ;;
  esac
}

metadata_for_workflow() {
  local requested_workflow_id=$1
  case "$requested_workflow_id" in
    301)
      workflow_path='.github/workflows/pr-policy.yml'
      workflow_name='PR policy'
      ;;
    302)
      workflow_path='.github/workflows/ci.yml'
      workflow_name='CI'
      ;;
    303)
      workflow_path='.github/workflows/unity-pr-artifacts.yml'
      workflow_name='Unity Source Validation'
      ;;
    304)
      workflow_path='.github/workflows/validate-registry.yml'
      workflow_name='Validate registry entries'
      ;;
    305)
      workflow_path='.github/workflows/dependency-review.yml'
      workflow_name='Dependency review'
      ;;
    306)
      workflow_path='.github/workflows/release-dry-run.yml'
      workflow_name='Release package dry run'
      ;;
    *)
      echo "Unexpected fake workflow: $requested_workflow_id" >&2
      exit 1
      ;;
  esac
}

emit_check() {
  local id=$1
  local name=$2
  local sha=$3
  local conclusion=$4
  local details_repository=furroxide/TopiaForge
  if [[ $name == "${FAKE_BAD_CHECK:-}" &&
        ${FAKE_BAD_MODE:-} == wrong_details_repository ]]; then
    details_repository=attacker/TopiaForge
  fi
  printf \
    '{"id":%s,"name":"%s","head_sha":"%s","status":"completed","conclusion":"%s","details_url":"https://github.com/%s/actions/runs/%s/job/%s","app":{"id":15368}}' \
    "$id" "$name" "$sha" "$conclusion" "$details_repository" \
    "$((1000 + id))" "$((2000 + id))"
}

case "$url" in
  repos/furroxide/TopiaForge/git/ref/tags/v1.0.0)
    printf '{"object":{"type":"tag","sha":"tag-object"}}\n'
    ;;
  repos/furroxide/TopiaForge/git/tags/tag-object)
    printf '{"tag":"v1.0.0","object":{"type":"commit","sha":"%s"},"verification":{"verified":%s,"reason":"%s"}}\n' \
      "$FAKE_TARGET" "$FAKE_VERIFIED" "$FAKE_REASON"
    ;;
  repos/furroxide/TopiaForge/commits/*/pulls?per_page=100)
    printf '[{"number":7,"base":{"ref":"main"},"head":{"ref":"release/1.0.0","sha":"%s","repo":{"full_name":"furroxide/TopiaForge"}},"merged_at":"2026-07-15T00:00:00Z","merge_commit_sha":"%s"}]\n' \
      "$FAKE_RELEASE_HEAD" "$FAKE_TARGET"
    ;;
  repos/furroxide/TopiaForge/commits/*/check-runs?filter=all\&per_page=100)
    sha=${url#repos/furroxide/TopiaForge/commits/}
    sha=${sha%/check-runs?filter=all&per_page=100}
    printf '{"check_runs":['
    separator=
    id=10
    for name in \
      'Required / PR policy' \
      'Required / CI validation' \
      'Required / Unity source validation' \
      'Required / Registry validation' \
      'Required / Dependency review' \
      'Required / Release packages'; do
      [[ $sha == "$FAKE_RELEASE_HEAD" ]] || continue
      [[ $name != "${FAKE_MISSING_CHECK:-}" ]] || continue
      printf '%s' "$separator"
      emit_check "$id" "$name" "$sha" success
      separator=,
      id=$((id + 10))
      if [[ $name == 'Required / Release packages' ]]; then
        printf ','
        emit_check \
          "$id" "$name" "$sha" "$FAKE_NEWEST_CHECK_CONCLUSION"
      fi
    done
    printf ']}\n'
    ;;
  repos/furroxide/TopiaForge/actions/runs/*/attempts/*/jobs?per_page=100)
    run_id=${url#repos/furroxide/TopiaForge/actions/runs/}
    run_id=${run_id%%/*}
    attempt=${url#*/attempts/}
    attempt=${attempt%%/*}
    metadata_for_run "$run_id"
    job_id=$((1000 + run_id))
    job_attempt=$attempt
    if [[ $check_name == "${FAKE_BAD_CHECK:-}" &&
          ${FAKE_BAD_MODE:-} == stale_attempt ]]; then
      job_attempt=$((attempt - 1))
    fi
    printf \
      '{"total_count":1,"jobs":[{"id":%s,"run_id":%s,"run_attempt":%s,"head_sha":"%s","name":"%s","status":"completed","conclusion":"success","html_url":"https://github.com/furroxide/TopiaForge/actions/runs/%s/job/%s"}]}\n' \
      "$job_id" "$run_id" "$job_attempt" "$FAKE_RELEASE_HEAD" \
      "$check_name" "$run_id" "$job_id"
    ;;
  repos/furroxide/TopiaForge/actions/runs/*)
    run_id=${url##*/}
    metadata_for_run "$run_id"
    run_repository=furroxide/TopiaForge
    head_repository=furroxide/TopiaForge
    head_sha=$FAKE_RELEASE_HEAD
    head_branch=release/1.0.0
    run_attempt=1
    run_status=completed
    run_conclusion=success
    pull_request_number=7
    pull_request_base=main
    if [[ $check_name == "${FAKE_BAD_CHECK:-}" ]]; then
      case "${FAKE_BAD_MODE:-}" in
        wrong_workflow) workflow_path='.github/workflows/impostor.yml' ;;
        wrong_event) event=workflow_dispatch ;;
        wrong_ref) head_branch=dev ;;
        wrong_repository) run_repository=attacker/TopiaForge ;;
        wrong_head_repository) head_repository=attacker/TopiaForge ;;
        wrong_head_sha)
          head_sha=ffffffffffffffffffffffffffffffffffffffff
          ;;
        wrong_pr_number) pull_request_number=8 ;;
        wrong_pr_base) pull_request_base=dev ;;
        stale_attempt) run_attempt=2 ;;
        failed_run) run_conclusion=failure ;;
      esac
    fi
    pull_requests='[]'
    if [[ $event == pull_request ]]; then
      pull_requests=$(jq -nc \
        --argjson number "$pull_request_number" \
        --arg base "$pull_request_base" \
        --arg head_sha "$head_sha" \
        '[
          {
            number:$number,
            url:("https://api.github.com/repos/furroxide/TopiaForge/pulls/" +
              ($number | tostring)),
            head:{
              ref:"release/1.0.0",
              sha:$head_sha,
              repo:{url:"https://api.github.com/repos/furroxide/TopiaForge"}
            },
            base:{
              ref:$base,
              repo:{url:"https://api.github.com/repos/furroxide/TopiaForge"}
            }
          }
        ]')
    fi
    jq -nc \
      --argjson id "$run_id" \
      --argjson workflow_id "$workflow_id" \
      --arg repository "$run_repository" \
      --arg head_repository "$head_repository" \
      --arg path "$workflow_path" \
      --arg event "$event" \
      --arg head_sha "$head_sha" \
      --arg head_branch "$head_branch" \
      --arg status "$run_status" \
      --arg conclusion "$run_conclusion" \
      --argjson run_attempt "$run_attempt" \
      --argjson pull_requests "$pull_requests" \
      '{
        id:$id,
        workflow_id:$workflow_id,
        repository:{full_name:$repository},
        head_repository:{full_name:$head_repository},
        path:$path,
        event:$event,
        head_sha:$head_sha,
        head_branch:$head_branch,
        status:$status,
        conclusion:$conclusion,
        run_attempt:$run_attempt,
        pull_requests:$pull_requests
      }'
    ;;
  repos/furroxide/TopiaForge/actions/workflows/*)
    workflow_id=${url##*/}
    metadata_for_workflow "$workflow_id"
    workflow_state=active
    if [[ $workflow_name == "${FAKE_BAD_CHECK_WORKFLOW_NAME:-}" &&
          ${FAKE_BAD_MODE:-} == inactive_workflow ]]; then
      workflow_state=disabled_manually
    fi
    jq -nc \
      --argjson id "$workflow_id" \
      --arg name "$workflow_name" \
      --arg path "$workflow_path" \
      --arg state "$workflow_state" \
      --arg html_url \
        "https://github.com/furroxide/TopiaForge/actions/workflows/${workflow_path##*/}" \
      '{id:$id,name:$name,path:$path,state:$state,html_url:$html_url}'
    ;;
  *)
    echo "Unexpected fake gh request: $url" >&2
    exit 1
    ;;
esac
FAKE_GH
chmod +x "$temp_root/bin/gh"

export PATH="$temp_root/bin:$PATH"
export GH_TOKEN=test-token
export FAKE_TARGET=$target_sha
export FAKE_RELEASE_HEAD=$release_head_sha
export FAKE_VERIFIED=true
export FAKE_REASON=valid
export FAKE_NEWEST_CHECK_CONCLUSION=success
export FAKE_MISSING_CHECK=
export FAKE_BAD_CHECK=
export FAKE_BAD_CHECK_WORKFLOW_NAME=
export FAKE_BAD_MODE=

(
  cd "$temp_root/source"
  "$verifier" furroxide/TopiaForge v1.0.0 1.0.0 "$target_sha" >/dev/null
)

export FAKE_VERIFIED=false
export FAKE_REASON=unsigned
if (cd "$temp_root/source" && "$verifier" furroxide/TopiaForge v1.0.0 1.0.0 "$target_sha" >/dev/null 2>&1); then
  echo "Unsigned GitHub tag verification was accepted." >&2
  exit 1
fi
export FAKE_VERIFIED=true
export FAKE_REASON=valid

export FAKE_NEWEST_CHECK_CONCLUSION=failure
if (cd "$temp_root/source" && "$verifier" furroxide/TopiaForge v1.0.0 1.0.0 "$target_sha" >/dev/null 2>&1); then
  echo "An older successful check overrode a newer failed rerun." >&2
  exit 1
fi
export FAKE_NEWEST_CHECK_CONCLUSION=success

export FAKE_MISSING_CHECK='Required / Registry validation'
if (cd "$temp_root/source" && "$verifier" furroxide/TopiaForge v1.0.0 1.0.0 "$target_sha" >/dev/null 2>&1); then
  echo "A candidate with a missing hosted branch check was accepted." >&2
  exit 1
fi
export FAKE_MISSING_CHECK=

for adversarial_case in \
  'Required / CI validation|wrong_workflow' \
  'Required / PR policy|wrong_event' \
  'Required / Unity source validation|wrong_ref' \
  'Required / Registry validation|wrong_repository' \
  'Required / Dependency review|wrong_head_sha' \
  'Required / CI validation|wrong_pr_number' \
  'Required / Dependency review|wrong_pr_base' \
  'Required / Release packages|stale_attempt'; do
  export FAKE_BAD_CHECK=${adversarial_case%%|*}
  export FAKE_BAD_MODE=${adversarial_case#*|}
  if (cd "$temp_root/source" && "$verifier" furroxide/TopiaForge v1.0.0 1.0.0 "$target_sha" >/dev/null 2>&1); then
    echo "A same-name check with invalid $FAKE_BAD_MODE provenance was accepted." >&2
    exit 1
  fi
done
export FAKE_BAD_CHECK=
export FAKE_BAD_MODE=

git -C "$temp_root/source" tag v1.0.1 "$target_sha"
if (cd "$temp_root/source" && "$verifier" furroxide/TopiaForge v1.0.1 1.0.1 "$target_sha" >/dev/null 2>&1); then
  echo "Lightweight tag was accepted." >&2
  exit 1
fi

if (cd "$temp_root/source" && "$verifier" furroxide/TopiaForge v1.0.0 1.0.0 "$first_sha" >/dev/null 2>&1); then
  echo "Tag targeting a different commit was accepted." >&2
  exit 1
fi

if (cd "$temp_root/source" && "$verifier" furroxide/TopiaForge v1.0.0 1.0.1 "$target_sha" >/dev/null 2>&1); then
  echo "Tag/catalog version mismatch was accepted." >&2
  exit 1
fi

# Even a two-parent merge is invalid when the checked release head does not
# contain the exact tree that will be tagged and published.
git -C "$temp_root/source" switch --quiet -c release/mismatched "$target_sha"
printf 'checked branch only\n' >"$temp_root/source/checked-only.txt"
git -C "$temp_root/source" add checked-only.txt
git -C "$temp_root/source" commit --quiet -m 'mismatched checked head'
mismatched_head=$(git -C "$temp_root/source" rev-parse HEAD)
git -C "$temp_root/source" switch --quiet main
printf 'main only\n' >"$temp_root/source/main-only.txt"
git -C "$temp_root/source" add main-only.txt
git -C "$temp_root/source" commit --quiet -m 'concurrent main bytes'
git -C "$temp_root/source" merge --quiet --no-ff release/mismatched \
  -m 'mismatched merge'
mismatched_target=$(git -C "$temp_root/source" rev-parse HEAD)
git -C "$temp_root/source" push --quiet --force origin \
  HEAD:refs/heads/main
git -C "$temp_root/source" tag --force -a v1.0.0 "$mismatched_target" \
  -m v1.0.0
export FAKE_TARGET=$mismatched_target
export FAKE_RELEASE_HEAD=$mismatched_head
if (cd "$temp_root/source" && "$verifier" furroxide/TopiaForge v1.0.0 1.0.0 "$mismatched_target" >/dev/null 2>&1); then
  echo "A merge tree different from its checked release head was accepted." >&2
  exit 1
fi

echo "Release candidate verifier regression tests passed."
