#!/usr/bin/env bash
set -euo pipefail

usage() {
  printf '%s\n' \
    'Usage: build-platform.sh --platform linux|macos --repo <checkout> --sha <40-hex>' \
    '  --version <semver> --canonical-archive <tar>' \
    '  --canonical-ecosystem-sha256 <tree-sha256>' \
    '  --canonical-archive-sha256 <transport-sha256>' \
    '  --output <dir> [--expected-mac-team-id <team-id>]'
}

platform=''
repo=''
source_sha=''
version=''
canonical_archive=''
canonical_ecosystem_sha256=''
canonical_archive_sha256=''
output=''
expected_mac_team_id=''

while (($#)); do
  case "$1" in
    --platform) platform=${2-}; shift 2 ;;
    --repo) repo=${2-}; shift 2 ;;
    --sha) source_sha=${2-}; shift 2 ;;
    --version) version=${2-}; shift 2 ;;
    --canonical-archive) canonical_archive=${2-}; shift 2 ;;
    --canonical-ecosystem-sha256) canonical_ecosystem_sha256=${2-}; shift 2 ;;
    --canonical-archive-sha256) canonical_archive_sha256=${2-}; shift 2 ;;
    --output) output=${2-}; shift 2 ;;
    --expected-mac-team-id) expected_mac_team_id=${2-}; shift 2 ;;
    --help|-h) usage; exit 0 ;;
    *) printf 'Unknown argument: %s\n' "$1" >&2; usage >&2; exit 2 ;;
  esac
done

if [[ "$platform" != linux && "$platform" != macos ]] ||
   [[ ! "$source_sha" =~ ^[0-9a-f]{40}$ ]] ||
   [[ ! "$canonical_ecosystem_sha256" =~ ^[0-9a-f]{64}$ ]] ||
   [[ ! "$canonical_archive_sha256" =~ ^[0-9a-f]{64}$ ]] ||
   [[ -z "$repo" || -z "$version" || -z "$canonical_archive" ||
      -z "$output" ]]; then
  usage >&2
  exit 2
fi

repo=$(cd "$repo" && pwd -P)
canonical_archive=$(cd "$(dirname "$canonical_archive")" &&
  printf '%s/%s\n' "$PWD" "$(basename "$canonical_archive")")
mkdir -p "$output"
output=$(cd "$output" && pwd -P)

if [[ "$(git -C "$repo" rev-parse HEAD)" != "$source_sha" ]]; then
  printf 'Checkout HEAD does not match requested source SHA.\n' >&2
  exit 1
fi
if [[ -n "$(git -C "$repo" status --porcelain --untracked-files=no)" ]]; then
  printf 'Checkout has tracked changes before the platform build.\n' >&2
  exit 1
fi
if [[ "$(git -C "$repo" show "$source_sha:release/release-policy.json" |
  jq -er '.versioning.productVersion')" != "$version" ]]; then
  printf 'Requested version does not match release policy at the source SHA.\n' >&2
  exit 1
fi

policy="$repo/release/release-policy.json"
platform_policy="$repo/release/platform-toolchains.json"
jq -e --arg platform "$platform" '
  .schemaVersion == 1 and
  if $platform == "linux" then
    (.linux.clang | type == "string") and
    (.linux.cmake | type == "string") and
    (.linux.ninja | type == "string") and
    (.linux.gtk | type == "string") and
    .linux.proton == "10.0-4" and
    .linux.executionEnvironment == "wsl2-wslg" and
    .linux.protonSteamAppId == "3658110" and
    .linux.protonSteamDepotId == "3658111" and
    .linux.protonSteamManifestId == "5413949673798237105" and
    .linux.protonSteamBuildId == "21617411" and
    .linux.protonSourceCommit ==
      "e2becb87430ca3ff510d949d9e75fa9b401da489"
  else
    (.macos.xcode | type == "string") and
    (.macos.macosSdk | type == "string") and
    (.macos.appleClang | type == "string")
  end
' "$platform_policy" >/dev/null
dotnet_pin=$(jq -er '.toolchains.dotnetSdk' "$policy")
dart_pin=$(jq -er '.toolchains.dart' "$policy")
flutter_pin=$(jq -er '.toolchains.flutter' "$policy")
node_pin=$(jq -er '.toolchains.node' "$policy")
flutter_command=${TOPIAFORGE_FLUTTER:-flutter}
dart_command=${TOPIAFORGE_DART:-dart}

require_command() {
  command -v "$1" >/dev/null 2>&1 || {
    printf 'Required command is unavailable: %s\n' "$1" >&2
    exit 1
  }
}

for command_name in git jq tar dotnet node "$flutter_command" "$dart_command"; do
  require_command "$command_name"
done
if [[ "$platform" == macos ]]; then
  require_command shasum
else
  require_command sha256sum
  require_command unzip
fi

sha256_file() {
  if [[ "$platform" == macos ]]; then
    shasum -a 256 -- "$1" | awk '{print $1}'
  else
    sha256sum -- "$1" | awk '{print $1}'
  fi
}

[[ "$(dotnet --version)" == "$dotnet_pin" ]] || {
  printf 'Expected .NET SDK %s.\n' "$dotnet_pin" >&2
  exit 1
}
node_version=$(node --version)
[[ "$node_version" == "v$node_pin" ]] || {
  printf 'Expected Node v%s, found %s.\n' "$node_pin" "$node_version" >&2
  exit 1
}
[[ "$("$flutter_command" --version --machine | jq -er '.frameworkVersion')" == "$flutter_pin" ]] || {
  printf 'Expected Flutter %s.\n' "$flutter_pin" >&2
  exit 1
}
dart_version=$("$dart_command" --version 2>&1)
[[ "$dart_version" == *"Dart SDK version: $dart_pin"* ]] || {
  printf 'Expected Dart %s.\n' "$dart_pin" >&2
  exit 1
}

if [[ ! -f "$canonical_archive" ]]; then
  printf 'Canonical ecosystem archive was not found.\n' >&2
  exit 1
fi
actual_canonical_sha=$(sha256_file "$canonical_archive")
[[ "$actual_canonical_sha" == "$canonical_archive_sha256" ]] || {
  printf 'Canonical ecosystem transport archive digest mismatch.\n' >&2
  exit 1
}

platform_toolchains_json=''
case "$platform" in
  linux)
    [[ "$(uname -s)" == Linux && "$(uname -m)" == x86_64 ]] || {
      printf 'Linux release packages require an x86_64 Linux host.\n' >&2
      exit 1
    }
    if [[ ! -r /etc/os-release ]] ||
       ! grep -Fxq 'ID=ubuntu' /etc/os-release ||
       ! grep -Fxq 'VERSION_ID="24.04"' /etc/os-release; then
       printf 'Linux release packages require Ubuntu 24.04.\n' >&2
       exit 1
    fi
    for command_name in clang cmake ninja pkg-config; do
      require_command "$command_name"
    done
    measured_clang=$(clang --version |
      sed -nE '1s/.*clang version ([0-9]+\.[0-9]+\.[0-9]+).*/\1/p')
    measured_cmake=$(cmake --version |
      sed -nE '1s/cmake version ([0-9]+\.[0-9]+\.[0-9]+).*/\1/p')
    measured_ninja=$(ninja --version)
    measured_gtk=$(pkg-config --modversion gtk+-3.0)
    pinned_clang=$(jq -er '.linux.clang' "$platform_policy")
    pinned_cmake=$(jq -er '.linux.cmake' "$platform_policy")
    pinned_ninja=$(jq -er '.linux.ninja' "$platform_policy")
    pinned_gtk=$(jq -er '.linux.gtk' "$platform_policy")
    pinned_proton=$(jq -er '.linux.proton' "$platform_policy")
    pinned_execution_environment=$(jq -er \
      '.linux.executionEnvironment' "$platform_policy")
    pinned_proton_app_id=$(jq -er '.linux.protonSteamAppId' "$platform_policy")
    pinned_proton_depot_id=$(jq -er \
      '.linux.protonSteamDepotId' "$platform_policy")
    pinned_proton_manifest_id=$(jq -er \
      '.linux.protonSteamManifestId' "$platform_policy")
    pinned_proton_build_id=$(jq -er \
      '.linux.protonSteamBuildId' "$platform_policy")
    pinned_proton_source_commit=$(jq -er \
      '.linux.protonSourceCommit' "$platform_policy")
    [[ "$measured_clang" == "$pinned_clang" ]] || {
      printf 'Expected clang %s, found %s.\n' "$pinned_clang" "$measured_clang" >&2
      exit 1
    }
    [[ "$measured_cmake" == "$pinned_cmake" ]] || {
      printf 'Expected CMake %s, found %s.\n' "$pinned_cmake" "$measured_cmake" >&2
      exit 1
    }
    [[ "$measured_ninja" == "$pinned_ninja" ]] || {
      printf 'Expected Ninja %s, found %s.\n' "$pinned_ninja" "$measured_ninja" >&2
      exit 1
    }
    [[ "$measured_gtk" == "$pinned_gtk" ]] || {
      printf 'Expected GTK %s, found %s.\n' "$pinned_gtk" "$measured_gtk" >&2
      exit 1
    }
    platform_toolchains_json=$(jq -cn \
      --arg node "$node_pin" \
      --arg clang "$measured_clang" \
      --arg cmake "$measured_cmake" \
      --arg ninja "$measured_ninja" \
      --arg gtk "$measured_gtk" \
      --arg proton "$pinned_proton" \
      --arg executionEnvironment "$pinned_execution_environment" \
      --arg protonSteamAppId "$pinned_proton_app_id" \
      --arg protonSteamDepotId "$pinned_proton_depot_id" \
      --arg protonSteamManifestId "$pinned_proton_manifest_id" \
      --arg protonSteamBuildId "$pinned_proton_build_id" \
      --arg protonSourceCommit "$pinned_proton_source_commit" \
      '{
        node:$node,
        clang:$clang,
        cmake:$cmake,
        ninja:$ninja,
        gtk:$gtk,
        proton:$proton,
        executionEnvironment:$executionEnvironment,
        protonSteamAppId:$protonSteamAppId,
        protonSteamDepotId:$protonSteamDepotId,
        protonSteamManifestId:$protonSteamManifestId,
        protonSteamBuildId:$protonSteamBuildId,
        protonSourceCommit:$protonSourceCommit
      }')
    ;;
  macos)
    [[ "$(uname -s)" == Darwin && "$(uname -m)" == arm64 ]] || {
      printf 'macOS release packages require an Apple Silicon host.\n' >&2
      exit 1
    }
    [[ "$(sw_vers -productVersion | cut -d. -f1)" == 15 ]] || {
      printf 'macOS release packages require macOS 15.\n' >&2
      exit 1
    }
    arch -x86_64 /usr/bin/true || {
      printf 'Rosetta 2 is required for the x64 Dart build.\n' >&2
      exit 1
    }
    : "${TOPIAFORGE_DART_X64:?TOPIAFORGE_DART_X64 is required on macOS.}"
    : "${MACOS_CERTIFICATE_P12:?MACOS_CERTIFICATE_P12 is required.}"
    : "${MACOS_CERTIFICATE_PASSWORD:?MACOS_CERTIFICATE_PASSWORD is required.}"
    : "${MACOS_DEVELOPER_ID_APPLICATION:?MACOS_DEVELOPER_ID_APPLICATION is required.}"
    : "${MACOS_NOTARY_APPLE_ID:?MACOS_NOTARY_APPLE_ID is required.}"
    : "${MACOS_NOTARY_PASSWORD:?MACOS_NOTARY_PASSWORD is required.}"
    : "${MACOS_NOTARY_TEAM_ID:?MACOS_NOTARY_TEAM_ID is required.}"
    for command_name in xcodebuild xcrun; do
      require_command "$command_name"
    done
    measured_xcode=$(xcodebuild -version | sed -n '1s/^Xcode //p')
    measured_macos_sdk=$(xcrun --sdk macosx --show-sdk-version)
    measured_apple_clang=$(xcrun clang --version |
      sed -nE '1s/Apple clang version ([0-9]+\.[0-9]+\.[0-9]+).*/\1/p')
    pinned_xcode=$(jq -er '.macos.xcode' "$platform_policy")
    pinned_macos_sdk=$(jq -er '.macos.macosSdk' "$platform_policy")
    pinned_apple_clang=$(jq -er '.macos.appleClang' "$platform_policy")
    [[ "$measured_xcode" == "$pinned_xcode" ]] || {
      printf 'Expected Xcode %s, found %s.\n' "$pinned_xcode" "$measured_xcode" >&2
      exit 1
    }
    [[ "$measured_macos_sdk" == "$pinned_macos_sdk" ]] || {
      printf 'Expected macOS SDK %s, found %s.\n' \
        "$pinned_macos_sdk" "$measured_macos_sdk" >&2
      exit 1
    }
    [[ "$measured_apple_clang" == "$pinned_apple_clang" ]] || {
      printf 'Expected Apple clang %s, found %s.\n' \
        "$pinned_apple_clang" "$measured_apple_clang" >&2
      exit 1
    }
    platform_toolchains_json=$(jq -cn \
      --arg node "$node_pin" \
      --arg xcode "$measured_xcode" \
      --arg macosSdk "$measured_macos_sdk" \
      --arg appleClang "$measured_apple_clang" \
      '{node:$node,xcode:$xcode,macosSdk:$macosSdk,appleClang:$appleClang}')
    [[ -n "$expected_mac_team_id" &&
       "$MACOS_NOTARY_TEAM_ID" == "$expected_mac_team_id" ]] || {
      printf 'The expected macOS Team ID is missing or does not match.\n' >&2
      exit 1
    }
    x64_version=$(arch -x86_64 "$TOPIAFORGE_DART_X64" --version 2>&1)
    [[ "$x64_version" == *"Dart SDK version: $dart_pin"* ]] || {
      printf 'Expected x64 Dart %s under Rosetta.\n' "$dart_pin" >&2
      exit 1
    }
    ;;
esac

work="$output/.topiaforge-platform-$platform"
case "$work/" in
  "$output"/*) ;;
  *)
    printf 'Unsafe platform work directory.\n' >&2
    exit 1
    ;;
esac
if [[ "$work" == / || "$work" == "$repo" || "$output" == "$repo" ]]; then
  printf 'Unsafe platform work directory.\n' >&2
  exit 1
fi
cleanup() {
  if [[ -n "${launcher_health_pid:-}" ]]; then
    kill "$launcher_health_pid" 2>/dev/null || true
    wait "$launcher_health_pid" 2>/dev/null || true
  fi
  rm -rf -- "$work"
}
trap cleanup EXIT
cleanup
mkdir -p "$work/canonical" "$work/launcher" "$work/cli" "$output"
if [[ "$platform" == linux ]]; then
  rm -f -- "$output/TopiaForge-linux-x64.zip" "$output/validation-linux.json"
else
  rm -f -- "$output/TopiaForge-macos-universal.zip" "$output/validation-macos.json"
fi
tar -xf "$canonical_archive" -C "$work/canonical"

launcher_project="$repo/apps/topiaforge_launcher_flutter"
cli_project="$repo/apps/topiaforge_cli"
launcher_build_root="$launcher_project/build"
cli_tool_state="$cli_project/.dart_tool"
[[ "$launcher_build_root" == "$repo/apps/topiaforge_launcher_flutter/build" &&
   "$cli_tool_state" == "$repo/apps/topiaforge_cli/.dart_tool" ]] || {
  printf 'Unsafe project output directory.\n' >&2
  exit 1
}
rm -rf -- "$launcher_build_root" "$cli_tool_state"
dotnet clean "$repo/TopiaForge.slnx" -c Release --nologo

pushd "$launcher_project" >/dev/null
"$flutter_command" clean
"$flutter_command" pub get --enforce-lockfile
if [[ "$platform" == macos ]]; then
  export FLUTTER_XCODE_CODE_SIGNING_ALLOWED=NO
  export FLUTTER_XCODE_CODE_SIGNING_REQUIRED=NO
fi
"$flutter_command" build "$platform" --release \
  --dart-define="TOPIAFORGE_PRODUCT_VERSION=$version"
popd >/dev/null

pushd "$cli_project" >/dev/null
"$dart_command" pub get --enforce-lockfile
if [[ "$platform" == linux ]]; then
  cp -R "$repo/apps/topiaforge_launcher_flutter/build/linux/x64/release/bundle/." \
    "$work/launcher/"
  "$dart_command" compile exe bin/topiaforge.dart -o "$work/cli/topiaforge"
  package_args=(
    release build-package --platform linux --output "$output"
    --configuration Release --prebuilt-launcher "$work/launcher"
    --prebuilt-cli "$work/cli/topiaforge" --prebuilt-dist "$work/canonical"
  )
  test_args=(
    release test-package --platform linux
    --zip "$output/TopiaForge-linux-x64.zip" --run-embedded-cli
    --expected-canonical-ecosystem-sha256 "$canonical_ecosystem_sha256"
    --canonical-assets "$work/canonical"
  )
else
  cp -R "$repo/apps/topiaforge_launcher_flutter/build/macos/Build/Products/Release/TopiaForge.app" \
    "$work/launcher/"
  "$dart_command" compile exe bin/topiaforge.dart \
    -o "$work/cli/topiaforge-arm64"
  arch -x86_64 "$TOPIAFORGE_DART_X64" pub get --enforce-lockfile
  arch -x86_64 "$TOPIAFORGE_DART_X64" compile exe bin/topiaforge.dart \
    -o "$work/cli/topiaforge-x64"
  [[ "$(lipo -archs "$work/cli/topiaforge-arm64")" == arm64 ]]
  [[ "$(lipo -archs "$work/cli/topiaforge-x64")" == x86_64 ]]
  package_args=(
    release build-package --platform macos --output "$output"
    --configuration Release --prebuilt-launcher "$work/launcher"
    --prebuilt-cli "$work/cli" --prebuilt-dist "$work/canonical"
    --require-macos-signing
  )
  test_args=(
    release test-package --platform macos
    --zip "$output/TopiaForge-macos-universal.zip" --run-embedded-cli
    --require-mac-universal --require-macos-trust
    --expected-mac-team-id "$expected_mac_team_id"
    --expected-canonical-ecosystem-sha256 "$canonical_ecosystem_sha256"
    --canonical-assets "$work/canonical"
  )
fi
"$dart_command" run bin/topiaforge.dart "${package_args[@]}"
"$dart_command" run bin/topiaforge.dart "${test_args[@]}"
popd >/dev/null

if [[ "$platform" == linux ]]; then
  health_root="$work/package-health"
  health_data="$work/launcher-health-data"
  transaction_id=${source_sha:0:32}
  health_nonce=$(printf 'launcher-health:%s:%s' "$version" "$source_sha" |
    sha256sum | awk '{print $1}')
  health_transaction="$health_data/updates/transactions/$transaction_id"
  health_marker="$health_transaction/health.json"
  health_log="$work/launcher-health.log"
  mkdir -p "$health_root" "$health_transaction"
  unzip -q "$output/TopiaForge-linux-x64.zip" -d "$health_root"
  packaged_launcher="$health_root/launcher/topiaforge_launcher"
  [[ -f "$packaged_launcher" && -x "$packaged_launcher" && ! -L "$packaged_launcher" ]] || {
    printf 'The packaged Linux launcher executable is missing or unsafe.\n' >&2
    exit 1
  }
  TOPIAFORGE_DATA_ROOT="$health_data" \
    "$packaged_launcher" \
    --topiaforge-update-health-nonce "$health_nonce" \
    --topiaforge-update-health-file "$health_marker" \
    >"$health_log" 2>&1 &
  launcher_health_pid=$!
  launcher_health_deadline=$((SECONDS + 30))
  while [[ ! -f "$health_marker" ]]; do
    if ! kill -0 "$launcher_health_pid" 2>/dev/null; then
      printf 'The packaged Linux launcher exited before its first healthy frame.\n' >&2
      sed -n '1,80p' "$health_log" >&2
      exit 1
    fi
    if ((SECONDS >= launcher_health_deadline)); then
      printf 'The packaged Linux launcher did not report a healthy first frame within 30 seconds.\n' >&2
      exit 1
    fi
    sleep 0.2
  done
  jq -e \
    --arg nonce "$health_nonce" \
    --argjson processId "$launcher_health_pid" \
    '
      (keys | sort) ==
        ["formatVersion","healthy","nonce","processId","reportedAtUtc"] and
      .formatVersion == 1 and
      .healthy == true and
      .nonce == $nonce and
      .processId == $processId and
      (.reportedAtUtc | type == "string" and endswith("Z"))
    ' "$health_marker" >/dev/null || {
      printf 'The packaged Linux launcher health marker is invalid.\n' >&2
      exit 1
    }
  kill "$launcher_health_pid" 2>/dev/null || true
  wait "$launcher_health_pid" 2>/dev/null || true
  launcher_health_pid=''
fi

if [[ -n "$(git -C "$repo" status --porcelain --untracked-files=no)" ]]; then
  printf 'Platform build changed tracked source files.\n' >&2
  exit 1
fi

archive="$output/TopiaForge-$platform-x64.zip"
signing_state='not-applicable'
checks='["archive-smoke","embedded-cli","canonical-ecosystem","packaged-launcher-health"]'
if [[ "$platform" == macos ]]; then
  archive="$output/TopiaForge-macos-universal.zip"
  signing_state='developer-id-notarized-stapled'
  checks='["archive-smoke","embedded-cli","canonical-ecosystem","universal-cli","developer-id","notarization","staple"]'
fi
[[ -f "$archive" ]]
archive_sha=$(sha256_file "$archive")
validation_path="$output/validation-$platform.json"
validation_temp="$validation_path.tmp"
jq -cn \
  --arg platform "$platform" \
  --arg version "$version" \
  --arg targetSha "$source_sha" \
  --arg archiveSha256 "$archive_sha" \
  --arg canonicalEcosystemSha256 "$canonical_ecosystem_sha256" \
  --arg canonicalArchiveSha256 "$canonical_archive_sha256" \
  --arg signingState "$signing_state" \
  --argjson platformToolchains "$platform_toolchains_json" \
  --argjson checks "$checks" \
  '{
    schema:"release-local-validation-v1",
    platform:$platform,
    version:$version,
    targetSha:$targetSha,
    archiveSha256:$archiveSha256,
    canonicalEcosystemSha256:$canonicalEcosystemSha256,
    canonicalArchiveSha256:$canonicalArchiveSha256,
    signingState:$signingState,
    platformToolchains:$platformToolchains,
    checks:$checks,
    passed:true
  }' >"$validation_temp"
mv -- "$validation_temp" "$validation_path"

cleanup
trap - EXIT
printf 'Built and validated %s\n' "$archive"
