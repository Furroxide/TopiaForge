#!/usr/bin/env bash
set -euo pipefail

script_dir=$(CDPATH='' cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
verifier="$script_dir/verify-release-tag.sh"
temp_root=$(mktemp -d)
trap 'rm -rf "$temp_root"' EXIT

git init --quiet --bare "$temp_root/remote.git"
git init --quiet "$temp_root/source"
git -C "$temp_root/source" config user.name "Robotopia Release Test"
git -C "$temp_root/source" config user.email "release-test@example.invalid"
git -C "$temp_root/source" remote add origin "$temp_root/remote.git"
git -C "$temp_root/source" commit --quiet --allow-empty -m first
first_sha=$(git -C "$temp_root/source" rev-parse HEAD)
git -C "$temp_root/source" commit --quiet --allow-empty -m second
second_sha=$(git -C "$temp_root/source" rev-parse HEAD)
git -C "$temp_root/source" push --quiet origin HEAD:refs/heads/main

git -C "$temp_root/source" tag -a v1.0.0 "$second_sha" -m v1.0.0
git -C "$temp_root/source" push --quiet origin refs/tags/v1.0.0
(
  cd "$temp_root/source"
  "$verifier" v1.0.0 "$second_sha" origin >/dev/null
)

git -C "$temp_root/source" tag v1.0.1 "$second_sha"
git -C "$temp_root/source" push --quiet origin refs/tags/v1.0.1
if (cd "$temp_root/source" && "$verifier" v1.0.1 "$second_sha" origin >/dev/null 2>&1); then
  echo "Lightweight release tag was accepted." >&2
  exit 1
fi

git -C "$temp_root/source" tag -a v1.0.2 "$first_sha" -m v1.0.2
git -C "$temp_root/source" push --quiet origin refs/tags/v1.0.2
if (cd "$temp_root/source" && "$verifier" v1.0.2 "$second_sha" origin >/dev/null 2>&1); then
  echo "Annotated release tag at the wrong commit was accepted." >&2
  exit 1
fi

if (cd "$temp_root/source" && "$verifier" v1.0.3 "$second_sha" origin >/dev/null 2>&1); then
  echo "Missing remote release tag was accepted." >&2
  exit 1
fi

echo "Release tag verifier regression tests passed."
