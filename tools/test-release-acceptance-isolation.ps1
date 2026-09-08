[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$testRoot = [System.IO.Directory]::CreateTempSubdirectory('topiaforge-release-isolation-').FullName
$oldImport = $env:TOPIAFORGE_RELEASE_TEST_IMPORT
$failures = [System.Collections.Generic.List[string]]::new()
$passed = 0
function Test-IsolationCase {
    param([string]$Name, [scriptblock]$Action)
    try { & $Action; $script:passed++; Write-Host "PASS $Name" }
    catch { $script:failures.Add("${Name}: $($_.Exception.Message)"); Write-Host "FAIL $Name`: $($_.Exception.Message)" }
}
function Assert-IsolationCondition {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}
function Assert-IsolationFailure {
    param([scriptblock]$Action, [string]$Pattern)
    try { & $Action | Out-Null } catch {
        if ($_.Exception.Message -match $Pattern) { return }
        throw "Expected '$Pattern', got '$($_.Exception.Message)'."
    }
    throw "Expected refusal matching '$Pattern'."
}
try {
    $env:TOPIAFORGE_RELEASE_TEST_IMPORT = '1'
    . (Join-Path $PSScriptRoot 'release-admin.ps1') -Command preflight -StateRoot $testRoot
    $script:AcceptanceIsolationRecord = Join-Path $testRoot 'isolation.json'
    [System.IO.File]::WriteAllText($AcceptanceIsolationRecord, '{"fixture":"private record bytes"}')
    $source = '1' * 40
    Test-IsolationCase 'state freezes exact provisioning bytes' {
        Write-State -Phase preflight -SourceSha $source
        $state = Read-State
        Assert-IsolationCondition ($state.PSObject.Properties.Name -contains 'acceptanceIsolationRecord') 'State omitted the explicit isolation record.'
        Assert-IsolationCondition ($state.acceptanceIsolationRecord -ceq $AcceptanceIsolationRecord) 'State changed the record path.'
        Assert-IsolationCondition ($state.acceptanceIsolationRecordSha256 -ceq (Get-Sha256 $AcceptanceIsolationRecord)) 'State omitted the exact record digest.'
    }
    Test-IsolationCase 'resume rejects changed provisioning bytes without rewriting state' {
        $state = [pscustomobject]@{ phase='built'; acceptanceIsolationRecord=$AcceptanceIsolationRecord; acceptanceIsolationRecordSha256=(Get-Sha256 $AcceptanceIsolationRecord) }
        $before = Get-Sha256 $statePath
        [System.IO.File]::AppendAllText($AcceptanceIsolationRecord, ' ')
        try { Assert-IsolationFailure { Use-StateConfiguration $state } 'isolation.*changed|changed.*isolation' }
        finally { [System.IO.File]::WriteAllText($AcceptanceIsolationRecord, '{"fixture":"private record bytes"}') }
        Assert-IsolationCondition ((Get-Sha256 $statePath) -ceq $before) 'Refusal rewrote state.'
    }
    Test-IsolationCase 'resume rejects replacement provisioning path' {
        $state = [pscustomobject]@{ phase='built'; acceptanceIsolationRecord=$AcceptanceIsolationRecord; acceptanceIsolationRecordSha256=(Get-Sha256 $AcceptanceIsolationRecord) }
        $original = $AcceptanceIsolationRecord
        $script:AcceptanceIsolationRecord = Join-Path $testRoot 'different.json'
        $script:explicitParameters['AcceptanceIsolationRecord'] = $true
        try { Assert-IsolationFailure { Use-StateConfiguration $state } 'Cannot change AcceptanceIsolationRecord' }
        finally { $script:AcceptanceIsolationRecord=$original; $script:explicitParameters.Remove('AcceptanceIsolationRecord') }
    }
    Test-IsolationCase 'Windows build requires explicit isolation record' {
        $tokens=$null; $errors=$null
        $ast = [System.Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'release/build-windows.ps1'),[ref]$tokens,[ref]$errors)
        $parameter = @($ast.ParamBlock.Parameters | Where-Object { $_.Name.VariablePath.UserPath -ceq 'AcceptanceIsolationRecord' })
        Assert-IsolationCondition ($parameter.Count -eq 1 -and $parameter[0].Extent.Text -match 'Mandatory\s*=\s*\$true') 'Windows build does not require -AcceptanceIsolationRecord.'
    }
    Test-IsolationCase 'Windows acceptance propagates record and reads admitted manager root' {
        $sourceText=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'release/build-windows.ps1') -Raw
        Assert-IsolationCondition ($sourceText.Contains('"--isolation-record", $AcceptanceIsolationRecord')) 'Acceptance invocation omitted explicit isolation record.'
        Assert-IsolationCondition ($sourceText.Contains('$isolation.managerRoot') -and -not ($sourceText -match '\$lastRunPath\s*=\s*Join-Path\s+\$GameDirectory')) 'Build still reads normal game last-run instead of verified isolated manager root.'
    }
    Test-IsolationCase 'Proton refuses unsupported isolation before invoking tools' {
        $bash = if ($IsWindows) { Join-Path $env:ProgramFiles 'Git/bin/bash.exe' } else { (Get-Command bash -CommandType Application | Select-Object -First 1).Source }
        $output = & $bash (Join-Path $PSScriptRoot 'release/test-proton.sh') --preflight-only 2>&1 | Out-String
        Assert-IsolationCondition ($LASTEXITCODE -ne 0 -and $output -match 'isolation.*not.*supported|isolation.*unsupported') "Proton did not fail early for unsupported isolation: $output"
    }
    Test-IsolationCase 'state write cannot replace frozen record bytes' {
        $before=Get-Sha256 $statePath
        [System.IO.File]::AppendAllText($AcceptanceIsolationRecord, ' ')
        try { Assert-IsolationFailure { Write-State -Phase built -SourceSha $source } 'isolation.*changed' }
        finally { [System.IO.File]::WriteAllText($AcceptanceIsolationRecord, '{"fixture":"private record bytes"}') }
        Assert-IsolationCondition ((Get-Sha256 $statePath) -ceq $before) 'Failed state write replaced frozen state.'
    }
    Test-IsolationCase 'missing and directory records fail closed' {
        Assert-IsolationFailure { Get-ReleaseIsolationRecordHash -Path '' } 'explicit'
        Assert-IsolationFailure { Get-ReleaseIsolationRecordHash -Path $testRoot } 'bounded regular'
        Assert-IsolationFailure { Get-ReleaseIsolationRecordHash -Path (Join-Path $testRoot 'missing.json') } 'bounded regular'
    }
    Test-IsolationCase 'linked record ancestry fails closed' {
        $actual=Join-Path $testRoot 'actual'; $link=Join-Path $testRoot 'linked'
        New-Item -ItemType Directory -Path $actual | Out-Null
        [System.IO.File]::WriteAllText((Join-Path $actual 'record.json'), '{}')
        $kind=if ($IsWindows) { 'Junction' } else { 'SymbolicLink' }
        New-Item -ItemType $kind -Path $link -Target $actual | Out-Null
        try { Assert-IsolationFailure { Get-ReleaseIsolationRecordHash -Path (Join-Path $link 'record.json') } 'links|reparse' }
        finally { Remove-Item -LiteralPath $link -Force }
    }
    Test-IsolationCase 'Linux preflight and native entry fail before effects' {
        $script:targetsLinux=$true
        try {
            Assert-IsolationFailure { Invoke-Preflight } 'isolation is not supported'
            Assert-IsolationFailure { Invoke-WslProtonAcceptance -SourceSha $source -LinuxArchive unread.zip -CanonicalSha ('2'*64) } 'isolation is not supported'
            Assert-IsolationFailure { Assert-ProtonEvidence -SourceSha $source -LinuxArchive unread.zip -CanonicalSha ('2'*64) } 'isolation is not supported'
        }
        finally { $script:targetsLinux=$false }
    }
    Test-IsolationCase 'relative provisioning input is frozen as an absolute path' {
        $relative=[System.IO.Path]::GetRelativePath((Get-Location).Path, $AcceptanceIsolationRecord)
        & {
            . (Join-Path $PSScriptRoot 'release-admin.ps1') -Command preflight -StateRoot $testRoot -AcceptanceIsolationRecord $relative
            Assert-IsolationCondition ([System.IO.Path]::IsPathFullyQualified($AcceptanceIsolationRecord)) 'Relative record would change meaning inside a build worktree.'
        }
    }
    Test-IsolationCase 'output cleanup cannot contain the provisioning record' {
        $before=Get-Sha256 $AcceptanceIsolationRecord
        Assert-IsolationFailure {
            Assert-ReleaseIsolationRecordOutsideOutputs -RecordPath $AcceptanceIsolationRecord -OutputDirectories @($testRoot)
        } 'output|overwrite'
        Assert-IsolationCondition ((Get-Sha256 $AcceptanceIsolationRecord) -ceq $before) 'Output refusal altered provisioning bytes.'
        Assert-ReleaseIsolationRecordOutsideOutputs -RecordPath $AcceptanceIsolationRecord -OutputDirectories @((Join-Path $testRoot 'separate'))
    }
    $evidencePath=Join-Path $testRoot 'acceptance-result.json'
    [System.IO.File]::WriteAllText($evidencePath, '{"fixture":"private verifier input"}')
    $script:verifierSummary=[ordered]@{
        schemaVersion=1; status='admitted'; gameDirectory=(Join-Path $testRoot 'isolated-game')
        managerRoot=(Join-Path $testRoot 'isolated-game/BepInEx/TopiaForge')
        provisioningRecordSha256=(Get-Sha256 $AcceptanceIsolationRecord); acknowledgementSha256=('a'*64)
    }
    $script:verifierMode='pass'
    $script:verifierArguments=@()
    function Invoke-ReleaseIsolationVerifier {
        param([string]$FilePath, [string[]]$Arguments, [string]$WorkingDirectory)
        Assert-IsolationCondition ($FilePath -ceq 'frozen-cli' -and $WorkingDirectory -ceq $testRoot) 'Wrong isolation verifier executable or CWD.'
        $script:verifierArguments=$Arguments
        switch ($script:verifierMode) {
            'reject' { throw 'The private acceptance isolation verifier rejected the evidence.' }
            'record-mutation' { [System.IO.File]::AppendAllText($AcceptanceIsolationRecord, ' ') }
            'evidence-mutation' { [System.IO.File]::AppendAllText($evidencePath, ' ') }
        }
        return ($script:verifierSummary | ConvertTo-Json -Depth 8 -Compress)
    }
    function Invoke-IsolationFixture {
        Get-VerifiedReleaseAcceptanceIsolation -EvidencePath $evidencePath `
            -IsolationRecordPath $AcceptanceIsolationRecord -CliPath frozen-cli `
            -WorkingDirectory $testRoot
    }
    Test-IsolationCase 'verifier receives exact private files and admitted roots remain authoritative' {
        $verified=Invoke-IsolationFixture
        $expected=@('acceptance','verify-isolation','--evidence',$evidencePath,'--isolation-record',$AcceptanceIsolationRecord)
        Assert-IsolationCondition (([string]::Join('|',$verifierArguments)) -ceq ([string]::Join('|',$expected))) 'Verifier did not receive exact evidence and frozen record.'
        Assert-IsolationCondition ($verified.managerRoot -ceq $verifierSummary.managerRoot -and $verified.gameDirectory -cne $GameDirectory) 'Normal game path replaced admitted roots.'
    }
    foreach ($field in @('schemaVersion','status','gameDirectory','managerRoot','provisioningRecordSha256','acknowledgementSha256')) {
        Test-IsolationCase "verifier rejects invalid $field" {
            $original=$script:verifierSummary[$field]
            $script:verifierSummary[$field]=if ($field -ceq 'schemaVersion') { '1' } else { 'invalid' }
            try { Assert-IsolationFailure { Invoke-IsolationFixture } 'invalid|mismatched' }
            finally { $script:verifierSummary[$field]=$original }
        }
    }
    Test-IsolationCase 'verifier rejects terminal newline in acknowledgement digest' {
        $script:verifierSummary.acknowledgementSha256 = ('a'*64) + "`n"
        try { Assert-IsolationFailure { Invoke-IsolationFixture } 'invalid|mismatched' }
        finally { $script:verifierSummary.acknowledgementSha256 = 'a'*64 }
    }
    Test-IsolationCase 'verifier rejects unexpected summary fields' {
        $script:verifierSummary['extra']='not trusted'
        try { Assert-IsolationFailure { Invoke-IsolationFixture } 'invalid|mismatched' }
        finally { $script:verifierSummary.Remove('extra') }
    }
    foreach ($mode in @('reject','record-mutation','evidence-mutation')) {
        Test-IsolationCase "verifier fails closed for $mode" {
            $script:verifierMode=$mode
            try { Assert-IsolationFailure { Invoke-IsolationFixture } 'rejected|changed' }
            finally {
                $script:verifierMode='pass'
                [System.IO.File]::WriteAllText($AcceptanceIsolationRecord, '{"fixture":"private record bytes"}')
                [System.IO.File]::WriteAllText($evidencePath, '{"fixture":"private verifier input"}')
            }
        }
    }
    Write-Host "Release isolation tests: $passed passed, $($failures.Count) failed."
    if ($failures.Count) { throw ($failures -join "`n") }
}
finally {
    $env:TOPIAFORGE_RELEASE_TEST_IMPORT = $oldImport
    if (Test-Path -LiteralPath $testRoot) { Remove-Item -LiteralPath $testRoot -Recurse -Force }
}
