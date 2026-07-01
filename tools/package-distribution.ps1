param(
    [string]$Configuration = "Release",
    # Skip building the Flutter launcher GUI (e.g. on machines without Flutter installed).
    [switch]$SkipLauncher
)

$ErrorActionPreference = "Stop"
$repo = Resolve-Path (Join-Path $PSScriptRoot "..")
$distRoot = Join-Path $repo "dist\RobotopiaModManager"
$zipPath = Join-Path $repo "dist\RobotopiaModManager.zip"
$bepInEx = Join-Path $repo "third_party\BepInEx\win_x64_5.4.23.5"
$pluginOut = Join-Path $repo "src\Robotopia.ModManager\bin\$Configuration\netstandard2.1"

dotnet build (Join-Path $repo "RobotopiaModManager.slnx") -c $Configuration

# Refresh the bundled local mod packages (dist\*.robotopiamod). The launcher derives its catalog
# directly from these files, so this is the single source of truth for what Browse can install —
# there is no separate registry document to keep in sync.
& (Join-Path $PSScriptRoot "pack-mods.ps1") -Configuration $Configuration -OutputDir (Join-Path $repo "dist") | Out-Host

# Refresh the bundled Unity (VPM) package listing (dist\vpm\index.json) from the com.robotopia.* packages so the
# launcher's Unity package manager + the embedded resolver can install/restore them. Same drift-proof pattern.
& (Join-Path $PSScriptRoot "pack-unity-packages.ps1") | Out-Host

if (Test-Path $distRoot) {
    Remove-Item -LiteralPath $distRoot -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $distRoot | Out-Null

Copy-Item -LiteralPath (Join-Path $bepInEx ".doorstop_version") -Destination $distRoot -Force
Copy-Item -LiteralPath (Join-Path $bepInEx "doorstop_config.ini") -Destination $distRoot -Force
Copy-Item -LiteralPath (Join-Path $bepInEx "winhttp.dll") -Destination $distRoot -Force
Copy-Item -LiteralPath (Join-Path $bepInEx "BepInEx") -Destination $distRoot -Recurse -Force

$pluginDir = Join-Path $distRoot "BepInEx\plugins\RobotopiaModManager"
New-Item -ItemType Directory -Force -Path $pluginDir | Out-Null
Copy-Item -LiteralPath (Join-Path $pluginOut "Robotopia.ModManager.dll") -Destination $pluginDir -Force
Copy-Item -LiteralPath (Join-Path $pluginOut "Robotopia.ModManager.Core.dll") -Destination $pluginDir -Force
Copy-Item -LiteralPath (Join-Path $pluginOut "Robotopia.Mods.Abstractions.dll") -Destination $pluginDir -Force
Copy-Item -LiteralPath (Join-Path $pluginOut "Robotopia.Mods.UnityUi.dll") -Destination $pluginDir -Force

Copy-Item -LiteralPath (Join-Path $repo "tools") -Destination (Join-Path $distRoot "tools") -Recurse -Force
Copy-Item -LiteralPath (Join-Path $repo "docs") -Destination (Join-Path $distRoot "docs") -Recurse -Force
Copy-Item -LiteralPath (Join-Path $repo "README.md") -Destination $distRoot -Force

$templatesSource = Join-Path $repo "templates"
$templatesDest = Join-Path $distRoot "templates"
Get-ChildItem -LiteralPath $templatesSource -Recurse -File | Where-Object {
    $_.FullName -notmatch "\\bin\\" -and $_.FullName -notmatch "\\obj\\"
} | ForEach-Object {
    $relative = $_.FullName.Substring($templatesSource.Length).TrimStart('\', '/')
    $target = Join-Path $templatesDest $relative
    New-Item -ItemType Directory -Force -Path (Split-Path $target -Parent) | Out-Null
    Copy-Item -LiteralPath $_.FullName -Destination $target -Force
}

# Build and bundle the launcher GUI so consumers get a runnable app with no Flutter/Dart toolchain. Guarded:
# if Flutter is absent we warn and ship the runtime-only package rather than failing the whole distribution.
$launcherApp = Join-Path $repo "apps\robotopia_launcher_flutter"
if ($SkipLauncher) {
    Write-Warning "Skipping launcher GUI build (-SkipLauncher). The package will not include a prebuilt launcher."
}
elseif ($null -eq (Get-Command flutter -ErrorAction SilentlyContinue)) {
    Write-Warning "Flutter not found on PATH; skipping launcher GUI build. Install Flutter (or pass -SkipLauncher) to bundle the consumer launcher."
}
else {
    Write-Host "Building the launcher GUI (flutter build windows --release)..."
    Push-Location $launcherApp
    try {
        & flutter build windows --release
        if ($LASTEXITCODE -ne 0) { throw "flutter build windows failed (exit $LASTEXITCODE)." }
    }
    finally {
        Pop-Location
    }

    $releaseDir = @(
        (Join-Path $launcherApp "build\windows\x64\runner\Release"),
        (Join-Path $launcherApp "build\windows\runner\Release")
    ) | Where-Object { Test-Path $_ } | Select-Object -First 1
    if ($null -eq $releaseDir) { throw "Could not locate the Flutter Windows Release output." }

    $launcherDest = Join-Path $distRoot "launcher"
    New-Item -ItemType Directory -Force -Path $launcherDest | Out-Null
    Copy-Item -Path (Join-Path $releaseDir "*") -Destination $launcherDest -Recurse -Force
    Write-Host "Bundled launcher GUI from $releaseDir"

    # Bundle the game-compatibility checker next to the launcher exe so the launcher can run it (WARN-ONLY compat).
    # Self-contained so a consumer needs no .NET runtime; the launcher degrades to a 'unknown' pill if it is absent.
    # The tool resolves bindings/ + baselines/ from beside its own exe (there is no repo in a consumer install).
    Write-Host "Publishing the GameCompat extractor (self-contained) into the launcher payload..."
    $extractorProj = Join-Path $repo "src\Robotopia.GameCompat.Extractor\Robotopia.GameCompat.Extractor.csproj"
    $extractorPublish = Join-Path $repo "src\Robotopia.GameCompat.Extractor\bin\$Configuration\publish"
    & dotnet publish $extractorProj -c $Configuration -r win-x64 --self-contained true -p:PublishSingleFile=true -o $extractorPublish
    if ($LASTEXITCODE -ne 0) {
        Write-Warning "GameCompat extractor publish failed; the launcher will report compatibility as 'unknown'."
    }
    else {
        Copy-Item -LiteralPath (Join-Path $extractorPublish "Robotopia.GameCompat.Extractor.exe") -Destination $launcherDest -Force
        Copy-Item -LiteralPath (Join-Path $repo "bindings") -Destination (Join-Path $launcherDest "bindings") -Recurse -Force
        Copy-Item -LiteralPath (Join-Path $repo "baselines") -Destination (Join-Path $launcherDest "baselines") -Recurse -Force
        Write-Host "Bundled GameCompat extractor + bindings + baseline into the launcher payload."
    }
}

if (Test-Path $zipPath) {
    Remove-Item -LiteralPath $zipPath -Force
}
Compress-Archive -Path (Join-Path $distRoot "*") -DestinationPath $zipPath -Force
Write-Host "Created $zipPath"
