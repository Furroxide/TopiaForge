# Regression coverage for the release-admin private-build/qualification boundary.
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = [System.IO.Directory]::CreateTempSubdirectory('topiaforge-qualification-').FullName
$oldImport = $env:TOPIAFORGE_RELEASE_TEST_IMPORT
try {
    $env:TOPIAFORGE_RELEASE_TEST_IMPORT = '1'
    . (Join-Path $PSScriptRoot 'release-admin.ps1') -Command preflight -StateRoot $root
    function Assert-QualificationTest {
        param([bool]$Condition, [string]$Message)
        if (-not $Condition) { throw $Message }
    }
    function Assert-QualificationThrows {
        param([scriptblock]$Action, [string]$Pattern)
        try { & $Action | Out-Null } catch {
            if ($_.Exception.Message -match $Pattern) { return }
            throw "Expected '$Pattern', received: $($_.Exception.Message)"
        }
        throw "Expected failure matching '$Pattern'."
    }
    # Native CLI diagnostics must not corrupt the JSON assessment on stdout.
    $diagnosticJson = Invoke-Checked (Join-Path $PSHOME $powerShellName) @(
        '-NoProfile', '-Command',
        '[Console]::Error.WriteLine("warning: advisory gate remains blocked"); [Console]::Out.WriteLine(''{"status":"eligible-for-private-build"}'')'
    ) -Capture -StandardOutputOnly
    Assert-QualificationTest (($diagnosticJson | ConvertFrom-Json).status -ceq 'eligible-for-private-build') 'Diagnostic stderr corrupted the CLI assessment.'
    function Assert-SourceStillExact { param([string]$SourceSha) $null = $SourceSha }
    function Assert-OriginStillExact { param([string]$SourceSha) $null = $SourceSha }
    function Build-Handoff { throw 'UNSAFE: attempted handoff work before qualification.' }
    function Invoke-Checked { throw 'UNSAFE: attempted an external command.' }
    $source = '1' * 40
    $hash = '2' * 64
    $buildFields = @{ canonicalSha256 = $hash; canonicalArchiveSha256 = $hash; ecosystemEvidenceSha256 = $hash }
    Write-State -Phase built -SourceSha $source -Additional $buildFields
    $before = Get-Sha256 $statePath
    Assert-QualificationThrows { Invoke-Stage } 'qualify'
    Assert-QualificationThrows { Invoke-All } 'qualify'
    Assert-QualificationThrows { Invoke-Dispatch } 'Stage the exact draft'
    Assert-QualificationTest ((Get-Sha256 $statePath) -ceq $before) 'Rejected built-phase paths changed state.'

    function Build-Handoff {
        param([string]$SourceSha, [string]$CanonicalSha, [string]$CanonicalArchiveSha,
            [string]$EcosystemEvidenceSha, [switch]$VerifyOnly)
        $null = @($SourceSha, $CanonicalSha, $CanonicalArchiveSha, $EcosystemEvidenceSha)
        Assert-QualificationTest ([bool]$VerifyOnly) 'Qualification rebuilt the candidate.'
    }
    $script:summary = [ordered]@{
        schema = 'release-candidate-readiness-summary-v1'; repository = 'Furroxide/TopiaForge'
        releaseVersion = $Version; targetSha = $source; status = 'ready'
        decisionSha256 = $hash; acceptanceSha256 = $hash; handoffSha256 = $hash
        baseReadinessSha256 = $hash; baseSchemaSha256 = $hash; policySha256 = $hash
        catalogSha256 = $hash; contractSha256 = $hash
        payloads = @([ordered]@{ name = 'TopiaForge-windows-x64.zip'; size = 12; sha256 = $hash })
        gates = @([ordered]@{ id = 'P0-GAME-01'; status = 'approved'; reviewer = 'fixture-reviewer' })
    }
    $script:qualificationCalls = @()
    function Get-DartAndFlutter { return @{ Dart = 'fixture-dart'; Flutter = 'fixture-flutter' } }
    function Invoke-Checked {
        param([string]$FilePath, [string[]]$Arguments, [string]$WorkingDirectory, [switch]$Capture, [switch]$StandardOutputOnly)
        Assert-QualificationTest ($FilePath -ceq 'fixture-dart' -and $Capture -and $StandardOutputOnly) 'Qualification did not capture the CLI assessment.'
        Assert-QualificationTest ($WorkingDirectory -ceq (Join-Path $repositoryRoot 'apps/topiaforge_cli')) 'Wrong CLI working directory.'
        $script:qualificationCalls += ,$Arguments
        return ($script:summary | ConvertTo-Json -Depth 32 -Compress)
    }
    Invoke-Qualify | Out-Null
    $accepted = Read-State
    Assert-QualificationTest ($accepted.phase -ceq 'accepted') 'Qualification did not commit accepted state.'
    Assert-QualificationTest ($accepted.qualification.contractSha256 -ceq $hash -and $accepted.qualification.gates[0].reviewer -ceq 'fixture-reviewer') 'Qualification omitted contract or gate evidence.'
    Assert-QualificationTest ($accepted.canonicalSha256 -ceq $hash) 'Qualification dropped private build identity.'
    $expectedArguments = @('run', 'bin/topiaforge.dart', 'release', 'validate-readiness', '--version', $Version, '--target-sha', $source, '--assets', $assetsDirectory)
    Assert-QualificationTest ([string]::Join('|', $qualificationCalls[0]) -ceq [string]::Join('|', $expectedArguments)) 'Qualification did not bind the exact assets/source/version.'
    $acceptedBytes = Get-Sha256 $statePath
    Invoke-Qualify | Out-Null
    Assert-QualificationTest ((Get-Sha256 $statePath) -ceq $acceptedBytes) 'Matching qualification retry rewrote accepted state.'
    Assert-QualificationThrows { Invoke-Build } 'accepted|qualified'
    Assert-QualificationTest ((Get-Sha256 $statePath) -ceq $acceptedBytes) 'Rebuild refusal changed accepted state.'

    foreach ($field in @('decisionSha256', 'acceptanceSha256', 'handoffSha256', 'baseReadinessSha256', 'baseSchemaSha256', 'policySha256', 'catalogSha256', 'contractSha256')) {
        $script:summary[$field] = '3' * 64
        Assert-QualificationThrows { Invoke-Qualify } 'changed|match'
        Assert-QualificationThrows { Invoke-Stage } 'changed|match'
        Assert-QualificationThrows { Invoke-All } 'changed|match'
        Assert-QualificationTest ((Get-Sha256 $statePath) -ceq $acceptedBytes) "Changed $field mutated accepted state."
        $script:summary[$field] = $hash
    }
    foreach ($field in @('name', 'size', 'sha256')) {
        $original = $script:summary.payloads[0][$field]
        $script:summary.payloads[0][$field] = switch ($field) { name { 'changed.zip' } size { 13 } sha256 { '3' * 64 } }
        Assert-QualificationThrows { Invoke-Qualify } 'changed|match'
        $script:summary.payloads[0][$field] = $original
    }
    $script:summary.gates[0].reviewer = 'another-reviewer'
    Assert-QualificationThrows { Invoke-Qualify } 'changed|match'
    $script:summary.gates[0].reviewer = 'fixture-reviewer'
    foreach ($phase in @('staged', 'dispatch-requested', 'published')) {
        Write-State -Phase $phase -SourceSha $source -Additional (@{} + $buildFields + @{ qualification = $accepted.qualification })
        $script:summary.contractSha256 = '3' * 64
        Assert-QualificationThrows { Invoke-Dispatch } 'changed|match'
        $script:summary.contractSha256 = $hash
    }
    Write-State -Phase built -SourceSha $source -Additional $buildFields
    $before = Get-Sha256 $statePath
    foreach ($field in @('status', 'targetSha', 'releaseVersion', 'repository', 'schema')) {
        $original = $script:summary[$field]
        $script:summary[$field] = 'invalid'
        Assert-QualificationThrows { Invoke-Qualify } 'summary|assessment|candidate'
        Assert-QualificationTest ((Get-Sha256 $statePath) -ceq $before) 'Invalid assessment mutated built state.'
        $script:summary[$field] = $original
    }
    # The private-build assessment is a distinct command and never declares readiness.
    $readySummary = $script:summary
    $script:summary = [ordered]@{
        schema = 'release-prerequisites-summary-v1'; status = 'eligible-for-private-build'
        releaseVersion = $Version; targetSha = $source; baseReadinessSha256 = $hash
        gates = @([ordered]@{ id = 'P0-GAME-01'; status = 'blocked' })
        deferredGateIds = @('P0-GAME-01')
    }
    Get-ReleaseAssessment -SourceSha $source -Kind prerequisites | Out-Null
    $privateArguments = @('run', 'bin/topiaforge.dart', 'release', 'validate-prerequisites', '--version', $Version, '--target-sha', $source)
    Assert-QualificationTest ([string]::Join('|', $qualificationCalls[-1]) -ceq [string]::Join('|', $privateArguments)) 'Private build used the final qualification command.'
    $script:summary.status = 'ready'
    Assert-QualificationThrows { Get-ReleaseAssessment -SourceSha $source -Kind prerequisites } 'assessment'
    $script:summary.status = 'blocked'
    Assert-QualificationThrows { Get-ReleaseAssessment -SourceSha $source -Kind prerequisites } 'assessment'
    $script:summary = $readySummary

    # Property order is immaterial; changed values and array order remain material.
    Invoke-Qualify | Out-Null
    $acceptedBytes = Get-Sha256 $statePath
    $reordered = [ordered]@{}
    $summaryKeys = @($script:summary.Keys)
    [Array]::Reverse($summaryKeys)
    foreach ($key in $summaryKeys) { $reordered[$key] = $script:summary[$key] }
    $script:summary = $reordered
    Invoke-Qualify | Out-Null
    Assert-QualificationTest ((Get-Sha256 $statePath) -ceq $acceptedBytes) 'Property order changed acceptance identity.'
    Write-State -Phase built -SourceSha $source -Additional $buildFields
    $before = Get-Sha256 $statePath
    $temporary = New-Item -ItemType Directory -Path "$statePath.tmp"
    try {
        Assert-QualificationThrows { Invoke-Qualify } 'denied|directory|container'
        Assert-QualificationTest ((Get-Sha256 $statePath) -ceq $before) 'Interrupted qualification lost the prior built state.'
    } finally { Remove-Item -LiteralPath $temporary.FullName -Force }
    Invoke-Qualify | Out-Null
    Assert-QualificationTest ((Read-State).phase -ceq 'accepted') 'Qualification did not recover after interrupted state publication.'
    $script:Rehearsal = $true
    Write-State -Phase built -SourceSha $source -Additional $buildFields
    Assert-QualificationThrows { Invoke-Qualify } 'rehearsal'
    Assert-QualificationThrows { Invoke-Stage } 'qualify|rehearsal'
    Invoke-All
    Assert-QualificationTest ((Read-State).phase -ceq 'built') 'Rehearsal advanced toward publication.'
    # Detached decisions are fixed human-admin assets, with CMS remaining mode-dependent.
    function Get-ReleaseCatalogEntry { return [pscustomobject]@{ artifacts = @('fixture.zip') } }
    $script:policy = [pscustomobject]@{
        artifactPolicy = [pscustomobject]@{ platformArchives = @('TopiaForge-windows-x64.zip') }
        signingIdentities = [pscustomobject]@{ windowsDistribution = 'unsigned' }
    }
    New-Item -ItemType Directory -Path $assetsDirectory -Force | Out-Null
    $expectedAssets = @('fixture.zip', 'release-platform-bundle-v1-windows-x64.json', 'release-handoff-v1.json', 'release-candidate-readiness-v1.json', 'release-candidate-acceptance-v1.json')
    foreach ($name in $expectedAssets + @('unknown.json', 'release-handoff-v1.json.p7s')) {
        Set-Content -LiteralPath (Join-Path $assetsDirectory $name) -Value 'fixture' -NoNewline
    }
    $unsignedNames = @(Get-StagedAssetPaths | ForEach-Object { [System.IO.Path]::GetFileName($_) })
    Assert-QualificationTest ([string]::Join('|', $unsignedNames) -ceq [string]::Join('|', $expectedAssets)) 'Unsigned staging allowlist changed or omitted qualification.'
    $script:policy.signingIdentities.windowsDistribution = 'signed'
    $signedNames = @(Get-StagedAssetPaths | ForEach-Object { [System.IO.Path]::GetFileName($_) })
    Assert-QualificationTest ($signedNames.Count -eq 6 -and $signedNames -contains 'release-handoff-v1.json.p7s' -and $signedNames -notcontains 'unknown.json') 'Signed CMS requirement or exact staging allowlist was lost.'
    Remove-Item -LiteralPath (Join-Path $assetsDirectory 'release-candidate-acceptance-v1.json')
    Assert-QualificationThrows { Get-StagedAssetPaths } 'release-candidate-acceptance-v1.json'
    Write-Host 'release-admin qualification tests passed.'
} finally {
    $env:TOPIAFORGE_RELEASE_TEST_IMPORT = $oldImport
    if ([System.IO.Path]::GetDirectoryName($root) -cne [System.IO.Path]::TrimEndingDirectorySeparator([System.IO.Path]::GetTempPath())) { throw 'Unexpected owned test root.' }
    Remove-Item -LiteralPath $root -Recurse -Force
}
