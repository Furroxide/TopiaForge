[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern("^[0-9a-f]{40}$")]
    [string]$SourceSha,

    [Parameter(Mandatory = $true)]
    [ValidatePattern("^[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?$")]
    [string]$Version,

    [Parameter(Mandatory = $true)]
    [string]$WindowsArchive,

    [Parameter(Mandatory = $true)]
    [ValidatePattern("^[0-9a-f]{64}$")]
    [string]$CanonicalEcosystemSha256,

    # The challenge-bound result written by
    # `topiaforge acceptance creator`. It is the only authority for the case
    # set, cycle count, session, package receipt, and persistence outcome.
    [Parameter(Mandatory = $true)]
    [string]$AcceptanceResult,

    # Directory holding the four canonical state pre-images the CLI retained.
    [Parameter(Mandatory = $true)]
    [string]$StateDirectory,

    [Parameter(Mandatory = $true)]
    [string]$CaseEvidenceDirectory,

    [Parameter(Mandatory = $true)]
    [string]$OutputBundle,

    [Parameter(Mandatory = $true)]
    [string]$OutputDescriptor,

    [string]$RepositoryRoot = (Resolve-Path -LiteralPath (
        Join-Path $PSScriptRoot "../.."
    )).Path
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

# The former implementation converted a directory with one arbitrary file per
# case, a caller-supplied cycle count, and two identical arbitrary byte pairs
# into a release pass. Those inputs cannot prove that CreatorTools produced a
# native result, so every claim below is now read from the acceptance result the
# interactive run produced and cross-checked against the source-SHA inventory.

function Get-BytesSha256Hex {
    param([Parameter(Mandatory = $true)][byte[]]$Bytes)
    return [Convert]::ToHexString(
        [System.Security.Cryptography.SHA256]::HashData($Bytes)
    ).ToLowerInvariant()
}

function Get-FileSha256Hex {
    param([Parameter(Mandatory = $true)][string]$Path)
    return Get-BytesSha256Hex ([System.IO.File]::ReadAllBytes($Path))
}

function ConvertTo-CanonicalJson {
    param([Parameter(Mandatory = $true)]$Value)
    return ($Value | ConvertTo-Json -Depth 32 -Compress)
}

if (-not (Test-Path -LiteralPath $AcceptanceResult -PathType Leaf)) {
    throw "The Creator acceptance result is required: $AcceptanceResult"
}
$acceptanceBytes = [System.IO.File]::ReadAllBytes($AcceptanceResult)
if ($acceptanceBytes.Length -le 0 -or $acceptanceBytes.Length -gt 8388608) {
    throw "The Creator acceptance result is empty or oversized."
}
$acceptance = [System.Text.UTF8Encoding]::new($false, $true).
    GetString($acceptanceBytes) | ConvertFrom-Json

$inventoryPath = Join-Path $RepositoryRoot "tests/live-game-acceptance.json"
$inventoryBytes = [System.IO.File]::ReadAllBytes($inventoryPath)
$inventorySha = Get-BytesSha256Hex $inventoryBytes
$inventory = [System.Text.UTF8Encoding]::new($false, $true).
    GetString($inventoryBytes) | ConvertFrom-Json
$policyPath = Join-Path $RepositoryRoot "release/release-policy.json"
$policy = Get-Content -LiteralPath $policyPath -Raw | ConvertFrom-Json
$expectedGameBuildId = [string]$policy.gameBuild.id
$expectedCaseIds = @(
    $inventory.creatorAcceptance.cases | ForEach-Object { [string]$_.id }
)
$minimumCycles = [Int64]$inventory.creatorAcceptance.minimumLifecycleCycles

$passedCases = @($acceptance.passedCases | ForEach-Object { [string]$_ })
$requiredCases = @($acceptance.requiredCases | ForEach-Object { [string]$_ })
$sortedExpected = @($expectedCaseIds | Sort-Object)
$acceptanceProblems = @()
if ($acceptance.schemaVersion -ne 1) {
    $acceptanceProblems += "schemaVersion"
}
if ([string]$acceptance.suite -cne "creator-full") {
    $acceptanceProblems += "suite"
}
if ($acceptance.succeeded -ne $true) { $acceptanceProblems += "succeeded" }
if ([string]$acceptance.acceptanceChallenge -cnotmatch "^[0-9a-f]{64}$") {
    $acceptanceProblems += "acceptanceChallenge"
}
if ([string]$acceptance.lastRunSessionId -cnotmatch
        "^[A-Za-z0-9][A-Za-z0-9._:-]{0,255}$") {
    $acceptanceProblems += "lastRunSessionId"
}
if ([string]$acceptance.creatorPackageReceipt.sourceSha256 -cnotmatch
        "^[0-9a-f]{64}$" -or
    @($acceptance.creatorPackageReceipt.criticalFiles).Count -lt 1) {
    $acceptanceProblems += "creatorPackageReceipt"
}
if ([string]$acceptance.gameBuild -cne $expectedGameBuildId) {
    $acceptanceProblems += (
        "gameBuild(got=" + [string]$acceptance.gameBuild +
        ",want=" + $expectedGameBuildId + ")"
    )
}
if ([Int64]$acceptance.lifecycleCycles -lt $minimumCycles) {
    $acceptanceProblems += (
        "lifecycleCycles(got=" + [string]$acceptance.lifecycleCycles +
        ",min=" + [string]$minimumCycles + ")"
    )
}
if ($acceptance.saveStateUnchanged -ne $true) {
    $acceptanceProblems += "saveStateUnchanged"
}
if ($acceptance.checkpointStateUnchanged -ne $true) {
    $acceptanceProblems += "checkpointStateUnchanged"
}
if (@($acceptance.failures).Count -ne 0) { $acceptanceProblems += "failures" }
if (@($acceptance.missingCases).Count -ne 0) {
    $acceptanceProblems += "missingCases"
}
if ($null -ne (Compare-Object $passedCases $sortedExpected -SyncWindow 0)) {
    $acceptanceProblems += "passedCases"
}
if ($null -ne (Compare-Object $requiredCases $sortedExpected -SyncWindow 0)) {
    $acceptanceProblems += "requiredCases"
}
if ($acceptanceProblems.Count -ne 0) {
    throw (
        "The Creator acceptance result does not prove a complete, " +
        "challenge-bound interactive run over the exact source-SHA cases. " +
        "Rejected fields: " + ($acceptanceProblems -join ", ") + "."
    )
}

$stateFiles = [ordered]@{
    "state/save-before.bin" = "save-before.bin"
    "state/save-after.bin" = "save-after.bin"
    "state/checkpoint-before.bin" = "checkpoint-before.bin"
    "state/checkpoint-after.bin" = "checkpoint-after.bin"
}
$stateRecords = @{}
foreach ($entryName in $stateFiles.Keys) {
    $statePath = Join-Path $StateDirectory $stateFiles[$entryName]
    if (-not (Test-Path -LiteralPath $statePath -PathType Leaf)) {
        throw "A retained Creator state pre-image is missing: $statePath"
    }
    $stateBytes = [System.IO.File]::ReadAllBytes($statePath)
    if ($stateBytes.Length -le 0 -or $stateBytes.Length -gt 67108864) {
        throw "A retained Creator state pre-image is empty or oversized."
    }
    $stateRecords[$entryName] = [ordered]@{
        entry = $entryName
        sha256 = Get-BytesSha256Hex $stateBytes
        size = [Int64]$stateBytes.Length
    }
}
# The pre-images must reproduce the digests the interactive run reported and
# must agree that nothing changed across End Session.
if ($stateRecords["state/save-before.bin"].sha256 -cne
        [string]$acceptance.persistence.save.before.sha256 -or
    $stateRecords["state/save-after.bin"].sha256 -cne
        [string]$acceptance.persistence.save.after.sha256 -or
    $stateRecords["state/checkpoint-before.bin"].sha256 -cne
        [string]$acceptance.persistence.checkpoint.before.sha256 -or
    $stateRecords["state/checkpoint-after.bin"].sha256 -cne
        [string]$acceptance.persistence.checkpoint.after.sha256) {
    throw "Retained Creator state pre-images do not match the acceptance result digests."
}
if ($stateRecords["state/save-before.bin"].sha256 -cne
        $stateRecords["state/save-after.bin"].sha256) {
    throw "Save state changed across End Session."
}
if ($stateRecords["state/checkpoint-before.bin"].sha256 -cne
        $stateRecords["state/checkpoint-after.bin"].sha256) {
    throw "Checkpoint state changed across End Session."
}

$caseBundles = @()
$artifactRecords = [ordered]@{}
foreach ($caseId in $expectedCaseIds) {
    $caseDirectory = Join-Path $CaseEvidenceDirectory $caseId
    if (-not (Test-Path -LiteralPath $caseDirectory -PathType Container)) {
        throw "Creator case evidence is missing for $caseId."
    }
    $artifacts = @()
    $files = @(
        Get-ChildItem -LiteralPath $caseDirectory -File -Recurse |
            Sort-Object -Property FullName
    )
    if ($files.Count -eq 0) {
        throw "Every Creator case must retain at least one evidence artifact."
    }
    foreach ($file in $files) {
        $relative = $file.FullName.Substring($caseDirectory.Length).
            TrimStart([char]92, [char]47).Replace("\", "/")
        $entryName = "artifacts/$caseId/$relative"
        $record = [ordered]@{
            entry = $entryName
            sha256 = Get-FileSha256Hex $file.FullName
            size = [Int64]$file.Length
        }
        $artifacts += $record
        $artifactRecords[$entryName] = $file.FullName
    }
    $caseBundles += [ordered]@{
        artifacts = $artifacts
        id = $caseId
        result = "pass"
    }
}
$observedCaseDirectories = @(
    Get-ChildItem -LiteralPath $CaseEvidenceDirectory -Directory |
        ForEach-Object { $_.Name } | Sort-Object
)
if ($null -ne (Compare-Object $observedCaseDirectories $sortedExpected `
            -SyncWindow 0)) {
    throw "Creator case evidence does not cover the exact source-SHA cases."
}

$windowsSha = Get-FileSha256Hex $WindowsArchive
$windowsSize = [Int64](Get-Item -LiteralPath $WindowsArchive).Length
$acceptanceSha = Get-BytesSha256Hex $acceptanceBytes
$layout = [ordered]@{
    exclusions = @(
        $acceptance.persistence.layout.exclusions | ForEach-Object {
            [string]$_
        }
    )
    roots = @(
        $acceptance.persistence.layout.roots | ForEach-Object { [string]$_ }
    )
    version = [Int64]$acceptance.persistence.layout.version
}
$bundleManifest = [ordered]@{
    acceptanceChallenge = [string]$acceptance.acceptanceChallenge
    acceptanceResult = [ordered]@{
        entry = "acceptance/creator-acceptance-result.json"
        sha256 = $acceptanceSha
        size = [Int64]$acceptanceBytes.Length
    }
    archiveSha256 = $windowsSha
    archiveSize = $windowsSize
    canonicalEcosystemSha256 = $CanonicalEcosystemSha256
    caseInventorySha256 = $inventorySha
    cases = $caseBundles
    gameBuildId = $expectedGameBuildId
    lastRunSessionId = [string]$acceptance.lastRunSessionId
    lifecycleCycles = [Int64]$acceptance.lifecycleCycles
    platform = "windows"
    schema = "release-windows-creator-evidence-bundle-v2"
    stateSnapshots = [ordered]@{
        checkpoint = [ordered]@{
            after = $stateRecords["state/checkpoint-after.bin"]
            before = $stateRecords["state/checkpoint-before.bin"]
            unchanged = $true
        }
        layout = $layout
        save = [ordered]@{
            after = $stateRecords["state/save-after.bin"]
            before = $stateRecords["state/save-before.bin"]
            unchanged = $true
        }
    }
    targetSha = $SourceSha
    version = $Version
}
$manifestBytes = [System.Text.Encoding]::UTF8.GetBytes(
    (ConvertTo-CanonicalJson $bundleManifest)
)

# Deterministic ZIP: fixed 1980-01-01 timestamps, stored entries, and one
# stable ordering, so identical inputs always produce identical bytes.
$epoch = [System.DateTimeOffset]::new(
    1980, 1, 1, 0, 0, 0, [System.TimeSpan]::Zero
)
$payloadEntries = [ordered]@{}
$payloadEntries["acceptance/creator-acceptance-result.json"] = $acceptanceBytes
foreach ($entryName in $stateFiles.Keys) {
    $payloadEntries[$entryName] = [System.IO.File]::ReadAllBytes(
        (Join-Path $StateDirectory $stateFiles[$entryName])
    )
}
foreach ($entryName in $artifactRecords.Keys) {
    $payloadEntries[$entryName] = [System.IO.File]::ReadAllBytes(
        $artifactRecords[$entryName]
    )
}
$sortedPayloadNames = [string[]]@($payloadEntries.Keys)
[Array]::Sort($sortedPayloadNames, [StringComparer]::Ordinal)

$bundleParent = Split-Path -Parent $OutputBundle
if (-not [string]::IsNullOrWhiteSpace($bundleParent)) {
    New-Item -ItemType Directory -Force -Path $bundleParent | Out-Null
}
if (Test-Path -LiteralPath $OutputBundle) {
    Remove-Item -LiteralPath $OutputBundle -Force
}
$bundleStream = [System.IO.File]::Open(
    $OutputBundle,
    [System.IO.FileMode]::CreateNew,
    [System.IO.FileAccess]::ReadWrite,
    [System.IO.FileShare]::None
)
try {
    $zip = [System.IO.Compression.ZipArchive]::new(
        $bundleStream,
        [System.IO.Compression.ZipArchiveMode]::Create,
        $true,
        [System.Text.UTF8Encoding]::new($false, $true)
    )
    try {
        $orderedNames = [string[]]@("bundle-manifest.json") + $sortedPayloadNames
        foreach ($entryName in $orderedNames) {
            $bytes = if ($entryName -ceq "bundle-manifest.json") {
                $manifestBytes
            }
            else {
                $payloadEntries[$entryName]
            }
            $entry = $zip.CreateEntry(
                $entryName,
                [System.IO.Compression.CompressionLevel]::NoCompression
            )
            $entry.LastWriteTime = $epoch
            # Pin the external attributes. On Unix .NET otherwise records the
            # host file mode here, so the same inputs would produce a different
            # bundle per platform and the verifier would reject it as
            # non-deterministic.
            $entry.ExternalAttributes = 0
            $entryStream = $entry.Open()
            try {
                $entryStream.Write($bytes, 0, $bytes.Length)
            }
            finally {
                $entryStream.Dispose()
            }
        }
    }
    finally {
        $zip.Dispose()
    }
}
finally {
    $bundleStream.Dispose()
}

$bundleSha = Get-FileSha256Hex $OutputBundle
$bundleSize = [Int64](Get-Item -LiteralPath $OutputBundle).Length
$descriptor = [ordered]@{
    acceptanceChallenge = [string]$acceptance.acceptanceChallenge
    acceptanceResultSha256 = $acceptanceSha
    archiveSha256 = $windowsSha
    archiveSize = $windowsSize
    canonicalEcosystemSha256 = $CanonicalEcosystemSha256
    caseInventorySha256 = $inventorySha
    caseResults = @(
        $expectedCaseIds | ForEach-Object {
            [ordered]@{ id = $_; result = "pass" }
        }
    )
    checkpointStateUnchanged = $true
    creatorPackageReceipt = [ordered]@{
        criticalFiles = @(
            $acceptance.creatorPackageReceipt.criticalFiles | ForEach-Object {
                [ordered]@{
                    path = [string]$_.path
                    sha256 = [string]$_.sha256
                }
            }
        )
        sourceSha256 = [string]$acceptance.creatorPackageReceipt.sourceSha256
    }
    evidenceSha256 = $bundleSha
    evidenceSize = $bundleSize
    gameBuildId = $expectedGameBuildId
    lastRunSessionId = [string]$acceptance.lastRunSessionId
    lifecycleCycles = [Int64]$acceptance.lifecycleCycles
    platform = "windows"
    result = "pass"
    saveStateUnchanged = $true
    schema = "release-windows-creator-evidence-v2"
    suite = "creator-full"
    targetSha = $SourceSha
    version = $Version
}
$descriptorParent = Split-Path -Parent $OutputDescriptor
if (-not [string]::IsNullOrWhiteSpace($descriptorParent)) {
    New-Item -ItemType Directory -Force -Path $descriptorParent | Out-Null
}
[System.IO.File]::WriteAllBytes(
    $OutputDescriptor,
    [System.Text.Encoding]::UTF8.GetBytes(
        (ConvertTo-CanonicalJson $descriptor)
    )
)
Write-Output $OutputDescriptor
