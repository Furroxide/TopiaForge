#!/usr/bin/env bash
set -euo pipefail

script_dir=$(CDPATH='' cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
repository_root=$(CDPATH='' cd -- "$script_dir/.." && pwd)
cd "$repository_root"

dotnet_command=${DOTNET_COMMAND:-dotnet}
command -v "$dotnet_command" >/dev/null 2>&1 || {
  echo "Required .NET command was not found: $dotnet_command" >&2
  exit 1
}
[[ $("$dotnet_command" --version) == 10.0.301 ]] || {
  echo "TopiaForge release validation requires .NET SDK 10.0.301." >&2
  exit 1
}
"$dotnet_command" --list-runtimes | \
  grep -E '^Microsoft\.NETCore\.App 10\.0\.9 \[' >/dev/null || {
  echo "TopiaForge release validation requires .NET runtime 10.0.9." >&2
  exit 1
}
command -v unzip >/dev/null 2>&1 || {
  echo "unzip is required for SDK package validation." >&2
  exit 1
}

temp_root=${RUNNER_TEMP:-${TMPDIR:-/tmp}}
sdk_output=$(mktemp -d "$temp_root/topiaforge-sdk-pack-audit.XXXXXX")
cleanup() {
  rm -rf -- "$sdk_output"
}
trap cleanup EXIT

projects=()
while IFS= read -r project; do
  if grep -F '<IsPackable>false</IsPackable>' "$project" >/dev/null; then
    continue
  fi
  projects+=("$project")
done < <(
  find src -mindepth 2 -maxdepth 2 -type f \
    -path 'src/TopiaForge.Mods.*/TopiaForge.Mods.*.csproj' | sort
)
[[ ${#projects[@]} -eq 12 ]] || {
  echo "Expected 12 packable TopiaForge.Mods SDK projects; found ${#projects[@]}." >&2
  exit 1
}

for project in "${projects[@]}"; do
  echo "> Building warning-free $project"
  build_command=(
    "$dotnet_command" build "$project" -c Release --no-restore
    --disable-build-servers --nologo -m:1 -nr:false
    -p:TreatWarningsAsErrors=true -p:UseSharedCompilation=false -t:Rebuild
  )
  if command -v timeout >/dev/null 2>&1; then
    timeout 5m "${build_command[@]}"
  else
    "${build_command[@]}"
  fi
  echo "> Packing $project"
  "$dotnet_command" pack "$project" -c Release --no-restore \
    --disable-build-servers --nologo -m:1 -nr:false \
    --output "$sdk_output" -p:TreatWarningsAsErrors=true
done

packages=()
while IFS= read -r package; do
  packages+=("$package")
done < <(find "$sdk_output" -maxdepth 1 -type f -name '*.nupkg' | sort)
[[ ${#packages[@]} -eq 12 ]] || {
  echo "Expected 12 SDK NuGet packages; found ${#packages[@]}." >&2
  exit 1
}
for package in "${packages[@]}"; do
  unzip -tq "$package"
  unzip -Z1 "$package" | grep -E '\.nuspec$' >/dev/null
done

interop_packages=()
while IFS= read -r package; do
  interop_packages+=("$package")
done < <(
  find "$sdk_output" -maxdepth 1 -type f \
    -name 'TopiaForge.Mods.Interop.Unity.*.nupkg' | sort
)
generator_packages=()
while IFS= read -r package; do
  generator_packages+=("$package")
done < <(
  find "$sdk_output" -maxdepth 1 -type f \
    -name 'TopiaForge.Mods.Multiplayer.Generators.*.nupkg' | sort
)
[[ ${#interop_packages[@]} -eq 1 ]]
[[ ${#generator_packages[@]} -eq 1 ]]
interop=${interop_packages[0]}
generator=${generator_packages[0]}
[[ $(unzip -Z1 "$interop" | grep -Fxc \
  'buildTransitive/TopiaForge.Mods.Interop.Unity.props') -eq 1 ]]
unzip -p "$interop" 'buildTransitive/TopiaForge.Mods.Interop.Unity.props' | \
  grep -F 'TopiaForgeSafeProject' >/dev/null
unzip -p "$interop" 'buildTransitive/TopiaForge.Mods.Interop.Unity.props' | \
  grep -F 'TF1101' >/dev/null
unzip -Z1 "$generator" | grep -Fx 'README.md' >/dev/null

harnesses=(
  tests/TopiaForge.ManagedRefs.Tests/TopiaForge.ManagedRefs.Tests.csproj
  tests/TopiaForge.ModManager.Tests/TopiaForge.ModManager.Tests.csproj
  tests/TopiaForge.ModRuntime.Tests/TopiaForge.ModRuntime.Tests.csproj
  tests/TopiaForge.ModPackageValidator.Tests/TopiaForge.ModPackageValidator.Tests.csproj
  tests/TopiaForge.Mods.Analyzers.Tests/TopiaForge.Mods.Analyzers.Tests.csproj
  tests/TopiaForge.Mods.Multiplayer.Generators.Tests/TopiaForge.Mods.Multiplayer.Generators.Tests.csproj
  tests/TopiaForge.Mods.Multiplayer.Tests/TopiaForge.Mods.Multiplayer.Tests.csproj
)
[[ ${#harnesses[@]} -eq 7 ]]
for harness in "${harnesses[@]}"; do
  [[ -f $harness ]] || {
    echo "C# release harness is missing: $harness" >&2
    exit 1
  }
  echo "> Running $harness"
  "$dotnet_command" run --project "$harness" -c Release --no-build
done

echo "Validated 12 SDK packages and all 7 C# release harnesses."
