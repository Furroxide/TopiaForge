#Requires -Version 7.0
[CmdletBinding()]
param([Parameter(Mandatory)][string]$Request)
$ErrorActionPreference = 'Stop'
$broker = $null
$result = $null
$readLeases = [Collections.Generic.List[IO.FileStream]]::new()
$leaseBudget = [pscustomobject]@{bytes=0L}
function Physical([string]$Path) {
    $full = [IO.Path]::GetFullPath($Path)
    if ($full -cne $Path -or $full -notmatch '^[A-Z]:\\' -or $full.Substring(3).Contains(':')) { throw 'Nonphysical provisioning path.' }
    $parent = $full
    while ($parent) {
        if ((Test-Path -LiteralPath $parent) -and ((Get-Item -LiteralPath $parent -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Linked provisioning path.' }
        $parent = [IO.Path]::GetDirectoryName($parent)
    }
    return $full
}
function OpenReadLease([string]$Path, [long]$MaximumBytes, [string]$ExpectedSha256 = '') {
    [void](Physical $Path)
    $stream = [IO.FileStream]::new($Path,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
    $readLeases.Add($stream)
    if ($stream.Length -gt $MaximumBytes -or $stream.Length -gt (512MB - $leaseBudget.bytes)) { throw 'Provisioning input exceeds its read-lease bound.' }
    $leaseBudget.bytes += $stream.Length
    if ($ExpectedSha256) {
        if ($ExpectedSha256 -cnotmatch '^[a-f0-9]{64}$') { throw 'Invalid expected input digest.' }
        $actual = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)).ToLowerInvariant()
        if ($actual -cne $ExpectedSha256) { throw 'Provisioning input changed before acquiring its read lease.' }
        $stream.Position = 0
    }
    return $stream
}
function CompleteOriginalBroker($Process, $Result) {
    $Result.ownedBrokerExitConfirmed = $false
    try {
        $exited=$Process.HasExited
        if ($exited -isnot [bool]) { throw 'Original broker exit state is unavailable.' }
        if (!$exited) { $Result.brokerForceTerminated=$true; $Process.Kill(); [void]$Process.WaitForExit(5000) }
        $exited=$Process.HasExited
        if ($exited -isnot [bool]) { throw 'Original broker exit state is unavailable after cleanup.' }
        $Result.ownedBrokerExitConfirmed=$exited
    } catch {
        $Result.completed=$false
        $Result.cleanupErrorType=$_.Exception.GetType().Name
    } finally {
        try { $Process.Dispose() }
        catch { $Result.completed=$false; $Result.cleanupErrorType=$_.Exception.GetType().Name }
    }
}
function WriteNew([string]$Path, $Value) {
    [void](Physical $Path)
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes(($Value | ConvertTo-Json -Depth 8))
    if ($bytes.Length -gt 256KB) { throw 'Provisioning wrapper output is unbounded.' }
    $stream = [IO.File]::Open($Path,[IO.FileMode]::CreateNew)
    try { $stream.Write($bytes); $stream.Flush($true) } finally { $stream.Dispose() }
}
try {
    [void](OpenReadLease $Request 128KB)
    $requestValue = Get-Content -LiteralPath $Request -Raw | ConvertFrom-Json
    $fields = @('schemaVersion','kind','userSid','toolRoot','outputRoot','launchInputPath','launchInputSha256','brokerFiles','desktopHelperSha256','readyTimeoutSeconds')
    if (@(Compare-Object @($requestValue.PSObject.Properties.Name) $fields).Count -ne 0 -or $requestValue.schemaVersion -ne 1 -or $requestValue.kind -ne 'sandbox-runtime-probe-dispatch-v1') { throw 'Unexpected provisioning wrapper request.' }
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    try {
        if ($identity.User.Value -cne $requestValue.userSid -or ([Security.Principal.WindowsPrincipal]::new($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Runtime observation requires the intended standard QA account.' }
    } finally { $identity.Dispose() }
    $toolRoot = Physical $requestValue.toolRoot
    if ((Physical $PSScriptRoot) -cne $toolRoot) { throw 'Actual provisioning wrapper directory differs from its bound tool root.' }
    [void](OpenReadLease $PSCommandPath 128KB)
    $outputRoot = Physical $requestValue.outputRoot
    $launchPath = Physical $requestValue.launchInputPath
    # The launch input lives either in the read-only tool bundle or in this attempt's bound state root (a driver may generate it there).
    $launchParent = [IO.Path]::GetDirectoryName($launchPath)
    if (!$toolRoot.StartsWith('D:\TopiaForgeQA\tools\',[StringComparison]::OrdinalIgnoreCase) -or !$outputRoot.StartsWith('D:\TopiaForgeQA\state\',[StringComparison]::OrdinalIgnoreCase) -or ($launchParent -cne $toolRoot -and $launchParent -cne $outputRoot) -or !(Test-Path -LiteralPath $outputRoot -PathType Container)) { throw 'Provisioning roots are outside their approved layout.' }
    if ($requestValue.launchInputSha256 -cnotmatch '^[a-f0-9]{64}$') { throw 'Invalid provisioning launch input digest.' }
    [void](OpenReadLease $launchPath 4MB $requestValue.launchInputSha256)
    $timeout = $requestValue.readyTimeoutSeconds
    if (($timeout -isnot [int] -and $timeout -isnot [long]) -or $timeout -lt 30 -or $timeout -gt 540) { throw 'Invalid desktop wait budget.' }
    $names = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    if ($requestValue.brokerFiles.Count -lt 5 -or $requestValue.brokerFiles.Count -gt 512) { throw 'Unbounded broker runtime inventory.' }
    foreach ($file in $requestValue.brokerFiles) {
        if (@(Compare-Object @($file.PSObject.Properties.Name) @('name','sha256')).Count -ne 0 -or $file.name -cnotmatch '^[A-Za-z0-9][A-Za-z0-9._-]*\.(dll|exe|json|pdb)$' -or !$names.Add($file.name) -or $file.sha256 -cnotmatch '^[a-f0-9]{64}$') { throw 'Invalid broker runtime binding.' }
        $path = Physical (Join-Path $toolRoot $file.name)
        [void](OpenReadLease $path 128MB $file.sha256)
    }
    if (!$names.Contains('TopiaForge.Acceptance.Windows.exe') -or !$names.Contains('TopiaForge.Acceptance.Windows.dll')) { throw 'Provisioning broker is absent.' }
    $helper = Physical (Join-Path $PSScriptRoot 'qa-host-desktop.cs')
    if ($requestValue.desktopHelperSha256 -cnotmatch '^[a-f0-9]{64}$') { throw 'Invalid desktop guard digest.' }
    [void](OpenReadLease $helper 128KB $requestValue.desktopHelperSha256)
    $resultPath = Join-Path $outputRoot 'runtime-probe-result.json'
    foreach ($name in @('runtime-probe-result.json','probe-started.json','broker-console.json')) { if (Test-Path -LiteralPath (Join-Path $outputRoot $name)) { throw 'Immutable provisioning wrapper output exists.' } }
    $result = [ordered]@{schemaVersion=1;kind='sandbox-runtime-probe-wrapper-result-v1';startedAtUtc=[DateTime]::UtcNow.ToString('o');brokerStarted=$false;brokerExitCode=$null;ownedBrokerExitConfirmed=$true;brokerForceTerminated=$false;completed=$false;errorType=$null;cleanupErrorType=$null;gameExecutionDeterminedByNativeReceipt=$true;isolationAdmitted=$false;qualifiesRelease=$false}
    WriteNew (Join-Path $outputRoot 'probe-started.json') ([ordered]@{startedAtUtc=$result.startedAtUtc;processId=$PID;sessionId=[Diagnostics.Process]::GetCurrentProcess().SessionId;waitingForInteractiveDesktop=$true})
    Add-Type -Path $helper
    $watch = [Diagnostics.Stopwatch]::StartNew()
    while (![SandboxQaHostDesktop]::IsReady()) {
        if ($watch.Elapsed.TotalSeconds -ge $timeout) { throw [TimeoutException]::new('QA desktop did not become active before the deadline.') }
        Start-Sleep -Milliseconds 250
    }
    $start = [Diagnostics.ProcessStartInfo]::new((Join-Path $toolRoot 'TopiaForge.Acceptance.Windows.exe'))
    $start.UseShellExecute=$false; $start.CreateNoWindow=$true; $start.WorkingDirectory=$toolRoot
    $start.RedirectStandardOutput=$true; $start.RedirectStandardError=$true
    [void]$start.ArgumentList.Add('--observe-runtime'); [void]$start.ArgumentList.Add($launchPath)
    $broker = [Diagnostics.Process]::Start($start)
    $result.brokerStarted=$true; $result.ownedBrokerExitConfirmed=$false
    $stdout=$broker.StandardOutput.ReadToEndAsync(); $stderr=$broker.StandardError.ReadToEndAsync()
    if (!$broker.WaitForExit(240000)) { throw [TimeoutException]::new('Original provisioning broker exceeded its total budget.') }
    $result.brokerExitCode=$broker.ExitCode; $result.ownedBrokerExitConfirmed=$true
    $output=$stdout.GetAwaiter().GetResult(); $errors=$stderr.GetAwaiter().GetResult()
    if ($output.Length -gt 64KB -or $errors.Length -gt 64KB) { throw 'Broker console output exceeds its bound.' }
    WriteNew (Join-Path $outputRoot 'broker-console.json') ([ordered]@{stdout=$output;stderr=$errors})
    $result.completed=($broker.ExitCode -eq 0)
}
catch { if ($null -eq $result) { throw }; $result.errorType=$_.Exception.GetType().Name }
finally {
    if ($null -ne $broker) { CompleteOriginalBroker $broker $result }
    foreach ($lease in $readLeases) {
        try { $lease.Dispose() }
        catch {
            if ($null -ne $result) { $result.completed=$false; $result.cleanupErrorType=$_.Exception.GetType().Name }
        }
    }
    if ($null -ne $result) { $result.completedAtUtc=[DateTime]::UtcNow.ToString('o'); WriteNew $resultPath $result }
}
if (!$result.completed -or !$result.ownedBrokerExitConfirmed -or $result.brokerForceTerminated) { exit 1 }
