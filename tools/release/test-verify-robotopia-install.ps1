[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

function Get-Sha256 {
    param([Parameter(Mandatory = $true)][string]$Path)
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).
        Hash.ToLowerInvariant()
}

function Assert-ThrowsMatch {
    param(
        [Parameter(Mandatory = $true)][scriptblock]$Action,
        [Parameter(Mandatory = $true)][string]$Pattern,
        [Parameter(Mandatory = $true)][string]$Message
    )
    try {
        & $Action
    }
    catch {
        if ($_.Exception.Message -match $Pattern) {
            return
        }
        throw "$Message Unexpected error: $($_.Exception.Message)"
    }
    throw $Message
}

$verifier = Join-Path $PSScriptRoot "verify-robotopia-install.ps1"
$root = Join-Path ([System.IO.Path]::GetTempPath()) (
    "topiaforge-robotopia-verifier-" + [Guid]::NewGuid().ToString("N")
)
$launcher = Join-Path $root "launcher"
$game = Join-Path $launcher "Robotopia"
try {
    New-Item -ItemType Directory -Force -Path $game | Out-Null
    $executable = Join-Path $game "Robotopia.exe"
    [System.IO.File]::WriteAllBytes(
        $executable,
        [System.Text.UTF8Encoding]::new($false).GetBytes("official-game")
    )
    $executableSha = Get-Sha256 $executable
    $filelist = Join-Path $launcher "filelist.json"
    [ordered]@{
        version = [Int64]1
        root = "Robotopia"
        files = @(
            [ordered]@{
                path = "Robotopia.exe"
                sha256 = $executableSha
                size = [Int64](Get-Item -LiteralPath $executable).Length
            }
        )
    } | ConvertTo-Json -Depth 5 -Compress |
        Set-Content -LiteralPath $filelist -Encoding utf8NoBOM
    $filelistSha = Get-Sha256 $filelist
    $metadata = Join-Path $root "robotopia-game-build.json"
    $metadataBody = [ordered]@{
        buildId = [Int64]2309
        baseUrl = "https://example.invalid"
        manifestUrl = "https://example.invalid/latest.json"
        sourcePlatform = "windows"
        windowsFilesManifest = [ordered]@{
            path = "filelist.json"
            sha256 = $filelistSha
            fileCount = [Int64]1
            gameExecutableSha256 = $executableSha
        }
        archives = [ordered]@{
            windows = [ordered]@{
                path = "Robotopia.7z"
                sha256 = ("a" * 64)
            }
            mac = [ordered]@{
                path = "Robotopia-Mac.7z"
                sha256 = ("b" * 64)
            }
        }
    }
    $metadataBody | ConvertTo-Json -Depth 6 -Compress |
        Set-Content -LiteralPath $metadata -Encoding utf8NoBOM

    $results = @(& $verifier -GameDirectory $game -MetadataPath $metadata)
    $result = [string]$results[-1] | ConvertFrom-Json
    if ($result.schema -cne "robotopia-official-install-v1" -or
        $result.filesVerified -ne 1 -or
        [string]$result.gameExecutableSha256 -cne $executableSha) {
        throw "Official-install verifier did not return the expected identity."
    }

    Add-Content -LiteralPath $executable -Value "tampered" -Encoding ascii
    Assert-ThrowsMatch -Action {
        & $verifier -GameDirectory $game -MetadataPath $metadata | Out-Null
    } -Pattern "size mismatch|digest mismatch" `
        -Message "A modified official game executable was accepted."

    [System.IO.File]::WriteAllBytes(
        $executable,
        [System.Text.UTF8Encoding]::new($false).GetBytes("official-game")
    )
    $badMetadata = $metadataBody | ConvertTo-Json -Depth 6 |
        ConvertFrom-Json
    $badMetadata.windowsFilesManifest.gameExecutableSha256 = "c" * 64
    $badMetadata | ConvertTo-Json -Depth 6 -Compress |
        Set-Content -LiteralPath $metadata -Encoding utf8NoBOM
    Assert-ThrowsMatch -Action {
        & $verifier -GameDirectory $game -MetadataPath $metadata | Out-Null
    } -Pattern "independently pinned digest" `
        -Message "A files manifest with a different executable pin was accepted."
}
finally {
    if (Test-Path -LiteralPath $root) {
        Remove-Item -LiteralPath $root -Recurse -Force
    }
}

Write-Host "Robotopia official-install verifier regression tests passed."
