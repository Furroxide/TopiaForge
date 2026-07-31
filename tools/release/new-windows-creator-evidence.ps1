[CmdletBinding()]
[Diagnostics.CodeAnalysis.SuppressMessageAttribute(
    "PSReviewUnusedParameter",
    "",
    Justification = "The retired command preserves its legacy fail-closed interface so old automation receives the security error instead of silently selecting another path."
)]
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

    [Parameter(Mandatory = $true)]
    [ValidateRange(1, 1000000)]
    [Int64]$LifecycleCycles,

    [Parameter(Mandatory = $true)]
    [string]$CaseEvidenceDirectory,

    [Parameter(Mandatory = $true)]
    [string]$SaveBefore,

    [Parameter(Mandatory = $true)]
    [string]$SaveAfter,

    [Parameter(Mandatory = $true)]
    [string]$CheckpointBefore,

    [Parameter(Mandatory = $true)]
    [string]$CheckpointAfter,

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

# Deliberately no compatibility switch exists. The former implementation
# converted a directory with one arbitrary file per case, a caller-supplied
# cycle count, and two identical arbitrary byte pairs into a release pass.
# Those inputs cannot prove that CreatorTools produced a native UI result.
throw (
    "Windows Creator evidence generation is blocked: TopiaForge does not yet " +
    "have a native in-game Creator result collector that emits explicit, " +
    "challenge-bound per-case results tied to the exact last-run session, " +
    "package receipts, candidate archive, and source-SHA case inventory. " +
    "Artifact presence, a manual cycle number, and identical state blobs " +
    "cannot synthesize a release pass."
)
