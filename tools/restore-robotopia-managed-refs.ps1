[CmdletBinding()]
param(
    [ValidateSet("auto", "public", "bundled")]
    [string]$Source = "auto",
    [string]$SourcePlatform = "",
    [string]$ConfigPath = "",
    [string]$CacheRoot = "",
    [switch]$Probe,
    [switch]$CacheKeyOnly,
    [switch]$WriteLocalProps
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"

if (!$PSBoundParameters.ContainsKey("Source") -and ![string]::IsNullOrWhiteSpace($env:ROBOTOPIA_REFS_SOURCE)) {
    $Source = $env:ROBOTOPIA_REFS_SOURCE.Trim().ToLowerInvariant()
}

if (!@("auto", "public", "bundled").Contains($Source)) {
    throw "Invalid ROBOTOPIA_REFS_SOURCE '$Source'. Expected auto, public, or bundled."
}

function Resolve-RepoRoot {
    $current = (Get-Location).Path
    while (![string]::IsNullOrWhiteSpace($current)) {
        if (Test-Path -LiteralPath (Join-Path $current ".git")) {
            return $current
        }
        $parent = Split-Path -Parent $current
        if ($parent -eq $current) {
            break
        }
        $current = $parent
    }
    throw "Could not locate the repository root from $(Get-Location)."
}

function Get-RefsConfig {
    if ([string]::IsNullOrWhiteSpace($script:ConfigPath)) {
        $script:ConfigPath = Join-Path (Resolve-RepoRoot) ".github/robotopia-game-build.json"
    }
    if (!(Test-Path -LiteralPath $script:ConfigPath)) {
        throw "Robotopia game build config was not found: $script:ConfigPath"
    }
    return Get-Content -LiteralPath $script:ConfigPath -Raw | ConvertFrom-Json
}

function Get-SelectedArchive {
    param([Parameter(Mandatory = $true)]$Config)

    $platform = $script:SourcePlatform
    if ([string]::IsNullOrWhiteSpace($platform)) {
        $platform = $env:ROBOTOPIA_REFS_SOURCE_PLATFORM
    }
    if ([string]::IsNullOrWhiteSpace($platform)) {
        $platform = $Config.sourcePlatform
    }
    if ([string]::IsNullOrWhiteSpace($platform)) {
        $platform = "windows"
    }
    $platform = $platform.Trim().ToLowerInvariant()

    $property = $Config.archives.PSObject.Properties[$platform]
    if ($null -eq $property) {
        throw "No archive entry named '$platform' exists in $script:ConfigPath."
    }

    $path = ([string]$property.Value.path).Trim()
    $sha256 = ([string]$property.Value.sha256).Trim().ToLowerInvariant()
    if ([string]::IsNullOrWhiteSpace($path)) {
        throw "Archive entry '$platform' has no path in $script:ConfigPath."
    }
    if ($sha256 -notmatch "^[0-9a-f]{64}$") {
        throw "Archive entry '$platform' has an invalid SHA-256 in $script:ConfigPath."
    }

    [PSCustomObject]@{
        Platform = $platform
        Path = $path
        Sha256 = $sha256
    }
}

function Join-Uri {
    param(
        [Parameter(Mandatory = $true)][string]$Base,
        [Parameter(Mandatory = $true)][string]$Path
    )
    if ($Path -match "^https?://") {
        return $Path
    }
    return $Base.TrimEnd("/") + "/" + $Path.TrimStart("/")
}

function Get-CacheRoot {
    if (![string]::IsNullOrWhiteSpace($script:CacheRoot)) {
        return $script:CacheRoot
    }
    if (![string]::IsNullOrWhiteSpace($env:ROBOTOPIA_REFS_CACHE)) {
        return $env:ROBOTOPIA_REFS_CACHE
    }
    if (![string]::IsNullOrWhiteSpace($env:RUNNER_TOOL_CACHE)) {
        return Join-Path $env:RUNNER_TOOL_CACHE "robotopia-managed-refs"
    }
    return Join-Path ([IO.Path]::GetTempPath()) "robotopia-managed-refs"
}

function Get-PublicCacheKey {
    $config = Get-RefsConfig
    $archive = Get-SelectedArchive $config
    return "robotopia-managed-refs-$($env:RUNNER_OS)-public-$($config.buildId)-$($archive.Platform)-$($archive.Sha256)"
}

function Get-BundledCacheKey {
    $config = Get-BundledConfig
    return "robotopia-managed-refs-$($env:RUNNER_OS)-bundled-$($config.Sha256)"
}

function Write-CacheKey {
    $key = switch ($Source) {
        "public" { Get-PublicCacheKey }
        "bundled" { Get-BundledCacheKey }
        default {
            $public = Get-PublicCacheKey
            $fallbackSha = ($env:ROBOTOPIA_REFS_SHA256 ?? "").Trim().ToLowerInvariant()
            if ($fallbackSha -match "^[0-9a-f]{64}$") {
                "$public-fallback-$fallbackSha"
            }
            else {
                $public
            }
        }
    }
    if (![string]::IsNullOrWhiteSpace($env:GITHUB_OUTPUT)) {
        "key=$key" | Add-Content -LiteralPath $env:GITHUB_OUTPUT
    }
    Write-Output $key
}

function Get-Headers {
    $headers = @{}
    if (![string]::IsNullOrWhiteSpace($env:ROBOTOPIA_REFS_TOKEN)) {
        $headers["Authorization"] = "Bearer $env:ROBOTOPIA_REFS_TOKEN"
    }
    return $headers
}

function Test-Head {
    param(
        [Parameter(Mandatory = $true)][string]$Uri,
        [string]$Label = "resource",
        [hashtable]$Headers = @{},
        [switch]$HideUri
    )
    try {
        # Never forward an Authorization header through an automatic redirect: even an HTTPS origin could
        # redirect to plaintext HTTP. Authenticated bundled sources must be direct HTTPS endpoints.
        $maximumRedirection = if ($Headers.ContainsKey("Authorization")) { 0 } else { 5 }
        $response = Invoke-WebRequest -Uri $Uri -Method Head -Headers $Headers -UseBasicParsing `
            -MaximumRedirection $maximumRedirection
    }
    catch {
        if ($HideUri) {
            throw "$Label HEAD request failed."
        }
        throw "$Label HEAD request failed for $Uri. $($_.Exception.Message)"
    }
    if ($response.StatusCode -lt 200 -or $response.StatusCode -ge 300) {
        $target = if ($HideUri) { "" } else { " for $Uri" }
        throw "$Label HEAD failed$target with HTTP $($response.StatusCode)."
    }
    return $response
}

function Assert-Sha256 {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Expected
    )
    $actual = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $Expected.Trim().ToLowerInvariant()) {
        throw "SHA-256 mismatch for $Path. Expected $Expected but got $actual."
    }
}

function Test-ManagedDir {
    param([Parameter(Mandatory = $true)][string]$Path)
    foreach ($name in @("GameCode.dll", "UnityEngine.dll", "UnityEngine.CoreModule.dll")) {
        if (!(Test-Path -LiteralPath (Join-Path $Path $name))) {
            return $false
        }
    }
    return $true
}

function Assert-ManagedDir {
    param([Parameter(Mandatory = $true)][string]$Path)
    if (!(Test-ManagedDir $Path)) {
        throw "Managed refs at $Path are incomplete. Expected GameCode.dll, UnityEngine.dll, and UnityEngine.CoreModule.dll."
    }
}

function Find-ManagedDir {
    param([Parameter(Mandatory = $true)][string]$Root)
    $gameCode = Get-ChildItem -LiteralPath $Root -Recurse -Filter "GameCode.dll" -File -ErrorAction SilentlyContinue |
        Select-Object -First 20
    foreach ($file in $gameCode) {
        $dir = Split-Path -Parent $file.FullName
        if (Test-ManagedDir $dir) {
            return $dir
        }
    }
    throw "Could not find a complete Robotopia managed refs directory under $Root."
}

function Copy-ManagedDir {
    param(
        [Parameter(Mandatory = $true)][string]$SourceDir,
        [Parameter(Mandatory = $true)][string]$DestinationDir
    )
    if (Test-Path -LiteralPath $DestinationDir) {
        Remove-Item -LiteralPath $DestinationDir -Recurse -Force
    }
    New-Item -ItemType Directory -Force -Path $DestinationDir | Out-Null
    Get-ChildItem -LiteralPath $SourceDir -Force | Copy-Item -Destination $DestinationDir -Recurse -Force
    Assert-ManagedDir $DestinationDir
}

function Write-ManagedEnv {
    param([Parameter(Mandatory = $true)][string]$ManagedDir)
    Assert-ManagedDir $ManagedDir
    $resolved = (Resolve-Path -LiteralPath $ManagedDir).Path
    if (![string]::IsNullOrWhiteSpace($env:GITHUB_ENV)) {
        "RobotopiaManagedDir=$resolved" | Add-Content -LiteralPath $env:GITHUB_ENV
    }
    if ($WriteLocalProps) {
        $repoRoot = Resolve-RepoRoot
        $propsPath = Join-Path $repoRoot "Directory.Build.local.props"
        $escaped = [Security.SecurityElement]::Escape($resolved)
        $content = @"
<Project>
  <PropertyGroup>
    <RobotopiaManagedDir>$escaped</RobotopiaManagedDir>
  </PropertyGroup>
</Project>
"@
        [IO.File]::WriteAllText(
            $propsPath,
            $content + [Environment]::NewLine,
            [Text.UTF8Encoding]::new($false)
        )
        Write-Host "Wrote local MSBuild references: $propsPath"
    }
    Write-Host "RobotopiaManagedDir=$resolved"
}

function Find-SevenZip {
    $candidates = @("7z", "7zz", "7za")
    if (![string]::IsNullOrWhiteSpace($env:ProgramFiles)) {
        $candidates += Join-Path $env:ProgramFiles "7-Zip/7z.exe"
    }
    if (![string]::IsNullOrWhiteSpace(${env:ProgramFiles(x86)})) {
        $candidates += Join-Path ${env:ProgramFiles(x86)} "7-Zip/7z.exe"
    }
    foreach ($candidate in $candidates) {
        $command = Get-Command $candidate -ErrorAction SilentlyContinue
        if ($command) {
            return $command.Source
        }
        if (Test-Path -LiteralPath $candidate) {
            return $candidate
        }
    }
    throw "7-Zip was not found. Install p7zip/7zip before restoring public Robotopia refs."
}

function Expand-ManagedRefsFrom7z {
    param(
        [Parameter(Mandatory = $true)][string]$ArchivePath,
        [Parameter(Mandatory = $true)][string]$DestinationManagedDir
    )
    $sevenZip = Find-SevenZip
    $extractRoot = Join-Path ([IO.Path]::GetTempPath()) ("robotopia-public-refs-extract-" + [Guid]::NewGuid().ToString("N"))
    try {
        New-Item -ItemType Directory -Force -Path $extractRoot | Out-Null
        & $sevenZip x $ArchivePath "-o$extractRoot" -y "*/Robotopia_Data/Managed/*" "Robotopia_Data/Managed/*" "*/Managed/*"
        if ($LASTEXITCODE -ne 0) {
            throw "7-Zip extraction failed with exit code $LASTEXITCODE."
        }
        $managed = Find-ManagedDir $extractRoot
        Copy-ManagedDir $managed $DestinationManagedDir
    }
    finally {
        if (Test-Path -LiteralPath $extractRoot) {
            Remove-Item -LiteralPath $extractRoot -Recurse -Force
        }
    }
}

function Expand-ManagedRefsFromZip {
    param(
        [Parameter(Mandatory = $true)][string]$ArchivePath,
        [Parameter(Mandatory = $true)][string]$DestinationManagedDir
    )
    $extractRoot = Join-Path ([IO.Path]::GetTempPath()) ("robotopia-bundled-refs-extract-" + [Guid]::NewGuid().ToString("N"))
    try {
        Expand-Archive -LiteralPath $ArchivePath -DestinationPath $extractRoot -Force
        $managed = Find-ManagedDir $extractRoot
        Copy-ManagedDir $managed $DestinationManagedDir
    }
    finally {
        if (Test-Path -LiteralPath $extractRoot) {
            Remove-Item -LiteralPath $extractRoot -Recurse -Force
        }
    }
}

function Probe-PublicRefs {
    $config = Get-RefsConfig
    $archive = Get-SelectedArchive $config
    $archiveUri = Join-Uri $config.baseUrl $archive.Path

    if (![string]::IsNullOrWhiteSpace($config.manifestUrl)) {
        Test-Head $config.manifestUrl "Robotopia build manifest" | Out-Null
        $manifest = Invoke-WebRequest -Uri $config.manifestUrl -UseBasicParsing -MaximumRedirection 5
        if ($manifest.StatusCode -ge 200 -and $manifest.StatusCode -lt 400) {
            $body = $manifest.Content | ConvertFrom-Json
            if ([int]$body.id -eq [int]$config.buildId) {
                $manifestArchive = $body.$($archive.Platform)
                if ($null -ne $manifestArchive) {
                    if ([string]$manifestArchive.path -ne $archive.Path) {
                        throw "Pinned $($archive.Platform) path '$($archive.Path)' does not match manifest path '$($manifestArchive.path)'."
                    }
                    if (([string]$manifestArchive.sha256).Trim().ToLowerInvariant() -ne $archive.Sha256) {
                        throw "Pinned $($archive.Platform) SHA does not match latest manifest."
                    }
                }
            }
            else {
                Write-Warning "Latest manifest reports build $($body.id), while CI is pinned to build $($config.buildId)."
            }
        }
    }

    $archiveHead = Test-Head $archiveUri "Robotopia $($archive.Platform) archive"
    Write-Host "Robotopia public refs probe succeeded: build $($config.buildId), $($archive.Platform), $archiveUri"
    if ($archiveHead.Headers["Content-Length"]) {
        Write-Host "Archive content length: $($archiveHead.Headers["Content-Length"])"
    }
}

function Restore-PublicRefs {
    if ($Probe) {
        Probe-PublicRefs
        return
    }

    $config = Get-RefsConfig
    $archive = Get-SelectedArchive $config
    $cacheEntry = Join-Path (Get-CacheRoot) "public-$($config.buildId)-$($archive.Platform)-$($archive.Sha256)"
    $managedDest = Join-Path $cacheEntry "Managed"
    if (Test-ManagedDir $managedDest) {
        Write-Host "Using cached Robotopia public managed refs for build $($config.buildId)."
        Write-ManagedEnv $managedDest
        return
    }

    New-Item -ItemType Directory -Force -Path $cacheEntry | Out-Null
    $archiveUri = Join-Uri $config.baseUrl $archive.Path
    Probe-PublicRefs

    $download = Join-Path ([IO.Path]::GetTempPath()) ("robotopia-public-refs-" + [Guid]::NewGuid().ToString("N") + ".7z")
    try {
        Write-Host "Downloading Robotopia build $($config.buildId) refs source from $archiveUri"
        Invoke-WebRequest -Uri $archiveUri -OutFile $download -UseBasicParsing -MaximumRedirection 5
        Assert-Sha256 $download $archive.Sha256
        Expand-ManagedRefsFrom7z $download $managedDest
    }
    finally {
        if (Test-Path -LiteralPath $download) {
            Remove-Item -LiteralPath $download -Force
        }
    }

    Write-ManagedEnv $managedDest
}

function BundledRefsConfigured {
    return ![string]::IsNullOrWhiteSpace($env:ROBOTOPIA_REFS_URL) -and
        ![string]::IsNullOrWhiteSpace($env:ROBOTOPIA_REFS_SHA256)
}

function Get-BundledConfig {
    if (!(BundledRefsConfigured)) {
        throw "Bundled Robotopia refs require ROBOTOPIA_REFS_URL and ROBOTOPIA_REFS_SHA256."
    }

    $url = $env:ROBOTOPIA_REFS_URL.Trim()
    $sha = $env:ROBOTOPIA_REFS_SHA256.Trim().ToLowerInvariant()
    if ($sha -notmatch "^[0-9a-f]{64}$") {
        throw "ROBOTOPIA_REFS_SHA256 must be exactly 64 hexadecimal characters."
    }
    $uri = $null
    if (![Uri]::TryCreate($url, [UriKind]::Absolute, [ref]$uri) -or $uri.Scheme -ne "https") {
        throw "ROBOTOPIA_REFS_URL must be an absolute HTTPS URL."
    }

    return [PSCustomObject]@{
        Url = $url
        Sha256 = $sha
    }
}

function Restore-BundledRefs {
    $config = Get-BundledConfig
    $url = $config.Url
    $sha = $config.Sha256
    $headers = Get-Headers

    if ($Probe) {
        Test-Head $url "bundled Robotopia refs" $headers -HideUri | Out-Null
        Write-Host "Bundled Robotopia refs probe succeeded."
        return
    }

    $cacheEntry = Join-Path (Get-CacheRoot) "bundled-$sha"
    $managedDest = Join-Path $cacheEntry "Managed"
    if (Test-ManagedDir $managedDest) {
        Write-Host "Using cached bundled Robotopia managed refs."
        Write-ManagedEnv $managedDest
        return
    }

    New-Item -ItemType Directory -Force -Path $cacheEntry | Out-Null
    $download = Join-Path ([IO.Path]::GetTempPath()) ("robotopia-bundled-refs-" + [Guid]::NewGuid().ToString("N") + ".zip")
    try {
        try {
            $maximumRedirection = if ($headers.ContainsKey("Authorization")) { 0 } else { 5 }
            $response = Invoke-WebRequest -Uri $url -Headers $headers -OutFile $download -UseBasicParsing `
                -MaximumRedirection $maximumRedirection
            if ($response.StatusCode -lt 200 -or $response.StatusCode -ge 300) {
                throw "Bundled Robotopia refs download returned HTTP $($response.StatusCode)."
            }
        }
        catch {
            throw "Bundled Robotopia refs download failed."
        }
        Assert-Sha256 $download $sha
        Expand-ManagedRefsFromZip $download $managedDest
    }
    finally {
        if (Test-Path -LiteralPath $download) {
            Remove-Item -LiteralPath $download -Force
        }
    }

    Write-ManagedEnv $managedDest
}

if ($CacheKeyOnly) {
    Write-CacheKey
    exit 0
}

switch ($Source) {
    "public" {
        Restore-PublicRefs
    }
    "bundled" {
        Restore-BundledRefs
    }
    "auto" {
        try {
            Restore-PublicRefs
        }
        catch {
            Write-Warning "Public Robotopia refs failed: $($_.Exception.Message)"
            if (BundledRefsConfigured) {
                Write-Warning "Falling back to bundled Robotopia refs."
                Restore-BundledRefs
            }
            else {
                throw "Public Robotopia refs failed and bundled refs are not configured. $($_.Exception.Message)"
            }
        }
    }
}
