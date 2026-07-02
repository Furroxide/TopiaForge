param(
    [string]$UnityExe = "",
    [string]$Configuration = "Release"
)

# Builds the QuantumWorks brand AssetBundle (quantumworks-ui.bundle) from the committed
# Unity project at tools/unity-ui-bundle and copies it into src/Robotopia.Mods.UnityUi/Assets
# where the kit csproj embeds it into Robotopia.Mods.UnityUi.dll.
#
# HARD REQUIREMENT: the editor must be a 6000.0.x build with patch <= 31. The Robotopia
# player is Unity 6000.0.31f1; AssetBundles serialized by a newer editor stream (6000.5+)
# are not safe to load in that player.

$ErrorActionPreference = "Stop"
$repo = Resolve-Path (Join-Path $PSScriptRoot "..")
$projectPath = Join-Path $repo "tools\unity-ui-bundle"
$logDir = Join-Path $repo "build"
$logFile = Join-Path $logDir "ui-bundle-build.log"
$outputBundle = Join-Path $repo "src\Robotopia.Mods.UnityUi\Assets\quantumworks-ui.bundle"

function Test-EditorVersion {
    param([string]$Version)
    if ($Version -notmatch '^6000\.0\.(\d+)') {
        return $false
    }
    return [int]$Matches[1] -le 31
}

function Find-UnityEditor {
    $candidates = @()
    $hubEditors = Join-Path $env:ProgramFiles "Unity\Hub\Editor"
    if (Test-Path $hubEditors) {
        $candidates += Get-ChildItem -LiteralPath $hubEditors -Directory | ForEach-Object {
            [PSCustomObject]@{ Version = $_.Name; Exe = Join-Path $_.FullName "Editor\Unity.exe" }
        }
    }

    $secondaryConfig = Join-Path $env:APPDATA "UnityHub\secondaryInstallPath.json"
    if (Test-Path $secondaryConfig) {
        $secondary = (Get-Content -LiteralPath $secondaryConfig -Raw).Trim('"', ' ', "`r", "`n")
        if ($secondary -and (Test-Path $secondary)) {
            $candidates += Get-ChildItem -LiteralPath $secondary -Directory | ForEach-Object {
                [PSCustomObject]@{ Version = $_.Name; Exe = Join-Path $_.FullName "Editor\Unity.exe" }
            }
        }
    }

    $eligible = $candidates | Where-Object { (Test-EditorVersion $_.Version) -and (Test-Path $_.Exe) }
    if (-not $eligible) {
        return $null
    }

    # Prefer the highest eligible patch (closest to the 6000.0.31f1 player).
    return ($eligible | Sort-Object { [int]([regex]::Match($_.Version, '^6000\.0\.(\d+)').Groups[1].Value) } -Descending | Select-Object -First 1).Exe
}

if ([string]::IsNullOrWhiteSpace($UnityExe)) {
    $UnityExe = Find-UnityEditor
    if (-not $UnityExe) {
        throw ("No eligible Unity editor found. Install Unity 6000.0.23f1-6000.0.31f1 via Unity Hub " +
            "(the Robotopia player is 6000.0.31f1; bundles built by newer editor streams will not load). " +
            "Headless install: & `"$env:ProgramFiles\Unity Hub\Unity Hub.exe`" -- --headless install --version 6000.0.31f1 --changeset a206c360e2a8")
    }
}

# Version guard also applies to an explicitly supplied editor.
$versionInfo = (Get-Item -LiteralPath $UnityExe).VersionInfo.ProductVersion
if ($versionInfo -and -not (Test-EditorVersion $versionInfo)) {
    throw "Editor at $UnityExe reports version '$versionInfo' - required: 6000.0.x with patch <= 31."
}

New-Item -ItemType Directory -Force -Path $logDir | Out-Null

# Unity.exe is a GUI-subsystem binary: '&' would return immediately, so launch via
# Start-Process and wait for the real exit code. No -nographics: it breaks Shader.Find,
# which the TMP font baking needs. No -quit: the builder methods exit explicitly (the
# essentials import is asynchronous and exits from its completion callback).
function Invoke-UnityMethod {
    param([string]$Method, [string]$PhaseLog)
    $unityArgs = @(
        "-batchmode",
        "-projectPath", ('"' + $projectPath + '"'),
        "-executeMethod", $Method,
        "-logFile", ('"' + $PhaseLog + '"')
    )
    $process = Start-Process -FilePath $UnityExe -ArgumentList $unityArgs -Wait -PassThru -NoNewWindow
    if ($process.ExitCode -ne 0) {
        if (Test-Path $PhaseLog) {
            Write-Host "--- last 40 log lines ($PhaseLog) ---"
            Get-Content -LiteralPath $PhaseLog -Tail 40
        }

        throw "Unity phase '$Method' failed with exit code $($process.ExitCode). Full log: $PhaseLog"
    }
}

if (!(Test-Path (Join-Path $projectPath "Assets\TextMesh Pro"))) {
    Write-Host "Importing TMP essentials (first run) with $UnityExe"
    Invoke-UnityMethod -Method "Robotopia.UiBundleBuilder.ImportEssentials" -PhaseLog (Join-Path $logDir "ui-bundle-essentials.log")
}

Write-Host "Building UI bundle with $UnityExe"
Invoke-UnityMethod -Method "Robotopia.UiBundleBuilder.Build" -PhaseLog $logFile

if (!(Test-Path $outputBundle)) {
    throw "Unity reported success but $outputBundle was not produced. Check $logFile."
}

$sha = (Get-FileHash -LiteralPath $outputBundle -Algorithm SHA256).Hash.ToLowerInvariant()
$size = (Get-Item -LiteralPath $outputBundle).Length
Write-Host "UI bundle written: $outputBundle"
Write-Host "  size:   $([math]::Round($size / 1MB, 2)) MB"
Write-Host "  sha256: $sha"
Write-Host "Rebuild Robotopia.Mods.UnityUi so the embedded resource picks up the new bundle."
