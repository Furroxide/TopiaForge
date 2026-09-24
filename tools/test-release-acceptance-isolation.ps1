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
    Test-IsolationCase 'Windows build requires an explicit mode and a record only for a live run' {
        $tokens=$null; $errors=$null
        $ast = [System.Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'release/build-windows.ps1'),[ref]$tokens,[ref]$errors)
        $mode = @($ast.ParamBlock.Parameters | Where-Object { $_.Name.VariablePath.UserPath -ceq 'LiveGameAcceptance' })
        Assert-IsolationCondition ($mode.Count -eq 1 -and $mode[0].Extent.Text -match 'Mandatory\s*=\s*\$true' -and
            $mode[0].Extent.Text -match 'ValidateSet\("run", "not-run", IgnoreCase = \$false\)') 'Windows build does not require an exact -LiveGameAcceptance mode.'
        $parameter = @($ast.ParamBlock.Parameters | Where-Object { $_.Name.VariablePath.UserPath -ceq 'AcceptanceIsolationRecord' })
        Assert-IsolationCondition ($parameter.Count -eq 1 -and $parameter[0].Extent.Text -notmatch 'Mandatory') 'The isolation record must be required by mode, not by the parameter block.'
        # Admission happens before any path is resolved: run the real script in a child.
        $build = Join-Path $PSScriptRoot 'release/build-windows.ps1'
        $absent = Join-Path $testRoot 'absent-repository'
        $common = @('-NoProfile', '-NonInteractive', '-File', $build, '-RepositoryRoot', $absent, '-SourceSha', ('1' * 40),
            '-Version', '0.1.0-rc.1', '-CanonicalArchive', $absent, '-CanonicalEcosystemSha256', ('2' * 64),
            '-CanonicalArchiveSha256', ('3' * 64), '-OutputDirectory', $absent, '-PrivateEvidenceDirectory', $absent,
            '-DartPath', 'dart', '-FlutterPath', 'flutter', '-UnityPath', 'unity', '-GameDirectory', $absent)
        foreach ($case in @(
                @{ Arguments = @('-LiveGameAcceptance', 'run'); Pattern = 'explicit -AcceptanceIsolationRecord' },
                @{ Arguments = @('-LiveGameAcceptance', 'not-run', '-AcceptanceIsolationRecord', $AcceptanceIsolationRecord); Pattern = 'refuses -AcceptanceIsolationRecord' },
                @{ Arguments = @('-LiveGameAcceptance', 'Run', '-AcceptanceIsolationRecord', $AcceptanceIsolationRecord); Pattern = 'does not belong to the set' },
                @{ Arguments = @('-LiveGameAcceptance', 'not-run'); Pattern = 'absent-repository' }
            )) {
            $output = & $powerShellExecutable @common @($case.Arguments) 2>&1 | Out-String
            # Error views wrap long messages to the console width behind a '|' gutter
            # (narrow on hosted Linux); match the unwrapped text.
            $flat = ($output -replace '(?m)^\s*\|', ' ') -replace '\s+', ' '
            Assert-IsolationCondition ($LASTEXITCODE -ne 0 -and $flat -match $case.Pattern) "Unexpected Windows build admission for $($case.Arguments -join ' '): $output"
        }
    }
    Test-IsolationCase 'Windows build launches live acceptance only inside its run branch' {
        $tokens=$null; $errors=$null
        $ast = [System.Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'release/build-windows.ps1'),[ref]$tokens,[ref]$errors)
        Assert-IsolationCondition ($errors.Count -eq 0) 'build-windows.ps1 has parse errors.'
        $launches = @($ast.FindAll({ param($node) $node -is [System.Management.Automation.Language.CommandAst] -and $node.Extent.Text -match '"acceptance",\s*"run"' }, $true))
        Assert-IsolationCondition ($launches.Count -eq 1) "Expected one live acceptance launch, found $($launches.Count)."
        $binding = @($ast.FindAll({ param($node) $node -is [System.Management.Automation.Language.AssignmentStatementAst] -and $node.Left.Extent.Text -ceq '$runsLiveGameAcceptance' }, $true))
        Assert-IsolationCondition ($binding.Count -eq 1 -and $binding[0].Right.Extent.Text -ceq '$LiveGameAcceptance -ceq "run"') 'The run branch is not bound to the case-sensitive run mode.'
        $enclosing = @($ast.FindAll({ param($node) $node -is [System.Management.Automation.Language.IfStatementAst] -and $node.Clauses.Count -eq 1 -and
                    $null -eq $node.ElseClause -and $node.Clauses[0].Item1.Extent.Text -ceq '$runsLiveGameAcceptance' }, $true) | Where-Object {
                $body = $_.Clauses[0].Item2.Extent
                $launches[0].Extent.StartOffset -ge $body.StartOffset -and $launches[0].Extent.EndOffset -le $body.EndOffset })
        Assert-IsolationCondition ($enclosing.Count -eq 1) 'The live acceptance launch escapes the run branch.'
        $parent = $launches[0].Parent
        while ($null -ne $parent -and $parent -ne $enclosing[0]) {
            Assert-IsolationCondition ($parent -isnot [System.Management.Automation.Language.IfStatementAst]) 'The live acceptance launch is nested under another condition.'
            $parent = $parent.Parent
        }
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
    $gameGate = { param([string]$Enforcement) [pscustomobject]@{ id = 'P0-GAME-01'; enforcement = $Enforcement; status = 'blocked' } }
    $advisory = [pscustomobject]@{ gates = @((& $gameGate 'advisory')) }
    Test-IsolationCase 'run mode keeps the explicit isolation-record checks either way' {
        foreach ($prerequisites in @($null, $advisory, [pscustomobject]@{ gates = @((& $gameGate 'blocking')) })) {
            Assert-LiveGameAcceptanceMode -Mode run -IsolationRecord $AcceptanceIsolationRecord -Prerequisites $prerequisites
            Assert-IsolationFailure { Assert-LiveGameAcceptanceMode -Mode run -IsolationRecord '' -Prerequisites $prerequisites } 'explicit'
        }
        Assert-IsolationFailure { Assert-LiveGameAcceptanceMode -Mode run -IsolationRecord (Join-Path $testRoot 'missing.json') } 'bounded regular'
        $insideOutputs = Join-Path $assetsDirectory 'isolation.json'
        New-Item -ItemType Directory -Force -Path $assetsDirectory | Out-Null
        [System.IO.File]::WriteAllText($insideOutputs, '{"fixture":"record inside outputs"}')
        try { Assert-IsolationFailure { Assert-LiveGameAcceptanceMode -Mode run -IsolationRecord $insideOutputs } 'output|overwrite' }
        finally { Remove-Item -LiteralPath $insideOutputs -Force }
    }
    Test-IsolationCase 'not-run mode refuses an isolation record' {
        foreach ($prerequisites in @($null, $advisory)) {
            Assert-IsolationFailure { Assert-LiveGameAcceptanceMode -Mode not-run -IsolationRecord $AcceptanceIsolationRecord -Prerequisites $prerequisites } 'not-run.*AcceptanceIsolationRecord'
        }
    }
    Test-IsolationCase 'not-run mode requires an advisory P0-GAME-01 at the exact SHA' {
        Assert-LiveGameAcceptanceMode -Mode not-run -IsolationRecord ''
        Assert-LiveGameAcceptanceMode -Mode not-run -IsolationRecord '' -Prerequisites $advisory
        foreach ($prerequisites in @(
                [pscustomobject]@{ gates = @((& $gameGate 'blocking')) },
                [pscustomobject]@{ gates = @((& $gameGate 'advisory'), (& $gameGate 'advisory')) },
                [pscustomobject]@{ gates = @([pscustomobject]@{ id = 'P0-IP-01'; enforcement = 'blocking' }) },
                [pscustomobject]@{ gates = @([pscustomobject]@{ id = 'P0-GAME-01' }) },
                [pscustomobject]@{ status = 'eligible-for-private-build' }
            )) {
            Assert-IsolationFailure { Assert-LiveGameAcceptanceMode -Mode not-run -IsolationRecord '' -Prerequisites $prerequisites } 'advisory'
        }
    }
    Test-IsolationCase 'live game acceptance mode is exact and case-sensitive' {
        foreach ($mode in @('Run', 'NOT-RUN', 'skip', '')) {
            Assert-IsolationFailure { Assert-LiveGameAcceptanceMode -Mode $mode -IsolationRecord '' } 'argument|validat'
        }
    }
    Test-IsolationCase 'state freezes the live game acceptance mode against resume' {
        $savedRecord = $AcceptanceIsolationRecord
        try {
            $script:LiveGameAcceptance = 'not-run'
            $script:AcceptanceIsolationRecord = ''
            Write-State -Phase preflight -SourceSha $source
            $frozen = Read-State
            Assert-IsolationCondition ($frozen.liveGameAcceptance -ceq 'not-run' -and $frozen.acceptanceIsolationRecord -ceq '' -and
                $frozen.acceptanceIsolationRecordSha256 -ceq '') 'State did not freeze not-run without a record.'
            $before = Get-Sha256 $statePath
            foreach ($change in @(@('LiveGameAcceptance', 'run'), @('AcceptanceIsolationRecord', $savedRecord))) {
                Set-Variable -Scope Script -Name $change[0] -Value $change[1]
                $script:explicitParameters[$change[0]] = $true
                try { Assert-IsolationFailure { Use-StateConfiguration $frozen } "Cannot change $($change[0])" }
                finally {
                    $script:explicitParameters.Remove($change[0])
                    $script:LiveGameAcceptance = 'not-run'
                    $script:AcceptanceIsolationRecord = ''
                }
            }
            Assert-IsolationCondition ((Get-Sha256 $statePath) -ceq $before) 'Refused resume rewrote state.'
            $script:LiveGameAcceptance = 'run'
            Use-StateConfiguration $frozen
            Assert-IsolationCondition ($LiveGameAcceptance -ceq 'not-run') 'Resume did not adopt the frozen not-run mode.'
            $script:AcceptanceIsolationRecord = $savedRecord
            $script:LiveGameAcceptance = 'run'
            Write-State -Phase preflight -SourceSha $source
            $running = Read-State
            $script:LiveGameAcceptance = 'not-run'
            $script:explicitParameters['LiveGameAcceptance'] = $true
            Assert-IsolationFailure { Use-StateConfiguration $running } 'Cannot change LiveGameAcceptance'
        }
        finally {
            $script:explicitParameters.Remove('LiveGameAcceptance')
            $script:LiveGameAcceptance = 'run'
            $script:AcceptanceIsolationRecord = $savedRecord
        }
    }
    Write-Host "Release isolation tests: $passed passed, $($failures.Count) failed."
    if ($failures.Count) { throw ($failures -join "`n") }
}
finally {
    $env:TOPIAFORGE_RELEASE_TEST_IMPORT = $oldImport
    if (Test-Path -LiteralPath $testRoot) { Remove-Item -LiteralPath $testRoot -Recurse -Force }
}
