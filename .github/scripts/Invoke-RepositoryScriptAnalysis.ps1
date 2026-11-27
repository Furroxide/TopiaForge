param(
    [Parameter(Mandatory)]
    [string] $Manifest,

    [Parameter(Mandatory)]
    [version] $ExpectedVersion,

    [string] $Path = 'tools'
)

$ErrorActionPreference = 'Stop'
$module = Import-Module -Name $Manifest -PassThru -ErrorAction Stop
if ($module.Version -ne $ExpectedVersion) {
    throw "Expected PSScriptAnalyzer $ExpectedVersion, imported $($module.Version)."
}
$analyze = $module.ExportedCommands['Invoke-ScriptAnalyzer']
if (-not $analyze) {
    throw 'The verified PSScriptAnalyzer module does not export Invoke-ScriptAnalyzer.'
}
$settings = Join-Path $PSScriptRoot '../PSScriptAnalyzerSettings.psd1'
$findings = @(& $analyze -Path $Path -Recurse -Settings $settings -ErrorAction Stop)
$findings | Format-Table -AutoSize
if ($findings.Count -ne 0) { exit 1 }
exit 0
