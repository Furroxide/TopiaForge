[CmdletBinding()]
param(
    [string]$CacheRoot = "",
    [switch]$SkipManagedRefs,
    [switch]$Verify
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"

$RepoRoot = Split-Path -Parent $PSScriptRoot
$PinnedFlutter = "3.41.4"
. (Join-Path $PSScriptRoot "flutter-sdk.ps1")

function Invoke-Checked {
    param(
        [Parameter(Mandatory = $true)][string]$Command,
        [string[]]$Arguments = @(),
        [string]$WorkingDirectory = $RepoRoot
    )
    Write-Host "> $Command $($Arguments -join ' ')"
    Push-Location $WorkingDirectory
    try {
        & $Command @Arguments
        if ($LASTEXITCODE -ne 0) {
            throw "Command failed with exit code ${LASTEXITCODE}: $Command $($Arguments -join ' ')"
        }
    }
    finally {
        Pop-Location
    }
}

function Require-Command {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$InstallHint
    )
    $command = Get-Command $Name -ErrorAction SilentlyContinue
    if ($null -eq $command) {
        throw "$Name was not found on PATH. $InstallHint"
    }
    return $command.Source
}

function Resolve-SevenZip {
    $candidates = @("7z", "7zz", "7za")
    if (![string]::IsNullOrWhiteSpace($env:ProgramFiles)) {
        $candidates += Join-Path $env:ProgramFiles "7-Zip/7z.exe"
    }
    if (![string]::IsNullOrWhiteSpace(${env:ProgramFiles(x86)})) {
        $candidates += Join-Path ${env:ProgramFiles(x86)} "7-Zip/7z.exe"
    }
    foreach ($name in $candidates) {
        $command = Get-Command $name -ErrorAction SilentlyContinue
        if ($command) {
            return $command.Source
        }
        if (Test-Path -LiteralPath $name) {
            return $name
        }
    }
    throw "7-Zip was not found. Install sevenzip on macOS or 7zip.7zip on Windows."
}

function Get-DefaultCacheRoot {
    if ($IsWindows) {
        return Join-Path ([Environment]::GetFolderPath("LocalApplicationData")) "Robotopia/managed-refs"
    }
    if ($IsMacOS) {
        return Join-Path $HOME "Library/Caches/Robotopia/managed-refs"
    }
    $base = if ([string]::IsNullOrWhiteSpace($env:XDG_CACHE_HOME)) {
        Join-Path $HOME ".cache"
    }
    else {
        $env:XDG_CACHE_HOME
    }
    return Join-Path $base "Robotopia/managed-refs"
}

function Restore-DartPackage {
    param([Parameter(Mandatory = $true)][string]$RelativePath)
    Invoke-Checked $script:DartCommand @("pub", "get", "--enforce-lockfile") (Join-Path $RepoRoot $RelativePath)
}

function Restore-FlutterPackage {
    param([Parameter(Mandatory = $true)][string]$RelativePath)
    Invoke-Checked $script:FlutterCommand @("pub", "get", "--enforce-lockfile") (Join-Path $RepoRoot $RelativePath)
}

function Verify-DartPackage {
    param([Parameter(Mandatory = $true)][string]$RelativePath)
    $path = Join-Path $RepoRoot $RelativePath
    Invoke-Checked $script:DartCommand @("analyze", "--fatal-infos") $path
    Invoke-Checked $script:DartCommand @("test") $path
}

function Verify-FlutterPackage {
    param([Parameter(Mandatory = $true)][string]$RelativePath)
    $path = Join-Path $RepoRoot $RelativePath
    Invoke-Checked $script:FlutterCommand @("analyze") $path
    Invoke-Checked $script:FlutterCommand @("test") $path
}

Set-Location $RepoRoot

$dotnet = Require-Command "dotnet" "Install the exact .NET SDK 10.0.301 pinned by global.json."
$git = Require-Command "git" "Install Git for your platform."
$gitLfs = Require-Command "git-lfs" "Install git-lfs with Homebrew or winget."
$fvm = Require-Command "fvm" "Follow docs/ContributorSetup.md to install the standalone FVM executable."
$sevenZip = if ($SkipManagedRefs) { $null } else { Resolve-SevenZip }
$node = Get-Command "node" -ErrorAction SilentlyContinue
$npm = Get-Command "npm" -ErrorAction SilentlyContinue
$cocoaPods = if ($IsMacOS) { Get-Command "pod" -ErrorAction SilentlyContinue } else { $null }

$dotnetVersion = (& $dotnet --version).Trim()
$requiredDotnetVersion = "10.0.301"
if ($dotnetVersion -ne $requiredDotnetVersion) {
    throw ".NET SDK $requiredDotnetVersion is required by global.json; found $dotnetVersion. Install the pinned SDK without changing rollForward."
}

if ($IsMacOS -and !$cocoaPods) {
    if ($Verify) {
        throw "CocoaPods was not found on PATH. Install it with 'brew install cocoapods' before using -Verify on macOS."
    }
    Write-Warning "CocoaPods was not found; macOS launcher builds will be unavailable. Install it with 'brew install cocoapods'."
}

$nodeVersion = ""
if ($node) {
    $nodeVersion = (& $node.Source --version).Trim()
    if ($nodeVersion -notmatch '^v?(?<major>[0-9]+)' -or [int]$Matches.major -lt 20) {
        Write-Warning "Node.js 20 or newer is required for the optional Automerge sidecar; found '$nodeVersion'. Skipping sidecar restore."
        $node = $null
    }
    elseif (!$npm) {
        Write-Warning "npm was not found on PATH. Skipping the optional Automerge sidecar restore."
        $node = $null
    }
}

Write-Host "QuantumWorks contributor bootstrap"
Write-Host "  .NET: $dotnetVersion"
Write-Host "  FVM: $fvm"
Write-Host "  Git LFS: $gitLfs"
if ($sevenZip) {
    Write-Host "  7-Zip: $sevenZip"
}
else {
    Write-Host "  7-Zip: skipped because managed-reference restore is disabled"
}
if ($node) {
    Write-Host "  Node: $nodeVersion"
}
else {
    Write-Warning "A usable Node.js 20+/npm toolchain was not found; the optional Automerge sidecar will not be restored."
}

Invoke-Checked $git @("config", "core.hooksPath", ".githooks")
if (!$IsWindows) {
    Get-ChildItem -LiteralPath (Join-Path $RepoRoot ".githooks") -File |
        ForEach-Object { & chmod +x $_.FullName }
}
Invoke-Checked $git @("lfs", "install", "--local")
Invoke-Checked $git @("lfs", "fsck")

Invoke-Checked $fvm @("install", $PinnedFlutter, "--skip-pub-get")
Invoke-Checked $fvm @("use", $PinnedFlutter, "--force", "--skip-pub-get")
$script:DartCommand = Resolve-RobotopiaSdkCommand -Tool dart -RepositoryRoot $RepoRoot
$script:FlutterCommand = Resolve-RobotopiaSdkCommand -Tool flutter -RepositoryRoot $RepoRoot
Write-Host "  Dart SDK command: $script:DartCommand"
Write-Host "  Flutter SDK command: $script:FlutterCommand"

foreach ($package in @(
    "packages/launcher_domain",
    "packages/launcher_data",
    "apps/robotopia_cli"
)) {
    Restore-DartPackage $package
}
foreach ($package in @(
    "packages/launcher_ui",
    "apps/robotopia_launcher_flutter"
)) {
    Restore-FlutterPackage $package
}

$sidecar = Join-Path $RepoRoot "tools/ugc-automerge-sidecar"
$sidecarRestored = $false
if ($node -and $npm) {
    if (!(Test-Path -LiteralPath (Join-Path $sidecar "package-lock.json"))) {
        throw "The Automerge sidecar package-lock.json is missing; refusing a non-deterministic npm restore."
    }
    Invoke-Checked $npm.Source @("ci", "--no-audit", "--no-fund") $sidecar
    $sidecarRestored = $true
}

Invoke-Checked $dotnet @("restore", "RobotopiaModManager.slnx")

if (!$SkipManagedRefs) {
    if ([string]::IsNullOrWhiteSpace($CacheRoot)) {
        $CacheRoot = Get-DefaultCacheRoot
    }
    Write-Host "Managed-reference cache: $CacheRoot"
    & (Join-Path $PSScriptRoot "restore-robotopia-managed-refs.ps1") `
        -CacheRoot $CacheRoot `
        -WriteLocalProps
    if ($LASTEXITCODE -ne 0) {
        throw "Managed-reference restore failed with exit code $LASTEXITCODE."
    }
}

if ($Verify) {
    Invoke-Checked $dotnet @("build", "RobotopiaModManager.slnx", "-c", "Release", "--no-restore")
    Invoke-Checked $dotnet @(
        "run", "--project", "tests/Robotopia.ModManager.Tests/Robotopia.ModManager.Tests.csproj",
        "-c", "Release", "--no-build"
    )

    foreach ($package in @(
        "packages/launcher_domain",
        "packages/launcher_data",
        "apps/robotopia_cli"
    )) {
        Verify-DartPackage $package
    }
    foreach ($package in @(
        "packages/launcher_ui",
        "apps/robotopia_launcher_flutter"
    )) {
        Verify-FlutterPackage $package
    }

    if ($sidecarRestored) {
        Invoke-Checked $node.Source @("--check", "index.mjs") $sidecar
        Invoke-Checked $npm.Source @("test") $sidecar
        Invoke-Checked $node.Source @("index.mjs", "--check") $sidecar
    }

    $platform = if ($IsWindows) { "windows" } elseif ($IsMacOS) { "macos" } else { "linux" }
    Invoke-Checked $script:FlutterCommand @("build", $platform, "--debug") `
        (Join-Path $RepoRoot "apps/robotopia_launcher_flutter")
    Invoke-Checked $git @("lfs", "fsck")
}

Write-Host "QuantumWorks bootstrap complete."
if (!$Verify) {
    Write-Host "Run again with -Verify to execute the complete contributor test suite."
}
