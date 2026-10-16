[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('editor', 'game')][string]$Lane,
    [Parameter(Mandatory)][string]$AdmissionRecord,
    [string]$EditorPath = 'C:\Program Files\Unity\Hub\Editor\6000.0.23f1\Editor\Unity.exe',
    [string]$DartExecutable, [string]$CliExecutable, [string]$IsolationRecord, [string]$DeviceProfile,
    [string]$Packages, [string]$Broker, [string]$DriverManifest, [string]$Spec,
    [string]$SourceWorkspace,
    [ValidateRange(30, 14400)][int]$TimeoutSeconds = 600
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'sandbox/ci-admission.ps1')
. (Join-Path $PSScriptRoot 'sandbox/ci-handoff.ps1')
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$admission = Get-SandboxCiLocalAdmission $AdmissionRecord $Lane $repo -SourceWorkspace $SourceWorkspace
$remaining = ([DateTimeOffset]::Parse($admission.expiresUtc) - [DateTimeOffset]::UtcNow).TotalSeconds
if ($remaining -lt ($TimeoutSeconds + 60)) { throw 'Device reservation must cover the timeout plus one minute of cleanup.' }
$admissionDigest = (Get-FileHash -LiteralPath $AdmissionRecord -Algorithm SHA256).Hash.ToLowerInvariant()
$runId = 'ci-' + [Guid]::NewGuid().ToString('N')
$runRoot = Join-Path $admission.privateRoot $runId
if (Test-Path -LiteralPath $runRoot) { throw 'Sandbox CI output already exists.' }
if ($Lane -ceq 'editor') {
    if ($TimeoutSeconds -lt 60 -or $TimeoutSeconds -gt 1800) { throw 'Editor timeout must be 60 through 1800 seconds.' }
    Assert-SandboxCiPhysicalPath $EditorPath -File
}
else {
    if ([string]::IsNullOrWhiteSpace($DartExecutable) -eq [string]::IsNullOrWhiteSpace($CliExecutable)) {
        throw 'Provide exactly one compiled CLI executable or pinned Dart executable.'
    }
    $command = if ($CliExecutable) { $CliExecutable } else { $DartExecutable }
    $commandPrefix = @()
    if ($DartExecutable) { $commandPrefix = @('run', 'bin/topiaforge.dart') }
    foreach ($path in @($command, $IsolationRecord, $DeviceProfile, $Packages, $Broker, $DriverManifest, $Spec)) {
        Assert-SandboxCiPhysicalPath $path -File
    }
    if ($DartExecutable) {
        $version = (& $DartExecutable --version 2>&1 | Out-String)
        if ($LASTEXITCODE -ne 0 -or $version -notmatch 'Dart SDK version: 3\.12\.2 ') { throw 'Pinned Dart 3.12.2 is required.' }
    }
    $isolation = Read-SandboxCiDocument $IsolationRecord
    $outputRoot = [string]$isolation.outputRoot
    Assert-SandboxCiPhysicalPath $outputRoot
    if (!$outputRoot.StartsWith($admission.privateRoot.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Native outputRoot must be a child of the reviewed private admission root.'
    }
}
$null = New-Item -ItemType Directory -Path $runRoot
$result = Invoke-SandboxCiReservedLane -Admission $admission -Action {
    $evidencePath = $null
    $verificationPath = Join-Path $runRoot 'verification.json'
    if ($Lane -ceq 'editor') {
        $editorOutput = Join-Path $runRoot 'editor'
        $shell = (Get-Process -Id $PID).Path
        $editorArgs = @('-NoProfile', '-File', (Join-Path $repo 'tools/run-sandbox-editor-automation.ps1'),
            '-EditorPath', $EditorPath, '-OutputDirectory', $editorOutput, '-TimeoutSeconds', $TimeoutSeconds)
        & $shell @editorArgs *> (Join-Path $runRoot 'runner-console.log')
        $runnerExit = $LASTEXITCODE
        $evidencePath = Join-Path $editorOutput 'runner-summary.json'
        $summary = Read-SandboxCiDocument $evidencePath -MaximumBytes 262144 -MaximumArrayLength 4096
        $cleanup = $summary.cleanupConfirmed -is [bool] -and $summary.cleanupConfirmed -and
            $summary.inputHeld -is [bool] -and !$summary.inputHeld
        $status = if ($runnerExit -eq 0 -and $summary.status -ceq 'passed' -and
            $summary.qualifiesRelease -is [bool] -and !$summary.qualifiesRelease -and
            $summary.sourceRevision -ceq $admission.sourceRevision -and !$summary.sourceDirty -and
            $summary.editorVersion -like '6000.0.23f1*' -and $cleanup) { 'passed' } else { 'failed' }
        @{kind='sandbox-editor-lane-verification-v1';status=$status;cleanupConfirmed=$cleanup;qualifiesRelease=$false} |
            ConvertTo-Json | Set-Content -LiteralPath $verificationPath -Encoding utf8
    }
    else {
        $sandboxRoot = Join-Path $outputRoot 'sandbox'
        if (Test-Path -LiteralPath $sandboxRoot) { Assert-SandboxCiPhysicalPath $sandboxRoot }
        $before = if (Test-Path -LiteralPath $sandboxRoot) {
            @(Get-ChildItem -LiteralPath $sandboxRoot -Directory | Select-Object -ExpandProperty Name)
        } else { @() }
        Push-Location (Join-Path $repo 'apps/topiaforge_cli')
        try {
            $runArgs = @($commandPrefix) + @('acceptance', 'sandbox', 'run', '--source-root', $repo,
                '--isolation-record', $IsolationRecord, '--device-profile', $DeviceProfile,
                '--packages', $Packages, '--broker', $Broker, '--output-root', $outputRoot,
                '--driver-manifest', $DriverManifest, '--spec', $Spec,
                '--timeout-seconds', $TimeoutSeconds)
            if ($SourceWorkspace) { $runArgs += @('--source-workspace', $SourceWorkspace) }
            & $command @runArgs *> (Join-Path $runRoot 'runner-console.log')
            $runnerExit = $LASTEXITCODE
            Assert-SandboxCiPhysicalPath $sandboxRoot
            $new = @(Get-ChildItem -LiteralPath $sandboxRoot -Directory | Where-Object { $_.Name -cnotin $before })
            if ($new.Count -ne 1) { throw 'Native run output is missing or ambiguous; preserve the layout.' }
            Assert-SandboxCiPhysicalPath $new[0].FullName
            $annexPath = Join-Path $new[0].FullName 'sandbox-annex.json'
            if (Test-Path -LiteralPath $annexPath) {
                $evidencePath = $annexPath
                $verifyArgs = @($commandPrefix) + @('acceptance', 'sandbox', 'verify',
                    '--annex', $annexPath, '--isolation-record', $IsolationRecord, '--device-profile', $DeviceProfile,
                    '--packages', $Packages, '--broker', $Broker, '--spec', $Spec,
                    '--driver-manifest', $DriverManifest)
                & $command @verifyArgs 1> $verificationPath 2> (Join-Path $runRoot 'verifier-errors.log')
                $verifyExit = $LASTEXITCODE
                if ($verifyExit -notin @(0, 1)) { throw 'Independent native evidence verification refused; retain recovery marker.' }
                $verified = Read-SandboxCiDocument $verificationPath
                $summary = Read-SandboxCiDocument $annexPath -MaximumBytes 262144 -MaximumArrayLength 256
                if ($SourceWorkspace -and $summary.sourceWorkspaceSha256 -cne $admission.sourceWorkspaceSha256) {
                    throw 'Native annex did not bind the operator-reviewed source snapshot.'
                }
                $cleanup = $summary.cleanup.inputReleased -is [bool] -and
                    $summary.cleanup.fixtureReleased -is [bool] -and
                    $summary.cleanup.originalProcessExitConfirmed -is [bool] -and
                    $summary.cleanup.inputReleased -ceq $true -and
                    $summary.cleanup.fixtureReleased -ceq $true -and
                    $summary.cleanup.originalProcessExitConfirmed -ceq $true -and
                    $summary.isolation.processExitConfirmed -ceq $true
                $status = [string]$verified.status
                if ($verified.qualifiesRelease -isnot [bool] -or $verified.qualifiesRelease -or
                    $status -cnotin @('passed', 'failed', 'incomplete') -or
                    ($status -ceq 'passed' -and ($runnerExit -ne 0 -or $verifyExit -ne 0))) {
                    throw 'Native verification result is inconsistent.'
                }
            }
            else {
                $evidencePath = Join-Path $new[0].FullName 'sandbox-failure.json'
                $summary = Read-SandboxCiDocument $evidencePath -MaximumBytes 262144 -MaximumArrayLength 4096
                if ($summary.kind -cne 'sandbox-native-run-failure-v1' -or $summary.processStarted -isnot [bool]) { throw 'Unknown native failure receipt.' }
                $cleanup = $summary.cleanup.inputReleased -is [bool] -and
                    $summary.cleanup.fixtureReleased -is [bool] -and
                    $summary.cleanup.originalProcessExitConfirmed -is [bool] -and
                    $summary.cleanup.inputReleased -ceq $true -and
                    $summary.cleanup.fixtureReleased -ceq $true -and
                    $summary.cleanup.originalProcessExitConfirmed -ceq $true
                $status = 'refused'
                @{kind='sandbox-native-refusal-verification-v1';status=$status;qualifiesRelease=$false} |
                    ConvertTo-Json | Set-Content -LiteralPath $verificationPath -Encoding utf8
            }
        }
        finally { Pop-Location }
    }
    if ($SourceWorkspace -and (Get-FileHash -LiteralPath $SourceWorkspace -Algorithm SHA256).Hash.ToLowerInvariant() -cne $admission.sourceWorkspaceSha256) {
        throw 'Operator-reviewed source snapshot changed during execution.'
    }
    if ((Get-FileHash -LiteralPath $AdmissionRecord -Algorithm SHA256).Hash.ToLowerInvariant() -cne $admissionDigest) {
        throw 'Private lane admission changed during execution.'
    }
    $handoff = @{
        schemaVersion=1;kind='sandbox-ci-handoff-v1';sourceRevision=$admission.sourceRevision
        runId=$runId;lane=$Lane;status=$status;cleanupConfirmed=[bool]$cleanup;qualifiesRelease=$false
        evidenceSha256=(Get-FileHash -LiteralPath $evidencePath -Algorithm SHA256).Hash.ToLowerInvariant()
        verificationSha256=(Get-FileHash -LiteralPath $verificationPath -Algorithm SHA256).Hash.ToLowerInvariant()
        admissionSha256=$admissionDigest
    }
    Assert-SandboxCiHandoff $handoff $admission.sourceRevision
    $handoff | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $runRoot 'scrubbed-handoff.json') -Encoding utf8
    return @{cleanupConfirmed=[bool]$cleanup;status=$status}
}
Write-Host "Supplementary $Lane lane: $($result.status). Private output: $runRoot"
Write-Host 'Review scrubbed-handoff.json before any separate hosted handoff validation.'
if ($result.status -cne 'passed') { exit 1 }
exit 0
