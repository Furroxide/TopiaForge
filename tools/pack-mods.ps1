param(
    [string]$Configuration = "Release",
    # Where the .robotopiamod packages are written. The launcher's bundled local source reads this
    # directory and derives its catalog from the packages, so refreshing it here is all that is
    # needed to update what Browse offers — there is no separate registry file to keep in sync.
    [string]$OutputDir = "",
    # DevTool-category mods (UI gallery etc.) never enter the published catalog unless requested;
    # install-local.ps1 still stages them for dev installs.
    [switch]$IncludeDevMods
)

$ErrorActionPreference = "Stop"
$repo = Resolve-Path (Join-Path $PSScriptRoot "..")
if ($OutputDir -eq "") {
    $OutputDir = Join-Path $repo "dist"
}
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
$packMod = Join-Path $PSScriptRoot "pack-mod.ps1"

# Every mod project plus the project template (which packs as the sample.hello mod). Discovered from
# disk, so a newly added mod is published automatically and a removed one stops being published — the
# set of packages can never lag the set of mod projects.
$projectDirs = @()
$modsDir = Join-Path $repo "mods"
if (Test-Path $modsDir) {
    $projectDirs += Get-ChildItem -LiteralPath $modsDir -Directory |
        Where-Object { Test-Path (Join-Path $_.FullName "robotopia.mod.json") } |
        ForEach-Object { $_.FullName }
}
$template = Join-Path $repo "templates\Robotopia.ModTemplate"
if (Test-Path (Join-Path $template "robotopia.mod.json")) {
    $projectDirs += $template
}

$packed = @()
foreach ($dir in $projectDirs) {
    $manifest = Get-Content -LiteralPath (Join-Path $dir "robotopia.mod.json") -Raw | ConvertFrom-Json
    if (-not $IncludeDevMods -and $manifest.category -eq "DevTool") {
        Write-Host ("Skipping dev-only mod {0} (pass -IncludeDevMods to pack it)." -f $manifest.id)
        continue
    }
    $safeId = ($manifest.id -replace "[^A-Za-z0-9_.-]", "_")

    # Drop any previously packed versions of this id so the directory holds exactly one (current)
    # package per mod and no superseded build can be installed by mistake.
    Get-ChildItem -LiteralPath $OutputDir -Filter "$safeId-*.robotopiamod" -ErrorAction SilentlyContinue |
        ForEach-Object { Remove-Item -LiteralPath $_.FullName -Force }

    $package = & $packMod -ProjectDir $dir -OutputDir $OutputDir -Configuration $Configuration | Select-Object -Last 1
    $sha = (Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash.ToLower()
    Write-Host ("Packed {0} ({1}) sha256={2}" -f $manifest.id, $manifest.version, $sha)
    $packed += [PSCustomObject]@{ Id = $manifest.id; Version = $manifest.version; Path = $package; Sha256 = $sha }
}

Write-Host ("Packed {0} mod package(s) into {1}." -f $packed.Count, $OutputDir)
$packed
