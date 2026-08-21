[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Assert-Equal {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Expected,

        [Parameter(Mandatory = $true)]
        [object]$Actual,

        [Parameter(Mandatory = $true)]
        [string]$Message
    )

    if ($Expected -cne $Actual) {
        throw "$Message Expected '$Expected'; got '$Actual'."
    }
}

$manifestPath = Join-Path $PSScriptRoot "releases.json"
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
Assert-Equal 1 $manifest.schemaVersion "Unexpected manifest schema."
Assert-Equal "3.44.6" $manifest.flutterVersion "Flutter must remain pinned."
Assert-Equal "3.12.2" $manifest.dartVersion "Dart must remain pinned."
Assert-Equal `
    "https://storage.googleapis.com/flutter_infra_release/releases/" `
    $manifest.baseUri `
    "Unexpected Flutter archive origin."

$expected = [ordered]@{
    "Linux/X64" = @(
        "stable/linux/flutter_linux_3.44.6-stable.tar.xz",
        "a6320fd72e9a2690c08e2a6a70874a30cb120dee7c78f49d2c628bd7c9e20525"
    )
    "Windows/X64" = @(
        "stable/windows/flutter_windows_3.44.6-stable.zip",
        "2e803e240c981733ec6b543752415196ff1b16e03f93474bddd59c777ac07a56"
    )
    "macOS/X64" = @(
        "stable/macos/flutter_macos_3.44.6-stable.zip",
        "d698aeb050198878ec6d5cdc1e6ccf1ef6850ec336985b03812b2525df2289ce"
    )
    "macOS/ARM64" = @(
        "stable/macos/flutter_macos_arm64_3.44.6-stable.zip",
        "e4824875f22cc7e0f4878bfc8b9382e98ff0137074c5d3ab4686f7a4b5ac775c"
    )
}

Assert-Equal $expected.Count @($manifest.archives).Count "Unexpected archive count."
foreach ($archive in $manifest.archives) {
    $key = "$($archive.runnerOs)/$($archive.runnerArch)"
    if (-not $expected.Contains($key)) {
        throw "Unexpected Flutter runner archive '$key'."
    }

    Assert-Equal $expected[$key][0] $archive.relativeUri "Unexpected archive path for $key."
    Assert-Equal $expected[$key][1] $archive.sha256 "Unexpected archive digest for $key."
}

$action = Get-Content -LiteralPath (Join-Path $PSScriptRoot "action.yml") -Raw
if ($action -notmatch
    'actions/cache@0057852bfaa89a56745cba8c7296529d2fc39830') {
    throw "The Flutter setup action must pin actions/cache to its approved full SHA."
}

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "../../..")).Path
$workflowDirectory = Join-Path $repositoryRoot ".github/workflows"
$allWorkflows = (
    Get-ChildItem -LiteralPath $workflowDirectory -File |
        Where-Object Extension -In @(".yml", ".yaml") |
        ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw }
) -join [Environment]::NewLine

if ($allWorkflows -match 'subosito/flutter-action@') {
    throw "Workflows must not use subosito/flutter-action or its transitive unpinned actions."
}

foreach ($workflowName in @("ci.yml", "deploy-pages.yml", "flutter-launcher-builds.yml")) {
    $workflow = Get-Content -LiteralPath (Join-Path $workflowDirectory $workflowName) -Raw
    if ($workflow -notmatch 'uses:\s+\./\.github/actions/setup-flutter(?:\s|$)') {
        throw "$workflowName must use the repository's verified Flutter setup action."
    }
}

# The installer's ownership guard is only meaningful while the action keeps pointing both
# directories at their dedicated runner-temp leaves, so pin that decision where it is made.
if ($action -notmatch
    ('(?m)^\s+TOPIAFORGE_FLUTTER_INSTALL_DIRECTORY:\s+' +
        '\$\{\{\s*runner\.temp\s*\}\}/topiaforge-flutter-sdk\s*$')) {
    throw "The Flutter setup action must install into its dedicated runner-temp directory."
}
if ($action -notmatch
    ('(?m)^\s+TOPIAFORGE_FLUTTER_ARCHIVE_DIRECTORY:\s+' +
        '\$\{\{\s*runner\.temp\s*\}\}/topiaforge-flutter-archives\s*$')) {
    throw "The Flutter setup action must cache archives in its dedicated runner-temp directory."
}

# The installer recursively deletes its install directory, so prove it refuses a directory it
# does not own, and refuses it before creating anything.
$installer = Join-Path $PSScriptRoot "install-flutter.ps1"
$rejectedDirectory = Join-Path ([System.IO.Path]::GetTempPath()) (
    "topiaforge-flutter-guard-" + [Guid]::NewGuid().ToString("N")
)
$guardVariableNames = @(
    "RUNNER_OS",
    "RUNNER_ARCH",
    "TOPIAFORGE_FLUTTER_ARCHIVE_DIRECTORY",
    "TOPIAFORGE_FLUTTER_INSTALL_DIRECTORY"
)
$previousEnvironment = @{}
foreach ($name in $guardVariableNames) {
    $previousEnvironment[$name] = [Environment]::GetEnvironmentVariable($name)
}

# A non-zero child exit must surface as $LASTEXITCODE here, not as a terminating error.
$restoreNativePreference = Test-Path Variable:PSNativeCommandUseErrorActionPreference
$previousNativePreference = if ($restoreNativePreference) {
    $PSNativeCommandUseErrorActionPreference
} else {
    $null
}

try {
    if ($restoreNativePreference) {
        $PSNativeCommandUseErrorActionPreference = $false
    }

    $env:RUNNER_OS = "Linux"
    $env:RUNNER_ARCH = "X64"
    $env:TOPIAFORGE_FLUTTER_ARCHIVE_DIRECTORY = Join-Path $rejectedDirectory "archives"
    $env:TOPIAFORGE_FLUTTER_INSTALL_DIRECTORY = $rejectedDirectory
    $guardOutput = (& pwsh -NoLogo -NoProfile -NonInteractive -File $installer 2>&1) |
        Out-String
    $guardExitCode = $LASTEXITCODE
    if ($guardExitCode -eq 0) {
        throw "The Flutter installer must refuse an install directory it does not own."
    }
    if ($guardOutput -notmatch "must name a dedicated") {
        throw "The Flutter installer refused the install directory for the wrong reason: " +
            $guardOutput
    }
}
finally {
    # The child is expected to fail, so keep its exit status out of this script's own.
    $global:LASTEXITCODE = 0
    if ($restoreNativePreference) {
        $PSNativeCommandUseErrorActionPreference = $previousNativePreference
    }

    foreach ($name in $guardVariableNames) {
        [Environment]::SetEnvironmentVariable($name, $previousEnvironment[$name])
    }
}

if (Test-Path -LiteralPath $rejectedDirectory) {
    throw "The Flutter installer must reject an unowned install directory before creating it."
}

Write-Host "Pinned Flutter setup contract tests passed."
