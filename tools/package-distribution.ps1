[CmdletBinding()]
param(
    [ValidateSet("", "windows", "macos", "linux")]
    [string]$Platform = "",
    [string]$Configuration = "Release",
    [string]$OutputRoot = "",
    [switch]$SkipLauncher,
    [switch]$SkipRuntime,
    [string]$PrebuiltCli = "",
    [string]$PrebuiltLauncher = "",
    [switch]$RequireMacSigning,
    [switch]$RequireWindowsSigning
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "flutter-sdk.ps1")

function Resolve-TargetPlatform([string]$Value) {
    if ($Value -ne "") { return $Value }
    if ($env:OS -eq "Windows_NT") { return "windows" }
    if ($IsMacOS) { return "macos" }
    if ($IsLinux) { return "linux" }
    throw "Could not infer target platform. Pass -Platform windows|macos|linux."
}

function Resolve-ExistingPath([string]$Value, [string]$Label) {
    if ([string]::IsNullOrWhiteSpace($Value)) { return "" }
    if (!(Test-Path -LiteralPath $Value)) {
        throw "$Label was not found: $Value"
    }
    return (Resolve-Path -LiteralPath $Value).Path
}

$targetPlatform = Resolve-TargetPlatform $Platform
$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $repo "dist/release"
}
$OutputRoot = (New-Item -ItemType Directory -Force -Path $OutputRoot).FullName
$PrebuiltCli = Resolve-ExistingPath $PrebuiltCli "Prebuilt CLI"
$PrebuiltLauncher = Resolve-ExistingPath $PrebuiltLauncher "Prebuilt launcher"

if ($SkipLauncher -and [string]::IsNullOrWhiteSpace($PrebuiltLauncher)) {
    throw "-SkipLauncher can no longer create an incomplete release archive. " +
        "Pass -PrebuiltLauncher to reuse a complete launcher build."
}

$dart = Resolve-TopiaForgeSdkCommand -Tool dart -RepositoryRoot $repo
$cliApp = Join-Path $repo "apps/topiaforge_cli"
$cliArguments = @(
    "run", (Join-Path "bin" "topiaforge.dart"),
    "release", "build-package",
    "--platform", $targetPlatform,
    "--output", $OutputRoot,
    "--configuration", $Configuration
)
if (![string]::IsNullOrWhiteSpace($PrebuiltLauncher)) {
    $cliArguments += @("--prebuilt-launcher", $PrebuiltLauncher)
}
if (![string]::IsNullOrWhiteSpace($PrebuiltCli)) {
    $cliArguments += @("--prebuilt-cli", $PrebuiltCli)
}
if ($SkipRuntime) { $cliArguments += "--skip-runtime-build" }
if ($RequireMacSigning) { $cliArguments += "--require-macos-signing" }
if ($RequireWindowsSigning) { $cliArguments += "--require-windows-signing" }

Push-Location $cliApp
try {
    & $dart @cliArguments
    if ($LASTEXITCODE -ne 0) {
        throw "Canonical release package build failed (exit $LASTEXITCODE)."
    }
}
finally {
    Pop-Location
}
