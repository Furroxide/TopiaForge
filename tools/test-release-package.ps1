param(
    [Parameter(Mandatory = $true)]
    [ValidateSet("windows", "macos", "linux")]
    [string]$Platform,
    [Parameter(Mandatory = $true)]
    [string]$ZipPath,
    [switch]$RequireMacUniversal
)

$ErrorActionPreference = "Stop"

function Join-Parts([string[]]$Parts) {
    if ($Parts.Count -eq 0) { return "" }
    $result = $Parts[0]
    for ($i = 1; $i -lt $Parts.Count; $i++) {
        $result = Join-Path $result $Parts[$i]
    }
    return $result
}

function Assert-Path([string]$Path, [string]$Message) {
    if (!(Test-Path $Path)) {
        throw "$Message Missing path: $Path"
    }
}

function Assert-Executable([string]$Path) {
    Assert-Path $Path "Expected executable file."
    if ($Platform -eq "windows") { return }
    & test -x $Path
    if ($LASTEXITCODE -ne 0) {
        throw "Expected executable bit to be set: $Path"
    }
}

function Expand-Package([string]$Archive, [string]$Destination, [string]$Platform) {
    New-Item -ItemType Directory -Force -Path $Destination | Out-Null
    if ($Platform -eq "macos") {
        & /usr/bin/ditto -x -k $Archive $Destination
        if ($LASTEXITCODE -ne 0) { throw "ditto extraction failed." }
        return
    }
    if ($Platform -eq "linux") {
        & unzip -q $Archive -d $Destination
        if ($LASTEXITCODE -ne 0) { throw "unzip extraction failed." }
        return
    }
    Expand-Archive -LiteralPath $Archive -DestinationPath $Destination -Force
}

function Assert-CliRuns([string]$CliPath) {
    Assert-Executable $CliPath
    $result = & $CliPath --help
    if ($LASTEXITCODE -ne 0) {
        throw "CLI help failed with exit $LASTEXITCODE."
    }
    if (($result -join "`n") -notmatch "QuantumWorks CLI") {
        throw "CLI help output did not contain the expected banner."
    }
}

function Assert-Payload([string]$PayloadRoot) {
    Assert-Path (Join-Path $PayloadRoot "tools") "Package must include tools/."
    Assert-Path (Join-Path $PayloadRoot "templates") "Package must include templates/."
    Assert-Path (Join-Path $PayloadRoot "docs") "Package must include docs/."
    Assert-Path (Join-Path $PayloadRoot "bindings") "Package must include bindings/."
    Assert-Path (Join-Path $PayloadRoot "baselines") "Package must include baselines/."
    Assert-Path (Join-Path $PayloadRoot "THIRD_PARTY_NOTICES.md") "Package must include third-party notices."
    Assert-Path (Join-Parts @($PayloadRoot, "dist", "vpm", "index.json")) "Package must include dist/vpm/index.json."
    $extractorName = if ($Platform -eq "windows") { "Robotopia.GameCompat.Extractor.exe" } else { "Robotopia.GameCompat.Extractor" }
    Assert-Path (Join-Path $PayloadRoot $extractorName) "Package must include the GameCompat extractor."

    $packages = Get-ChildItem -LiteralPath (Join-Path $PayloadRoot "dist") -Filter "*.robotopiamod" -File -ErrorAction SilentlyContinue
    if ($packages.Count -eq 0) {
        throw "Package must include at least one dist/*.robotopiamod file."
    }
}

# Every platform ships the loader runtime the launcher's Repair flow reads: the BepInEx bundle under
# third_party/ (win_x64 for windows AND linux — Proton runs the Windows game; macos_universal for macOS)
# plus the built loader DLLs under src/. Windows additionally keeps the legacy game-overlay layout at
# the payload root.
function Assert-RuntimePayload([string]$PayloadRoot) {
    $bundleName = if ($Platform -eq "macos") { "macos_universal_5.4.23.5" } else { "win_x64_5.4.23.5" }
    $bundle = Join-Parts @($PayloadRoot, "third_party", "BepInEx", $bundleName)
    if ($Platform -eq "macos") {
        Assert-Path (Join-Path $bundle "run_bepinex.sh") "macOS package must include the BepInEx run script."
        Assert-Path (Join-Path $bundle "libdoorstop.dylib") "macOS package must include libdoorstop."
    }
    else {
        Assert-Path (Join-Path $bundle "winhttp.dll") "Package must include Doorstop."
        Assert-Path (Join-Path $bundle "doorstop_config.ini") "Package must include Doorstop config."
    }
    Assert-Path (Join-Parts @($bundle, "BepInEx", "core")) "Package must include BepInEx core."

    $loaderDir = Join-Parts @($PayloadRoot, "src", "Robotopia.ModManager", "bin", "Release", "netstandard2.1")
    Assert-Path (Join-Path $loaderDir "Robotopia.ModManager.dll") "Package must include the loader."
    Assert-Path (Join-Path $loaderDir "Robotopia.Mods.UnityUi.dll") "Package must include the UI kit."

    if ($Platform -eq "windows") {
        Assert-Path (Join-Path $PayloadRoot "winhttp.dll") "Windows package must include the game-overlay Doorstop."
        Assert-Path (Join-Parts @($PayloadRoot, "BepInEx", "plugins", "RobotopiaModManager", "Robotopia.ModManager.dll")) "Windows package must include the overlay loader."
    }
}

function Assert-MacUniversal([string]$Path, [string]$Label) {
    if (!$RequireMacUniversal) { return }
    $archs = (& lipo -archs $Path) -join " "
    if ($LASTEXITCODE -ne 0) {
        throw "lipo failed for $Label."
    }
    if ($archs -notmatch "arm64" -or $archs -notmatch "x86_64") {
        throw "$Label is not universal. Found archs: $archs"
    }
}

$ZipPath = (Resolve-Path $ZipPath).Path
$tempRoot = Join-Path ([IO.Path]::GetTempPath()) ("quantumworks-package-test-" + [Guid]::NewGuid().ToString("N"))
try {
    Expand-Package $ZipPath $tempRoot $Platform

    if ($Platform -eq "macos") {
        $app = Join-Path $tempRoot "QuantumWorks.app"
        Assert-Path $app "macOS package must include QuantumWorks.app."
        $payload = Join-Parts @($app, "Contents", "Resources", "QuantumWorks")
        $cli = Join-Path $payload "robotopia"
        $shim = Join-Path $tempRoot "robotopia"
        $appBinary = Join-Parts @($app, "Contents", "MacOS", "QuantumWorks")
        Assert-Payload $payload
        Assert-RuntimePayload $payload
        Assert-CliRuns $cli
        Assert-CliRuns $shim
        Assert-MacUniversal $cli "robotopia CLI"
        Assert-MacUniversal $appBinary "QuantumWorks.app binary"
    }
    else {
        $payload = $tempRoot
        $cli = if ($Platform -eq "windows") {
            Join-Path $tempRoot "robotopia.exe"
        }
        else {
            Join-Path $tempRoot "robotopia"
        }
        Assert-Path (Join-Path $tempRoot "launcher") "Package must include launcher/."
        Assert-Payload $payload
        Assert-RuntimePayload $payload
        Assert-CliRuns $cli
        if ($Platform -eq "linux") {
            Assert-Executable (Join-Parts @($tempRoot, "launcher", "robotopia_launcher_flutter"))
        }
        if ($Platform -eq "windows") {
            Assert-Path (Join-Parts @($tempRoot, "launcher", "robotopia_launcher_flutter.exe")) "Windows package must include launcher exe."
        }
    }

    Write-Host "Package smoke test passed: $ZipPath"
}
finally {
    if (Test-Path $tempRoot) {
        Remove-Item -LiteralPath $tempRoot -Recurse -Force
    }
}
