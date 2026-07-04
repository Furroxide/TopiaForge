param(
    [ValidateSet("", "windows", "macos", "linux")]
    [string]$Platform = "",
    [string]$Configuration = "Release",
    [string]$OutputRoot = "",
    # Skip building the Flutter launcher GUI (e.g. on machines without Flutter installed).
    [switch]$SkipLauncher,
    # Skip the C# runtime/mod package build and copy only existing dist payloads.
    [switch]$SkipRuntime,
    # Used by CI for the macOS universal CLI produced with lipo.
    [string]$PrebuiltCli = ""
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

function Resolve-TargetPlatform([string]$Value) {
    if ($Value -ne "") { return $Value }
    if ($env:OS -eq "Windows_NT") { return "windows" }
    if ($IsMacOS) { return "macos" }
    if ($IsLinux) { return "linux" }
    throw "Could not infer target platform. Pass -Platform windows|macos|linux."
}

function Invoke-Step([string]$Description, [string]$WorkingDirectory, [scriptblock]$Block) {
    Write-Host $Description
    Push-Location $WorkingDirectory
    try {
        & $Block
        if ($LASTEXITCODE -ne 0) {
            throw "$Description failed (exit $LASTEXITCODE)."
        }
    }
    finally {
        Pop-Location
    }
}

function Copy-DirectoryContents([string]$Source, [string]$Destination) {
    if (!(Test-Path $Source)) { return }
    New-Item -ItemType Directory -Force -Path $Destination | Out-Null
    Copy-Item -Path (Join-Path $Source "*") -Destination $Destination -Recurse -Force
}

function Copy-DirectoryIfExists([string]$Source, [string]$Destination) {
    if (Test-Path $Source) {
        Copy-Item -LiteralPath $Source -Destination $Destination -Recurse -Force
    }
}

function Copy-FileIfExists([string]$Source, [string]$Destination) {
    if (Test-Path $Source) {
        New-Item -ItemType Directory -Force -Path (Split-Path $Destination -Parent) | Out-Null
        Copy-Item -LiteralPath $Source -Destination $Destination -Force
    }
}

function Set-ExecutableBit([string]$Path) {
    if ($script:TargetPlatform -eq "windows") { return }
    if (Test-Path $Path) {
        & chmod +x $Path
        if ($LASTEXITCODE -ne 0) { throw "chmod +x failed for $Path." }
    }
}

function Get-ArchiveName([string]$Platform) {
    switch ($Platform) {
        "windows" { return "QuantumWorks-windows-x64.zip" }
        "macos" { return "QuantumWorks-macos-universal.zip" }
        "linux" { return "QuantumWorks-linux-x64.zip" }
    }
}

function Get-DotnetRuntimeId([string]$Platform) {
    switch ($Platform) {
        "windows" { return "win-x64" }
        "macos" {
            if ([System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture -eq [System.Runtime.InteropServices.Architecture]::Arm64) {
                return "osx-arm64"
            }
            return "osx-x64"
        }
        "linux" { return "linux-x64" }
    }
}

function New-ZipFromDirectory([string]$SourceDir, [string]$DestinationZip, [string]$Platform) {
    if (Test-Path $DestinationZip) {
        Remove-Item -LiteralPath $DestinationZip -Force
    }

    if ($Platform -eq "windows") {
        Compress-Archive -Path (Join-Path $SourceDir "*") -DestinationPath $DestinationZip -Force
        return
    }

    Push-Location $SourceDir
    try {
        if ($Platform -eq "macos") {
            & /usr/bin/ditto -c -k --sequesterRsrc --rsrc "." $DestinationZip
        }
        else {
            & zip -q -r $DestinationZip "."
        }
        if ($LASTEXITCODE -ne 0) {
            throw "Creating $DestinationZip failed (exit $LASTEXITCODE)."
        }
    }
    finally {
        Pop-Location
    }
}

function Test-EnvValue([string]$Name) {
    $value = [Environment]::GetEnvironmentVariable($Name)
    return ![string]::IsNullOrWhiteSpace($value)
}

function Invoke-CodeSign([string]$Path, [string]$Identity, [string]$Keychain, [switch]$Deep) {
    if (!(Test-Path $Path)) { return }

    $args = @("--force")
    if ($Identity -ne "-") {
        $args += @("--options", "runtime", "--timestamp")
    }
    if ($Deep) {
        $args += "--deep"
    }
    $args += @("--sign", $Identity)
    if ($Keychain -ne "") {
        $args += @("--keychain", $Keychain)
    }
    $args += $Path

    & codesign @args
    if ($LASTEXITCODE -ne 0) {
        throw "codesign failed for $Path."
    }
}

function Invoke-MacNotarizationIfConfigured([string]$AppBundle, [string]$StageRoot) {
    $hasNotarySecrets = (Test-EnvValue "MACOS_NOTARY_APPLE_ID") -and
        (Test-EnvValue "MACOS_NOTARY_PASSWORD") -and
        (Test-EnvValue "MACOS_NOTARY_TEAM_ID")
    if (!$hasNotarySecrets) {
        Write-Warning "macOS notary secrets are incomplete; package will be signed but not notarized."
        return
    }

    $notaryZip = Join-Path ([IO.Path]::GetTempPath()) ("quantumworks-notary-" + [Guid]::NewGuid().ToString("N") + ".zip")
    try {
        Push-Location $StageRoot
        try {
            & /usr/bin/ditto -c -k --keepParent (Split-Path $AppBundle -Leaf) $notaryZip
            if ($LASTEXITCODE -ne 0) { throw "Creating notarization zip failed." }
        }
        finally {
            Pop-Location
        }

        & xcrun notarytool submit $notaryZip `
            --apple-id $env:MACOS_NOTARY_APPLE_ID `
            --password $env:MACOS_NOTARY_PASSWORD `
            --team-id $env:MACOS_NOTARY_TEAM_ID `
            --wait
        if ($LASTEXITCODE -ne 0) { throw "notarytool submit failed." }

        & xcrun stapler staple $AppBundle
        if ($LASTEXITCODE -ne 0) { throw "stapler failed." }
    }
    finally {
        if (Test-Path $notaryZip) {
            Remove-Item -LiteralPath $notaryZip -Force
        }
    }
}

function Sign-MacAppIfConfigured([string]$AppBundle, [string]$StageRoot) {
    if ($script:TargetPlatform -ne "macos") { return }
    if ($null -eq (Get-Command codesign -ErrorAction SilentlyContinue)) {
        Write-Warning "codesign was not found; macOS package will be unsigned."
        return
    }

    $hasSigningSecrets = (Test-EnvValue "MACOS_CERTIFICATE_P12") -and
        (Test-EnvValue "MACOS_CERTIFICATE_PASSWORD") -and
        (Test-EnvValue "MACOS_DEVELOPER_ID_APPLICATION")

    if (!$hasSigningSecrets) {
        Write-Warning "macOS signing secrets are incomplete; applying an ad-hoc app signature when possible."
        try {
            Invoke-CodeSign $AppBundle "-" "" -Deep
            & codesign --verify --deep --strict --verbose=2 $AppBundle
            if ($LASTEXITCODE -ne 0) { throw "codesign verification failed." }
        }
        catch {
            Write-Warning "Ad-hoc signing failed; continuing with an unsigned macOS package. $($_.Exception.Message)"
        }
        return
    }

    $keychain = Join-Path ([IO.Path]::GetTempPath()) ("quantumworks-signing-" + [Guid]::NewGuid().ToString("N") + ".keychain-db")
    $keychainPassword = [Guid]::NewGuid().ToString("N")
    $certPath = Join-Path ([IO.Path]::GetTempPath()) ("quantumworks-cert-" + [Guid]::NewGuid().ToString("N") + ".p12")
    try {
        $certBase64 = $env:MACOS_CERTIFICATE_P12 -replace "\s", ""
        [IO.File]::WriteAllBytes($certPath, [Convert]::FromBase64String($certBase64))

        & security create-keychain -p $keychainPassword $keychain
        if ($LASTEXITCODE -ne 0) { throw "security create-keychain failed." }
        & security set-keychain-settings -lut 21600 $keychain
        if ($LASTEXITCODE -ne 0) { throw "security set-keychain-settings failed." }
        & security unlock-keychain -p $keychainPassword $keychain
        if ($LASTEXITCODE -ne 0) { throw "security unlock-keychain failed." }
        & security import $certPath -P $env:MACOS_CERTIFICATE_PASSWORD -A -t cert -f pkcs12 -k $keychain
        if ($LASTEXITCODE -ne 0) { throw "security import failed." }
        & security set-key-partition-list -S apple-tool:,apple: -s -k $keychainPassword $keychain
        if ($LASTEXITCODE -ne 0) { throw "security set-key-partition-list failed." }

        $identity = $env:MACOS_DEVELOPER_ID_APPLICATION
        $payloadRoot = Join-Parts @($AppBundle, "Contents", "Resources", "QuantumWorks")
        foreach ($binary in @(
            (Join-Path $payloadRoot "robotopia"),
            (Join-Path $payloadRoot "Robotopia.GameCompat.Extractor")
        )) {
            Invoke-CodeSign $binary $identity $keychain
        }
        Invoke-CodeSign $AppBundle $identity $keychain -Deep

        & codesign --verify --deep --strict --verbose=2 $AppBundle
        if ($LASTEXITCODE -ne 0) { throw "codesign verification failed." }

        Invoke-MacNotarizationIfConfigured $AppBundle $StageRoot
    }
    finally {
        if (Test-Path $certPath) {
            Remove-Item -LiteralPath $certPath -Force
        }
        if (Test-Path $keychain) {
            & security delete-keychain $keychain | Out-Null
        }
    }
}

function Copy-Templates([string]$Repo, [string]$DestinationRoot) {
    $templatesSource = Join-Path $Repo "templates"
    $templatesDest = Join-Path $DestinationRoot "templates"
    if (!(Test-Path $templatesSource)) { return }

    $pathSeparatorPattern = "[/\\]"
    Get-ChildItem -LiteralPath $templatesSource -Recurse -File | Where-Object {
        $_.FullName -notmatch "${pathSeparatorPattern}bin${pathSeparatorPattern}" -and
        $_.FullName -notmatch "${pathSeparatorPattern}obj${pathSeparatorPattern}"
    } | ForEach-Object {
        $relative = $_.FullName.Substring($templatesSource.Length).TrimStart('\', '/')
        $target = Join-Path $templatesDest $relative
        New-Item -ItemType Directory -Force -Path (Split-Path $target -Parent) | Out-Null
        Copy-Item -LiteralPath $_.FullName -Destination $target -Force
    }
}

function Copy-DistPayload([string]$Repo, [string]$DestinationRoot) {
    $distSource = Join-Path $Repo "dist"
    $distDest = Join-Path $DestinationRoot "dist"
    New-Item -ItemType Directory -Force -Path $distDest | Out-Null

    if (Test-Path $distSource) {
        Get-ChildItem -LiteralPath $distSource -Filter "*.robotopiamod" -File -ErrorAction SilentlyContinue |
            ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $distDest -Force }
        Copy-DirectoryIfExists (Join-Path $distSource "vpm") (Join-Path $distDest "vpm")
    }
}

function Build-Cli([string]$Repo, [string]$DestinationRoot, [string]$Platform, [string]$PrebuiltCliPath) {
    $cliName = if ($Platform -eq "windows") { "robotopia.exe" } else { "robotopia" }
    $destination = Join-Path $DestinationRoot $cliName

    if ($PrebuiltCliPath -ne "") {
        if (!(Test-Path $PrebuiltCliPath)) {
            throw "Prebuilt CLI was not found: $PrebuiltCliPath"
        }
        Copy-Item -LiteralPath $PrebuiltCliPath -Destination $destination -Force
        Set-ExecutableBit $destination
        return
    }

    $cliApp = Join-Parts @($Repo, "apps", "robotopia_cli")
    Invoke-Step "Restoring CLI packages..." $cliApp { & dart pub get }
    Invoke-Step "Compiling Robotopia CLI..." $cliApp {
        & dart compile exe (Join-Path "bin" "robotopia.dart") -o $destination
    }
    Set-ExecutableBit $destination
}

function Build-Launcher([string]$Repo, [string]$StageRoot, [string]$Platform) {
    if ($SkipLauncher) {
        Write-Warning "Skipping launcher GUI build (-SkipLauncher)."
        return
    }
    if ($null -eq (Get-Command flutter -ErrorAction SilentlyContinue)) {
        Write-Warning "Flutter not found on PATH; skipping launcher GUI build."
        return
    }

    $launcherApp = Join-Parts @($Repo, "apps", "robotopia_launcher_flutter")
    Invoke-Step "Building the launcher GUI (flutter build $Platform --release)..." $launcherApp {
        & flutter build $Platform --release
    }

    if ($Platform -eq "windows") {
        $releaseDir = @(
            (Join-Parts @($launcherApp, "build", "windows", "x64", "runner", "Release")),
            (Join-Parts @($launcherApp, "build", "windows", "runner", "Release"))
        ) | Where-Object { Test-Path $_ } | Select-Object -First 1
        if ($null -eq $releaseDir) { throw "Could not locate the Flutter Windows Release output." }
        Copy-DirectoryContents $releaseDir (Join-Path $StageRoot "launcher")
        return
    }

    if ($Platform -eq "linux") {
        $releaseDir = @(
            (Join-Parts @($launcherApp, "build", "linux", "x64", "release", "bundle")),
            (Join-Parts @($launcherApp, "build", "linux", "release", "bundle"))
        ) | Where-Object { Test-Path $_ } | Select-Object -First 1
        if ($null -eq $releaseDir) { throw "Could not locate the Flutter Linux release bundle." }
        $launcherDest = Join-Path $StageRoot "launcher"
        Copy-DirectoryContents $releaseDir $launcherDest
        Set-ExecutableBit (Join-Path $launcherDest "robotopia_launcher_flutter")
        return
    }

    $appBundle = Join-Parts @($launcherApp, "build", "macos", "Build", "Products", "Release", "QuantumWorks.app")
    if (!(Test-Path $appBundle)) {
        $appBundle = Get-ChildItem -LiteralPath (Join-Parts @($launcherApp, "build", "macos", "Build", "Products", "Release")) -Directory -Filter "*.app" |
            Select-Object -ExpandProperty FullName -First 1
    }
    if ($null -eq $appBundle -or !(Test-Path $appBundle)) {
        throw "Could not locate the Flutter macOS app bundle."
    }
    Copy-Item -LiteralPath $appBundle -Destination $StageRoot -Recurse -Force
}

function Publish-GameCompatExtractor([string]$Repo, [string]$DestinationRoot, [string]$Platform, [string]$Configuration) {
    $extractorProj = Join-Parts @($Repo, "src", "Robotopia.GameCompat.Extractor", "Robotopia.GameCompat.Extractor.csproj")
    if (!(Test-Path $extractorProj)) { return }

    $runtimeId = Get-DotnetRuntimeId $Platform
    $extractorPublish = Join-Parts @($Repo, "src", "Robotopia.GameCompat.Extractor", "bin", $Configuration, "publish", $runtimeId)
    Write-Host "Publishing the GameCompat extractor ($runtimeId) into the package payload..."
    & dotnet publish $extractorProj -c $Configuration -r $runtimeId --self-contained true -p:PublishSingleFile=true -o $extractorPublish
    if ($LASTEXITCODE -ne 0) {
        Write-Warning "GameCompat extractor publish failed; the launcher will report compatibility as 'unknown'."
        return
    }

    $extractorName = if ($Platform -eq "windows") { "Robotopia.GameCompat.Extractor.exe" } else { "Robotopia.GameCompat.Extractor" }
    Copy-FileIfExists (Join-Path $extractorPublish $extractorName) (Join-Path $DestinationRoot $extractorName)
    Set-ExecutableBit (Join-Path $DestinationRoot $extractorName)
}

# Every platform archive carries the loader runtime the launcher's Repair flow installs from. The
# launcher resolves its payload root and reads third_party/BepInEx/<bundle> plus the built loader DLLs
# under src/Robotopia.ModManager/bin/Release/netstandard2.1, so those repo-relative paths are recreated
# inside the payload. Windows AND Linux take the win_x64 BepInEx (Linux runs the Windows game under
# Proton, which needs the identical Windows-layout payload); macOS takes the unix universal bundle.
function Copy-LoaderRuntime([string]$Repo, [string]$DestinationRoot, [string]$Platform, [string]$Configuration) {
    $bundleName = if ($Platform -eq "macos") { "macos_universal_5.4.23.5" } else { "win_x64_5.4.23.5" }
    $bepInEx = Join-Parts @($Repo, "third_party", "BepInEx", $bundleName)
    $pluginOut = Join-Parts @($Repo, "src", "Robotopia.ModManager", "bin", $Configuration, "netstandard2.1")
    if (!(Test-Path $bepInEx)) {
        Write-Warning "BepInEx payload was not found at $bepInEx."
        return
    }

    # 1. The bundle the launcher's Repair flow copies into the game folder.
    $bundleDest = Join-Parts @($DestinationRoot, "third_party", "BepInEx", $bundleName)
    New-Item -ItemType Directory -Force -Path $bundleDest | Out-Null
    Copy-Item -Path (Join-Path $bepInEx "*") -Destination $bundleDest -Recurse -Force
    Copy-FileIfExists (Join-Path $bepInEx ".doorstop_version") (Join-Path $bundleDest ".doorstop_version")
    if ($Platform -eq "macos") {
        Set-ExecutableBit (Join-Path $bundleDest "run_bepinex.sh")
        Set-ExecutableBit (Join-Path $bundleDest "libdoorstop.dylib")
    }

    # 2. The built loader DLLs the Repair flow installs as the BepInEx plugin (repair reads Release).
    $loaderDest = Join-Parts @($DestinationRoot, "src", "Robotopia.ModManager", "bin", "Release", "netstandard2.1")
    New-Item -ItemType Directory -Force -Path $loaderDest | Out-Null
    foreach ($dll in @(
        "Robotopia.ModManager.dll",
        "Robotopia.ModManager.Core.dll",
        "Robotopia.Mods.Abstractions.dll",
        "Robotopia.Mods.UnityUi.dll"
    )) {
        Copy-FileIfExists (Join-Path $pluginOut $dll) (Join-Path $loaderDest $dll)
    }

    # 3. Legacy game-overlay layout at the archive root (Windows only): lets a player copy the archive
    # contents straight over the game folder without the launcher.
    if ($Platform -eq "windows") {
        Copy-FileIfExists (Join-Path $bepInEx ".doorstop_version") (Join-Path $DestinationRoot ".doorstop_version")
        Copy-FileIfExists (Join-Path $bepInEx "doorstop_config.ini") (Join-Path $DestinationRoot "doorstop_config.ini")
        Copy-FileIfExists (Join-Path $bepInEx "winhttp.dll") (Join-Path $DestinationRoot "winhttp.dll")
        Copy-DirectoryIfExists (Join-Path $bepInEx "BepInEx") (Join-Path $DestinationRoot "BepInEx")
        $pluginDir = Join-Parts @($DestinationRoot, "BepInEx", "plugins", "RobotopiaModManager")
        New-Item -ItemType Directory -Force -Path $pluginDir | Out-Null
        foreach ($dll in @(
            "Robotopia.ModManager.dll",
            "Robotopia.ModManager.Core.dll",
            "Robotopia.Mods.Abstractions.dll",
            "Robotopia.Mods.UnityUi.dll"
        )) {
            Copy-FileIfExists (Join-Path $pluginOut $dll) (Join-Path $pluginDir $dll)
        }
    }
}

function Copy-CommonPayload([string]$Repo, [string]$DestinationRoot, [string]$Platform, [string]$Configuration) {
    Copy-DistPayload $Repo $DestinationRoot
    Copy-DirectoryIfExists (Join-Path $Repo "tools") (Join-Path $DestinationRoot "tools")
    Copy-DirectoryIfExists (Join-Path $Repo "docs") (Join-Path $DestinationRoot "docs")
    # Maintainer planning docs are not part of the shipped payload.
    $internalDocs = Join-Path $DestinationRoot "docs\internal"
    if (Test-Path $internalDocs) {
        Remove-Item -Recurse -Force $internalDocs
    }
    Copy-DirectoryIfExists (Join-Path $Repo "bindings") (Join-Path $DestinationRoot "bindings")
    Copy-DirectoryIfExists (Join-Path $Repo "baselines") (Join-Path $DestinationRoot "baselines")
    Copy-Templates $Repo $DestinationRoot
    Copy-FileIfExists (Join-Path $Repo "README.md") (Join-Path $DestinationRoot "README.md")
    Copy-FileIfExists (Join-Path $Repo "THIRD_PARTY_NOTICES.md") (Join-Path $DestinationRoot "THIRD_PARTY_NOTICES.md")
    if (!$SkipRuntime) {
        Publish-GameCompatExtractor $Repo $DestinationRoot $Platform $Configuration
    }
}

$script:TargetPlatform = Resolve-TargetPlatform $Platform
$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$distDir = Join-Path $repo "dist"
if ($OutputRoot -eq "") {
    $OutputRoot = Join-Path $distDir "release"
}
$OutputRoot = (New-Item -ItemType Directory -Force -Path $OutputRoot).FullName
$assetName = Get-ArchiveName $script:TargetPlatform
$stageRoot = Join-Path $OutputRoot ([IO.Path]::GetFileNameWithoutExtension($assetName))
$zipPath = Join-Path $OutputRoot $assetName

if (Test-Path $stageRoot) {
    Remove-Item -LiteralPath $stageRoot -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $stageRoot | Out-Null

if (!$SkipRuntime) {
    & dotnet build (Join-Path $repo "RobotopiaModManager.slnx") -c $Configuration
    if ($LASTEXITCODE -ne 0) { throw "dotnet build failed (exit $LASTEXITCODE)." }

    $cliApp = Join-Parts @($repo, "apps", "robotopia_cli")
    Invoke-Step "Restoring CLI packages..." $cliApp { & dart pub get }
    Invoke-Step "Packing mod packages (robotopia pack --all)..." $cliApp {
        & dart run (Join-Path "bin" "robotopia.dart") pack --all --output $distDir --configuration $Configuration
    }
    Invoke-Step "Packing Unity VPM packages (robotopia unity pack-packages)..." $cliApp {
        & dart run (Join-Path "bin" "robotopia.dart") unity pack-packages --output (Join-Path $distDir "vpm")
    }
}
else {
    Write-Warning "Skipping runtime/mod rebuild (-SkipRuntime); copying existing dist payloads when present."
}

Build-Launcher $repo $stageRoot $script:TargetPlatform

if ($script:TargetPlatform -eq "macos") {
    $appBundle = Join-Path $stageRoot "QuantumWorks.app"
    if (!(Test-Path $appBundle)) {
        $appBundle = Get-ChildItem -LiteralPath $stageRoot -Directory -Filter "*.app" |
            Select-Object -ExpandProperty FullName -First 1
    }
    if ($null -eq $appBundle -or !(Test-Path $appBundle)) {
        throw "macOS package requires a QuantumWorks.app bundle. Build failed or use -SkipLauncher only for CLI-only validation."
    }

    $payloadRoot = Join-Parts @($appBundle, "Contents", "Resources", "QuantumWorks")
    New-Item -ItemType Directory -Force -Path $payloadRoot | Out-Null
    Build-Cli $repo $payloadRoot $script:TargetPlatform $PrebuiltCli
    Copy-CommonPayload $repo $payloadRoot $script:TargetPlatform $Configuration
    if (!$SkipRuntime) {
        Copy-LoaderRuntime $repo $payloadRoot $script:TargetPlatform $Configuration
    }
    Sign-MacAppIfConfigured $appBundle $stageRoot

    $shim = Join-Path $stageRoot "robotopia"
    @'
#!/bin/sh
set -eu
DIR="$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)"
exec "$DIR/QuantumWorks.app/Contents/Resources/QuantumWorks/robotopia" "$@"
'@ | Set-Content -LiteralPath $shim -Encoding ASCII
    Set-ExecutableBit $shim
}
else {
    Build-Cli $repo $stageRoot $script:TargetPlatform $PrebuiltCli
    Copy-CommonPayload $repo $stageRoot $script:TargetPlatform $Configuration
    if (!$SkipRuntime) {
        Copy-LoaderRuntime $repo $stageRoot $script:TargetPlatform $Configuration
    }
}

New-ZipFromDirectory $stageRoot $zipPath $script:TargetPlatform
Write-Host "Created $zipPath"
Write-Output $zipPath
