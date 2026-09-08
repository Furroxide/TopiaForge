#!/usr/bin/env bash
set -euo pipefail

script_dir=$(CDPATH='' cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)
helper="$script_dir/prepare-neutral-build-root.sh"
[[ -f $helper ]] || { echo 'FAIL: neutral build-root helper is missing' >&2; exit 1; }
case $(uname -s) in
  MINGW*|MSYS*) platform=Windows; fixture_parent=${RUNNER_TEMP:-${SYSTEMROOT:-C:/Windows}/Temp} ;;
  Darwin) platform=macOS; fixture_parent=/tmp ;;
  Linux) platform=Linux; fixture_parent=/tmp ;;
  *) echo 'Unsupported test platform' >&2; exit 1 ;;
esac
fixture_parent=$(CDPATH='' cd -- "$fixture_parent" && pwd -P)
fixture=$(mktemp -d "$fixture_parent/topiaforge-neutral-test.XXXXXX")
printf 'Owned neutral test fixture: %s\n' "$fixture"
cleanup_fixture() {
  [[ -d $fixture && ! -L $fixture && ${fixture%/*} == "$fixture_parent" &&
    ${fixture##*/} == topiaforge-neutral-test.* ]] || return 1
  rm -rf -- "$fixture"
}
trap cleanup_fixture EXIT
mkdir -p "$fixture/neutral pool" "$fixture/work space"
export GIT_CONFIG_NOSYSTEM=1 GIT_CONFIG_GLOBAL=/dev/null
export RUNNER_OS=$platform RUNNER_TEMP="$fixture/neutral pool"
repo="$fixture/work space"
git init --quiet "$repo"
git -C "$repo" config user.name 'Neutral Build Test'
git -C "$repo" config user.email 'neutral-test@example.invalid'
git -C "$repo" config core.autocrlf false
git -C "$repo" config core.hooksPath "$fixture/no-hooks"
printf 'tracked bytes\n' >"$repo/space name.txt"
printf '#!/usr/bin/env bash\nprintf synthetic\n' >"$repo/executable.sh"
chmod +x "$repo/executable.sh"
printf 'leading dash\n' >"$repo/-leading.txt"
if [[ $platform != Windows ]]; then
  printf 'newline name\n' >"$repo/line"$'\n'"break.txt"
fi
mkdir -p "$repo/.github/workflows"
printf 'tracked workflow\n' >"$repo/.github/workflows/sample.yml"
# The clean filter stores a pointer while the working tree retains hydrated bytes.
printf '*.asset filter=fixture-lfs -text\n' >"$repo/.gitattributes"
git -C "$repo" config filter.fixture-lfs.clean "printf 'synthetic LFS index pointer\\n'"
printf 'hydrated binary\000payload\377\n' >"$repo/hydrated.asset"
git -C "$repo" add -- .
git -C "$repo" commit --quiet -m 'Synthetic tracked fixture'
mkdir -p "$repo/.dart_tool" "$repo/untracked output"
printf 'untracked package configuration\n' >"$repo/.dart_tool/package_config.json"
printf 'untracked output\n' >"$repo/untracked output/generated.dll"
initial_head=$(git -C "$repo" rev-parse HEAD)
passed=0
run_case() { "$@"; passed=$((passed + 1)); printf 'PASS: %s\n' "$1"; }
refuses() {
  local log="$fixture/refusal.log"
  if (source "$helper" "$repo") >"$log" 2>&1; then
    echo 'Unexpected neutral-root admission' >&2; return 1
  fi
  grep -q 'Neutral build root:' "$log"
}
copy_and_cleanup() (
  local_home=$HOME
  old_pwd=$PWD
  source "$helper" "$repo"
  [[ $HOME == "$local_home" && $PWD == "$old_pwd" ]]
  [[ -d $TOPIAFORGE_NEUTRAL_PUB_CACHE && -d $TOPIAFORGE_NEUTRAL_SOURCE ]]
  [[ -n $(find "$TOPIAFORGE_NEUTRAL_SOURCE/.github" -type f -print) ]]
  cmp "$repo/space name.txt" "$TOPIAFORGE_NEUTRAL_SOURCE/space name.txt"
  cmp "$repo/hydrated.asset" "$TOPIAFORGE_NEUTRAL_SOURCE/hydrated.asset"
  cmp "$repo/-leading.txt" "$TOPIAFORGE_NEUTRAL_SOURCE/-leading.txt"
  if [[ $platform != Windows ]]; then
    cmp "$repo/line"$'\n'"break.txt" "$TOPIAFORGE_NEUTRAL_SOURCE/line"$'\n'"break.txt"
  fi
  [[ ! -e $TOPIAFORGE_NEUTRAL_SOURCE/.git && ! -e $TOPIAFORGE_NEUTRAL_SOURCE/.dart_tool &&
    ! -e "$TOPIAFORGE_NEUTRAL_SOURCE/untracked output" ]]
  [[ -x $TOPIAFORGE_NEUTRAL_SOURCE/executable.sh ]]
  if [[ $platform == Windows ]]; then
    [[ $TOPIAFORGE_NEUTRAL_ROOT =~ ^[A-Za-z]:/ && $TOPIAFORGE_NEUTRAL_ROOT != *\\* ]]
  else
    [[ $TOPIAFORGE_NEUTRAL_ROOT == /tmp/topiaforge-build.* ||
      $TOPIAFORGE_NEUTRAL_ROOT == /private/tmp/topiaforge-build.* ]]
  fi
  original=$TOPIAFORGE_NEUTRAL_ROOT
  mkdir "$fixture/unrelated"
  printf 'sentinel\n' >"$fixture/unrelated/keep.txt"
  export TOPIAFORGE_NEUTRAL_ROOT="$fixture/unrelated"
  topiaforge_cleanup_neutral_build_root
  [[ ! -e $original && -f $fixture/unrelated/keep.txt ]]
  topiaforge_cleanup_neutral_build_root
)
run_case copy_and_cleanup
unstaged_refusal() { printf 'dirty\n' >>"$repo/space name.txt"; refuses; git -C "$repo" checkout -- 'space name.txt'; }
run_case unstaged_refusal
staged_refusal() { printf 'staged\n' >"$repo/staged.txt"; git -C "$repo" add staged.txt; refuses; git -C "$repo" reset --quiet HEAD -- staged.txt; rm "$repo/staged.txt"; }
run_case staged_refusal
missing_refusal() { mv "$repo/space name.txt" "$repo/missing.txt"; refuses; mv "$repo/missing.txt" "$repo/space name.txt"; }
run_case missing_refusal
assume_unchanged_refusal() { git -C "$repo" update-index --assume-unchanged 'space name.txt'; refuses; git -C "$repo" update-index --no-assume-unchanged 'space name.txt'; }
run_case assume_unchanged_refusal
subdirectory_refusal() { if (source "$helper" "$repo/.github") >"$fixture/subdir.log" 2>&1; then return 1; fi; }
run_case subdirectory_refusal
unknown_platform_refusal() { (export RUNNER_OS=Unknown; refuses); }
run_case unknown_platform_refusal
home_temp_refusal() (
  if [[ $platform == Windows ]]; then
    export RUNNER_TEMP=$HOME
    refuses
    grep -q 'temporary parent resolves to a personal home path' "$fixture/refusal.log"
  else
    source "$helper" "$repo"
    _topiaforge_neutral_home_path "$HOME"
    topiaforge_cleanup_neutral_build_root
  fi
)
run_case home_temp_refusal
home_grammar_refusal() (
  source "$helper" "$repo"
  _topiaforge_neutral_home_path "$fixture/Users/synthetic/temp"
  _topiaforge_neutral_home_path "$fixture/home/synthetic/temp"
  topiaforge_cleanup_neutral_build_root
)
run_case home_grammar_refusal
# A tracked symlink is refused by index mode even on Windows checkouts without symlinks.
tracked_link_refusal() {
  link_blob=$(printf 'space name.txt' | git -C "$repo" hash-object -w --stdin)
  git -C "$repo" update-index --add --cacheinfo 120000 "$link_blob" tracked-link
  git -C "$repo" commit --quiet -m 'Synthetic symlink'
  refuses
  git -C "$repo" reset --quiet --hard "$initial_head"
}
run_case tracked_link_refusal
# Exercise physical link refusal when the host supports actual links.
mkdir -p "$fixture/home/synthetic/temp"
printf 'synthetic link target\n' >"$fixture/home/synthetic/temp/sentinel.txt"
if MSYS=winsymlinks:nativestrict ln -s "$fixture/home/synthetic/temp" "$fixture/temp-link" 2>/dev/null && [[ -L $fixture/temp-link ]]; then
  physical_home_refusal() (
    source "$helper" "$repo"
    _topiaforge_neutral_home_path "$(_topiaforge_neutral_physical "$fixture/temp-link")"
    topiaforge_cleanup_neutral_build_root
  )
  run_case physical_home_refusal
fi
cleanup_replacement_refusal() (
  source "$helper" "$repo"
  original=$TOPIAFORGE_NEUTRAL_ROOT
  mv "$original" "$original.saved"
  mkdir "$original"
  printf 'replacement\n' >"$original/keep.txt"
  if topiaforge_cleanup_neutral_build_root; then return 1; fi
  [[ -f $original/keep.txt && -d $original.saved ]]
  rmdir_path=$original
  rm "$rmdir_path/keep.txt"
  rmdir "$rmdir_path"
  mv "$original.saved" "$original"
  topiaforge_cleanup_neutral_build_root
)
run_case cleanup_replacement_refusal
state_preserved() {
  [[ $(git -C "$repo" rev-parse HEAD) == "$initial_head" ]]
  git -C "$repo" diff --quiet
  git -C "$repo" diff --cached --quiet
  [[ -f $repo/.dart_tool/package_config.json && -f "$repo/untracked output/generated.dll" ]]
  # Git's own fixture operations refresh index stat data; helper never changes its bytes.
  before=$(git -C "$repo" hash-object .git/index)
  (source "$helper" "$repo"; topiaforge_cleanup_neutral_build_root)
  [[ $(git -C "$repo" hash-object .git/index) == "$before" ]]
}
run_case state_preserved
printf 'Neutral build-root tests passed: %s\n' "$passed"
