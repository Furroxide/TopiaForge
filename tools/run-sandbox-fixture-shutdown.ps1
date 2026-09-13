#requires -Version 7.0
<#
Builds a one-scene Unity fixture player with the pinned Editor and runs the Windows broker's
--fixture-shutdown mode against it. This verifies only the headless launch/log/wait mechanism
(fixed -batchmode -nographics -noaudio flags plus one computed -logFile destination, original
process ownership, bounded 90-second wait, read-only diagnostics and Unity log retention).
It executes no game, admits no isolation and closes no gate.
#>
[CmdletBinding()]
param(
    [string]$EditorPath = 'C:\Program Files\Unity\Hub\Editor\6000.0.23f1\Editor\Unity.exe',
    [Parameter(Mandatory)][string]$BrokerExecutable,
    [string]$OutputDirectory,
    [ValidateRange(180, 1800)][int]$TimeoutSeconds = 900
)
$ErrorActionPreference = 'Stop'
$deadlineUtc = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
function Get-RemainingMilliseconds {
    $remaining = [Math]::Floor(($deadlineUtc - [DateTime]::UtcNow).TotalMilliseconds)
    if ($remaining -le 0) { throw 'Fixture verification total budget expired.' }
    return [int][Math]::Min([int]::MaxValue, $remaining)
}
function Assert-PhysicalAncestors([string]$path) {
    $current = $path
    while ($current) {
        if (Test-Path -LiteralPath $current) {
            if (((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Reparse point refused: $current" }
        }
        $parent = [IO.Path]::GetDirectoryName($current)
        if ($parent -eq $current) { break }
        $current = $parent
    }
}
function Hash([string]$path) { (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() }
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$runId = (Get-Date).ToUniversalTime().ToString('yyyyMMddTHHmmssZ') + '-' + [Guid]::NewGuid().ToString('N')
$output = if ($OutputDirectory) { [IO.Path]::GetFullPath($OutputDirectory) } else { Join-Path $repo ".dart_tool\sandbox-fixture-player\$runId" }
Assert-PhysicalAncestors $output
if (Test-Path -LiteralPath $output) { throw 'Fixture verification output must be new; existing output is never replaced.' }
$editor = Get-Item -LiteralPath $EditorPath
if ($editor.VersionInfo.ProductVersion -notlike '6000.0.23f1*') { throw 'Unity 6000.0.23f1 is required.' }
$broker = Get-Item -LiteralPath $BrokerExecutable
if ($broker.Name -cne 'TopiaForge.Acceptance.Windows.exe') { throw 'The Windows broker executable is required.' }
$scripts = @(
    (Join-Path $repo 'tools\sandbox\fixture-player\FixtureQuit.cs'),
    (Join-Path $repo 'tools\sandbox\fixture-player\Editor\FixturePlayerBuild.cs'))
foreach ($script in $scripts) { if (!(Test-Path -LiteralPath $script)) { throw "Fixture script missing: $script" } }
New-Item -ItemType Directory -Path $output | Out-Null
$project = Join-Path $output 'project'
$receipt = [ordered]@{
    schemaVersion = 1; kind = 'sandbox-fixture-shutdown-run-v1'; runId = $runId; startedAtUtc = [DateTime]::UtcNow.ToString('o')
    editorVersion = $editor.VersionInfo.ProductVersion; editorSha256 = (Hash $EditorPath); brokerSha256 = (Hash $BrokerExecutable)
    scripts = @($scripts | ForEach-Object { [ordered]@{ path = [IO.Path]::GetRelativePath($repo, $_).Replace([char]92, [char]47); sha256 = (Hash $_) } })
    steps = [ordered]@{}; status = 'started'; gameExecuted = $false; isolationAdmitted = $false; qualifiesRelease = $false
}
function Save-Receipt { $receipt | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $output 'fixture-shutdown-receipt.json') -Encoding utf8 }
function Invoke-Owned([string]$Executable, [string[]]$Arguments, [string]$Label, [string]$WorkingDirectory) {
    $start = New-Object Diagnostics.ProcessStartInfo
    $start.FileName = $Executable
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $start.WorkingDirectory = $WorkingDirectory
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in $Arguments) { [void]$start.ArgumentList.Add($argument) }
    # A short, explicit environment: no inherited toolchain PATH tail, no credentials, no launch-mode variables.
    $start.EnvironmentVariables.Clear()
    foreach ($name in @('SystemRoot','SystemDrive','WINDIR','COMSPEC','PATHEXT','TEMP','TMP','USERPROFILE','APPDATA','LOCALAPPDATA','PROGRAMDATA','ProgramFiles','ProgramFiles(x86)','CommonProgramFiles','USERNAME','USERDOMAIN','ALLUSERSPROFILE','PROGRAMW6432','HOMEDRIVE','HOMEPATH','NUMBER_OF_PROCESSORS','PROCESSOR_ARCHITECTURE','OS')) {
        $value = [Environment]::GetEnvironmentVariable($name)
        if ($value) { $start.EnvironmentVariables[$name] = $value }
    }
    $start.EnvironmentVariables['PATH'] = (Split-Path -Parent $EditorPath) + ';C:\Windows\System32;C:\Windows;C:\Windows\System32\Wbem'
    $start.EnvironmentVariables['UPM_CACHE_ROOT'] = Join-Path $output 'upm-cache'
    $process = New-Object Diagnostics.Process
    $process.StartInfo = $start
    $step = [ordered]@{ started = $false; exitCode = $null; forced = $false; exitConfirmed = $false }
    $receipt.steps[$Label] = $step
    try {
        $null = Get-RemainingMilliseconds
        if (!$process.Start()) { throw "$Label did not start." }
        $step.started = $true
        $stdout = $process.StandardOutput.ReadToEndAsync(); $stderr = $process.StandardError.ReadToEndAsync()
        if (!$process.WaitForExit((Get-RemainingMilliseconds))) { $step.forced = $true; $process.Kill($true); $process.WaitForExit(15000) | Out-Null }
        $step.exitCode = $process.ExitCode
        $step.exitConfirmed = $process.HasExited
        ($stdout.GetAwaiter().GetResult() + $stderr.GetAwaiter().GetResult()) | Set-Content -LiteralPath (Join-Path $output "$Label-console.log") -Encoding utf8
    } finally {
        if ($step.started -and !$process.HasExited) { $process.Kill($true); $process.WaitForExit(15000) | Out-Null }
        $step.exitConfirmed = $process.HasExited
        $process.Dispose()
        Save-Receipt
    }
    if ($step.forced -or $step.exitCode -ne 0) { throw "$Label failed with exit $($step.exitCode) (forced: $($step.forced)); see $Label-console.log and the Unity log." }
}
try {
    Save-Receipt
    Invoke-Owned $EditorPath @('-batchmode','-nographics','-quit','-createProject',$project,'-logFile',(Join-Path $output 'create.log')) 'create-project' $output
    Copy-Item -LiteralPath $scripts[0] -Destination (Join-Path $project 'Assets\FixtureQuit.cs')
    New-Item -ItemType Directory -Path (Join-Path $project 'Assets\Editor') | Out-Null
    Copy-Item -LiteralPath $scripts[1] -Destination (Join-Path $project 'Assets\Editor\FixturePlayerBuild.cs')
    Invoke-Owned $EditorPath @('-batchmode','-nographics','-quit','-projectPath',$project,'-executeMethod','TopiaForge.FixturePlayerBuild.Build','-logFile',(Join-Path $output 'build.log')) 'build-player' $output
    $player = Join-Path $project 'Build\FixturePlayer.exe'
    if (!(Test-Path -LiteralPath $player) -or !(Test-Path -LiteralPath (Join-Path $project 'Build\UnityPlayer.dll'))) { throw 'Fixture player was not produced.' }
    $receipt.player = [ordered]@{ path = $player; sha256 = (Hash $player); unityPlayerSha256 = (Hash (Join-Path $project 'Build\UnityPlayer.dll')) }
    $run = Join-Path $output 'run-1'
    $brokerFailure = $null
    try { Invoke-Owned $BrokerExecutable @('--fixture-shutdown', $player, $run) 'fixture-shutdown' (Split-Path -Parent $BrokerExecutable) }
    catch { $brokerFailure = $_.Exception.Message }
    $resultPath = Join-Path $run 'fixture-shutdown-result.json'
    if (!(Test-Path -LiteralPath $resultPath)) { throw 'Broker fixture result is missing.' }
    $result = Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json
    $receipt.result = [ordered]@{ path = $resultPath; sha256 = (Hash $resultPath); status = $result.status; exitedUnforced = $result.exitedUnforced
        exitCode = $result.exitCode; runtimeMilliseconds = $result.runtimeMilliseconds; playerLogBytes = $result.playerLog.bytes; playerLogSha256 = $result.playerLog.sha256 }
    if ($null -ne $brokerFailure) { $receipt.brokerFailure = $brokerFailure }
    $receipt.status = if ($null -eq $brokerFailure -and $result.status -ceq 'passed') { 'passed' } else { 'failed' }
} catch {
    $receipt.status = 'failed'
    $receipt.error = $_.Exception.Message
} finally {
    $receipt.completedAtUtc = [DateTime]::UtcNow.ToString('o')
    Save-Receipt
}
Write-Output "Fixture shutdown verification $($receipt.status): $output"
if ($receipt.status -cne 'passed') { exit 1 }
exit 0
