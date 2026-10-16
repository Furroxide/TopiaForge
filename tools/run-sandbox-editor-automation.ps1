#requires -Version 7.0
[CmdletBinding()]
param(
    [string]$EditorPath = 'C:\Program Files\Unity\Hub\Editor\6000.0.23f1\Editor\Unity.exe',
    [string]$OutputDirectory,
    [ValidateRange(60,1800)][int]$TimeoutSeconds = 600
)
$ErrorActionPreference = 'Stop'
$deadlineUtc = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
function Get-RemainingMilliseconds {
    $remaining = [Math]::Floor(($deadlineUtc - [DateTime]::UtcNow).TotalMilliseconds)
    if ($remaining -le 0) { throw 'Sandbox Editor total execution budget expired.' }
    return [int][Math]::Min([int]::MaxValue, $remaining)
}
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$runId = (Get-Date).ToUniversalTime().ToString('yyyyMMddTHHmmssZ') + '-' + [Guid]::NewGuid().ToString('N')
$scratchRoot = Join-Path $repo '.dart_tool\sandbox-editor'
$scratch = Join-Path $scratchRoot $runId
$output = if ($OutputDirectory) { [IO.Path]::GetFullPath($OutputDirectory) } else { Join-Path $scratch 'evidence' }
function Assert-PhysicalAncestors([string]$path) {
    $current = $path
    while ($current) {
        if (Test-Path -LiteralPath $current) {
            $item = Get-Item -LiteralPath $current -Force
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Reparse point refused: $current" }
        }
        $parent = [IO.Path]::GetDirectoryName($current)
        if ($parent -eq $current) { break }
        $current = $parent
    }
}
Assert-PhysicalAncestors $scratch
Assert-PhysicalAncestors $output
if (Test-Path -LiteralPath $output) { throw 'Evidence output must be new; existing output is never replaced.' }
$editor = Get-Item -LiteralPath $EditorPath
if ($editor.VersionInfo.ProductVersion -notlike '6000.0.23f1*') { throw 'Unity 6000.0.23f1 is required.' }
New-Item -ItemType Directory -Path $scratch -Force | Out-Null
New-Item -ItemType Directory -Path $output | Out-Null
$project = Join-Path $scratch 'tools\unity-ui-bundle'
$assemblies = Join-Path $scratch 'assemblies'
New-Item -ItemType Directory -Path $project,$assemblies -Force | Out-Null
$fixture = Join-Path $repo 'tests\TopiaForge.SandboxAutomation.Unity\TopiaForge.SandboxAutomation.Unity.csproj'
$buildStart = [Diagnostics.ProcessStartInfo]::new()
$buildStart.FileName = (Get-Command dotnet -CommandType Application | Select-Object -First 1).Source
$buildStart.UseShellExecute = $false; $buildStart.CreateNoWindow = $true
$buildStart.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
$buildStart.RedirectStandardOutput = $true; $buildStart.RedirectStandardError = $true
foreach ($argument in @('build',$fixture,'-c','Release','--nologo','-v','minimal','-nodeReuse:false','-p:UseSharedCompilation=false')) { $buildStart.ArgumentList.Add($argument) }
$build = [Diagnostics.Process]::new(); $build.StartInfo = $buildStart
$buildStarted = $false
try {
    $null = Get-RemainingMilliseconds
    if (!$build.Start()) { throw 'Fixture build did not start.' }
    $buildStarted = $true
    $buildOutput = $build.StandardOutput.ReadToEndAsync(); $buildErrors = $build.StandardError.ReadToEndAsync()
    if (!$build.WaitForExit((Get-RemainingMilliseconds))) { $build.Kill($true); $build.WaitForExit(15000) | Out-Null; throw 'Fixture build exceeded the total execution budget.' }
    ($buildOutput.GetAwaiter().GetResult() + $buildErrors.GetAwaiter().GetResult()) | Set-Content -LiteralPath (Join-Path $output 'fixture-build.log') -Encoding utf8
    if ($build.ExitCode -ne 0) { throw 'Fixture build failed; inspect the private fixture-build.log.' }
} finally {
    if ($buildStarted -and !$build.HasExited) { $build.Kill($true); $build.WaitForExit(15000) | Out-Null }
    $build.Dispose()
}
foreach ($folder in @('Assets','Packages','ProjectSettings')) {
    Copy-Item -LiteralPath (Join-Path $repo "tools\unity-ui-bundle\$folder") -Destination $project -Recurse
}
$fixtureOutput = Join-Path $repo 'tests\TopiaForge.SandboxAutomation.Unity\bin\Release\netstandard2.1'
Get-ChildItem -LiteralPath $fixtureOutput -Filter '*.dll' | Where-Object { $_.Name -notlike 'Unity*' } |
    Copy-Item -Destination $assemblies
$referenceProperty = & dotnet msbuild $fixture -getProperty:RobotopiaManagedDir -nologo
if ($LASTEXITCODE -ne 0) { throw 'Cannot resolve the existing local Unity input reference.' }
Copy-Item -LiteralPath (Join-Path ($referenceProperty | Select-Object -Last 1) 'Unity.InputSystem.dll') -Destination $assemblies
# Preserve the unchanged UiLifecycleSmoke's expected root layout, using staged bytes.
foreach ($name in @('TopiaForge.Mods.Abstractions','TopiaForge.Mods.Worlds','TopiaForge.Mods.UnityUi')) {
    $target = Join-Path $scratch "src\$name\bin\Release\netstandard2.1"
    New-Item -ItemType Directory -Path $target -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $assemblies "$name.dll") -Destination $target
}
$loader = Join-Path $scratch 'src\TopiaForge.ModManager\bin\Release\netstandard2.1'
New-Item -ItemType Directory -Path $loader -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $assemblies 'TopiaForge.ModManager.Core.dll') -Destination $loader
foreach ($name in @('System.Collections.Immutable.dll','System.Reflection.Metadata.dll')) {
    Copy-Item -LiteralPath (Join-Path $repo "src\TopiaForge.ModManager\bin\Release\netstandard2.1\$name") -Destination $loader
}
$managed = Join-Path $scratch 'managed-refs'
New-Item -ItemType Directory -Path $managed | Out-Null
foreach ($name in @('System.Buffers.dll','System.Memory.dll','System.Runtime.CompilerServices.Unsafe.dll','Unity.InputSystem.dll')) {
    Copy-Item -LiteralPath (Join-Path ($referenceProperty | Select-Object -Last 1) $name) -Destination $managed
}
$revision = (& git -C $repo rev-parse HEAD).Trim()
$sourcePaths = @(& git -C $repo ls-files --cached --others --exclude-standard 'mods/TopiaForge.Sandbox/CreatorTools' 'src/TopiaForge.ModManager/UnityUi*' 'src/TopiaForge.ModManager/UnityMainThreadGuard.cs' 'src/TopiaForge.Mods.UnityUi' 'src/TopiaForge.Mods.Testing' 'tests/TopiaForge.SandboxAutomation.Unity' 'tools/unity-ui-bundle' 'tools/run-sandbox-editor-automation.ps1')
$inventory = @($sourcePaths | Sort-Object -Unique | ForEach-Object { if (Test-Path -LiteralPath (Join-Path $repo $_) -PathType Leaf) {
    [ordered]@{path=$_;sha256=(Get-FileHash -LiteralPath (Join-Path $repo $_) -Algorithm SHA256).Hash.ToLowerInvariant()}
}})
$receipt = [ordered]@{schemaVersion=1;kind='sandbox-editor-run-v1';runId=$runId;sourceRevision=$revision;
    sourceDirty=([bool](& git -C $repo status --porcelain));sourceInventory=$inventory;
    editorVersion=$editor.VersionInfo.ProductVersion;editorSha256=(Get-FileHash -LiteralPath $EditorPath -Algorithm SHA256).Hash.ToLowerInvariant();
    assemblyInventory=@(Get-ChildItem -LiteralPath $assemblies -Filter '*.dll' | Sort-Object Name | ForEach-Object {
        [ordered]@{name=$_.Name;sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()}});
    status='started';qualifiesRelease=$false;inputMode='synthetic-unity-eventsystem';inputHeld=$false;cleanupConfirmed=$false;timedOut=$false;editorExitCode=$null}
$receipt | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $output 'runner-summary.json') -Encoding utf8
$arguments = @('-batchmode','-projectPath',$project,'-executeMethod','TopiaForge.SandboxEditorAutomation.Run',
    '-topiaforgeSandboxAssemblies',$assemblies,'-topiaforgeSandboxEvidence',$output,
    '-screen-width','1920','-screen-height','1080','-logFile',(Join-Path $output 'editor.log'))
$start = New-Object Diagnostics.ProcessStartInfo
$start.FileName = $EditorPath
$start.UseShellExecute = $false
$start.CreateNoWindow = $true
$start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
$start.Arguments = ($arguments | ForEach-Object { '"' + $_.Replace('"','\"') + '"' }) -join ' '
$start.EnvironmentVariables.Clear()
foreach ($name in @('SystemRoot','SystemDrive','WINDIR','COMSPEC','PATH','PATHEXT','TEMP','TMP','USERPROFILE','APPDATA','LOCALAPPDATA','PROGRAMDATA','ProgramFiles','ProgramFiles(x86)','CommonProgramFiles','USERNAME','USERDOMAIN','ALLUSERSPROFILE','PROGRAMW6432','HOMEDRIVE','HOMEPATH','USERDOMAIN_ROAMINGPROFILE','NUMBER_OF_PROCESSORS','PROCESSOR_ARCHITECTURE','OS')) {
    $value = [Environment]::GetEnvironmentVariable($name)
    if ($value) { $start.EnvironmentVariables[$name] = $value }
}
$start.EnvironmentVariables['PATH'] = (Split-Path -Parent $EditorPath) + ';C:\Windows\System32;C:\Windows;C:\Windows\System32\Wbem'
$start.EnvironmentVariables['UPM_CACHE_ROOT'] = Join-Path $scratch 'upm-cache'
$process = New-Object Diagnostics.Process
$process.StartInfo = $start
$started = $false
try {
    $null = Get-RemainingMilliseconds
    if (!$process.Start()) { throw 'Editor failed to start.' }
    $started = $true
    $receipt.editorProcessId = $process.Id
    $receipt.editorStartUtc = $process.StartTime.ToUniversalTime().ToString('o')
    if (!$process.WaitForExit((Get-RemainingMilliseconds))) {
        $receipt.timedOut = $true
        $process.Kill()
        $process.WaitForExit(15000) | Out-Null
    }
    $receipt.editorExitCode = $process.ExitCode
    $receipt.cleanupConfirmed = $process.HasExited
    $receipt.status = if ($process.ExitCode -eq 0 -and !$receipt.timedOut -and (Test-Path -LiteralPath (Join-Path $output 'editor-observations.json'))) { 'passed' } else { 'failed' }
} finally {
    if ($started -and !$process.HasExited) { $process.Kill(); $process.WaitForExit(15000) | Out-Null }
    $receipt | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $output 'runner-summary.json') -Encoding utf8
    $process.Dispose()
}
if ($receipt.status -eq 'passed') {
    $smokePath = Join-Path $output 'ui-lifecycle-observations.json'
    $smokeArguments = @('-batchmode','-nographics','-projectPath',$project,'-executeMethod','TopiaForge.UiLifecycleSmoke.Run',
        '-robotopiaManagedDir',$managed,'-topiaforgeLifecycleEvidence',$smokePath,'-logFile',(Join-Path $output 'ui-lifecycle-editor.log'))
    $start.Arguments = ($smokeArguments | ForEach-Object { '"' + $_.Replace('"','\"') + '"' }) -join ' '
    $smoke = New-Object Diagnostics.Process
    $smoke.StartInfo = $start
    $smokeStarted = $false
    $receipt.existingUiSmoke = [ordered]@{status='started';cycles=0;exitCode=$null;processExitConfirmed=$false}
    try {
        $null = Get-RemainingMilliseconds
        if (!$smoke.Start()) { throw 'Separate UI lifecycle Editor failed to start.' }
        $smokeStarted = $true
        if (!$smoke.WaitForExit((Get-RemainingMilliseconds))) { $smoke.Kill(); $smoke.WaitForExit(15000) | Out-Null }
        $receipt.existingUiSmoke.exitCode = $smoke.ExitCode
        $receipt.existingUiSmoke.processExitConfirmed = $smoke.HasExited
        if ($smoke.ExitCode -ne 0 -or !(Test-Path -LiteralPath $smokePath)) { throw 'Separate UI lifecycle smoke failed.' }
        $smokeResult = Get-Content -LiteralPath $smokePath -Raw | ConvertFrom-Json
        if ($smokeResult.result -ne 'pass' -or $smokeResult.cycles -ne 16) { throw 'Separate UI lifecycle smoke result mismatch.' }
        $receipt.existingUiSmoke.status = 'passed'
        $receipt.existingUiSmoke.cycles = 16
    } catch {
        $receipt.existingUiSmoke.status = 'failed'
        $receipt.status = 'failed'
        Write-Warning $_.Exception.Message
    } finally {
        if ($smokeStarted -and !$smoke.HasExited) { $smoke.Kill(); $smoke.WaitForExit(15000) | Out-Null }
        $receipt.cleanupConfirmed = $receipt.cleanupConfirmed -and (!$smokeStarted -or $smoke.HasExited)
        $smoke.Dispose()
        $receipt | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $output 'runner-summary.json') -Encoding utf8
    }
}
Write-Output "Sandbox Editor $($receipt.status): $output"
if ($receipt.status -ne 'passed') { exit 1 }
exit 0
