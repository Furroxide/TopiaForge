[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"

. (Join-Path $PSScriptRoot "restore-robotopia-managed-refs.ps1")

function Get-TestConfig {
    [PSCustomObject]@{
        buildId = 2227
        baseUrl = "https://example.invalid"
        manifestUrl = "https://example.invalid/latest.json"
        sourcePlatform = "windows"
        archives = [PSCustomObject]@{
            windows = [PSCustomObject]@{
                path = "Robotopia-v02227-Win64.7z"
                sha256 = "a" * 64
            }
            mac = [PSCustomObject]@{
                path = "Robotopia-v02227-Mac.7z"
                sha256 = "b" * 64
            }
        }
    }
}

function Get-TestManifest {
    [PSCustomObject]@{
        id = 2227
        windows = [PSCustomObject]@{
            path = "Robotopia-v02227-Win64.7z"
            sha256 = "a" * 64
        }
        mac = [PSCustomObject]@{
            path = "Robotopia-v02227-Mac.7z"
            sha256 = "b" * 64
        }
    }
}

function Assert-Throws {
    param(
        [Parameter(Mandatory = $true)][scriptblock]$Action,
        [Parameter(Mandatory = $true)][string]$Pattern
    )
    try {
        & $Action
    }
    catch {
        if ($_.Exception.Message -notmatch $Pattern) {
            throw "Expected error matching '$Pattern', got '$($_.Exception.Message)'."
        }
        return
    }
    throw "Expected action to throw an error matching '$Pattern'."
}

Assert-PublicManifestMatchesConfig (Get-TestConfig) (Get-TestManifest)

$missingMac = Get-TestManifest
$missingMac.PSObject.Properties.Remove("mac")
Assert-Throws {
    Assert-PublicManifestMatchesConfig (Get-TestConfig) $missingMac
} "missing the mac archive"

$wrongWindowsHash = Get-TestManifest
$wrongWindowsHash.windows.sha256 = "c" * 64
Assert-Throws {
    Assert-PublicManifestMatchesConfig (Get-TestConfig) $wrongWindowsHash
} "windows.*SHA"

$wrongMacPath = Get-TestManifest
$wrongMacPath.mac.path = "other.7z"
Assert-Throws {
    Assert-PublicManifestMatchesConfig (Get-TestConfig) $wrongMacPath
} "mac.*path"

$wrongBuild = Get-TestManifest
$wrongBuild.id = 2228
Assert-Throws {
    Assert-PublicManifestMatchesConfig (Get-TestConfig) $wrongBuild
} "reports build 2228"

$extraArchive = Get-TestConfig
$extraArchive.archives | Add-Member -NotePropertyName linux -NotePropertyValue ([PSCustomObject]@{})
Assert-Throws {
    Get-ConfiguredPublicArchives $extraArchive
} "exactly windows and mac"

# Prove -RequireLatest is a public gate even when the restore source is bundled.
$script:RequireLatest = $true
$script:Source = "bundled"
$script:CacheKeyOnly = $false
$script:PublicLatestGateComplete = $false
$script:ProbeCalls = 0
$script:BundledCalls = 0
function Probe-PublicRefs {
    $script:ProbeCalls += 1
    $script:PublicLatestGateComplete = $true
}
function Restore-BundledRefs {
    $script:BundledCalls += 1
}
Invoke-RestoreManagedRefs
if ($script:ProbeCalls -ne 1 -or $script:BundledCalls -ne 1) {
    throw "RequireLatest did not run exactly one public gate before the bundled restore."
}

Write-Host "restore-robotopia-managed-refs policy tests passed."
