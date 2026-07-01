param(
    [string]$GameDir = "C:\Users\vanst\AppData\Local\Tomato Cake\launcher\Robotopia",
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$repo = Resolve-Path (Join-Path $PSScriptRoot "..")
$bepInEx = Join-Path $repo "third_party\BepInEx\win_x64_5.4.23.5"
$pluginOut = Join-Path $repo "src\Robotopia.ModManager\bin\$Configuration\netstandard2.1"
$templateOut = Join-Path $repo "templates\Robotopia.ModTemplate\bin\$Configuration\netstandard2.1"
$pluginDir = Join-Path $GameDir "BepInEx\plugins\RobotopiaModManager"

dotnet build (Join-Path $repo "RobotopiaModManager.slnx") -c $Configuration

if (!(Test-Path (Join-Path $GameDir "Robotopia.exe"))) {
    throw "Robotopia.exe was not found in $GameDir"
}

Copy-Item -LiteralPath (Join-Path $bepInEx ".doorstop_version") -Destination $GameDir -Force
Copy-Item -LiteralPath (Join-Path $bepInEx "doorstop_config.ini") -Destination $GameDir -Force
Copy-Item -LiteralPath (Join-Path $bepInEx "winhttp.dll") -Destination $GameDir -Force
Copy-Item -LiteralPath (Join-Path $bepInEx "BepInEx") -Destination $GameDir -Recurse -Force

New-Item -ItemType Directory -Force -Path $pluginDir | Out-Null
Copy-Item -LiteralPath (Join-Path $pluginOut "Robotopia.ModManager.dll") -Destination $pluginDir -Force
Copy-Item -LiteralPath (Join-Path $pluginOut "Robotopia.ModManager.Core.dll") -Destination $pluginDir -Force
Copy-Item -LiteralPath (Join-Path $pluginOut "Robotopia.Mods.Abstractions.dll") -Destination $pluginDir -Force
Copy-Item -LiteralPath (Join-Path $pluginOut "Robotopia.Mods.UnityUi.dll") -Destination $pluginDir -Force

$managerRoot = Join-Path $GameDir "BepInEx\RobotopiaModManager"
New-Item -ItemType Directory -Force -Path (Join-Path $managerRoot "package-inbox") | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $managerRoot "logs") | Out-Null

$inbox = Join-Path $managerRoot "package-inbox"
$samplePackage = & (Join-Path $PSScriptRoot "pack-mod.ps1") -ProjectDir (Join-Path $repo "templates\Robotopia.ModTemplate") -OutputDir $inbox -Configuration $Configuration

# Stage every first-party mod under mods\ so a dev install also refreshes them. Without this the only thing
# ever packed was the template, so source fixes to shipped mods (e.g. Robotopia.Worlds) were never pushed to
# the game and the running build silently went stale. Install them from the package-inbox after launching.
$modsRoot = Join-Path $repo "mods"
$stagedMods = @()
if (Test-Path $modsRoot) {
    Get-ChildItem -LiteralPath $modsRoot -Directory | Where-Object {
        Test-Path (Join-Path $_.FullName "robotopia.mod.json")
    } | ForEach-Object {
        $modPackage = & (Join-Path $PSScriptRoot "pack-mod.ps1") -ProjectDir $_.FullName -OutputDir $inbox -Configuration $Configuration
        $stagedMods += $modPackage
    }
}

Write-Host "Installed QuantumWorks to $pluginDir"
Write-Host "Sample package created in package-inbox: $samplePackage"
foreach ($staged in $stagedMods) {
    Write-Host "Mod package staged in package-inbox: $staged"
}
Write-Host "Launch Robotopia once, then press F10 (or the main-menu Mod Manager button) and install the package-inbox to apply the latest mod builds."
