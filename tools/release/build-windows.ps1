[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$RepositoryRoot,

    [Parameter(Mandatory = $true)]
    [ValidatePattern("^[0-9a-f]{40}$")]
    [string]$SourceSha,

    [Parameter(Mandatory = $true)]
    [string]$Version,

    [Parameter(Mandatory = $true)]
    [string]$CanonicalArchive,

    [Parameter(Mandatory = $true)]
    [ValidatePattern("^[0-9a-f]{64}$")]
    [string]$CanonicalEcosystemSha256,

    [Parameter(Mandatory = $true)]
    [ValidatePattern("^[0-9a-f]{64}$")]
    [string]$CanonicalArchiveSha256,

    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory,

    [Parameter(Mandatory = $true)]
    [string]$PrivateEvidenceDirectory,

    [Parameter(Mandatory = $true)]
    [string]$DartPath,

    [Parameter(Mandatory = $true)]
    [string]$FlutterPath,

    [Parameter(Mandatory = $true)]
    [string]$UnityPath,

    [Parameter(Mandatory = $true)]
    [string]$GameDirectory
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

function Invoke-Checked {
    param(
        [Parameter(Mandatory = $true)]
        [string]$FilePath,
        [Parameter()]
        [string[]]$Arguments = @(),
        [Parameter()]
        [string]$WorkingDirectory = $RepositoryRoot
    )

    Push-Location $WorkingDirectory
    try {
        & $FilePath @Arguments
        if ($LASTEXITCODE -ne 0) {
            throw "'$FilePath' failed with exit code $LASTEXITCODE."
        }
    }
    finally {
        Pop-Location
    }
}

function Get-Sha256 {
    param([Parameter(Mandatory = $true)][string]$Path)
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Invoke-PackagedLauncherHealthCheck {
    param(
        [Parameter(Mandatory = $true)][string]$ArchivePath,
        [Parameter(Mandatory = $true)][string]$WorkDirectory
    )

    $extract = Join-Path $WorkDirectory "package-health"
    $dataRoot = Join-Path $WorkDirectory "launcher-health-data"
    New-Item -ItemType Directory -Force -Path $extract | Out-Null
    Expand-Archive -LiteralPath $ArchivePath -DestinationPath $extract
    $launcher = Join-Path $extract "launcher/topiaforge_launcher.exe"
    if (-not (Test-Path -LiteralPath $launcher -PathType Leaf)) {
        throw "The packaged Windows launcher executable is missing."
    }

    $transactionId = $SourceSha.Substring(0, 32)
    $nonce = Get-Sha256FromUtf8 "launcher-health:$Version`:$SourceSha"
    $transaction = Join-Path $dataRoot "updates/transactions/$transactionId"
    New-Item -ItemType Directory -Force -Path $transaction | Out-Null
    $marker = Join-Path $transaction "health.json"
    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $launcher
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.Environment["TOPIAFORGE_DATA_ROOT"] = $dataRoot
    $startInfo.ArgumentList.Add("--topiaforge-update-health-nonce")
    $startInfo.ArgumentList.Add($nonce)
    $startInfo.ArgumentList.Add("--topiaforge-update-health-file")
    $startInfo.ArgumentList.Add($marker)

    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    $started = $false
    try {
        if (-not $process.Start()) {
            throw "The packaged Windows launcher did not start."
        }
        $started = $true
        $deadline = [DateTimeOffset]::UtcNow.AddSeconds(30)
        while (-not (Test-Path -LiteralPath $marker -PathType Leaf)) {
            if ($process.HasExited) {
                $stderr = $process.StandardError.ReadToEnd()
                throw "The packaged Windows launcher exited before its first " +
                    "healthy frame. $stderr"
            }
            if ([DateTimeOffset]::UtcNow -ge $deadline) {
                throw "The packaged Windows launcher did not report a healthy " +
                    "first frame within 30 seconds."
            }
            Start-Sleep -Milliseconds 200
        }
        $health = Get-Content -LiteralPath $marker -Raw | ConvertFrom-Json
        $healthKeys = @($health.PSObject.Properties.Name | Sort-Object)
        $expectedHealthKeys = @(
            "formatVersion",
            "healthy",
            "nonce",
            "processId",
            "reportedAtUtc"
        ) | Sort-Object
        if ((Compare-Object $healthKeys $expectedHealthKeys -SyncWindow 0) -or
            $health.formatVersion -isnot [Int64] -or
            [Int64]$health.formatVersion -ne 1 -or
            $health.healthy -ne $true -or
            [string]$health.nonce -cne $nonce -or
            $health.processId -isnot [Int64] -or
            [Int64]$health.processId -ne [Int64]$process.Id) {
            throw "The packaged Windows launcher health marker is invalid."
        }
    }
    finally {
        if ($started -and -not $process.HasExited) {
            $process.Kill($true)
            $process.WaitForExit(5000) | Out-Null
        }
        $process.Dispose()
    }
}

function Get-Sha256FromUtf8 {
    param([Parameter(Mandatory = $true)][string]$Value)
    $bytes = [System.Text.UTF8Encoding]::new($false).GetBytes($Value)
    return [Convert]::ToHexString(
        [System.Security.Cryptography.SHA256]::HashData($bytes)
    ).ToLowerInvariant()
}

function Get-RobotopiaInstalledBuildId {
    param([Parameter(Mandatory = $true)][string]$GameRoot)
    $resolvedGameRoot = [System.IO.Path]::GetFullPath($GameRoot).TrimEnd("\", "/")
    $candidates = [System.Collections.Generic.List[string]]::new()
    $candidates.Add((Join-Path $resolvedGameRoot "installed-build.json"))
    if ((Split-Path -Leaf $resolvedGameRoot).Equals(
            "Robotopia",
            [System.StringComparison]::OrdinalIgnoreCase
        )) {
        $candidates.Add((Join-Path (Split-Path -Parent $resolvedGameRoot) `
                    "installed-build.json"))
    }
    foreach ($candidate in $candidates) {
        if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) {
            continue
        }
        $length = (Get-Item -LiteralPath $candidate).Length
        if ($length -le 0 -or $length -gt 4096) {
            throw "Robotopia installed-build.json is invalid."
        }
        try {
            $metadata = Get-Content -LiteralPath $candidate -Raw |
                ConvertFrom-Json
            if ($metadata.PSObject.Properties.Name -notcontains "id") {
                throw "missing id"
            }
            $idText = [string]$metadata.id
            if ($idText -notmatch "^[1-9][0-9]*$") {
                throw "invalid id"
            }
            return [int]$idText
        }
        catch {
            throw "Robotopia installed-build.json is invalid."
        }
    }
    throw "Robotopia installed-build.json is missing."
}

function Clear-OwnedDirectory {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$AllowedParent,
        [Parameter(Mandatory = $true)][string]$Marker
    )

    $resolvedPath = [System.IO.Path]::GetFullPath($Path)
    $resolvedParent = [System.IO.Path]::GetFullPath($AllowedParent)
    $prefix = $resolvedParent.TrimEnd("\", "/") +
        [System.IO.Path]::DirectorySeparatorChar
    if (-not $resolvedPath.StartsWith(
            $prefix,
            [System.StringComparison]::OrdinalIgnoreCase
        )) {
        throw "Refusing to clear a directory outside its release-owned parent."
    }
    $markerPath = Join-Path $resolvedPath ".topiaforge-release-owner"
    if (Test-Path -LiteralPath $resolvedPath) {
        if (-not (Test-Path -LiteralPath $markerPath -PathType Leaf) -or
            (Get-Content -LiteralPath $markerPath -Raw).Trim() -ne $Marker) {
            throw "Refusing to clear an unowned release directory: $resolvedPath"
        }
        Remove-Item -LiteralPath $resolvedPath -Recurse -Force
    }
    New-Item -ItemType Directory -Force -Path $resolvedPath | Out-Null
    [System.IO.File]::WriteAllText(
        $markerPath,
        "$Marker`n",
        [System.Text.UTF8Encoding]::new($false)
    )
}

function Remove-OwnedProjectDirectory {
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute(
        "PSUseShouldProcessForStateChangingFunctions",
        "",
        Justification = "This non-interactive build helper only removes an exact allow-listed child directory after validating its resolved path."
    )]
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$ProjectRoot,
        [Parameter(Mandatory = $true)]
        [ValidateSet("build", ".dart_tool")]
        [string]$ExpectedName
    )

    $resolvedPath = [System.IO.Path]::GetFullPath($Path)
    $resolvedProject = [System.IO.Path]::GetFullPath($ProjectRoot)
    $prefix = $resolvedProject.TrimEnd("\", "/") +
        [System.IO.Path]::DirectorySeparatorChar
    if ((Split-Path -Leaf $resolvedPath) -ne $ExpectedName -or
        -not $resolvedPath.StartsWith(
            $prefix,
            [System.StringComparison]::OrdinalIgnoreCase
        )) {
        throw "Refusing to clear an unexpected project output directory."
    }
    if (Test-Path -LiteralPath $resolvedPath) {
        Remove-Item -LiteralPath $resolvedPath -Recurse -Force
    }
}

$repository = (Resolve-Path -LiteralPath $RepositoryRoot).Path
$canonical = (Resolve-Path -LiteralPath $CanonicalArchive).Path
$output = [System.IO.Path]::GetFullPath($OutputDirectory)
$privateEvidenceRoot = [System.IO.Path]::GetFullPath($PrivateEvidenceDirectory)
if ($output -eq $repository -or
    $repository.StartsWith($output.TrimEnd("\", "/") + [System.IO.Path]::DirectorySeparatorChar,
        [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "The release output cannot be the repository or one of its parents."
}
if ($privateEvidenceRoot.Equals(
        $output,
        [System.StringComparison]::OrdinalIgnoreCase
    ) -or
    $privateEvidenceRoot.StartsWith(
        $output.TrimEnd("\", "/") +
            [System.IO.Path]::DirectorySeparatorChar,
        [System.StringComparison]::OrdinalIgnoreCase
    ) -or
    $output.StartsWith(
        $privateEvidenceRoot.TrimEnd("\", "/") +
            [System.IO.Path]::DirectorySeparatorChar,
        [System.StringComparison]::OrdinalIgnoreCase
    )) {
    throw "Private Windows evidence must not overlap the public asset directory."
}
New-Item -ItemType Directory -Force -Path $output | Out-Null

$head = (& git -C $repository rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $head -ne $SourceSha) {
    throw "The Windows checkout is not at the requested source SHA."
}
$trackedChanges = & git -C $repository status --porcelain --untracked-files=no
if ($LASTEXITCODE -ne 0 -or $trackedChanges) {
    throw "The Windows checkout has tracked changes before the build."
}
$policy = Get-Content -LiteralPath (Join-Path $repository "release/release-policy.json") -Raw |
    ConvertFrom-Json
$platformPolicy = Get-Content -LiteralPath `
    (Join-Path $repository "release/platform-toolchains.json") -Raw |
    ConvertFrom-Json
if ($platformPolicy.schemaVersion -ne 1 -or
    [string]::IsNullOrWhiteSpace($platformPolicy.windows.msvc) -or
    [string]::IsNullOrWhiteSpace($platformPolicy.windows.windowsSdk)) {
    throw "release/platform-toolchains.json does not contain the Windows toolchain pins."
}
if ($policy.versioning.productVersion -ne $Version) {
    throw "The requested version does not match release-policy.json."
}
$windowsCertificatePin = ""
if ($policy.signingIdentities.PSObject.Properties.Name -contains
    "windowsCertificateSha256") {
    $windowsCertificatePin =
        [string]$policy.signingIdentities.windowsCertificateSha256
}
$hasWindowsCertificatePin =
    $windowsCertificatePin -cmatch "^(?!0{64}$)[0-9a-f]{64}$"
if ($policy.publication.PSObject.Properties.Name -contains
    "codeSigningException") {
    throw "Production Windows builds forbid every code-signing exception."
}
$windowsDistribution = "signed"
if ($policy.signingIdentities.PSObject.Properties.Name -contains
    "windowsDistribution") {
    $windowsDistribution = [string]$policy.signingIdentities.windowsDistribution
}
if ($windowsDistribution -cne "signed" -and $windowsDistribution -cne "unsigned") {
    throw "Unknown Windows distribution mode '$windowsDistribution'; expected 'signed' or 'unsigned'."
}
if ($windowsDistribution -ceq "unsigned" -and $hasWindowsCertificatePin) {
    throw "An unsigned Windows distribution must not also pin a signing certificate."
}
if ($windowsDistribution -ceq "signed" -and -not $hasWindowsCertificatePin) {
    throw "release-policy.json must pin the reviewed Windows signing certificate."
}
foreach ($name in @(
        "WINDOWS_CERTIFICATE_PFX",
        "WINDOWS_CERTIFICATE_PASSWORD",
        "WINDOWS_TIMESTAMP_URL"
    )) {
    if ([string]::IsNullOrWhiteSpace(
            [Environment]::GetEnvironmentVariable($name)
        )) {
        throw "$name is mandatory for a production Windows build."
    }
}
if ((Get-Sha256 $canonical) -ne $CanonicalArchiveSha256) {
    throw "The canonical ecosystem transport archive digest does not match."
}

$flutterCommand = Get-Command $FlutterPath -CommandType Application -ErrorAction SilentlyContinue |
    Select-Object -First 1
$dartCommand = Get-Command $DartPath -CommandType Application -ErrorAction SilentlyContinue |
    Select-Object -First 1
if ($null -eq $flutterCommand -or $null -eq $dartCommand) {
    throw "The pinned Dart and Flutter command paths must resolve to applications."
}
$flutter = $flutterCommand.Source
$dart = $dartCommand.Source
$flutterVersion = (& $flutter --version --machine | ConvertFrom-Json).frameworkVersion
if ($flutterVersion -ne $policy.toolchains.flutter) {
    throw "Expected Flutter $($policy.toolchains.flutter), found $flutterVersion."
}
$dartVersion = (& $dart --version 2>&1 | Out-String)
if ($dartVersion -notmatch [regex]::Escape("Dart SDK version: $($policy.toolchains.dart)")) {
    throw "Expected Dart $($policy.toolchains.dart)."
}
if ((& dotnet --version).Trim() -ne $policy.toolchains.dotnetSdk) {
    throw "Expected .NET SDK $($policy.toolchains.dotnetSdk)."
}
$nodeVersionText = (& node --version).Trim()
if ($nodeVersionText -cne "v$($policy.toolchains.node)") {
    throw "Expected Node v$($policy.toolchains.node), found $nodeVersionText."
}
$measuredNode = $nodeVersionText.Substring(1)

$vswhere = Join-Path ${env:ProgramFiles(x86)} `
    "Microsoft Visual Studio/Installer/vswhere.exe"
if (-not (Test-Path -LiteralPath $vswhere -PathType Leaf)) {
    throw "vswhere is required to verify the pinned MSVC toolchain."
}
$visualStudioPath = (& $vswhere -latest -products * `
        -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 `
        -property installationPath | Select-Object -First 1 | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($visualStudioPath)) {
    throw "A complete Visual Studio C++ toolchain was not found."
}
$msvcVersionFile = Join-Path $visualStudioPath `
    "VC/Auxiliary/Build/Microsoft.VCToolsVersion.default.txt"
if (-not (Test-Path -LiteralPath $msvcVersionFile -PathType Leaf)) {
    throw "The selected Visual Studio installation has no default MSVC version."
}
$measuredMsvc = (Get-Content -LiteralPath $msvcVersionFile -Raw).Trim()
$pinnedMsvc = [string]$platformPolicy.windows.msvc
if ($measuredMsvc -ne $pinnedMsvc -or
    -not (Test-Path -LiteralPath `
        (Join-Path $visualStudioPath "VC/Tools/MSVC/$pinnedMsvc/bin/Hostx64/x64/cl.exe") `
        -PathType Leaf)) {
    throw "Expected MSVC $pinnedMsvc, found $measuredMsvc."
}

$windowsSdkRoot = Join-Path ${env:ProgramFiles(x86)} "Windows Kits/10"
$installedWindowsSdks = @(
    Get-ChildItem -LiteralPath (Join-Path $windowsSdkRoot "Include") -Directory `
        -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match "^[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+$" } |
        Sort-Object { [version]$_.Name }
)
if ($installedWindowsSdks.Count -eq 0) {
    throw "No Windows SDK installation was found."
}
$measuredWindowsSdk = [string]$installedWindowsSdks[-1].Name
$pinnedWindowsSdk = [string]$platformPolicy.windows.windowsSdk
if ($measuredWindowsSdk -ne $pinnedWindowsSdk -or
    -not (Test-Path -LiteralPath `
        (Join-Path $windowsSdkRoot "Lib/$pinnedWindowsSdk") -PathType Container)) {
    throw "Expected Windows SDK $pinnedWindowsSdk as the selected latest SDK, " +
        "found $measuredWindowsSdk."
}

$work = Join-Path $output ".topiaforge-platform-windows"
$outputWithSeparator = $output.TrimEnd("\", "/") + [System.IO.Path]::DirectorySeparatorChar
if (-not $work.StartsWith($outputWithSeparator, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Unsafe Windows release work directory."
}
if (Test-Path -LiteralPath $work) {
    Remove-Item -LiteralPath $work -Recurse -Force
}
$canonicalDirectory = Join-Path $work "canonical"
$launcherDirectory = Join-Path $work "launcher"
$cliDirectory = Join-Path $work "cli"
$acceptanceDirectory = Join-Path $privateEvidenceRoot "windows"
Clear-OwnedDirectory -Path $acceptanceDirectory -AllowedParent $privateEvidenceRoot `
    -Marker "release-evidence-v1:$Version`:$SourceSha`:windows"
New-Item -ItemType Directory -Force -Path `
    $canonicalDirectory, $launcherDirectory, $cliDirectory | Out-Null
Invoke-Checked -FilePath "tar" -Arguments @("-xf", $canonical, "-C", $canonicalDirectory)
Invoke-Checked -FilePath "dotnet" -WorkingDirectory $repository `
    -Arguments @("clean", "TopiaForge.slnx", "-c", "Release", "--nologo")

$launcherProject = Join-Path $repository "apps/topiaforge_launcher_flutter"
Remove-OwnedProjectDirectory -Path (Join-Path $launcherProject "build") `
    -ProjectRoot $launcherProject -ExpectedName "build"
Invoke-Checked -FilePath $flutter -WorkingDirectory $launcherProject `
    -Arguments @("clean")
Invoke-Checked -FilePath $flutter -WorkingDirectory $launcherProject `
    -Arguments @("pub", "get", "--enforce-lockfile")
Invoke-Checked -FilePath $flutter -WorkingDirectory $launcherProject `
    -Arguments @(
        "build", "windows", "--release",
        "--dart-define=TOPIAFORGE_PRODUCT_VERSION=$Version"
    )
$launcherBuild = Join-Path $launcherProject "build/windows/x64/runner/Release"
if (-not (Test-Path -LiteralPath $launcherBuild -PathType Container)) {
    $launcherBuild = Join-Path $launcherProject "build/windows/runner/Release"
}
if (-not (Test-Path -LiteralPath $launcherBuild -PathType Container)) {
    throw "The Windows Flutter launcher output was not found."
}
$cmakeCache = Get-ChildItem -LiteralPath `
    (Join-Path $launcherProject "build/windows") -Filter "CMakeCache.txt" -File `
    -Recurse | Select-Object -First 1
if ($null -eq $cmakeCache) {
    throw "The Windows launcher build did not produce a CMake toolchain cache."
}
$cmakeCacheText = Get-Content -LiteralPath $cmakeCache.FullName -Raw
$msvcPathPattern = "[\\/]MSVC[\\/]" + [regex]::Escape($pinnedMsvc) + "[\\/]"
if ($cmakeCacheText -notmatch $msvcPathPattern) {
    throw "The Windows launcher was not compiled with pinned MSVC $pinnedMsvc."
}
$launcherProjectFile = Get-ChildItem -LiteralPath `
    (Join-Path $launcherProject "build/windows") -Filter "*.vcxproj" -File `
    -Recurse |
    Where-Object { $_.Name -match "topiaforge_launcher" } |
    Select-Object -First 1
if ($null -eq $launcherProjectFile) {
    throw "The Windows launcher build did not produce its Visual Studio project."
}
$launcherProjectText = Get-Content -LiteralPath $launcherProjectFile.FullName -Raw
$sdkMatch = [regex]::Match(
    $launcherProjectText,
    "<WindowsTargetPlatformVersion>([^<]+)</WindowsTargetPlatformVersion>"
)
if (-not $sdkMatch.Success -or $sdkMatch.Groups[1].Value -ne $pinnedWindowsSdk) {
    throw "The Windows launcher was not compiled with Windows SDK $pinnedWindowsSdk."
}
Copy-Item -Path (Join-Path $launcherBuild "*") -Destination $launcherDirectory `
    -Recurse -Force

$cliProject = Join-Path $repository "apps/topiaforge_cli"
$compiledCli = Join-Path $cliDirectory "topiaforge.exe"
Remove-OwnedProjectDirectory -Path (Join-Path $cliProject ".dart_tool") `
    -ProjectRoot $cliProject -ExpectedName ".dart_tool"
Invoke-Checked -FilePath $dart -WorkingDirectory $cliProject `
    -Arguments @("pub", "get", "--enforce-lockfile")
Invoke-Checked -FilePath $dart -WorkingDirectory $cliProject `
    -Arguments @("compile", "exe", "bin/topiaforge.dart", "-o", $compiledCli)
$buildPackageArguments = @(
    "run", "bin/topiaforge.dart", "release", "build-package",
    "--platform", "windows", "--output", $output, "--configuration", "Release",
    "--prebuilt-launcher", $launcherDirectory, "--prebuilt-cli", $compiledCli,
    "--prebuilt-dist", $canonicalDirectory
)
if ($windowsDistribution -ceq "signed") {
    $buildPackageArguments += "--require-windows-signing"
}
Invoke-Checked -FilePath $dart -WorkingDirectory $cliProject `
    -Arguments $buildPackageArguments
$archive = Join-Path $output "TopiaForge-windows-x64.zip"
$testPackageArguments = @(
    "run", "bin/topiaforge.dart", "release", "test-package",
    "--platform", "windows", "--zip", $archive,
    "--run-embedded-cli",
    "--expected-canonical-ecosystem-sha256", $CanonicalEcosystemSha256,
    "--canonical-assets", $canonicalDirectory
)
if ($windowsDistribution -ceq "unsigned") {
    # Prove the artifacts really are unsigned rather than accidentally signed by
    # stray credentials in the environment.
    $testPackageArguments += "--require-windows-unsigned"
}
else {
    $testPackageArguments += @(
        "--require-windows-signature",
        "--expected-windows-signer-sha256", $windowsCertificatePin
    )
}
Invoke-Checked -FilePath $dart -WorkingDirectory $cliProject `
    -Arguments $testPackageArguments
Invoke-PackagedLauncherHealthCheck -ArchivePath $archive `
    -WorkDirectory $work

# The tracked Unity bundle must be reproducible with the exact local editor.
$unityInputs = @(
    "src/TopiaForge.Mods.UnityUi/Assets/topiaforge-ui.bundle",
    "src/TopiaForge.Mods.UnityUi/Assets/topiaforge-ui.manifest.json",
    "tools/unity-ui-bundle/Assets/UiBundleManifest.json"
)
Invoke-Checked -FilePath $dart -WorkingDirectory $cliProject -Arguments @(
    "run", "bin/topiaforge.dart", "unity", "build-ui-bundle",
    "--unity", $UnityPath, "--rebuild"
)
$firstUnityHashes = @{}
foreach ($relative in $unityInputs) {
    $firstUnityHashes[$relative] = Get-Sha256 (Join-Path $repository $relative)
}
Invoke-Checked -FilePath $dart -WorkingDirectory $cliProject -Arguments @(
    "run", "bin/topiaforge.dart", "unity", "build-ui-bundle",
    "--unity", $UnityPath, "--rebuild"
)
foreach ($relative in $unityInputs) {
    if ((Get-Sha256 (Join-Path $repository $relative)) -ne $firstUnityHashes[$relative]) {
        throw "Unity produced different bytes on the second build: $relative"
    }
}
Invoke-Checked -FilePath "git" -WorkingDirectory $repository `
    -Arguments (@("diff", "--exit-code", "--") + $unityInputs)

$managedDirectory = Join-Path $GameDirectory "Robotopia_Data/Managed"
if (-not (Test-Path -LiteralPath $managedDirectory -PathType Container)) {
    throw "Robotopia managed assemblies were not found for Unity validation."
}
$installedGameBuildId = Get-RobotopiaInstalledBuildId $GameDirectory
if ($installedGameBuildId -ne [int]$policy.gameBuild.id) {
    throw "Robotopia must be the pinned build $($policy.gameBuild.id); " +
        "found build $installedGameBuildId."
}
$gameInstallVerifier = Join-Path $repository `
    "tools/release/verify-robotopia-install.ps1"
$gameMetadataPath = Join-Path $repository ([string]$policy.gameBuild.metadataFile)
$officialInstallBeforeText = & $gameInstallVerifier `
    -GameDirectory $GameDirectory -MetadataPath $gameMetadataPath | Out-String
if (-not $?) {
    throw "Official Robotopia installation verification failed before acceptance."
}
$officialInstallBefore = $officialInstallBeforeText.Trim() | ConvertFrom-Json
$unityEvidenceDirectory = Join-Path $acceptanceDirectory "unity"
New-Item -ItemType Directory -Force -Path $unityEvidenceDirectory | Out-Null
$unityEvidence = Join-Path $unityEvidenceDirectory "lifecycle.json"
$unityLog = Join-Path $unityEvidenceDirectory "unity-lifecycle.log"
Invoke-Checked -FilePath $UnityPath -WorkingDirectory $repository -Arguments @(
    "-batchmode",
    "-buildTarget", "StandaloneWindows64",
    "-projectPath", (Join-Path $repository "tools/unity-ui-bundle"),
    "-executeMethod", "TopiaForge.UiLifecycleSmoke.Run",
    "-robotopiaManagedDir", $managedDirectory,
    "-topiaforgeLifecycleEvidence", $unityEvidence,
    "-logFile", $unityLog
)
$unityResult = Get-Content -LiteralPath $unityEvidence -Raw | ConvertFrom-Json
if ($unityResult.result -ne "pass" -or
    $unityResult.editorVersion -ne $policy.toolchains.unity -or
    $unityResult.cycles -ne 16 -or
    $unityResult.validatorSmoke -ne $true) {
    throw "Exact-Unity lifecycle evidence is invalid."
}

# Exercise the exact packaged CLI in the full Robotopia journey.
$extracted = Join-Path $work "extracted"
New-Item -ItemType Directory -Force -Path $extracted | Out-Null
$sevenZip = Get-Command 7z -CommandType Application -ErrorAction SilentlyContinue |
    Select-Object -First 1
if ($null -eq $sevenZip) {
    $sevenZipPath = Join-Path $env:ProgramFiles "7-Zip/7z.exe"
    if (-not (Test-Path -LiteralPath $sevenZipPath -PathType Leaf)) {
        throw "7-Zip is required for the Windows acceptance journey."
    }
}
else {
    $sevenZipPath = $sevenZip.Source
}
Invoke-Checked -FilePath $sevenZipPath -Arguments @("x", "-y", $archive, "-o$extracted")
$packagedCli = Join-Path $extracted "topiaforge.exe"
$journeyProjects = Join-Path $work "journey-projects"
New-Item -ItemType Directory -Force -Path $journeyProjects | Out-Null
$journeyId = "dev.topiaforge.release-$($SourceSha.Substring(0, 12))"
$journeyName = "TopiaForge release $Version"
Invoke-Checked -FilePath $packagedCli -WorkingDirectory $extracted -Arguments @(
    "new", "mod", $journeyId, "--template", "minimal", "--name", $journeyName,
    "--author", "TopiaForge Release", "--license", "MIT", "--dir", $journeyProjects
)
$journeyProject = Join-Path $journeyProjects $journeyId
$marker = "$journeyName loaded. Run '$journeyId`:greet' to try its command."
$gameEvidence = Join-Path $acceptanceDirectory "robotopia"
Invoke-Checked -FilePath $packagedCli -WorkingDirectory $repository -Arguments @(
    "acceptance", "run",
    "--game-dir", $GameDirectory, "--output", $gameEvidence,
    "--timeout-seconds", "1800", "--dev-cli", $packagedCli,
    "--dev-project", $journeyProject, "--required-loaded-package", $journeyId,
    "--required-log-marker", $marker, "--all"
)
$gameEvidenceFile = Join-Path $gameEvidence "acceptance-result.json"
if (-not (Test-Path -LiteralPath $gameEvidenceFile -PathType Leaf)) {
    throw "Robotopia acceptance did not produce its bounded result."
}
$gameAcceptance = Get-Content -LiteralPath $gameEvidenceFile -Raw |
    ConvertFrom-Json
if ($gameAcceptance.schemaVersion -ne 2 -or
    [string]$gameAcceptance.acceptanceChallenge -cnotmatch
        "^[0-9a-f]{64}$" -or
    [string]$gameAcceptance.acceptancePackageReceipt.sourceSha256 -cnotmatch
        "^[0-9a-f]{64}$" -or
    @($gameAcceptance.acceptancePackageReceipt.criticalFiles).Count -lt 1 -or
    [string]$gameAcceptance.requiredLoadedPackageReceipt.sourceSha256 `
        -cnotmatch "^[0-9a-f]{64}$" -or
    @($gameAcceptance.requiredLoadedPackageReceipt.criticalFiles).Count -lt 1) {
    throw "Robotopia acceptance did not bind its challenge and exact package receipts."
}
$lastRunPath = Join-Path $GameDirectory `
    "BepInEx/TopiaForge/logs/last-run.json"
if (-not (Test-Path -LiteralPath $lastRunPath -PathType Leaf)) {
    throw "Robotopia acceptance last-run evidence is missing."
}
$lastRun = Get-Content -LiteralPath $lastRunPath -Raw | ConvertFrom-Json
$acceptanceRunPackage = @(
    $lastRun.packages |
        Where-Object { [string]$_.id -ceq "dev.topiaforge.sdk-acceptance" }
)
$journeyRunPackage = @(
    $lastRun.packages |
        Where-Object { [string]$_.id -ceq $journeyId }
)
if ($lastRun.schemaVersion -ne 1 -or
    [string]$lastRun.sessionId -cne
        [string]$gameAcceptance.lastRunSessionId -or
    $acceptanceRunPackage.Count -ne 1 -or
    $journeyRunPackage.Count -ne 1 -or
    [string]$acceptanceRunPackage[0].sourceSha256 -cne
        [string]$gameAcceptance.acceptancePackageReceipt.sourceSha256 -or
    (($acceptanceRunPackage[0].criticalFiles | ConvertTo-Json -Compress) -cne
        ($gameAcceptance.acceptancePackageReceipt.criticalFiles |
            ConvertTo-Json -Compress)) -or
    [string]$journeyRunPackage[0].sourceSha256 -cne
        [string]$gameAcceptance.requiredLoadedPackageReceipt.sourceSha256 -or
    (($journeyRunPackage[0].criticalFiles | ConvertTo-Json -Compress) -cne
        ($gameAcceptance.requiredLoadedPackageReceipt.criticalFiles |
            ConvertTo-Json -Compress))) {
    throw "Robotopia last-run package receipts do not match the exact accepted packages."
}
if ((Get-RobotopiaInstalledBuildId $GameDirectory) -ne $installedGameBuildId) {
    throw "Robotopia installed-build.json changed during acceptance."
}
$officialInstallAfterText = & $gameInstallVerifier `
    -GameDirectory $GameDirectory -MetadataPath $gameMetadataPath | Out-String
if (-not $?) {
    throw "Official Robotopia installation verification failed after acceptance."
}
$officialInstallAfter = $officialInstallAfterText.Trim() | ConvertFrom-Json
if (($officialInstallBefore | ConvertTo-Json -Compress) -cne
    ($officialInstallAfter | ConvertTo-Json -Compress)) {
    throw "Official Robotopia base-game identity changed during acceptance."
}
$finalTrackedChanges = (& git -C $repository status --porcelain --untracked-files=no |
        Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or -not [string]::IsNullOrWhiteSpace($finalTrackedChanges)) {
    throw "The Windows release build changed tracked source files."
}

$validation = [ordered]@{
    schema = "release-local-validation-v1"
    platform = "windows"
    version = $Version
    targetSha = $SourceSha
    archiveSha256 = Get-Sha256 $archive
    canonicalEcosystemSha256 = $CanonicalEcosystemSha256
    canonicalArchiveSha256 = $CanonicalArchiveSha256
    signingState = "authenticode-timestamped"
    platformToolchains = [ordered]@{
        node = $measuredNode
        msvc = $measuredMsvc
        windowsSdk = $measuredWindowsSdk
    }
    gameBuildId = $installedGameBuildId
    gameArchiveSha256 = [string]$officialInstallBefore.archiveSha256
    gameFilesManifestSha256 = [string]$officialInstallBefore.filesManifestSha256
    gameFilesVerified = [Int64]$officialInstallBefore.filesVerified
    gameExecutableSha256 = [string]$officialInstallBefore.gameExecutableSha256
    checks = @(
        "archive-smoke",
        "embedded-cli",
        "packaged-launcher-health",
        "canonical-ecosystem",
        "authenticode",
        "unity-reproducibility",
        "unity-lifecycle",
        "official-game-bytes",
        "robotopia-acceptance"
    )
    evidenceSha256 = [ordered]@{
        unity = Get-Sha256 $unityEvidence
        robotopia = Get-Sha256 $gameEvidenceFile
    }
    passed = $true
}
$validation | ConvertTo-Json -Depth 6 -Compress |
    Set-Content -LiteralPath (Join-Path $output "validation-windows.json") -Encoding utf8NoBOM

Remove-Item -LiteralPath $work -Recurse -Force
Write-Host "Built and validated $archive"
