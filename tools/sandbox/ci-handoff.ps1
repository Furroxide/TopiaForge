# This validator checks a scrubbed handoff's grammar and reported outcome only.
# It never authenticates raw native evidence or qualifies a release.
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'ci-admission.ps1')

function Assert-SandboxCiHandoff {
    param([Collections.IDictionary]$Record, [string]$ExpectedSourceRevision)
    Assert-SandboxCiFields $Record @('schemaVersion', 'kind', 'sourceRevision',
        'runId', 'lane', 'status', 'cleanupConfirmed', 'qualifiesRelease',
        'evidenceSha256', 'verificationSha256', 'admissionSha256')
    if (($Record.schemaVersion -isnot [long] -and $Record.schemaVersion -isnot [int]) -or
        $Record.schemaVersion -ne 1 -or $Record.kind -cne 'sandbox-ci-handoff-v1' -or
        $Record.sourceRevision -cnotmatch '^[0-9a-f]{40}$' -or
        $Record.sourceRevision -cne $ExpectedSourceRevision) {
        throw 'Sandbox handoff identity or source revision differs.'
    }
    if ($Record.runId -isnot [string] -or $Record.runId -cnotmatch '^[a-z0-9][a-z0-9-]{0,95}$' -or
        $Record.lane -cnotin @('editor', 'game') -or
        $Record.status -cnotin @('passed', 'failed', 'incomplete', 'refused') -or
        $Record.cleanupConfirmed -isnot [bool] -or
        $Record.qualifiesRelease -isnot [bool] -or $Record.qualifiesRelease) {
        throw 'Sandbox handoff outcome is invalid or claims release authority.'
    }
    foreach ($key in @('evidenceSha256', 'verificationSha256', 'admissionSha256')) {
        if ($Record[$key] -isnot [string] -or $Record[$key] -cnotmatch '^[0-9a-f]{64}$') {
            throw 'Sandbox handoff requires exact lowercase evidence digests.'
        }
    }
    if ($Record.status -ceq 'passed' -and !$Record.cleanupConfirmed) {
        throw 'Sandbox handoff cannot pass with unconfirmed cleanup.'
    }
}
