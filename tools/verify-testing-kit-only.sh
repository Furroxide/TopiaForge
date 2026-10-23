#!/usr/bin/env bash
set -euo pipefail

script_dir=$(CDPATH='' cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
repository_root=$(CDPATH='' cd -- "$script_dir/.." && pwd)
cd "$repository_root"

dotnet_command=${DOTNET_COMMAND:-dotnet}
project=tests/TopiaForge.ModManager.Tests/TopiaForge.ModManager.Tests.csproj
temp_root=${RUNNER_TEMP:-${TMPDIR:-/tmp}}
temp_root=$(CDPATH='' cd -- "$temp_root" && pwd)
mode_output=$(mktemp -d "$temp_root/topiaforge-testing-kit.XXXXXX")
cleanup() {
  # Only this invocation's direct child of the resolved temporary root may be removed.
  case "$mode_output" in
    "$temp_root"/topiaforge-testing-kit.??????) rm -rf -- "$mode_output" ;;
    *) echo "Refusing unexpected testing-kit cleanup target." >&2; return 1 ;;
  esac
}
trap cleanup EXIT

echo "> Building the Unity-free TestingKitOnly harness in isolated artifacts"
"$dotnet_command" build "$project" -c Release --artifacts-path "$mode_output" \
  --nologo -m:1 -nr:false -p:TestingKitOnly=true -p:TreatWarningsAsErrors=true \
  -p:RobotopiaManagedDir=testing-kit-no-game-references
echo "> Running the focused testing-kit harness"
focused_output=$("$dotnet_command" run --project "$project" -c Release --no-build \
  --artifacts-path "$mode_output" -p:TestingKitOnly=true \
  -p:RobotopiaManagedDir=testing-kit-no-game-references)
printf '%s\n' "$focused_output"
[[ ${focused_output//$'\r'/} == "TestingKitTests passed." ]] || {
  echo "Expected only the focused testing-kit result; the full harness must keep its own output." >&2
  exit 1
}
