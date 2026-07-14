[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet("windows", "macos", "linux")]
    [string]$Platform,
    [Parameter(Mandatory = $true)]
    [string]$ZipPath,
    [switch]$RequireMacUniversal,
    [switch]$RequireWindowsSignature
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "flutter-sdk.ps1")

$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
if (!(Test-Path -LiteralPath $ZipPath -PathType Leaf)) {
    throw "Release archive was not found: $ZipPath"
}
$ZipPath = (Resolve-Path -LiteralPath $ZipPath).Path
$dart = Resolve-TopiaForgeSdkCommand -Tool dart -RepositoryRoot $repo
$cliApp = Join-Path $repo "apps/topiaforge_cli"
$cliArguments = @(
    "run", (Join-Path "bin" "topiaforge.dart"),
    "release", "test-package",
    "--platform", $Platform,
    "--zip", $ZipPath,
    "--run-embedded-cli"
)
if ($RequireMacUniversal) { $cliArguments += "--require-mac-universal" }
if ($RequireWindowsSignature) {
    $cliArguments += "--require-windows-signature"
}

Push-Location $cliApp
try {
    & $dart @cliArguments
    if ($LASTEXITCODE -ne 0) {
        throw "Canonical release package validation failed (exit $LASTEXITCODE)."
    }
}
finally {
    Pop-Location
}
