#!/usr/bin/env bash
set -euo pipefail

usage() {
  echo "Usage: verify-release-tag.sh <tag> <target-sha> [remote]" >&2
  exit 64
}

[[ $# -ge 2 && $# -le 3 ]] || usage
tag=$1
target_sha=$2
remote=${3:-origin}

[[ $tag =~ ^v[0-9]+\.[0-9]+\.[0-9]+([.-][0-9A-Za-z.-]+)?$ ]] || usage
[[ $target_sha =~ ^[0-9a-f]{40,64}$ ]] || usage
[[ $remote =~ ^[A-Za-z0-9._-]+$ ]] || usage
command -v git >/dev/null || {
  echo "git is required." >&2
  exit 1
}
git rev-parse --git-dir >/dev/null 2>&1 || {
  echo "Release tag verification must run inside a Git repository." >&2
  exit 1
}
git remote get-url "$remote" >/dev/null 2>&1 || {
  echo "Git remote '$remote' does not exist." >&2
  exit 1
}

# Fetch the one remote ref into an isolated namespace. A local tag is not
# evidence that the protected remote tag already exists, and --no-tags avoids
# accepting any opportunistically advertised tag.
verification_ref="refs/robotopia/release-tag-verification/$$-${RANDOM}"
cleanup() {
  git update-ref -d "$verification_ref" >/dev/null 2>&1 || true
}
trap cleanup EXIT

if ! GIT_TERMINAL_PROMPT=0 git -c credential.helper= fetch --quiet --no-tags \
  --force "$remote" "+refs/tags/$tag:$verification_ref"; then
  echo "Remote release tag refs/tags/$tag does not exist or cannot be fetched without prompting." >&2
  exit 1
fi

object_type=$(git cat-file -t "$verification_ref" 2>/dev/null || true)
if [[ $object_type != tag ]]; then
  echo "Remote release tag $tag must be annotated; lightweight tags are rejected." >&2
  exit 1
fi

peeled_sha=$(git rev-parse --verify "$verification_ref^{}" 2>/dev/null || true)
peeled_type=$(git cat-file -t "$peeled_sha" 2>/dev/null || true)
if [[ $peeled_type != commit ]]; then
  echo "Remote release tag $tag does not peel to a commit." >&2
  exit 1
fi
if [[ $peeled_sha != "$target_sha" ]]; then
  echo "Remote release tag $tag peels to $peeled_sha, not approved target $target_sha." >&2
  exit 1
fi

echo "Verified pre-existing annotated remote tag $tag at $target_sha."
