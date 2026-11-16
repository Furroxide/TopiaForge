[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$source = Join-Path $PSScriptRoot "../release-admin.ps1"
$tokens = $null
$parseErrors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile(
    $source, [ref]$tokens, [ref]$parseErrors
)
if ($parseErrors.Count -ne 0) { throw "Release administrator source did not parse." }
$definition = $ast.Find({
        param($node)
        $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
        $node.Name -eq "Assert-ExactPublishedRelease"
    }, $true)
if ($null -eq $definition) { throw "Published-release verifier was not found." }
. ([scriptblock]::Create($definition.Extent.Text))

# Execute the production verifier against an in-memory GitHub response and catalog.
# No release API, native process, credential or working tree is accessed by the stubs.
$gitHubCli = "fixture-gh"
$Repository = "fixture/repository"
$repositoryRoot = "fixture-root"
$sourceSha = "a" * 40
$script:catalogPrerelease = $false
$script:published = $null
$script:resolved = $sourceSha
$script:catalogReads = 0

function Get-ReleaseCatalogEntry {
    $script:catalogReads++
    return [pscustomobject]@{ prerelease = $script:catalogPrerelease }
}

function Invoke-Checked {
    param([string]$Executable, [string[]]$Arguments, [switch]$Capture)
    if (-not $Capture) { throw "The verifier must capture its response." }
    if ($Executable -ceq $gitHubCli -and $Arguments.Count -eq 7 -and
        $Arguments[0] -ceq "release" -and $Arguments[1] -ceq "view" -and
        $Arguments[2] -ceq $tag -and $Arguments[3] -ceq "--repo" -and
        $Arguments[4] -ceq $Repository -and $Arguments[5] -ceq "--json" -and
        $Arguments[6] -ceq "tagName,isDraft,isImmutable,isPrerelease,targetCommitish") {
        return $script:published | ConvertTo-Json -Compress
    }
    if ($Executable -ceq "git" -and $Arguments[0] -ceq "-C" -and
        $Arguments[1] -ceq $repositoryRoot -and $Arguments[2] -ceq "rev-parse") {
        return $script:resolved
    }
    throw "Unexpected verifier call."
}

$passed = 0
foreach ($prerelease in @($false, $true)) {
    $tag = if ($prerelease) { "v0.2.0-rc.1" } else { "v0.2.0" }
    foreach ($fault in @("none", "channel", "draft", "mutable", "tag", "sha", "missing")) {
        $script:catalogPrerelease = $prerelease
        $script:catalogReads = 0
        $script:resolved = if ($fault -ceq "sha") { "b" * 40 } else { $sourceSha }
        $script:published = [ordered]@{
            tagName = if ($fault -ceq "tag") { "v0.3.0" } else { $tag }
            isDraft = $fault -ceq "draft"
            isImmutable = $fault -cne "mutable"
            isPrerelease = if ($fault -ceq "channel") { -not $prerelease } else { $prerelease }
            targetCommitish = $sourceSha
        }
        if ($fault -ceq "missing") { $script:published.Remove("isPrerelease") }
        $failure = $null
        try { Assert-ExactPublishedRelease -SourceSha $sourceSha }
        catch { $failure = $_.Exception.Message }
        if ($fault -ceq "none") {
            if ($null -ne $failure -or $script:catalogReads -ne 1) {
                throw "Matching catalog channel was refused or not consulted: $failure"
            }
        }
        else {
            $expected = switch ($fault) {
                "sha" { "frozen source SHA" }
                "missing" { "missing 'isPrerelease'" }
                default { "exact immutable release" }
            }
            if ($null -eq $failure -or -not $failure.Contains($expected)) {
                throw "Fault '$fault' for prerelease=$prerelease was not refused correctly: $failure"
            }
        }
        $passed++
    }
    foreach ($property in @("isDraft", "isImmutable", "isPrerelease")) {
        foreach ($malformed in @($null, "", "false", "true", 0, 1)) {
            $script:catalogPrerelease = $prerelease
            $script:resolved = $sourceSha
            $script:published = [ordered]@{
                tagName = $tag
                isDraft = $false
                isImmutable = $true
                isPrerelease = $prerelease
                targetCommitish = $sourceSha
            }
            $script:published[$property] = $malformed
            $failure = $null
            try { Assert-ExactPublishedRelease -SourceSha $sourceSha }
            catch { $failure = $_.Exception.Message }
            $expected = "'$property' must be a boolean"
            if ($null -eq $failure -or -not $failure.Contains($expected)) {
                throw "Malformed '$property' for prerelease=$prerelease was not refused correctly: $failure"
            }
            $passed++
        }
    }
}
Write-Host "Published release policy: $passed cases passed."
