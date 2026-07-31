[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"

function Get-RequiredEnvironmentPath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name
    )

    $value = [Environment]::GetEnvironmentVariable($Name)
    if ([string]::IsNullOrWhiteSpace($value)) {
        throw "Required environment variable '$Name' is not set."
    }

    return [System.IO.Path]::GetFullPath($value)
}

function Get-Sha256 {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Test-ArchiveDigest {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [string]$ExpectedSha256
    )

    return (Test-Path -LiteralPath $Path -PathType Leaf) -and
        ((Get-Sha256 -Path $Path) -ceq $ExpectedSha256)
}

$manifestPath = Join-Path $PSScriptRoot "releases.json"
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ($manifest.schemaVersion -ne 1) {
    throw "Unsupported Flutter archive manifest schema '$($manifest.schemaVersion)'."
}
if ($manifest.flutterVersion -cne "3.44.6" -or $manifest.dartVersion -cne "3.12.2") {
    throw "The Flutter archive manifest does not contain the repository-pinned SDK versions."
}

$runnerOs = $env:RUNNER_OS
$runnerArch = $env:RUNNER_ARCH
if ([string]::IsNullOrWhiteSpace($runnerOs) -or [string]::IsNullOrWhiteSpace($runnerArch)) {
    throw "RUNNER_OS and RUNNER_ARCH must identify the GitHub-hosted runner."
}

$matchingArchives = @(
    $manifest.archives | Where-Object {
        $_.runnerOs -ceq $runnerOs -and $_.runnerArch -ceq $runnerArch
    }
)
if ($matchingArchives.Count -ne 1) {
    throw "No unique pinned Flutter archive exists for runner '$runnerOs/$runnerArch'."
}
$archive = $matchingArchives[0]

$relativeUri = [string]$archive.relativeUri
$expectedSha256 = [string]$archive.sha256
if ($relativeUri -notmatch '^stable/(linux|windows|macos)/[A-Za-z0-9._/-]+$' -or
    $relativeUri -match '(^|/)\.\.?(/|$)') {
    throw "The pinned Flutter archive path is invalid."
}
if ($expectedSha256 -cnotmatch '^[0-9a-f]{64}$') {
    throw "The pinned Flutter archive digest is invalid."
}

$baseUri = [Uri]$manifest.baseUri
$downloadUri = [Uri]::new($baseUri, $relativeUri)
if ($downloadUri.Scheme -cne "https" -or
    $downloadUri.Host -cne "storage.googleapis.com" -or
    -not $downloadUri.AbsolutePath.StartsWith(
        "/flutter_infra_release/releases/",
        [StringComparison]::Ordinal
    )) {
    throw "The pinned Flutter archive URI is not an approved official HTTPS location."
}

$archiveDirectory = Get-RequiredEnvironmentPath `
    -Name "TOPIAFORGE_FLUTTER_ARCHIVE_DIRECTORY"
$installDirectory = Get-RequiredEnvironmentPath `
    -Name "TOPIAFORGE_FLUTTER_INSTALL_DIRECTORY"
New-Item -ItemType Directory -Path $archiveDirectory -Force | Out-Null

if (Test-Path -LiteralPath $installDirectory) {
    if (@(Get-ChildItem -LiteralPath $installDirectory -Force).Count -ne 0) {
        throw "Flutter install directory '$installDirectory' must be empty."
    }
}
else {
    New-Item -ItemType Directory -Path $installDirectory | Out-Null
}

$archiveName = [System.IO.Path]::GetFileName($downloadUri.AbsolutePath)
$archivePath = Join-Path $archiveDirectory $archiveName
if ((Test-Path -LiteralPath $archivePath -PathType Leaf) -and
    -not (Test-ArchiveDigest -Path $archivePath -ExpectedSha256 $expectedSha256)) {
    Write-Warning "Discarding a cached Flutter archive with the wrong SHA-256 digest."
    Remove-Item -LiteralPath $archivePath -Force
}

if (-not (Test-Path -LiteralPath $archivePath -PathType Leaf)) {
    $partialPath = "$archivePath.partial"
    if (Test-Path -LiteralPath $partialPath -PathType Leaf) {
        Remove-Item -LiteralPath $partialPath -Force
    }

    try {
        Write-Host "Downloading Flutter $($manifest.flutterVersion) for $runnerOs/$runnerArch."
        Invoke-WebRequest `
            -Uri $downloadUri `
            -OutFile $partialPath `
            -MaximumRetryCount 4 `
            -RetryIntervalSec 2 `
            -TimeoutSec 1800

        $actualSha256 = Get-Sha256 -Path $partialPath
        if ($actualSha256 -cne $expectedSha256) {
            throw "Flutter archive SHA-256 mismatch. Expected '$expectedSha256'; " +
                "downloaded '$actualSha256'."
        }

        Move-Item -LiteralPath $partialPath -Destination $archivePath
    }
    finally {
        if (Test-Path -LiteralPath $partialPath -PathType Leaf) {
            Remove-Item -LiteralPath $partialPath -Force
        }
    }
}

if (-not (Test-ArchiveDigest -Path $archivePath -ExpectedSha256 $expectedSha256)) {
    throw "The cached Flutter archive failed final SHA-256 verification."
}

$tar = Get-Command tar -CommandType Application -ErrorAction Stop |
    Select-Object -First 1
& $tar.Source -xf $archivePath -C $installDirectory
if ($LASTEXITCODE -ne 0) {
    throw "Could not extract the verified Flutter archive."
}

$flutterRoot = Join-Path $installDirectory "flutter"
$flutterExecutableName = if ($runnerOs -ceq "Windows") { "flutter.bat" } else { "flutter" }
$flutterExecutable = Join-Path $flutterRoot "bin/$flutterExecutableName"
$dartVersionPath = Join-Path $flutterRoot "bin/cache/dart-sdk/version"
if (-not (Test-Path -LiteralPath $flutterExecutable -PathType Leaf)) {
    throw "The verified Flutter archive did not contain '$flutterExecutable'."
}
if (-not (Test-Path -LiteralPath $dartVersionPath -PathType Leaf)) {
    throw "The verified Flutter archive did not contain the pinned Dart SDK version file."
}

$embeddedDartVersion = (Get-Content -LiteralPath $dartVersionPath -Raw).Trim()
if ($embeddedDartVersion -cne $manifest.dartVersion) {
    throw "The verified Flutter archive contains Dart '$embeddedDartVersion', not " +
        "'$($manifest.dartVersion)'."
}

git config --global --add safe.directory $flutterRoot
if ($LASTEXITCODE -ne 0) {
    throw "Could not mark the verified Flutter SDK as a safe Git directory."
}

& $flutterExecutable config --no-analytics
if ($LASTEXITCODE -ne 0) {
    throw "Could not disable Flutter analytics on the ephemeral runner."
}

$versionJson = (& $flutterExecutable --version --machine) -join [Environment]::NewLine
if ($LASTEXITCODE -ne 0) {
    throw "Could not query the verified Flutter SDK version."
}
$version = $versionJson | ConvertFrom-Json
if ($version.frameworkVersion -cne $manifest.flutterVersion -or
    $version.dartSdkVersion -cne $manifest.dartVersion) {
    throw "Flutter reported unexpected framework or Dart SDK versions."
}

$flutterBin = Join-Path $flutterRoot "bin"
Add-Content -LiteralPath $env:GITHUB_PATH -Value $flutterBin -Encoding utf8
Add-Content -LiteralPath $env:GITHUB_ENV -Value "FLUTTER_ROOT=$flutterRoot" -Encoding utf8
Write-Host "Verified Flutter $($version.frameworkVersion) and Dart $($version.dartSdkVersion)."
