# DEPRECATED: this wrapper forwards to the cross-platform CLI and will be removed in a future release.
# Use instead:  dart run bin/robotopia.dart unity build-ui-bundle [--unity <editor>]   (from apps/robotopia_cli)
param(
    [string]$UnityExe = ""
)

$ErrorActionPreference = "Stop"
Write-Warning "tools/build-ui-bundle.ps1 is deprecated; use 'robotopia unity build-ui-bundle' instead."

$repo = Resolve-Path (Join-Path $PSScriptRoot "..")
$cliApp = Join-Path $repo "apps/robotopia_cli"
. (Join-Path $PSScriptRoot "flutter-sdk.ps1")
$dart = Resolve-RobotopiaSdkCommand -Tool dart -RepositoryRoot $repo

Push-Location $cliApp
try {
    & $dart pub get
    if ($LASTEXITCODE -ne 0) { throw "dart pub get failed (exit $LASTEXITCODE)." }

    $commandArguments = @("run", (Join-Path "bin" "robotopia.dart"), "unity", "build-ui-bundle")
    if ($UnityExe -ne "") {
        $commandArguments += @("--unity", $UnityExe)
    }
    & $dart @commandArguments
    if ($LASTEXITCODE -ne 0) { throw "robotopia unity build-ui-bundle failed (exit $LASTEXITCODE)." }
}
finally {
    Pop-Location
}
