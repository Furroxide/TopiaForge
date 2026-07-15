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
git -C "$temp_root/source" remote add origin "$temp_root/remote.git"
git -C "$temp_root/source" commit --quiet --allow-empty -m first
first_sha=$(git -C "$temp_root/source" rev-parse HEAD)
git -C "$temp_root/source" commit --quiet --allow-empty -m second
target_sha=$(git -C "$temp_root/source" rev-parse HEAD)
release_head_sha=1111111111111111111111111111111111111111
git -C "$temp_root/source" push --quiet origin HEAD:refs/heads/main
git -C "$temp_root/source" tag -a v1.0.0 "$target_sha" -m v1.0.0

mkdir -p "$temp_root/bin"
cat >"$temp_root/bin/gh" <<'FAKE_GH'
#!/usr/bin/env bash
set -euo pipefail
[[ ${1:-} == api ]] || exit 64
url=${*: -1}
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
    if [[ $sha == "$FAKE_RELEASE_HEAD" ]]; then
      name='Required / Release packages'
    else
      name='Required / Unity validation'
    fi
    printf '{"check_runs":['
    printf '{"id":10,"name":"%s","head_sha":"%s","status":"completed","conclusion":"success","app":{"id":15368}},' "$name" "$sha"
    printf '{"id":20,"name":"%s","head_sha":"%s","status":"completed","conclusion":"%s","app":{"id":15368}}' "$name" "$sha" "$FAKE_NEWEST_CHECK_CONCLUSION"
    if [[ $sha == "$FAKE_TARGET" ]]; then
      printf ',{"id":30,"name":"Required / Game SDK acceptance","head_sha":"%s","status":"completed","conclusion":"success","app":{"id":15368}}' "$sha"
    fi
    printf ']}\n'
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

echo "Release candidate verifier regression tests passed."
