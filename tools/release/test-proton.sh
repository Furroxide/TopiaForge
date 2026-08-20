#!/usr/bin/env bash
set -euo pipefail

export LC_ALL=C

usage() {
  printf '%s\n' \
    'Usage: test-proton.sh [--preflight-only] --repo <checkout>' \
    '  --source-sha <40-hex> --version <semver> --game-dir <Robotopia>' \
    '  --game-build-id <id> --proton-executable <proton>' \
    '  --steam-root <dir> --compat-data-root <private-dir>' \
    '  [--archive <TopiaForge-linux-x64.zip>]' \
    '  [--canonical-ecosystem-sha256 <sha256>] [--output <dir>]'
}

die() {
  printf 'Proton acceptance: %s\n' "$*" >&2
  exit 1
}

require_command() {
  command -v "$1" >/dev/null 2>&1 ||
    die "required command is unavailable: $1"
}

require_absolute_path() {
  local label=$1
  local value=$2
  [[ -n "$value" && "$value" == /* &&
     "$value" != *$'\r'* && "$value" != *$'\n'* ]] ||
    die "$label must be an absolute newline-free Linux path"
}

sha256_file() {
  sha256sum -- "$1" | awk '{print $1}'
}

canonical_tree_sha256() {
  local root=$1
  tar --sort=name \
    --mtime='UTC 1970-01-01' \
    --owner=0 \
    --group=0 \
    --numeric-owner \
    --hard-dereference \
    --format=gnu \
    -cf - \
    -C "$root" . |
    sha256sum |
    awk '{print $1}'
}

preflight_only=false
repo=''
source_sha=''
version=''
archive=''
canonical_ecosystem_sha256=''
game_dir=''
game_build_id=''
proton_executable=''
steam_root=''
compat_data_root=''
output=''

while (($#)); do
  case "$1" in
    --preflight-only) preflight_only=true; shift ;;
    --repo) repo=${2-}; shift 2 ;;
    --source-sha) source_sha=${2-}; shift 2 ;;
    --version) version=${2-}; shift 2 ;;
    --archive) archive=${2-}; shift 2 ;;
    --canonical-ecosystem-sha256)
      canonical_ecosystem_sha256=${2-}
      shift 2
      ;;
    --game-dir) game_dir=${2-}; shift 2 ;;
    --game-build-id) game_build_id=${2-}; shift 2 ;;
    --proton-executable) proton_executable=${2-}; shift 2 ;;
    --steam-root) steam_root=${2-}; shift 2 ;;
    --compat-data-root) compat_data_root=${2-}; shift 2 ;;
    --output) output=${2-}; shift 2 ;;
    --help|-h) usage; exit 0 ;;
    *) printf 'Unknown argument: %s\n' "$1" >&2; usage >&2; exit 2 ;;
  esac
done

if [[ ! "$source_sha" =~ ^[0-9a-f]{40}$ ]] ||
   [[ ! "$game_build_id" =~ ^[1-9][0-9]*$ ]] ||
   [[ -z "$version" ]]; then
  usage >&2
  exit 2
fi
for path_binding in \
  "Repository:$repo" \
  "GameDirectory:$game_dir" \
  "ProtonExecutable:$proton_executable" \
  "SteamRoot:$steam_root" \
  "CompatDataRoot:$compat_data_root"; do
  require_absolute_path "${path_binding%%:*}" "${path_binding#*:}"
done
if ! $preflight_only; then
  require_absolute_path Archive "$archive"
  require_absolute_path Output "$output"
  [[ "$canonical_ecosystem_sha256" =~ ^[0-9a-f]{64}$ ]] || {
    usage >&2
    exit 2
  }
fi

for command_name in \
  awk cmp comm dotnet find git grep id jq readlink realpath sed sha256sum \
  sort stat tar tr uname unzip; do
  require_command "$command_name"
done

[[ -d "$repo" && ! -L "$repo" ]] ||
  die "repository is not a real directory"
repo=$(cd "$repo" && pwd -P)
[[ "$(git -C "$repo" rev-parse --show-toplevel)" == "$repo" ]] ||
  die "repository path is not the checkout root"
[[ "$(git -C "$repo" rev-parse HEAD)" == "$source_sha" ]] ||
  die "checkout HEAD does not match the requested source SHA"
git -C "$repo" cat-file -e "$source_sha^{commit}" 2>/dev/null ||
  die "requested source SHA is not a commit in this checkout"
[[ -z "$(git -C "$repo" status --porcelain --untracked-files=all)" ]] ||
  die "checkout is not exact and clean"

policy_json=$(git -C "$repo" show \
  "$source_sha:release/release-policy.json") ||
  die "release policy is missing at the requested source SHA"
platform_toolchains_json=$(git -C "$repo" show \
  "$source_sha:release/platform-toolchains.json") ||
  die "platform toolchain policy is missing at the requested source SHA"
game_policy_path=$(jq -er '.gameBuild.metadataFile' <<<"$policy_json") ||
  die "release policy game-build metadata path is invalid"
game_policy_json=$(git -C "$repo" show "$source_sha:$game_policy_path") ||
  die "game-build metadata is missing at the requested source SHA"

jq -e \
  --arg version "$version" \
  --argjson gameBuildId "$game_build_id" \
  '
    .schemaVersion == 2 and
    .versioning.productVersion == $version and
    .gameBuild.id == $gameBuildId and
    (.toolchains.dotnetSdk | type == "string") and
    (.toolchains.node | type == "string")
  ' <<<"$policy_json" >/dev/null ||
  die "requested version or game build does not match the source policy"
jq -e --argjson gameBuildId "$game_build_id" '
  .buildId == $gameBuildId and
  .sourcePlatform == "windows" and
  (.archives.windows.sha256 |
    type == "string" and test("^[0-9a-f]{64}$")) and
  .windowsFilesManifest.path == "filelist.json" and
  (.windowsFilesManifest.sha256 |
    type == "string" and test("^[0-9a-f]{64}$")) and
  (.windowsFilesManifest.fileCount |
    type == "number" and . > 0 and floor == .) and
  (.windowsFilesManifest.gameExecutableSha256 |
    type == "string" and test("^[0-9a-f]{64}$"))
' <<<"$game_policy_json" >/dev/null ||
  die "source game-build metadata does not identify the pinned Windows build"
game_archive_sha256=$(jq -er '.archives.windows.sha256' \
  <<<"$game_policy_json")
game_files_manifest_sha256=$(jq -er '.windowsFilesManifest.sha256' \
  <<<"$game_policy_json")
game_files_verified=$(jq -er '.windowsFilesManifest.fileCount' \
  <<<"$game_policy_json")
expected_game_executable_sha256=$(jq -er \
  '.windowsFilesManifest.gameExecutableSha256' <<<"$game_policy_json")

proton_pin=$(jq -er '.linux.proton' <<<"$platform_toolchains_json") ||
  die "Linux Proton toolchain pin is missing"
execution_environment=$(jq -er \
  '.linux.executionEnvironment' <<<"$platform_toolchains_json") ||
  die "Linux Proton execution-environment pin is missing"
proton_app_id=$(jq -er '.linux.protonSteamAppId' \
  <<<"$platform_toolchains_json") ||
  die "Linux Proton Steam app pin is missing"
proton_depot_id=$(jq -er '.linux.protonSteamDepotId' \
  <<<"$platform_toolchains_json") ||
  die "Linux Proton Steam depot pin is missing"
proton_manifest_id=$(jq -er '.linux.protonSteamManifestId' \
  <<<"$platform_toolchains_json") ||
  die "Linux Proton Steam manifest pin is missing"
proton_build_id=$(jq -er '.linux.protonSteamBuildId' \
  <<<"$platform_toolchains_json") ||
  die "Linux Proton Steam build pin is missing"
proton_source_commit=$(jq -er '.linux.protonSourceCommit' \
  <<<"$platform_toolchains_json") ||
  die "Linux Proton source-commit pin is missing"
jq -e '.schemaVersion == 1' <<<"$platform_toolchains_json" >/dev/null ||
  die "platform toolchain policy schema is invalid"
[[ "$proton_pin" == "10.0-4" &&
   "$execution_environment" == "wsl2-wslg" &&
   "$proton_app_id" == 3658110 &&
   "$proton_depot_id" == 3658111 &&
   "$proton_manifest_id" == 5413949673798237105 &&
   "$proton_build_id" == 21617411 &&
   "$proton_source_commit" == \
     e2becb87430ca3ff510d949d9e75fa9b401da489 ]] ||
  die "source policy must pin the reviewed Proton 10.0-4 Steam provenance"
[[ "$game_build_id" == "2309" ]] ||
  die "this acceptance runner is locked to Robotopia build 2309"

[[ "$(uname -s)" == Linux && "$(uname -m)" == x86_64 ]] ||
  die "acceptance requires x86_64 Linux"
kernel_release=$(uname -r)
[[ "$kernel_release" == *microsoft-standard-WSL2* ||
   "$kernel_release" == *microsoft-standard-wsl2* ]] ||
  die "acceptance requires the Microsoft WSL2 kernel"
[[ -r /etc/os-release ]] ||
  die "Linux distribution metadata is unavailable"
# shellcheck disable=SC1091
. /etc/os-release
[[ "${ID-}" == ubuntu && "${VERSION_ID-}" == 24.04 ]] ||
  die "acceptance requires Ubuntu 24.04"
[[ -n "${WSL_INTEROP-}" && -S "${WSL_INTEROP-}" ]] ||
  die "WSL interoperability is unavailable"
if [[ ! -r /proc/sys/fs/binfmt_misc/WSLInterop ]] ||
   ! grep -Fxq enabled /proc/sys/fs/binfmt_misc/WSLInterop; then
  die "WSL interoperability is disabled"
fi
[[ -d /mnt/wslg && -d /mnt/wslg/runtime-dir ]] ||
  die "WSLg is unavailable"
[[ -c /dev/dxg ]] ||
  die "the WSL virtual GPU device is unavailable"
[[ -n "${DISPLAY-}" && -n "${WAYLAND_DISPLAY-}" ]] ||
  die "WSLg display variables are unavailable"
[[ -S "/mnt/wslg/runtime-dir/$WAYLAND_DISPLAY" ]] ||
  die "the WSLg Wayland socket is unavailable"

dotnet_pin=$(jq -er '.toolchains.dotnetSdk' <<<"$policy_json")
node_pin=$(jq -er '.toolchains.node' <<<"$policy_json")
[[ "$(dotnet --version)" == "$dotnet_pin" ]] ||
  die "expected .NET SDK $dotnet_pin"
require_command node
node_version=$(node --version)
[[ "$node_version" == "v$node_pin" ]] ||
  die "expected Node v$node_pin; found $node_version"

[[ -d "$game_dir" && ! -L "$game_dir" ]] ||
  die "Robotopia game directory is not a real directory"
game_dir=$(cd "$game_dir" && pwd -P)
game_executable="$game_dir/Robotopia.exe"
[[ -f "$game_executable" && ! -L "$game_executable" ]] ||
  die "the exact Windows Robotopia executable is missing"

game_marker="$game_dir/installed-build.json"
if [[ ! -f "$game_marker" &&
      "$(basename "$game_dir")" == "Robotopia" ]]; then
  game_marker="$(dirname "$game_dir")/installed-build.json"
fi
[[ -f "$game_marker" && ! -L "$game_marker" ]] ||
  die "Robotopia installed-build.json is missing or not a regular file"
marker_size=$(stat -c '%s' -- "$game_marker")
((marker_size > 0 && marker_size <= 4096)) ||
  die "Robotopia installed-build.json is outside its size bound"
jq -e --argjson gameBuildId "$game_build_id" '
  type == "object" and
  has("id") and
  ((.id | tostring) == ($gameBuildId | tostring))
' "$game_marker" >/dev/null ||
  die "installed Robotopia build does not match the pinned build"

game_files_manifest="$(dirname "$game_dir")/filelist.json"
[[ -f "$game_files_manifest" && ! -L "$game_files_manifest" ]] ||
  die "the official Robotopia files manifest is missing or linked"
game_files_manifest_size=$(stat -c '%s' -- "$game_files_manifest")
((game_files_manifest_size > 0 && game_files_manifest_size <= 1048576)) ||
  die "the official Robotopia files manifest is empty or unbounded"
[[ "$(sha256_file "$game_files_manifest")" == \
    "$game_files_manifest_sha256" ]] ||
  die "the installed files manifest is not from the pinned official archive"
jq -e --argjson fileCount "$game_files_verified" '
  type == "object" and
  (keys | sort) == ["files", "root", "version"] and
  .version == 1 and
  .root == "Robotopia" and
  (.files | type == "array" and length == $fileCount) and
  (all(.files[];
    type == "object" and
    (keys | sort) == ["path", "sha256", "size"] and
    (.path |
      type == "string" and
      length > 0 and
      ((contains("\\") or startswith("/") or
        contains("\u0000") or contains("\t") or
        contains("\r") or contains("\n") or
        test("(^|/)\\.\\.?(/|$)")) | not)) and
    (.sha256 |
      type == "string" and test("^[0-9a-f]{64}$")) and
    (.size | type == "number" and . >= 0 and floor == .)
  )) and
  ([.files[].path] == ([.files[].path] | sort)) and
  (([.files[].path] | unique | length) == $fileCount) and
  (([.files[].path | ascii_downcase] | unique | length) == $fileCount)
' "$game_files_manifest" >/dev/null ||
  die "the official Robotopia files manifest contract is invalid"

verify_official_game_files() {
  local relative expected_sha expected_size candidate actual_sha
  local verified=0
  while IFS=$'\t' read -r -d '' \
    relative expected_sha expected_size; do
    candidate=$(realpath -e -- "$game_dir/$relative") ||
      die "official Robotopia file is missing: $relative"
    [[ "$candidate" == "$game_dir/"* &&
       -f "$candidate" && ! -L "$candidate" ]] ||
      die "official Robotopia file is unsafe: $relative"
    [[ "$(stat -c '%s' -- "$candidate")" == "$expected_size" ]] ||
      die "official Robotopia file size mismatch: $relative"
    actual_sha=$(sha256_file "$candidate")
    [[ "$actual_sha" == "$expected_sha" ]] ||
      die "official Robotopia file digest mismatch: $relative"
    verified=$((verified + 1))
  done < <(
    jq -j '.files[] |
      [.path, .sha256, (.size | tostring)] |
      @tsv + "\u0000"' "$game_files_manifest"
  )
  [[ "$verified" == "$game_files_verified" ]] ||
    die "official Robotopia file verification count mismatch"
  printf '%s:%s:%s\n' \
    "$game_archive_sha256" "$game_files_manifest_sha256" "$verified"
}

official_game_identity_before=$(verify_official_game_files) ||
  die "the installed Robotopia base game is not the pinned official build"
game_executable_sha256=$(jq -er \
  '.files[] | select(.path == "Robotopia.exe") | .sha256' \
  "$game_files_manifest")
[[ "$game_executable_sha256" == "$expected_game_executable_sha256" ]] ||
  die "Robotopia.exe does not match the independently pinned digest"
[[ "$(sha256_file "$game_executable")" == "$game_executable_sha256" ]] ||
  die "Robotopia.exe does not match the official files manifest"

[[ -d "$steam_root" ]] ||
  die "Steam root does not resolve to a directory"
steam_root=$(realpath -e -- "$steam_root")
[[ -d "$steam_root" && ! -L "$steam_root" ]] ||
  die "resolved Steam root is not a real directory"
[[ "$steam_root" != / ]] || die "Steam root cannot be the filesystem root"
[[ -f "$steam_root/steam.exe" || -x "$steam_root/steam.sh" ]] ||
  die "Steam root does not contain a Steam client entry point"
proton_appmanifest="$steam_root/steamapps/appmanifest_$proton_app_id.acf"
[[ -f "$proton_appmanifest" && ! -L "$proton_appmanifest" ]] ||
  die "the pinned Proton Steam appmanifest is missing or linked"
appmanifest_size=$(stat -c '%s' -- "$proton_appmanifest")
((appmanifest_size > 0 && appmanifest_size <= 1048576)) ||
  die "the Proton Steam appmanifest is empty or unbounded"

acf_values() {
  local key=$1
  awk -v key="$key" '
    $0 ~ "^[[:space:]]*\"" key "\"[[:space:]]+\"" {
      value=$0
      sub("^[[:space:]]*\"" key "\"[[:space:]]+\"", "", value)
      sub("\"[[:space:]]*$", "", value)
      print value
    }
  ' "$proton_appmanifest"
}

require_one_acf_value() {
  local key=$1
  local expected=$2
  local values
  values=$(acf_values "$key")
  [[ "$(wc -l <<<"$values")" -eq 1 && "$values" == "$expected" ]] ||
    die "the Proton Steam appmanifest has an invalid $key"
}

require_one_acf_value appid "$proton_app_id"
require_one_acf_value buildid "$proton_build_id"
proton_install_dir=$(acf_values installdir)
proton_install_dir_pattern='^[A-Za-z0-9._+ -]+$'
[[ "$(wc -l <<<"$proton_install_dir")" -eq 1 &&
   "$proton_install_dir" =~ $proton_install_dir_pattern ]] ||
  die "the Proton Steam appmanifest has an invalid installdir"
depot_manifest_matches=$(awk \
  -v depot="$proton_depot_id" \
  -v manifest="$proton_manifest_id" '
  $0 ~ "^[[:space:]]*\"" depot "\"[[:space:]]*$" {
    in_depot=1
    next
  }
  in_depot &&
    $0 ~ "^[[:space:]]*\"manifest\"[[:space:]]+\"" manifest \
      "\"[[:space:]]*$" {
    matches += 1
    in_depot=0
  }
  in_depot && $0 ~ "^[[:space:]]*}[[:space:]]*$" {
    in_depot=0
  }
  END { print matches + 0 }
' "$proton_appmanifest")
[[ "$depot_manifest_matches" == 1 ]] ||
  die "the Proton Steam appmanifest does not bind the pinned depot manifest"
[[ -f "$proton_executable" && ! -L "$proton_executable" &&
   -x "$proton_executable" ]] ||
  die "Proton executable is missing, linked, or not executable"
proton_executable=$(realpath -e -- "$proton_executable")
proton_runtime_root=$(dirname "$proton_executable")
expected_proton_runtime_root="$steam_root/steamapps/common/$proton_install_dir"
[[ "$proton_runtime_root" == "$expected_proton_runtime_root" ]] ||
  die "the Proton launcher is not the appmanifest-installed runtime"
case "$proton_runtime_root" in
  "$steam_root"/*) ;;
  *) die "resolved Proton runtime must be strictly beneath the Steam root" ;;
esac
[[ -f "$proton_runtime_root/compatibilitytool.vdf" &&
   ! -L "$proton_runtime_root/compatibilitytool.vdf" &&
   -d "$proton_runtime_root/files" &&
   ! -L "$proton_runtime_root/files" ]] ||
  die "Proton executable is not the root launcher of a complete runtime tree"
[[ -z "$(find "$proton_runtime_root" \
  ! -type f ! -type d ! -type l -print -quit)" ]] ||
  die "Proton runtime tree contains a special filesystem entry"
while IFS= read -r -d '' runtime_link; do
  runtime_target=$(realpath -e -- "$runtime_link") ||
    die "Proton runtime tree contains a broken symbolic link"
  case "$runtime_target" in
    "$proton_runtime_root"|"$proton_runtime_root"/*) ;;
    *) die "Proton runtime tree contains a link outside its root" ;;
  esac
done < <(find "$proton_runtime_root" -type l -print0)

[[ -d "$compat_data_root" && ! -L "$compat_data_root" &&
   -w "$compat_data_root" ]] ||
  die "compat-data root must be an existing writable real directory"
compat_data_root=$(cd "$compat_data_root" && pwd -P)
[[ "$compat_data_root" != / && "$compat_data_root" != "$repo" &&
   "$compat_data_root" != "$game_dir" &&
   "$compat_data_root" != "$steam_root" ]] ||
  die "compat-data root is unsafe"
[[ "$(stat -c '%u' -- "$compat_data_root")" == "$(id -u)" ]] ||
  die "compat-data root must be owned by the current WSL user"

proton_version_output=$(PYTHONDONTWRITEBYTECODE=1 \
  "$proton_executable" --version 2>&1) ||
  die "Proton did not report its version"
[[ ${#proton_version_output} -gt 0 &&
   ${#proton_version_output} -le 4096 ]] ||
  die "Proton version output is empty or unbounded"
grep -Eq '(^|[^0-9])10\.0-4([^0-9]|$)' <<<"$proton_version_output" ||
  die "expected exact Proton 10.0-4"
grep -Eq "(^|[^0-9])${proton_pin//./\\.}([^0-9]|$)" \
  <<<"$proton_version_output" ||
  die "Proton version does not match the source toolchain pin"

if $preflight_only; then
  printf 'Proton acceptance preflight passed for Robotopia %s.\n' \
    "$game_build_id"
  exit 0
fi

[[ -f "$archive" && ! -L "$archive" ]] ||
  die "Linux candidate archive is missing or not a regular file"
archive=$(realpath -e -- "$archive")
[[ "$(basename "$archive")" == "TopiaForge-linux-x64.zip" ]] ||
  die "candidate archive must be TopiaForge-linux-x64.zip"
archive_size=$(stat -c '%s' -- "$archive")
((archive_size > 0)) || die "Linux candidate archive is empty"
archive_sha256=$(sha256_file "$archive")

mkdir -p -- "$output"
[[ ! -L "$output" ]] || die "output directory cannot be a symbolic link"
output=$(cd "$output" && pwd -P)
[[ "$output" != / && "$output" != "$repo" &&
   "$output" != "$game_dir" && "$output" != "$steam_root" &&
   "$output" != "$compat_data_root" ]] ||
  die "output directory is unsafe"
descriptor_path="$output/proton-evidence.json"
bundle_path="$output/proton-evidence.bundle"
[[ ! -e "$descriptor_path" && ! -L "$descriptor_path" &&
   ! -e "$bundle_path" && ! -L "$bundle_path" ]] ||
  die "refusing to replace existing Proton evidence"

work=''
work_parent=''
compat_session=''
published=false

matching_compat_pids() {
  local environment_file process_id
  for environment_file in /proc/[0-9]*/environ; do
    [[ -r "$environment_file" ]] || continue
    process_id=${environment_file#/proc/}
    process_id=${process_id%/environ}
    [[ "$process_id" != "$$" && "$process_id" != "$PPID" ]] || continue
    if tr '\0' '\n' <"$environment_file" 2>/dev/null |
      grep -Fxq \
        -e "STEAM_COMPAT_DATA_PATH=$compat_session" \
        -e "WINEPREFIX=$compat_session/pfx"; then
      printf '%s\n' "$process_id"
    fi
  done
}

stop_compat_processes() {
  local process_id
  [[ -n "$compat_session" ]] || return 0
  while IFS= read -r process_id; do
    [[ -n "$process_id" ]] && kill -TERM "$process_id" 2>/dev/null || true
  done < <(matching_compat_pids)
  sleep 2
  while IFS= read -r process_id; do
    [[ -n "$process_id" ]] && kill -KILL "$process_id" 2>/dev/null || true
  done < <(matching_compat_pids)
}

cleanup() {
  local exit_code=$?
  stop_compat_processes || true
  if [[ -n "$work" && -n "$work_parent" && -d "$work" ]]; then
    case "$work/" in
      "$work_parent"/topiaforge-proton.*/*)
        rm -rf -- "$work"
        ;;
    esac
  fi
  if [[ -n "$compat_session" && -d "$compat_session" ]]; then
    case "$compat_session/" in
      "$compat_data_root"/.topiaforge-proton.*/*)
        rm -rf -- "$compat_session"
        ;;
    esac
  fi
  rm -f -- "$output/.proton-evidence.json.tmp.$$" \
    "$output/.proton-evidence.bundle.tmp.$$"
  if [[ "$published" != true ]]; then
    rm -f -- "$descriptor_path" "$bundle_path"
  fi
  return "$exit_code"
}
trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM

work_parent=$(realpath -e -- "${TMPDIR:-/tmp}")
[[ -d "$work_parent" && -w "$work_parent" ]] ||
  die "temporary root is not a writable directory"
work=$(mktemp -d "$work_parent/topiaforge-proton.XXXXXXXXXX")
chmod 700 -- "$work"
compat_session=$(mktemp -d "$compat_data_root/.topiaforge-proton.XXXXXXXXXX")
chmod 700 -- "$compat_session"
[[ "$(stat -c '%a' -- "$work")" == 700 &&
   "$(stat -c '%a' -- "$compat_session")" == 700 ]] ||
  die "private Proton working directories did not retain mode 0700"

data_root="$work/data"
extracted="$work/extracted"
journey_projects="$work/journey-projects"
acceptance_output="$work/acceptance"
evidence_stage="$work/evidence"
mkdir -p -- "$data_root" "$extracted" "$journey_projects" \
  "$acceptance_output" "$evidence_stage"
chmod 700 -- "$data_root" "$acceptance_output" "$evidence_stage"

wine_wrapper="$data_root/run-proton"
{
  printf '#!/usr/bin/env bash\n'
  printf 'set -euo pipefail\n'
  printf 'export STEAM_COMPAT_CLIENT_INSTALL_PATH=%q\n' "$steam_root"
  printf 'export STEAM_COMPAT_DATA_PATH=%q\n' "$compat_session"
  printf 'export WINEDLLOVERRIDES=%q\n' 'winhttp=n,b'
  printf 'export PYTHONDONTWRITEBYTECODE=1\n'
  printf 'exec %q run "$@"\n' "$proton_executable"
} >"$wine_wrapper"
chmod 700 -- "$wine_wrapper"
jq -cn --arg wineCommand "$wine_wrapper" \
  '{wineCommand:$wineCommand}' >"$data_root/settings.json"
chmod 600 -- "$data_root/settings.json"

export TOPIAFORGE_DATA_ROOT="$data_root"
export ROBOTOPIA_GAME_DIR="$game_dir"

archive_inventory="$work/archive-inventory.txt"
unzip -Z1 "$archive" >"$archive_inventory" ||
  die "candidate archive inventory could not be read"
[[ -s "$archive_inventory" ]] || die "candidate archive inventory is empty"
while IFS= read -r entry; do
  [[ -n "$entry" && "$entry" != /* && "$entry" != *\\* ]] ||
    die "candidate archive contains an unsafe entry"
  IFS='/' read -r -a entry_parts <<<"$entry"
  for entry_part in "${entry_parts[@]}"; do
    [[ "$entry_part" != .. ]] ||
      die "candidate archive contains a parent-path entry"
  done
done <"$archive_inventory"
[[ "$(grep -Fxc topiaforge "$archive_inventory")" == 1 ]] ||
  die "candidate archive must contain exactly one root packaged CLI"
[[ -z "$(sort "$archive_inventory" | uniq -d)" ]] ||
  die "candidate archive contains duplicate entries"

unzip -q "$archive" -d "$extracted"
[[ -z "$(find "$extracted" -type l -print -quit)" ]] ||
  die "candidate archive extracted symbolic links"
packaged_cli="$extracted/topiaforge"
[[ -f "$packaged_cli" && ! -L "$packaged_cli" && -x "$packaged_cli" ]] ||
  die "exact packaged Linux CLI is missing or not executable"

(cd "$extracted" && "$packaged_cli" release test-package \
  --platform linux \
  --zip "$archive" \
  --run-embedded-cli \
  --expected-canonical-ecosystem-sha256 "$canonical_ecosystem_sha256" \
  --canonical-assets "$extracted/dist")
(cd "$extracted" && "$packaged_cli" --help) \
  >"$evidence_stage/cli-help.txt" 2>&1
journey_id="dev.topiaforge.release-${source_sha:0:12}"
journey_name="TopiaForge release $version"
(cd "$extracted" && "$packaged_cli" new mod "$journey_id" \
  --template minimal \
  --name "$journey_name" \
  --author "TopiaForge Release" \
  --license MIT \
  --dir "$journey_projects") >"$evidence_stage/new-mod.txt" 2>&1
journey_project="$journey_projects/$journey_id"
[[ -d "$journey_project" && ! -L "$journey_project" ]] ||
  die "packaged CLI did not create the release-journey project"
journey_marker="$journey_name loaded. Run '$journey_id:greet' to try its command."

marker_sha256_before=$(sha256_file "$game_marker")
proton_runtime_sha256=$(canonical_tree_sha256 "$proton_runtime_root") ||
  die "Proton runtime tree could not be hashed canonically"
wine_command_sha256=$(sha256_file "$wine_wrapper")

(cd "$repo" && "$packaged_cli" acceptance run \
  --game-dir "$game_dir" \
  --output "$acceptance_output" \
  --timeout-seconds 1800 \
  --dev-cli "$packaged_cli" \
  --dev-project "$journey_project" \
  --required-loaded-package "$journey_id" \
  --required-log-marker "$journey_marker" \
  --all)

stop_compat_processes
acceptance_result="$acceptance_output/acceptance-result.json"
[[ -f "$acceptance_result" && ! -L "$acceptance_result" ]] ||
  die "packaged CLI did not produce bounded acceptance evidence"
acceptance_size=$(stat -c '%s' -- "$acceptance_result")
((acceptance_size > 0 && acceptance_size <= 16777216)) ||
  die "acceptance evidence is empty or exceeds 16 MiB"
jq -e '
  def lower_sha256:
    type == "string" and test("^[0-9a-f]{64}$");
  def receipt:
    type == "object" and
    (.sourceSha256 | lower_sha256) and
    (.criticalFiles | type == "array" and length > 0 and length <= 8192 and
      all(.[];
        type == "object" and
        (.path | type == "string" and length > 0 and length <= 512) and
        (.sha256 | lower_sha256)));
  .schemaVersion == 2 and
  (.acceptanceChallenge | lower_sha256) and
  (.acceptancePackageReceipt | receipt) and
  (.requiredLoadedPackageReceipt | receipt) and
  .succeeded == true and
  (.requiredCases | type == "array" and length > 0 and
    all(.[]; type == "string")) and
  (.passedCases | type == "array" and all(.[]; type == "string")) and
  (.missingCases | type == "array" and length == 0) and
  (.failures | type == "array" and length == 0) and
  .releaseJourneyEnabled == true and
  .releaseJourneyAuthoringCommandCount == 2 and
  .requiredLoadedPackageStatus == "loaded" and
  .requiredLogMarkerObserved == true and
  .acceptancePackageStatus == "loaded" and
  (.lastRunSessionId | type == "string" and length > 0)
' "$acceptance_result" >/dev/null ||
  die "packaged acceptance result is incomplete"

source_case_inventory="$work/source-case-inventory.json"
git -C "$repo" cat-file blob \
  "$source_sha:tests/live-game-acceptance.json" >"$source_case_inventory" ||
  die "canonical acceptance case inventory is missing at the source SHA"
cmp -s "$source_case_inventory" "$repo/tests/live-game-acceptance.json" ||
  die "working acceptance case inventory differs from the source-SHA blob"
expected_cases="$work/expected-cases.txt"
required_cases="$work/required-cases.txt"
passed_cases="$work/passed-cases.txt"
jq -er '.cases | type == "array" and length > 0' \
  "$source_case_inventory" >/dev/null ||
  die "canonical acceptance case inventory is invalid"
jq -r '.cases[].id' "$source_case_inventory" |
  sort >"$expected_cases"
jq -r '.requiredCases[]' "$acceptance_result" | sort >"$required_cases"
jq -r '.passedCases[]' "$acceptance_result" | sort >"$passed_cases"
[[ -z "$(uniq -d "$expected_cases")" &&
   -z "$(uniq -d "$required_cases")" &&
   -z "$(uniq -d "$passed_cases")" ]] ||
  die "acceptance case sets contain duplicates"
if ! cmp -s "$expected_cases" "$required_cases" ||
   ! cmp -s "$expected_cases" "$passed_cases"; then
  die "required and passed acceptance sets do not match the exact inventory"
fi

[[ "$(sha256_file "$game_marker")" == "$marker_sha256_before" ]] ||
  die "Robotopia installed-build.json changed during acceptance"
[[ "$(sha256_file "$game_executable")" == "$game_executable_sha256" ]] ||
  die "Robotopia.exe changed during acceptance"
official_game_identity_after=$(verify_official_game_files) ||
  die "the official Robotopia base game failed post-acceptance verification"
[[ "$official_game_identity_after" == "$official_game_identity_before" ]] ||
  die "the official Robotopia base-game identity changed during acceptance"
[[ "$(sha256_file "$archive")" == "$archive_sha256" ]] ||
  die "Linux candidate archive changed during acceptance"
proton_runtime_sha256_after=$(canonical_tree_sha256 "$proton_runtime_root") ||
  die "Proton runtime tree could not be re-hashed canonically"
[[ "$proton_runtime_sha256_after" == "$proton_runtime_sha256" ]] ||
  die "Proton runtime tree changed during acceptance"
[[ -z "$(git -C "$repo" status --porcelain --untracked-files=all)" ]] ||
  die "acceptance changed the exact source checkout"

manager_log="$game_dir/BepInEx/TopiaForge/logs/manager.log"
last_run="$game_dir/BepInEx/TopiaForge/logs/last-run.json"
[[ -f "$manager_log" && ! -L "$manager_log" ]] ||
  die "manager.log is missing after acceptance"
[[ -f "$last_run" && ! -L "$last_run" ]] ||
  die "last-run.json is missing after acceptance"
manager_log_size=$(stat -c '%s' -- "$manager_log")
last_run_size=$(stat -c '%s' -- "$last_run")
((manager_log_size > 0 && manager_log_size <= 134217728)) ||
  die "manager.log is empty or exceeds 128 MiB"
((last_run_size > 0 && last_run_size <= 16777216)) ||
  die "last-run.json is empty or exceeds 16 MiB"
jq -e --slurpfile acceptance "$acceptance_result" \
  --arg journey_id "$journey_id" '
  .schemaVersion == 1 and
  (.sessionId == $acceptance[0].lastRunSessionId) and
  ([.packages[] | select(.id == "dev.topiaforge.sdk-acceptance")] | length == 1) and
  ([.packages[] | select(.id == $journey_id)] | length == 1) and
  ([.packages[] | select(.id == "dev.topiaforge.sdk-acceptance")][0] |
    .sourceSha256 == $acceptance[0].acceptancePackageReceipt.sourceSha256 and
    .criticalFiles == $acceptance[0].acceptancePackageReceipt.criticalFiles) and
  ([.packages[] | select(.id == $journey_id)][0] |
    .sourceSha256 == $acceptance[0].requiredLoadedPackageReceipt.sourceSha256 and
    .criticalFiles == $acceptance[0].requiredLoadedPackageReceipt.criticalFiles)
' "$last_run" >/dev/null ||
  die "last-run package receipts do not match the exact accepted packages"

cp -- "$acceptance_result" "$evidence_stage/acceptance-result.json"
cp -- "$game_marker" "$evidence_stage/game-build-marker.json"
cp -- "$last_run" "$evidence_stage/last-run.json"
cp -- "$manager_log" "$evidence_stage/manager.log"
printf '%s\n' "$proton_version_output" \
  >"$evidence_stage/proton-version.txt"

runtime_context="$evidence_stage/runtime-context.txt"
{
  printf 'executionEnvironment=%s\n' "$execution_environment"
  printf 'gameBuildId=%s\n' "$game_build_id"
  printf 'gameArchiveSha256=%s\n' "$game_archive_sha256"
  printf 'gameExecutableSha256=%s\n' "$game_executable_sha256"
  printf 'gameFilesManifestSha256=%s\n' "$game_files_manifest_sha256"
  printf 'gameFilesVerified=%s\n' "$game_files_verified"
  printf 'independentQa=false\n'
  printf 'protonRuntimeSha256=%s\n' "$proton_runtime_sha256"
  printf 'protonVersion=%s\n' "$proton_pin"
  printf 'runtime=windows-x64-via-proton\n'
  printf 'winDllOverrides=winhttp=n,b\n'
  printf 'wineCommandSha256=%s\n' "$wine_command_sha256"
} >"$runtime_context"
runtime_configuration_sha256=$(sha256_file "$runtime_context")
acceptance_result_sha256=$(sha256_file \
  "$evidence_stage/acceptance-result.json")
case_inventory_sha256=$(sha256_file "$source_case_inventory")
case_set_sha256=$(sha256_file "$expected_cases")

bundle_entries=(
  acceptance-result.json
  cli-help.txt
  game-build-marker.json
  last-run.json
  manager.log
  new-mod.txt
  proton-version.txt
  runtime-context.txt
)
bundle_temp="$work/proton-evidence.bundle"
tar --sort=name \
  --mtime='UTC 1970-01-01' \
  --owner=0 \
  --group=0 \
  --numeric-owner \
  --mode='u=rw,go=' \
  --format=ustar \
  -cf "$bundle_temp" \
  -C "$evidence_stage" \
  "${bundle_entries[@]}"
mapfile -t actual_bundle_entries < <(tar -tf "$bundle_temp")
[[ "${actual_bundle_entries[*]}" == "${bundle_entries[*]}" ]] ||
  die "private evidence bundle inventory is not deterministic"
evidence_size=$(stat -c '%s' -- "$bundle_temp")
((evidence_size > 0 && evidence_size <= 268435456)) ||
  die "private evidence bundle is empty or exceeds 256 MiB"
evidence_sha256=$(sha256_file "$bundle_temp")

required_cases_json=$(jq -Rsc \
  'split("\n") | map(select(length > 0))' "$expected_cases")
descriptor_temp="$work/proton-evidence.json"
jq -cn \
  --arg schema "release-proton-evidence-v1" \
  --arg version "$version" \
  --arg targetSha "$source_sha" \
  --arg platform "linux-proton" \
  --arg archiveSha256 "$archive_sha256" \
  --argjson archiveSize "$archive_size" \
  --arg canonicalEcosystemSha256 "$canonical_ecosystem_sha256" \
  --argjson gameBuildId "$game_build_id" \
  --arg gameArchiveSha256 "$game_archive_sha256" \
  --arg gameFilesManifestSha256 "$game_files_manifest_sha256" \
  --argjson gameFilesVerified "$game_files_verified" \
  --arg result "pass" \
  --arg suite "full" \
  --arg protonVersion "$proton_pin" \
  --argjson protonAppId "$proton_app_id" \
  --argjson protonDepotId "$proton_depot_id" \
  --arg protonManifestId "$proton_manifest_id" \
  --argjson protonBuildId "$proton_build_id" \
  --arg protonSourceCommit "$proton_source_commit" \
  --arg protonRuntimeSha256 "$proton_runtime_sha256" \
  --arg executionEnvironment "$execution_environment" \
  --arg runtime "windows-x64-via-proton" \
  --arg runtimeConfigurationSha256 "$runtime_configuration_sha256" \
  --arg gameExecutableSha256 "$game_executable_sha256" \
  --arg wineCommandSha256 "$wine_command_sha256" \
  --arg winDllOverrides "winhttp=n,b" \
  --arg caseInventorySha256 "$case_inventory_sha256" \
  --arg requiredCasesSha256 "$case_set_sha256" \
  --arg passedCasesSha256 "$case_set_sha256" \
  --arg acceptanceResultSha256 "$acceptance_result_sha256" \
  --arg evidenceSha256 "$evidence_sha256" \
  --argjson evidenceSize "$evidence_size" \
  --argjson cases "$required_cases_json" \
  '{
    acceptanceResultSha256:$acceptanceResultSha256,
    archiveSha256:$archiveSha256,
    archiveSize:$archiveSize,
    canonicalEcosystemSha256:$canonicalEcosystemSha256,
    caseInventorySha256:$caseInventorySha256,
    evidenceSha256:$evidenceSha256,
    evidenceSize:$evidenceSize,
    executionEnvironment:$executionEnvironment,
    failures:[],
    gameArchiveSha256:$gameArchiveSha256,
    gameBuildId:$gameBuildId,
    gameExecutableSha256:$gameExecutableSha256,
    gameFilesManifestSha256:$gameFilesManifestSha256,
    gameFilesVerified:$gameFilesVerified,
    independentQa:false,
    passedCases:$cases,
    passedCasesSha256:$passedCasesSha256,
    platform:$platform,
    protonAppId:$protonAppId,
    protonBuildId:$protonBuildId,
    protonDepotId:$protonDepotId,
    protonManifestId:$protonManifestId,
    protonRuntimeSha256:$protonRuntimeSha256,
    protonSourceCommit:$protonSourceCommit,
    protonVersion:$protonVersion,
    releaseJourney:{
      authoringCommandCount:2,
      enabled:true,
      loadedPackageStatus:"loaded",
      logMarkerObserved:true
    },
    requiredCases:$cases,
    requiredCasesSha256:$requiredCasesSha256,
    result:$result,
    runtime:$runtime,
    runtimeConfigurationSha256:$runtimeConfigurationSha256,
    schema:$schema,
    suite:$suite,
    targetSha:$targetSha,
    version:$version,
    winDllOverrides:$winDllOverrides,
    wineCommandSha256:$wineCommandSha256
  }' >"$descriptor_temp"

jq -e 'type == "object"' "$descriptor_temp" >/dev/null ||
  die "public Proton descriptor was not valid JSON"
cp -- "$bundle_temp" "$output/.proton-evidence.bundle.tmp.$$"
cp -- "$descriptor_temp" "$output/.proton-evidence.json.tmp.$$"
mv -- "$output/.proton-evidence.bundle.tmp.$$" "$bundle_path"
mv -- "$output/.proton-evidence.json.tmp.$$" "$descriptor_path"
published=true

printf 'Same-host Proton %s acceptance passed for Robotopia %s.\n' \
  "$proton_pin" "$game_build_id"
