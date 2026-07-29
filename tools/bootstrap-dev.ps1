[CmdletBinding()]
param(
    [string]$CacheRoot = "",
    [switch]$SkipManagedRefs,
    [switch]$Verify
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"

$RepoRoot = Split-Path -Parent $PSScriptRoot
$PinnedFlutter = "3.44.6"
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

function Assert-WindowsLongPathSupport {
    if (!$IsWindows) {
        return
    }
    $registryPath = "HKLM:\SYSTEM\CurrentControlSet\Control\FileSystem"
    $enabled = Get-ItemPropertyValue -LiteralPath $registryPath -Name "LongPathsEnabled" -ErrorAction SilentlyContinue
    if ($enabled -ne 1) {
        throw "Windows long-path support is required for the locked NuGet and Flutter dependency trees. From an elevated PowerShell terminal, run: New-ItemProperty -LiteralPath '$registryPath' -Name LongPathsEnabled -PropertyType DWord -Value 1 -Force. Then open a new terminal and rerun bootstrap."
    }
}

function Assert-WindowsSymlinkSupport {
    if (!$IsWindows) {
        return
    }
    $probeRoot = Join-Path ([System.IO.Path]::GetTempPath()) "topiaforge-symlink-probe-$PID-$([guid]::NewGuid().ToString('N'))"
    try {
        $null = New-Item -ItemType Directory -Path $probeRoot
        $target = Join-Path $probeRoot "target.txt"
        $link = Join-Path $probeRoot "link.txt"
        [System.IO.File]::WriteAllText($target, "probe")
        $null = New-Item -ItemType SymbolicLink -Path $link -Target $target -ErrorAction Stop
    }
    catch {
        $registryPath = "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock"
        throw "Windows symlink creation is required by FVM and the security test fixtures. Enable Developer Mode in Windows Settings, or from an elevated PowerShell terminal run: New-ItemProperty -LiteralPath '$registryPath' -Name AllowDevelopmentWithoutDevLicense -PropertyType DWord -Value 1 -Force. Then open a new terminal and rerun bootstrap."
    }
    finally {
        if (Test-Path -LiteralPath $probeRoot) {
            Remove-Item -LiteralPath $probeRoot -Recurse -Force
        }
    }
}

function Normalize-WindowsFlutterDartSources {
    param(
        [Parameter(Mandatory = $true)][string]$FlutterSdkRoot,
        [Parameter(Mandatory = $true)][string]$GitCommand
    )
    if (!$IsWindows) {
        return
    }

    $packagesRoot = Join-Path $FlutterSdkRoot "packages"
    $gitMetadata = Join-Path $FlutterSdkRoot ".git"
    $isGitCheckout = Test-Path -LiteralPath $gitMetadata
    if ($isGitCheckout) {
        & $GitCommand -C $FlutterSdkRoot diff --quiet --ignore-submodules
        if ($LASTEXITCODE -ne 0) {
            throw "The FVM-managed Flutter SDK contains local changes. Preserve or remove them before running bootstrap verification."
        }
        & $GitCommand -C $FlutterSdkRoot diff --cached --quiet --ignore-submodules
        if ($LASTEXITCODE -ne 0) {
            throw "The FVM-managed Flutter SDK contains staged changes. Preserve or remove them before running bootstrap verification."
        }
        Invoke-Checked $GitCommand @("-C", $FlutterSdkRoot, "config", "core.autocrlf", "false")
    }

    # Dartdoc 9.0.4 calculates @docImport offsets against LF text. Git for
    # Windows can check out the pinned Flutter sources as CRLF, causing dartdoc
    # to crash before it reaches this repository's documentation.
    $normalized = 0
    $utf8 = [System.Text.UTF8Encoding]::new($false, $true)
    foreach ($file in Get-ChildItem -LiteralPath $packagesRoot -Recurse -File -Filter "*.dart") {
        $bytes = [System.IO.File]::ReadAllBytes($file.FullName)
        $text = $utf8.GetString($bytes)
        if (!$text.Contains("`r`n")) {
            continue
        }
        $output = $utf8.GetBytes($text.Replace("`r`n", "`n"))
        [System.IO.File]::WriteAllBytes($file.FullName, $output)
        $normalized++
    }

    if ($isGitCheckout) {
        & $GitCommand -C $FlutterSdkRoot diff --quiet --ignore-submodules
        if ($LASTEXITCODE -ne 0) {
            throw "Flutter SDK line-ending normalization changed content unexpectedly."
        }
        Invoke-Checked $GitCommand @("-C", $FlutterSdkRoot, "add", "-u", "--", "packages")
        & $GitCommand -C $FlutterSdkRoot diff --cached --quiet --ignore-submodules
        if ($LASTEXITCODE -ne 0) {
            throw "Flutter SDK line-ending normalization changed the managed SDK index unexpectedly."
        }
    }
    if ($normalized -gt 0) {
        Write-Host "  Normalized $normalized Flutter Dart source file(s) to LF for Windows dartdoc."
    }
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
Assert-WindowsLongPathSupport
Assert-WindowsSymlinkSupport

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
    $requiredNodeVersion = [version]"24.16.0"
    if ($nodeVersion -notmatch '^v?(?<version>[0-9]+\.[0-9]+\.[0-9]+)$' -or [version]$Matches.version -lt $requiredNodeVersion) {
        Write-Warning "Node.js $requiredNodeVersion or newer is required for the documentation portal and optional Automerge sidecar; found '$nodeVersion'. Skipping documentation and sidecar restore."
        $node = $null
    }
    elseif (!$npm) {
        Write-Warning "npm was not found on PATH. Skipping documentation and sidecar restore."
        $node = $null
    }
}

if ($Verify -and (!$node -or !$npm)) {
    throw "Node.js 24.16.0 or newer with npm is required by -Verify for documentation and sidecar checks."
}

Write-Host "TopiaForge contributor bootstrap"
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
    Write-Warning "A usable Node.js 24.16+/npm toolchain was not found; documentation and the optional Automerge sidecar will not be restored."
}

Invoke-Checked $git @("config", "core.hooksPath", ".githooks")
if ($IsWindows) {
    Invoke-Checked $git @("config", "core.longpaths", "true")
}
if (!$IsWindows) {
    Get-ChildItem -LiteralPath (Join-Path $RepoRoot ".githooks") -File |
        ForEach-Object { & chmod +x $_.FullName }
}
Invoke-Checked $git @("lfs", "install", "--local")
Invoke-Checked $git @("lfs", "fsck")

Invoke-Checked $fvm @("install", $PinnedFlutter, "--skip-pub-get")
Invoke-Checked $fvm @("use", $PinnedFlutter, "--force", "--skip-pub-get")
$flutterSdkRoot = (Resolve-Path -LiteralPath (Join-Path $RepoRoot ".fvm/flutter_sdk")).Path
if ($Verify) {
    Normalize-WindowsFlutterDartSources -FlutterSdkRoot $flutterSdkRoot -GitCommand $git
}
$script:DartCommand = Resolve-TopiaForgeSdkCommand -Tool dart -RepositoryRoot $RepoRoot
$script:FlutterCommand = Resolve-TopiaForgeSdkCommand -Tool flutter -RepositoryRoot $RepoRoot
$flutterSdkBin = Split-Path -Parent $script:DartCommand
$env:Path = "$flutterSdkBin$([System.IO.Path]::PathSeparator)$env:Path"
$env:FLUTTER_ROOT = $flutterSdkRoot
$env:TOPIAFORGE_DART_BIN = if ($IsWindows) {
    Join-Path $flutterSdkRoot "bin/cache/dart-sdk/bin/dart.exe"
}
else {
    Join-Path $flutterSdkRoot "bin/cache/dart-sdk/bin/dart"
}
Write-Host "  Dart SDK command: $script:DartCommand"
Write-Host "  Flutter SDK command: $script:FlutterCommand"

foreach ($package in @(
    "packages/launcher_domain",
    "packages/launcher_data",
    "apps/topiaforge_cli"
)) {
    Restore-DartPackage $package
}
foreach ($package in @(
    "packages/launcher_ui",
    "apps/topiaforge_launcher_flutter"
)) {
    Restore-FlutterPackage $package
}

$website = Join-Path $RepoRoot "website"
$sidecar = Join-Path $RepoRoot "tools/ugc-automerge-sidecar"
$websiteRestored = $false
$sidecarRestored = $false
if ($node -and $npm) {
    foreach ($npmPackage in @($website, $sidecar)) {
        if (!(Test-Path -LiteralPath (Join-Path $npmPackage "package-lock.json"))) {
            throw "The package-lock.json file is missing from $npmPackage; refusing a non-deterministic npm restore."
        }
        Invoke-Checked $npm.Source @("ci", "--ignore-scripts", "--no-audit", "--no-fund") $npmPackage
    }
    $websiteRestored = $true
    $sidecarRestored = $true
}

if (!$SkipManagedRefs) {
    if ([string]::IsNullOrWhiteSpace($CacheRoot)) {
        $CacheRoot = Get-DefaultCacheRoot
    }
    Write-Host "Managed-reference cache: $CacheRoot"
    Invoke-Checked $dotnet @(
        "run",
        "--project", "tools/TopiaForge.ManagedRefs/TopiaForge.ManagedRefs.csproj",
        "-c", "Release",
        "--",
        "--cache-root", $CacheRoot,
        "--write-local-props"
    )
}

Invoke-Checked $dotnet @("restore", "TopiaForge.slnx")

if ($Verify) {
    Invoke-Checked $dotnet @(
        "run", "--project", "tests/TopiaForge.ManagedRefs.Tests/TopiaForge.ManagedRefs.Tests.csproj",
        "-c", "Release"
    )
    Invoke-Checked $dotnet @("build", "TopiaForge.slnx", "-c", "Release", "--no-restore")
    Invoke-Checked $dotnet @(
        "run", "--project", "tests/TopiaForge.ModManager.Tests/TopiaForge.ModManager.Tests.csproj",
        "-c", "Release", "--no-build"
    )
    Invoke-Checked $dotnet @(
        "run", "--project", "tests/TopiaForge.ModRuntime.Tests/TopiaForge.ModRuntime.Tests.csproj",
        "-c", "Release", "--no-build"
    )
    Invoke-Checked $dotnet @(
        "run", "--project", "tests/TopiaForge.ModPackageValidator.Tests/TopiaForge.ModPackageValidator.Tests.csproj",
        "-c", "Release", "--no-build"
    )
    Invoke-Checked $dotnet @(
        "run", "--project", "tests/TopiaForge.Mods.Analyzers.Tests/TopiaForge.Mods.Analyzers.Tests.csproj",
        "-c", "Release", "--no-build"
    )
    Invoke-Checked $dotnet @(
        "run", "--project", "tests/TopiaForge.Mods.Multiplayer.Generators.Tests/TopiaForge.Mods.Multiplayer.Generators.Tests.csproj",
        "-c", "Release", "--no-build"
    )
    Invoke-Checked $dotnet @(
        "run", "--project", "tests/TopiaForge.Mods.Multiplayer.Tests/TopiaForge.Mods.Multiplayer.Tests.csproj",
        "-c", "Release", "--no-build"
    )
    foreach ($package in @(
        "packages/launcher_domain",
        "packages/launcher_data",
        "apps/topiaforge_cli"
    )) {
        Verify-DartPackage $package
    }
    foreach ($package in @(
        "packages/launcher_ui",
        "apps/topiaforge_launcher_flutter"
    )) {
        Verify-FlutterPackage $package
    }

    if ($sidecarRestored) {
        Invoke-Checked $node.Source @("--check", "index.mjs") $sidecar
        Invoke-Checked $npm.Source @("test") $sidecar
        Invoke-Checked $node.Source @("index.mjs", "--check") $sidecar
    }

    if ($websiteRestored) {
        Invoke-Checked $dotnet @("tool", "restore")
        Invoke-Checked $npm.Source @("run", "check") $website
    }

    $platform = if ($IsWindows) { "windows" } elseif ($IsMacOS) { "macos" } else { "linux" }
    Invoke-Checked $script:FlutterCommand @("build", $platform, "--debug") `
        (Join-Path $RepoRoot "apps/topiaforge_launcher_flutter")
    Invoke-Checked $git @("lfs", "fsck")
}

Write-Host "TopiaForge bootstrap complete."
if (!$Verify) {
    Write-Host "Run again with -Verify to execute the local contributor verification suite."
}
