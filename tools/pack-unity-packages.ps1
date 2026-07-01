param(
    # Where the VPM packages + listing are written. The launcher/CLI/embedded resolver read dist/vpm/index.json
    # and derive the catalog from it — there is no separate registry file to keep in sync.
    [string]$OutputDir = ""
)

$ErrorActionPreference = "Stop"
$repo = Resolve-Path (Join-Path $PSScriptRoot "..")
if ($OutputDir -eq "") {
    $OutputDir = Join-Path $repo "dist\vpm"
}
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

# Discover every shipped com.robotopia.* Unity package (a folder with a package.json whose name starts with
# com.robotopia.), excluding the package SCAFFOLD template and any Samples~ payloads. Disk-discovered, so a new
# package publishes automatically — the listing can never lag the packages.
$templatesDir = Join-Path $repo "templates"
$packageJsons = Get-ChildItem -LiteralPath $templatesDir -Recurse -Filter "package.json" -File |
    Where-Object {
        $_.FullName -notmatch "Samples~" -and
        $_.FullName -notmatch "Robotopia\.UnityPackageTemplate"
    }

$packages = @{}
foreach ($pj in $packageJsons) {
    $manifest = Get-Content -LiteralPath $pj.FullName -Raw | ConvertFrom-Json
    if (-not $manifest.name -or -not ($manifest.name -like "com.robotopia.*")) {
        continue
    }
    $id = $manifest.name
    $version = $manifest.version
    if (-not $version) { $version = "0.0.0" }
    $packageDir = Split-Path -Parent $pj.FullName
    $safeId = ($id -replace "[^A-Za-z0-9_.-]", "_")

    # Exactly one current zip per id.
    Get-ChildItem -LiteralPath $OutputDir -Filter "$safeId-*.zip" -ErrorAction SilentlyContinue |
        ForEach-Object { Remove-Item -LiteralPath $_.FullName -Force }

    $zipFileName = "$safeId-$version.zip"
    $zip = Join-Path $OutputDir $zipFileName
    if (Test-Path $zip) { Remove-Item -LiteralPath $zip -Force }
    # Zip the package CONTENTS (package.json at the zip root) so the resolver extracts straight into Packages/<id>.
    Compress-Archive -Path (Join-Path $packageDir "*") -DestinationPath $zip -Force
    $sha = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLower()

    # The version entry is the full package.json plus url + zipSHA256 (VPM listing shape).
    $entry = $manifest | ConvertTo-Json -Depth 20 | ConvertFrom-Json
    $entry | Add-Member -NotePropertyName "url" -NotePropertyValue $zipFileName -Force
    $entry | Add-Member -NotePropertyName "zipSHA256" -NotePropertyValue $sha -Force

    if (-not $packages.ContainsKey($id)) {
        $packages[$id] = @{ versions = @{} }
    }
    $packages[$id].versions[$version] = $entry
    Write-Host "Packed $id $version -> $zip"
}

$indexPath = Join-Path $OutputDir "index.json"
$listing = [ordered]@{
    name     = "QuantumWorks Local"
    id       = "com.robotopia.repos.local"
    author   = "QuantumWorks"
    url      = "index.json"
    packages = $packages
}
$listing | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $indexPath -Encoding UTF8
Write-Host "Wrote $indexPath ($($packages.Count) package(s))."
