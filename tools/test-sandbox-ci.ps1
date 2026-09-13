[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'sandbox/ci-admission.ps1')
$passed = 0
$failed = [Collections.Generic.List[string]]::new()
function Test-Case([string]$Name, [scriptblock]$Action) {
    try { & $Action; $script:passed++; Write-Host "PASS $Name" }
    catch { $script:failed.Add($Name); Write-Host "FAIL $Name" }
}
function Assert-Refused([scriptblock]$Action) {
    $refused = $false
    try { & $Action | Out-Null } catch { $refused = $true }
    if (!$refused) { throw 'Expected refusal.' }
}
function New-Fixture {
    return @{
        schemaVersion = 1; kind = 'sandbox-local-lane-admission-v1'; lane = 'game'
        sourceRevision = 'a' * 40; operatorId = 'named-test-operator'
        reviewReference = 'test-only-review'; reservationId = 'test-reservation'
        notBeforeUtc = '2026-09-09T10:00:00Z'; expiresUtc = '2026-09-09T11:00:00Z'
        machineName = 'TEST-HOST'; userSid = 'S-1-5-21-123-1001'
        workspaceRoot = 'D:\test-source'; privateRoot = 'D:\test-private'
        recoveryReference = 'test-only-recovery'; baselineReviewReference = 'test-only-baseline'
        devices = @(
            @{kind = 'display'; id = 'test-display'}
            @{kind = 'keyboard'; id = 'test-keyboard'}
            @{kind = 'mouse'; id = 'test-mouse'}
            @{kind = 'audio-output'; id = 'test-output'}
        )
    }
}
function Assert-Fixture($Record) {
    Assert-SandboxCiAdmission $Record 'game' ('a' * 40) 'D:\test-source' 'TEST-HOST' 'S-1-5-21-123-1001' ([DateTimeOffset]'2026-09-09T10:30:00Z')
}
Test-Case 'valid test-only admission shape' { Assert-Fixture (New-Fixture) }
Test-Case 'reviewed game snapshot digest permits explicit dirty-source binding' {
    $r=New-Fixture
    $r.sourceWorkspaceSha256='e'*64
    Assert-SandboxCiAdmission $r 'game' ('a'*40) 'D:\test-source' 'TEST-HOST' 'S-1-5-21-123-1001' ([DateTimeOffset]'2026-09-09T10:30:00Z') -SourceWorkspaceSha256 ('e'*64)
}
Test-Case 'snapshot digest mismatch cannot enter reviewed lane' {
    $r=New-Fixture
    $r.sourceWorkspaceSha256='e'*64
    Assert-Refused { Assert-SandboxCiAdmission $r 'game' ('a'*40) 'D:\test-source' 'TEST-HOST' 'S-1-5-21-123-1001' ([DateTimeOffset]'2026-09-09T10:30:00Z') -SourceWorkspaceSha256 ('f'*64) }
}
Test-Case 'snapshot field cannot silently bypass clean Git mode' {
    $r=New-Fixture
    $r.sourceWorkspaceSha256='e'*64
    Assert-Refused { Assert-Fixture $r }
}

$mutations = [ordered]@{
    'unknown property' = { param($r) $r.unknown = $true }
    'missing operator' = { param($r) $r.Remove('operatorId') }
    'empty operator' = { param($r) $r.operatorId = ' ' }
    'floating version' = { param($r) $r.schemaVersion = 1.0 }
    'changed revision' = { param($r) $r.sourceRevision = 'b' * 40 }
    'changed lane' = { param($r) $r.lane = 'editor' }
    'wrong machine' = { param($r) $r.machineName = 'OTHER' }
    'wrong SID' = { param($r) $r.userSid = 'S-1-5-21-123-1002' }
    'wrong source root' = { param($r) $r.workspaceRoot = 'D:\other' }
    'future reservation' = { param($r) $r.notBeforeUtc = '2026-09-09T10:31:00Z' }
    'expired reservation' = { param($r) $r.expiresUtc = '2026-09-09T10:30:00Z' }
    'overlong reservation' = { param($r) $r.expiresUtc = '2026-09-09T15:00:00Z' }
    'non UTC reservation' = { param($r) $r.expiresUtc = '2026-09-09T12:00:00+01:00' }
    'missing audio device' = { param($r) $r.devices = $r.devices[0..2] }
    'repeated device kind' = { param($r) $r.devices += @{kind = 'display'; id = 'second'} }
    'missing device identity' = { param($r) $r.devices[0].id = '' }
    'unknown device kind' = { param($r) $r.devices[0].kind = 'arbitrary' }
    'missing recovery record' = { param($r) $r.recoveryReference = '' }
    'missing baseline review' = { param($r) $r.baselineReviewReference = '' }
    'control character' = { param($r) $r.operatorId = "operator\n".Replace('\n', [string][char]10) }
}
foreach ($entry in $mutations.GetEnumerator()) {
    Test-Case $entry.Key {
        $record = New-Fixture
        & $entry.Value $record | Out-Null
        Assert-Refused { Assert-Fixture $record }
    }
}

. (Join-Path $PSScriptRoot 'sandbox/ci-handoff.ps1')
function New-HandoffFixture {
    return @{schemaVersion=1;kind='sandbox-ci-handoff-v1';sourceRevision=('a'*40)
        runId='test-only-run';lane='game';status='passed';cleanupConfirmed=$true
        qualifiesRelease=$false;evidenceSha256=('b'*64);verificationSha256=('c'*64)
        admissionSha256=('d'*64)}
}
Test-Case 'scrubbed handoff exact shape' { Assert-SandboxCiHandoff (New-HandoffFixture) ('a'*40) }
$handoffMutations = [ordered]@{
    'raw identity cannot enter handoff' = {param($r) $r.userSid='private'}
    'handoff cannot qualify release' = {param($r) $r.qualifiesRelease=$true}
    'handoff cannot pass without cleanup' = {param($r) $r.cleanupConfirmed=$false}
    'handoff cannot change source' = {param($r) $r.sourceRevision='e'*40}
    'handoff cannot omit evidence digest' = {param($r) $r.Remove('evidenceSha256')}
    'handoff cannot use unknown status' = {param($r) $r.status='skipped'}
    'handoff rejects text cleanup' = {param($r) $r.cleanupConfirmed='true'}
    'handoff rejects malformed digest' = {param($r) $r.verificationSha256='none'}
}
foreach ($entry in $handoffMutations.GetEnumerator()) {
    Test-Case $entry.Key {
        $r=New-HandoffFixture
        & $entry.Value $r | Out-Null
        Assert-Refused { Assert-SandboxCiHandoff $r ('a'*40) }
    }
}
Test-Case 'incomplete remains an explicit valid handoff status' {
    $r=New-HandoffFixture
    $r.status='incomplete'
    Assert-SandboxCiHandoff $r ('a'*40)
}

$owned = [IO.Directory]::CreateTempSubdirectory('sandbox-ci-regression-').FullName
try {
    $path = Join-Path $owned 'input.json'
    Test-Case 'bounded JSON reads shape' {
        [IO.File]::WriteAllText($path, ((New-Fixture) | ConvertTo-Json -Depth 8))
        Assert-Fixture (Read-SandboxCiDocument $path)
    }
    Test-Case 'duplicate JSON property refused' {
        [IO.File]::WriteAllText($path, '{"kind":"a","kind":"b"}')
        Assert-Refused { Read-SandboxCiDocument $path }
    }
    Test-Case 'oversized JSON refused' {
        [IO.File]::WriteAllText($path, (' ' * 65537))
        Assert-Refused { Read-SandboxCiDocument $path }
    }
    Test-Case 'invalid UTF8 refused' {
        [IO.File]::WriteAllBytes($path, [byte[]]@(0xff, 0xff))
        Assert-Refused { Read-SandboxCiDocument $path }
    }
    Test-Case 'relative path refused' { Assert-Refused { Read-SandboxCiDocument './input.json' } }
    Test-Case 'missing admission refused' { Assert-Refused { Read-SandboxCiDocument (Join-Path $owned 'absent.json') } }
    Test-Case 'Actions cannot enter a licensed lane' {
        $previous = $env:GITHUB_ACTIONS
        try {
            $env:GITHUB_ACTIONS = 'true'
            Assert-Refused { Get-SandboxCiLocalAdmission $path 'game' $owned }
        }
        finally { $env:GITHUB_ACTIONS = $previous }
    }
    Test-Case 'completed cleanup releases reservation even after test failure' {
        $r=New-Fixture
        $r.privateRoot=$owned
        $result=Invoke-SandboxCiReservedLane $r { return @{cleanupConfirmed=$true;status='failed'} }
        if ($result.status -cne 'failed' -or (Test-Path -LiteralPath (Join-Path $owned 'sandbox-recovery-required.json'))) {
            throw 'Failed scenario result or reservation cleanup was lost.'
        }
    }
    Test-Case 'unknown cleanup preserves marker and blocks next action' {
        $r=New-Fixture
        $r.privateRoot=$owned
        Assert-Refused { Invoke-SandboxCiReservedLane $r { return @{cleanupConfirmed=$false;status='failed'} } }
        $marker=Join-Path $owned 'sandbox-recovery-required.json'
        if (!(Test-Path -LiteralPath $marker)) { throw 'Recovery marker missing.' }
        $script:unexpectedAction=$false
        Assert-Refused { Invoke-SandboxCiReservedLane $r { $script:unexpectedAction=$true; return @{cleanupConfirmed=$true} } }
        if ($script:unexpectedAction) { throw 'Blocked reservation executed an action.' }
        # Test-owned synthetic marker only, no real device/process state exists.
        [IO.File]::Delete($marker)
    }

    Test-Case 'abandoned mutex creates a persistent recovery block across retries' {
        $r=New-Fixture
        $r.privateRoot=$owned
        $name='Global\TopiaForgeSandboxAcceptanceV1'
        $keepAlive=[Threading.Mutex]::new($false, $name)
        $childScript=Join-Path $owned 'abandon-test-mutex.ps1'
        $marker=Join-Path $owned 'sandbox-recovery-required.json'
        [IO.File]::WriteAllText($childScript, '$m=[Threading.Mutex]::new($false, ''Global\TopiaForgeSandboxAcceptanceV1''); if (!$m.WaitOne(0)) { [Environment]::Exit(9) }; [Environment]::Exit(0)')
        $start=[Diagnostics.ProcessStartInfo]::new()
        $start.FileName=(Get-Process -Id $PID).Path
        $start.UseShellExecute=$false
        $start.CreateNoWindow=$true
        foreach ($arg in @('-NoProfile','-File',$childScript)) { $start.ArgumentList.Add($arg) }
        $child=[Diagnostics.Process]::Start($start)
        try {
            if (!$child.WaitForExit(10000) -or $child.ExitCode -ne 0) { throw 'Test child did not establish abandonment.' }
            $script:unexpectedAction=$false
            Assert-Refused { Invoke-SandboxCiReservedLane $r { $script:unexpectedAction=$true; return @{cleanupConfirmed=$true} } }
            if (!(Test-Path -LiteralPath $marker)) { throw 'Abandonment must persist its recovery marker.' }
            Assert-Refused { Invoke-SandboxCiReservedLane $r { $script:unexpectedAction=$true; return @{cleanupConfirmed=$true} } }
            if ($script:unexpectedAction) { throw 'Abandoned reservation executed an action.' }
        }
        finally {
            if (!$child.HasExited) { $child.Kill($true); $child.WaitForExit(10000) | Out-Null }
            $child.Dispose()
            $keepAlive.Dispose()
            if (Test-Path -LiteralPath $marker) { [IO.File]::Delete($marker) }
        }
    }

}
finally {
    $resolved = [IO.Path]::GetFullPath($owned)
    if ([IO.Path]::GetFileName($resolved) -notlike 'sandbox-ci-regression-*') { throw 'Unexpected test cleanup root.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
Write-Host "Sandbox CI admission: $passed passed, $($failed.Count) failed."
if ($failed.Count) { throw ($failed -join ', ') }
