#Requires -Version 7.0
[CmdletBinding()]
param([Parameter(Mandatory)][string]$Request)
$ErrorActionPreference = 'Stop'
$probe = $null
$receipt = $null
$resultPath = $null
function RequirePhysical([string]$Path) {
    $full = [IO.Path]::GetFullPath($Path)
    if ($full -cne $Path -or $full -notmatch '^[A-Z]:\\' -or $full.Substring(3).Contains(':')) { throw 'Nonphysical QA path.' }
    $parent = $full
    while ($parent) {
        if ((Test-Path -LiteralPath $parent) -and ((Get-Item -LiteralPath $parent -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Linked QA path.' }
        $parent = [IO.Path]::GetDirectoryName($parent)
    }
    return $full
}
function WriteNew([string]$Path, $Value) {
    [void](RequirePhysical $Path)
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes(($Value | ConvertTo-Json -Depth 12))
    if ($bytes.Length -gt 256KB) { throw 'Probe output exceeds its bound.' }
    $stream = [IO.File]::Open($Path, [IO.FileMode]::CreateNew)
    try { $stream.Write($bytes); $stream.Flush($true) } finally { $stream.Dispose() }
}
try {
    [void](RequirePhysical $Request)
    if ((Get-Item -LiteralPath $Request).Length -gt 64KB) { throw 'Unbounded host-probe request.' }
    $inputRecord = Get-Content -LiteralPath $Request -Raw | ConvertFrom-Json
    $expectedFields = @('schemaVersion','kind','userSid','toolRoot','outputRoot','brokerFiles','desktopHelperSha256','timeoutSeconds')
    if (@(Compare-Object @($inputRecord.PSObject.Properties.Name) $expectedFields).Count -ne 0 -or $inputRecord.schemaVersion -ne 1 -or $inputRecord.kind -ne 'sandbox-qa-host-probe-request-v1') { throw 'Unexpected host-probe request.' }
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    try {
        if ($identity.User.Value -cne $inputRecord.userSid -or ([Security.Principal.WindowsPrincipal]::new($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Probe requires the intended standard QA account.' }
    } finally { $identity.Dispose() }
    $toolRoot = RequirePhysical $inputRecord.toolRoot
    $outputRoot = RequirePhysical $inputRecord.outputRoot
    if (!$toolRoot.StartsWith('D:\TopiaForgeQA\tools\', [StringComparison]::OrdinalIgnoreCase) -or !$outputRoot.StartsWith('D:\TopiaForgeQA\state\', [StringComparison]::OrdinalIgnoreCase) -or !(Test-Path -LiteralPath $outputRoot -PathType Container)) { throw 'Probe roots are outside the approved QA layout.' }
    $timeout = $inputRecord.timeoutSeconds
    if (($timeout -isnot [int] -and $timeout -isnot [long]) -or $timeout -lt 30 -or $timeout -gt 540) { throw 'Invalid probe time budget.' }
    $allowed = @('TopiaForge.Acceptance.Windows.exe','TopiaForge.Acceptance.Windows.dll','TopiaForge.Acceptance.Windows.deps.json','TopiaForge.Acceptance.Windows.runtimeconfig.json','TopiaForge.Acceptance.Windows.pdb')
    if (@(Compare-Object @($inputRecord.brokerFiles.name) $allowed).Count -ne 0 -or $inputRecord.brokerFiles.Count -ne 5) { throw 'Unexpected broker runtime inventory.' }
    foreach ($file in $inputRecord.brokerFiles) {
        if (@(Compare-Object @($file.PSObject.Properties.Name) @('name','sha256')).Count -ne 0 -or $file.sha256 -cnotmatch '^[a-f0-9]{64}$') { throw 'Invalid broker file binding.' }
        $path = RequirePhysical (Join-Path $toolRoot $file.name)
        if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -cne $file.sha256) { throw 'Broker runtime changed.' }
    }
    $helper = RequirePhysical (Join-Path $PSScriptRoot 'qa-host-desktop.cs')
    if ($inputRecord.desktopHelperSha256 -cnotmatch '^[a-f0-9]{64}$' -or (Get-FileHash -LiteralPath $helper -Algorithm SHA256).Hash.ToLowerInvariant() -cne $inputRecord.desktopHelperSha256) { throw 'Desktop metadata helper changed.' }
    $resultPath = Join-Path $outputRoot 'host-probe-result.json'
    foreach ($name in @('host-probe-result.json','host-observation.json','desktop-observation.json','probe-stdout.txt','probe-stderr.txt','probe-started.json')) {
        if (Test-Path -LiteralPath (Join-Path $outputRoot $name)) { throw 'Immutable probe output already exists.' }
    }
    $receipt = [ordered]@{schemaVersion=1;kind='sandbox-qa-host-probe-result-v1';startedAtUtc=[DateTime]::UtcNow.ToString('o');completed=$false;waitingForInteractiveDesktop=$true;probeExitCode=$null;ownedProcessExitConfirmed=$true;errorType=$null;microphoneOpened=$false;audioCaptured=$false;gameExecuted=$false;isolationAdmitted=$false;qualifiesRelease=$false}
    WriteNew (Join-Path $outputRoot 'probe-started.json') ([ordered]@{startedAtUtc=$receipt.startedAtUtc;processId=$PID;sessionId=[Diagnostics.Process]::GetCurrentProcess().SessionId;waitingForInteractiveDesktop=$true})
    Add-Type -Path $helper
    $watch = [Diagnostics.Stopwatch]::StartNew()
    while (![SandboxQaHostDesktop]::IsReady()) {
        if ($watch.Elapsed.TotalSeconds -ge $timeout) { throw [TimeoutException]::new('QA desktop was not unlocked before the probe deadline.') }
        Start-Sleep -Milliseconds 250
    }
    $receipt.waitingForInteractiveDesktop = $false
    WriteNew (Join-Path $outputRoot 'desktop-observation.json') ([ordered]@{schemaVersion=1;kind='sandbox-qa-desktop-observation-v1';observedAtUtc=[DateTime]::UtcNow.ToString('o');devices=[SandboxQaHostDesktop]::Describe();isolationAdmitted=$false})
    $info = [Diagnostics.ProcessStartInfo]::new((Join-Path $toolRoot 'TopiaForge.Acceptance.Windows.exe'))
    $info.UseShellExecute = $false; $info.CreateNoWindow = $true; $info.WorkingDirectory = $toolRoot
    $info.RedirectStandardOutput = $true; $info.RedirectStandardError = $true
    [void]$info.ArgumentList.Add('--probe'); [void]$info.ArgumentList.Add((Join-Path $outputRoot 'host-observation.json'))
    $probe = [Diagnostics.Process]::Start($info)
    $receipt.ownedProcessExitConfirmed = $false
    $stdout = $probe.StandardOutput.ReadToEndAsync(); $stderr = $probe.StandardError.ReadToEndAsync()
    if (!$probe.WaitForExit(30000)) { throw [TimeoutException]::new('The owned host probe exceeded its deadline.') }
    $receipt.ownedProcessExitConfirmed = $true; $receipt.probeExitCode = $probe.ExitCode
    foreach ($item in @(@('probe-stdout.txt',$stdout.GetAwaiter().GetResult()),@('probe-stderr.txt',$stderr.GetAwaiter().GetResult()))) {
        if ($item[1].Length -gt 64KB) { throw 'Probe console output exceeds its bound.' }
        $path = Join-Path $outputRoot $item[0]
        $stream = [IO.File]::Open($path,[IO.FileMode]::CreateNew)
        try { $bytes = [Text.UTF8Encoding]::new($false).GetBytes($item[1]); $stream.Write($bytes); $stream.Flush($true) } finally { $stream.Dispose() }
    }
    if ($probe.ExitCode -ne 0 -or ![SandboxQaHostDesktop]::IsReady()) { throw 'Native host observation failed or QA session changed.' }
    $receipt.completed = $true
}
catch {
    if ($null -eq $receipt) { throw }
    $receipt.errorType = $_.Exception.GetType().Name
}
finally {
    if ($null -ne $probe) {
        try { if (!$probe.HasExited) { $probe.Kill(); [void]$probe.WaitForExit(5000) }; $receipt.ownedProcessExitConfirmed = $probe.HasExited } finally { $probe.Dispose() }
    }
    if ($null -ne $receipt) { $receipt.completedAtUtc = [DateTime]::UtcNow.ToString('o'); WriteNew $resultPath $receipt }
}
if (!$receipt.completed -or !$receipt.ownedProcessExitConfirmed) { exit 1 }
