[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$HandoffPath,
    [Parameter(Mandatory)][string]$ExpectedSourceRevision
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'sandbox/ci-handoff.ps1')
try {
    $record = Read-SandboxCiDocument $HandoffPath
    Assert-SandboxCiHandoff $record $ExpectedSourceRevision
    Write-Host "Supplementary $($record.lane) handoff: $($record.status); cleanup confirmed: $($record.cleanupConfirmed)."
    Write-Host 'This hosted check validates the scrubbed handoff only; it is not native execution or release qualification.'
    if ($record.status -cne 'passed' -or !$record.cleanupConfirmed) { exit 1 }
    exit 0
}
catch {
    # Avoid echoing raw handoff contents, private paths or thrown JSON text.
    Write-Error 'Sandbox handoff refused: invalid, unreadable or mismatched scrubbed input.' -ErrorAction Continue
    exit 2
}
