#!/usr/bin/env bash
# Source with the checkout root. No cwd, HOME, shell-option or trap changes.
# Cleanup is deliberately bound to the original shell's creation receipt.

_topiaforge_neutral_error() { printf 'Neutral build root: %s\n' "$*" >&2; }
_topiaforge_neutral_physical() { (CDPATH='' cd -P -- "$1" && pwd -P); }
_topiaforge_neutral_identity() {
  stat -c '%d:%i' -- "$1" 2>/dev/null || stat -f '%d:%i' "$1" 2>/dev/null
}
_topiaforge_neutral_native() {
  if [[ $_topiaforge_neutral_platform == Windows ]]; then cygpath -m -- "$1"; else printf '%s\n' "$1"; fi
}
_topiaforge_neutral_home_path() {
  local candidate=$1 home_path lower
  lower=$(printf '%s' "$candidate" | LC_ALL=C tr '[:upper:]' '[:lower:]')
  case "$lower/" in
    */users/*|*/home/*|*'/documents and settings/'*) return 0 ;;
  esac
  for home_path in "${HOME:-}" "$([[ $_topiaforge_neutral_platform == Windows ]] && printf '%s' "${USERPROFILE:-}")"; do
    [[ -n $home_path ]] || continue
    [[ -d $home_path ]] || { _topiaforge_neutral_error 'home directory cannot be resolved'; return 0; }
    home_path=$(_topiaforge_neutral_physical "$home_path") || return 0
    if [[ $_topiaforge_neutral_platform == Windows ]]; then
      home_path=$(printf '%s' "$home_path" | LC_ALL=C tr '[:upper:]' '[:lower:]')
      case "$lower/" in "$home_path/"*) return 0 ;; esac
    else
      case "$candidate/" in "$home_path/"*) return 0 ;; esac
    fi
  done
  return 1
}

topiaforge_cleanup_neutral_build_root() {
  local prior_status=$? actual parent identity
  [[ -n ${_topiaforge_neutral_owned_root:-} ]] || return "$prior_status"
  actual=$_topiaforge_neutral_owned_root
  parent=$_topiaforge_neutral_owned_parent
  if [[ ! -d $actual || -L $actual || ${actual%/*} != "$parent" ||
    ${actual##*/} != topiaforge-build.* ]] ||
    [[ $(_topiaforge_neutral_physical "$parent") != "$parent" ]] ||
    [[ $(_topiaforge_neutral_physical "$actual") != "$actual" ]]; then
    _topiaforge_neutral_error 'cleanup refused: original physical directory is no longer owned'
    return 1
  fi
  identity=$(_topiaforge_neutral_identity "$actual") || return 1
  if [[ $identity != "$_topiaforge_neutral_owned_identity" ]]; then
    _topiaforge_neutral_error 'cleanup refused: directory identity changed'
    return 1
  fi
  rm -rf -- "$actual" || return 1
  unset _topiaforge_neutral_owned_root _topiaforge_neutral_owned_parent _topiaforge_neutral_owned_identity
  unset TOPIAFORGE_NEUTRAL_ROOT TOPIAFORGE_NEUTRAL_SOURCE TOPIAFORGE_NEUTRAL_PUB_CACHE
  return "$prior_status"
}

_topiaforge_prepare_neutral_build_root() {
  local workspace checkout platform temp_base owned entry path metadata mode stage
  local relative component ancestor files native_root
  [[ $# == 1 && -n $1 ]] || { _topiaforge_neutral_error 'one checkout root is required'; return 1; }
  [[ -z ${_topiaforge_neutral_owned_root:-} ]] || { _topiaforge_neutral_error 'an owned build root is already active'; return 1; }
  platform=${RUNNER_OS:-}
  if [[ -z $platform ]]; then
    case $(uname -s) in
      Linux) platform=Linux ;; Darwin) platform=macOS ;; MINGW*|MSYS*) platform=Windows ;;
    esac
  fi
  case $platform in
    Linux|macOS) temp_base=/tmp ;;
    Windows)
      [[ -n ${RUNNER_TEMP:-} ]] || { _topiaforge_neutral_error 'Windows requires RUNNER_TEMP'; return 1; }
      command -v cygpath >/dev/null || { _topiaforge_neutral_error 'Windows requires Git Bash cygpath'; return 1; }
      temp_base=$RUNNER_TEMP ;;
    *) _topiaforge_neutral_error 'unsupported runner platform'; return 1 ;;
  esac
  _topiaforge_neutral_platform=$platform
  [[ -n ${HOME:-} ]] || { _topiaforge_neutral_error 'HOME must identify the existing caller home'; return 1; }
  workspace=$(_topiaforge_neutral_physical "$1") || { _topiaforge_neutral_error 'checkout root does not exist'; return 1; }
  checkout=$(git -C "$workspace" rev-parse --show-toplevel 2>/dev/null) || { _topiaforge_neutral_error 'source is not a Git checkout'; return 1; }
  checkout=$(_topiaforge_neutral_physical "$checkout") || return 1
  [[ $checkout == "$workspace" ]] || { _topiaforge_neutral_error 'source must be the complete checkout root'; return 1; }
  if ! GIT_OPTIONAL_LOCKS=0 git -C "$workspace" diff --quiet --no-ext-diff --ignore-submodules=none -- ||
    ! GIT_OPTIONAL_LOCKS=0 git -C "$workspace" diff --cached --quiet --no-ext-diff --ignore-submodules=none --; then
    _topiaforge_neutral_error 'tracked source or index is dirty'; return 1
  fi
  temp_base=$(_topiaforge_neutral_physical "$temp_base") || { _topiaforge_neutral_error 'temporary parent must already exist'; return 1; }
  if _topiaforge_neutral_home_path "$temp_base"; then
    _topiaforge_neutral_error 'temporary parent resolves to a personal home path'; return 1
  fi
  owned=$(mktemp -d "$temp_base/topiaforge-build.XXXXXX") || { _topiaforge_neutral_error 'cannot allocate owned directory'; return 1; }
  _topiaforge_neutral_owned_root=$owned
  _topiaforge_neutral_owned_parent=$temp_base
  _topiaforge_neutral_owned_identity=$(_topiaforge_neutral_identity "$owned") || {
    _topiaforge_neutral_error 'cannot capture owned directory identity'
    rmdir -- "$owned"
    unset _topiaforge_neutral_owned_root _topiaforge_neutral_owned_parent _topiaforge_neutral_owned_identity
    return 1
  }
  files="$owned/tracked-files"
  if ! git -C "$workspace" ls-files --stage -z >"$owned/index-entries"; then
    topiaforge_cleanup_neutral_build_root; return 1
  fi
  if ! git -C "$workspace" ls-files -v -z >"$owned/index-flags"; then
    topiaforge_cleanup_neutral_build_root; return 1
  fi
  while IFS= read -r -d '' entry; do
    [[ ${entry:0:2} == 'H ' ]] || { _topiaforge_neutral_error 'hidden, sparse or unmerged index entries are not allowed'; topiaforge_cleanup_neutral_build_root; return 1; }
  done <"$owned/index-flags"
  : >"$files"
  while IFS= read -r -d '' entry; do
    metadata=${entry%%$'\t'*}
    path=${entry#*$'\t'}
    mode=${metadata%% *}
    stage=${metadata##* }
    case $mode in 100644|100755) ;; *) _topiaforge_neutral_error 'tracked links/submodules are not admitted'; topiaforge_cleanup_neutral_build_root; return 1 ;; esac
    [[ $stage == 0 && $path != /* && $path != *\\* ]] || { _topiaforge_neutral_error 'unsafe index entry'; topiaforge_cleanup_neutral_build_root; return 1; }
    ancestor=$workspace
    relative=$path
    while [[ -n $relative ]]; do
      component=${relative%%/*}
      if [[ $relative == */* ]]; then relative=${relative#*/}; else relative=; fi
      case $component in
        ''|.|..|.git|.dart_tool|.packages|node_modules|obj)
          _topiaforge_neutral_error 'tracked metadata/generated path is not admitted'; topiaforge_cleanup_neutral_build_root; return 1 ;;
      esac
      ancestor="$ancestor/$component"
      [[ ! -L $ancestor ]] || { _topiaforge_neutral_error 'tracked path traverses a link'; topiaforge_cleanup_neutral_build_root; return 1; }
    done
    [[ -f $ancestor ]] || { _topiaforge_neutral_error 'tracked payload is not a regular file'; topiaforge_cleanup_neutral_build_root; return 1; }
    printf '%s\0' "$path" >>"$files"
  done <"$owned/index-entries"
  if ! mkdir "$owned/source" "$owned/pub-cache" ||
    ! (set -o pipefail; tar -C "$workspace" --null -T "$files" -cf - | tar -C "$owned/source" -xf -); then
    _topiaforge_neutral_error 'tracked source copy failed'; topiaforge_cleanup_neutral_build_root; return 1
  fi
  rm -- "$files" "$owned/index-entries" "$owned/index-flags" || { topiaforge_cleanup_neutral_build_root; return 1; }
  native_root=$(_topiaforge_neutral_native "$owned") || { topiaforge_cleanup_neutral_build_root; return 1; }
  export TOPIAFORGE_NEUTRAL_ROOT=$native_root
  export TOPIAFORGE_NEUTRAL_SOURCE="$native_root/source"
  export TOPIAFORGE_NEUTRAL_PUB_CACHE="$native_root/pub-cache"
}

if [[ ${BASH_SOURCE[0]} == "$0" ]]; then
  _topiaforge_neutral_error 'source this helper with the checkout root'
  exit 1
fi
_topiaforge_prepare_neutral_build_root "$@"
