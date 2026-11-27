#!/usr/bin/env bash
set -euo pipefail

# Dart AOT embeds source URIs. Keep both local packages and cached dependencies
# outside the runner's home directory, using only the checkout's tracked bytes.
root="$(mktemp -d /tmp/topiaforge-build.XXXXXXXX)"
root="$(cd "$root" && pwd -P)"
mkdir "$root/source" "$root/pub-cache"

git -C "$GITHUB_WORKSPACE" ls-files -z |
  tar -cf - -C "$GITHUB_WORKSPACE" --null -T - |
  tar -xf - -C "$root/source"

printf 'TOPIAFORGE_CI_BUILD_ROOT=%s\nPUB_CACHE=%s\n' \
  "$root/source" "$root/pub-cache" >> "$GITHUB_ENV"
