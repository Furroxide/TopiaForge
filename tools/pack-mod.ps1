param(
    [Parameter(Mandatory = $true)]
    [string]$ProjectDir,
    [string]$OutputDir = "",
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$ProjectDir = (Resolve-Path $ProjectDir).Path
if ($OutputDir -eq "") {
    $OutputDir = Join-Path $ProjectDir "dist"
}

New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
$manifestPath = Join-Path $ProjectDir "robotopia.mod.json"
if (!(Test-Path $manifestPath)) {
    throw "robotopia.mod.json was not found in $ProjectDir"
}

$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if (!$manifest.id -or !$manifest.version -or !$manifest.entryAssembly -or !$manifest.entryType) {
    throw "Manifest must include id, version, entryAssembly, and entryType."
}

$stage = Join-Path ([IO.Path]::GetTempPath()) ("robotopia-pack-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force -Path $stage | Out-Null

try {
    $csproj = Get-ChildItem -LiteralPath $ProjectDir -Filter "*.csproj" | Select-Object -First 1
    if ($csproj) {
        dotnet build $csproj.FullName -c $Configuration | Out-Host
        $bin = Join-Path $ProjectDir "bin\$Configuration"
        $tfmDir = Get-ChildItem -LiteralPath $bin -Directory | Select-Object -First 1
        if (!$tfmDir) {
            throw "Could not find build output under $bin"
        }

        $entryDll = Join-Path $tfmDir.FullName $manifest.entryAssembly
        if (!(Test-Path $entryDll)) {
            throw "entryAssembly was not found in build output: $entryDll"
        }

        Copy-Item -LiteralPath $manifestPath -Destination (Join-Path $stage "robotopia.mod.json") -Force
        Get-ChildItem -LiteralPath $tfmDir.FullName -File | Where-Object {
            $_.Extension -in @(".dll", ".pdb") -and $_.Name -notlike "Robotopia.Mods.Abstractions.*"
        } | ForEach-Object {
            Copy-Item -LiteralPath $_.FullName -Destination $stage -Force
        }

        @("ref", "assets", "AssetBundles", "Resources") | ForEach-Object {
            $contentDir = Join-Path $ProjectDir $_
            if (Test-Path $contentDir) {
                Copy-Item -LiteralPath $contentDir -Destination (Join-Path $stage $_) -Recurse -Force
            }
        }

        foreach ($apiAssembly in @($manifest.apiAssemblies)) {
            if ([string]::IsNullOrWhiteSpace($apiAssembly)) {
                continue
            }
            $source = Join-Path $ProjectDir $apiAssembly
            if (!(Test-Path $source)) {
                $source = Join-Path $tfmDir.FullName $apiAssembly
            }
            if (!(Test-Path $source)) {
                throw "apiAssemblies entry was not found: $apiAssembly"
            }
            $target = Join-Path $stage $apiAssembly
            New-Item -ItemType Directory -Force -Path (Split-Path $target -Parent) | Out-Null
            Copy-Item -LiteralPath $source -Destination $target -Force
        }
    } else {
        Get-ChildItem -LiteralPath $ProjectDir -Recurse -File | Where-Object {
            $_.FullName -notmatch "\\bin\\" -and $_.FullName -notmatch "\\obj\\" -and $_.FullName -notmatch "\\dist\\" -and $_.FullName -notmatch "\\.robotopia\\"
        } | ForEach-Object {
            $relative = $_.FullName.Substring($ProjectDir.Length).TrimStart('\', '/')
            $target = Join-Path $stage $relative
            New-Item -ItemType Directory -Force -Path (Split-Path $target -Parent) | Out-Null
            Copy-Item -LiteralPath $_.FullName -Destination $target -Force
        }
    }

    # Ship the mod's game-binding manifest (from the centralized repo-root bindings/ dir) inside its package, so a
    # game-compatibility check can travel with the mod. Forward-compatible with per-package binding ownership.
    $repoRoot = Split-Path (Split-Path $ProjectDir -Parent) -Parent
    $bindingFile = Join-Path $repoRoot "bindings\$($manifest.id).gamebindings.json"
    if (Test-Path $bindingFile) {
        $bindingsStage = Join-Path $stage "bindings"
        New-Item -ItemType Directory -Force -Path $bindingsStage | Out-Null
        Copy-Item -LiteralPath $bindingFile -Destination (Join-Path $bindingsStage "$($manifest.id).gamebindings.json") -Force
    }

    $safeId = ($manifest.id -replace "[^A-Za-z0-9_.-]", "_")
    $safeVersion = ($manifest.version -replace "[^A-Za-z0-9_.-]", "_")
    $packagePath = Join-Path $OutputDir "$safeId-$safeVersion.robotopiamod"
    if (Test-Path $packagePath) {
        Remove-Item -LiteralPath $packagePath -Force
    }

    $zipPath = Join-Path $OutputDir "$safeId-$safeVersion.zip"
    if (Test-Path $zipPath) {
        Remove-Item -LiteralPath $zipPath -Force
    }

    Compress-Archive -Path (Join-Path $stage "*") -DestinationPath $zipPath -Force
    Move-Item -LiteralPath $zipPath -Destination $packagePath -Force
    Write-Output $packagePath
}
finally {
    if (Test-Path $stage) {
        Remove-Item -LiteralPath $stage -Recurse -Force
    }
}
