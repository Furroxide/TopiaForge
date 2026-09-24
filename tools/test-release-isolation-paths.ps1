[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'release/acceptance-isolation.ps1')
$passed = 0
$failures = [System.Collections.Generic.List[string]]::new()
$owned = [System.IO.Directory]::CreateTempSubdirectory('release-isolation-path-regression-').FullName
if ($IsWindows) {
    Add-Type -TypeDefinition @"
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
public static class ReleaseIsolationTestPathNames {
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    private static extern uint GetShortPathNameW(string path, StringBuilder output, uint capacity);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    private static extern uint GetLongPathNameW(string path, StringBuilder output, uint capacity);
    public static string Name(string path, bool useShort) {
        var text = new StringBuilder(32768);
        var count = useShort ? GetShortPathNameW(path, text, 32768) : GetLongPathNameW(path, text, 32768);
        if (count == 0 || count >= 32768) throw new Win32Exception(Marshal.GetLastWin32Error());
        return text.ToString();
    }
}
"@
    $owned = [ReleaseIsolationTestPathNames]::Name($owned, $false)
}
function Test-PathFence {
    param([string]$Name, [scriptblock]$Action)
    try { & $Action; $script:passed++; Write-Host "PASS $Name" }
    catch { $script:failures.Add("${Name}: $($_.Exception.Message)"); Write-Host "FAIL $Name`: $($_.Exception.Message)" }
}
function Assert-PathRefusal {
    param([string]$Record, [string]$Output)
    try { Assert-ReleaseIsolationRecordOutsideOutputs -RecordPath $Record -OutputDirectories @($Output) }
    catch {
        if ($_.Exception.Message -match 'output cleanup|path.*alias|links|reparse') { return }
        throw
    }
    throw 'A path alias/link bypassed the provisioning-record cleanup fence.'
}
try {
    $output = Join-Path $owned 'long-output-directory'
    $null = New-Item -ItemType Directory -Path $output
    $record = Join-Path $output 'provisioning-record.json'
    [System.IO.File]::WriteAllText($record, '{"fixture":"retained"}')
    $original = (Get-FileHash -LiteralPath $record).Hash
    Test-PathFence 'direct contained record is refused' { Assert-PathRefusal $record $output }
    Test-PathFence 'separate future output is permitted' {
        Assert-ReleaseIsolationRecordOutsideOutputs -RecordPath $record -OutputDirectories @((Join-Path $owned 'future/output'))
    }
    if ($IsWindows) {
        $shortOutput = [ReleaseIsolationTestPathNames]::Name($output, $true)
        $shortRecord = [ReleaseIsolationTestPathNames]::Name($record, $true)
        if ($shortOutput -ine $output -and $shortRecord -ine $record) {
            Test-PathFence 'short record cannot hide inside long output' { Assert-PathRefusal $shortRecord $output }
            Test-PathFence 'short output cannot hide its long record' { Assert-PathRefusal $record $shortOutput }
        }
        else { Write-Host 'SKIP Windows short-path aliases: this filesystem assigns none.' }
        foreach ($suffix in @('.', ' ')) {
            Test-PathFence "trailing '$suffix' output alias is refused" { Assert-PathRefusal $record ($output + $suffix) }
        }
        Test-PathFence 'extended namespace record cannot hide inside ordinary output' {
            Assert-PathRefusal ('\\?\' + $record) $output
        }
        Test-PathFence 'extended namespace output cannot hide its ordinary record' {
            Assert-PathRefusal $record ('\\?\' + $output)
        }
        Test-PathFence 'normal case equivalence is retained' {
            Assert-ReleaseIsolationRecordOutsideOutputs -RecordPath $record.ToUpperInvariant() -OutputDirectories @((Join-Path $owned 'SEPARATE'))
        }
    }
    $link = Join-Path $owned 'linked-output'
    $kind = if ($IsWindows) { 'Junction' } else { 'SymbolicLink' }
    $null = New-Item -ItemType $kind -Path $link -Target $output
    try {
        Test-PathFence 'linked output cannot hide its contained record' { Assert-PathRefusal $record $link }
        Test-PathFence 'future output beneath a link is refused' { Assert-PathRefusal $record (Join-Path $link 'future') }
    }
    finally { Remove-Item -LiteralPath $link -Force }
    if ((Get-FileHash -LiteralPath $record).Hash -cne $original) { throw 'Path checks altered the record bytes.' }
    Write-Host "Release isolation path tests: $passed passed, $($failures.Count) failed."
    if ($failures.Count) { throw ($failures -join "`n") }
}
finally {
    $resolved = [System.IO.Path]::GetFullPath($owned)
    if ([System.IO.Path]::GetFileName($resolved) -notlike 'release-isolation-path-regression-*') { throw 'Unexpected owned cleanup root.' }
    if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
