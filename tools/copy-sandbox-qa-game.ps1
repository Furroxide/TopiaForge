#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$SourceGame,
    [Parameter(Mandatory)][string]$QaRoot,
    [Parameter(Mandatory)][string]$ReceiptPath
)
$ErrorActionPreference = 'Stop'
$source = [IO.Path]::GetFullPath($SourceGame).TrimEnd('\')
$qa = [IO.Path]::GetFullPath($QaRoot).TrimEnd('\')
if ($qa -notmatch '^[A-Z]:\\TopiaForgeQA$') { throw 'Use the explicitly provisioned QA root.' }
if (!(Test-Path -LiteralPath (Join-Path $source 'Robotopia.exe') -PathType Leaf)) { throw 'Source game executable is absent.' }
foreach ($path in @($source, $qa)) {
    for ($cursor = $path; $cursor; $cursor = [IO.Path]::GetDirectoryName($cursor)) {
        if ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Source or QA path uses a reparse point.' }
    }
}
$destinations = @((Join-Path $qa 'source-game'), (Join-Path $qa 'game'))
foreach ($destination in $destinations) {
    if (!(Test-Path -LiteralPath $destination -PathType Container)) { throw 'Provision QA directories before copying.' }
    if (@(Get-ChildItem -LiteralPath $destination -Force).Count -ne 0) { throw 'Refusing to merge into a nonempty game directory.' }
}
if (Test-Path -LiteralPath $ReceiptPath) { throw 'Refusing to overwrite a copy receipt.' }
# Only game binary/resource roots are copied. BepInEx, Doorstop configuration,
# winhttp.dll, launcher credentials, logs, saves and user profiles are excluded.
$allowedFiles = @('Robotopia.exe', 'UnityPlayer.dll', 'UnityCrashHandler64.exe', 'crashpad_handler.exe', 'crashpad_wer.dll')
$allowedDirectories = @('Robotopia_Data', 'MonoBleedingEdge', 'D3D12')
$files = [Collections.Generic.List[IO.FileInfo]]::new()
foreach ($name in $allowedFiles) {
    $path = Join-Path $source $name
    if (Test-Path -LiteralPath $path -PathType Leaf) { $files.Add((Get-Item -LiteralPath $path)) }
}
foreach ($name in $allowedDirectories) {
    $path = Join-Path $source $name
    if (!(Test-Path -LiteralPath $path -PathType Container)) { throw "Required binary/resource directory absent: $name" }
    foreach ($entry in Get-ChildItem -LiteralPath $path -Force -Recurse) {
        if ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Game resource tree contains a reparse point.' }
        if (!$entry.PSIsContainer) {
            if ($entry.Extension -in @('.log', '.dmp', '.tmp')) { throw 'Unexpected diagnostic/temporary file in game binary tree; review it before copying.' }
            $files.Add($entry)
        }
    }
}
if ($files.Count -gt 20000) { throw 'Unexpectedly large game file inventory.' }
$totalBytes = ($files | Measure-Object -Property Length -Sum).Sum
$available = (Get-Volume -DriveLetter $qa.Substring(0, 1)).SizeRemaining
if ($available -lt (2 * $totalBytes + 10GB)) { throw 'Insufficient measured disk space for both copies and a 10 GiB working reserve.' }
$rows = [Collections.Generic.List[object]]::new()
$progress = [ordered]@{ schemaVersion = 1; kind = 'sandbox-qa-game-copy-v1'; source = $source; qaRoot = $qa; completed = $false; sourceBytes = $totalBytes; fileCount = $files.Count; copiedFiles = 0; error = ''; scope = 'Exact-byte copy of user-approved existing game binaries/resources; no independent vendor/archive authenticity claim'; inventory = @() }
$progress | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $ReceiptPath -Encoding utf8
try {
    foreach ($file in $files | Sort-Object FullName) {
        if ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Source binary became a reparse point.' }
        $relative = [IO.Path]::GetRelativePath($source, $file.FullName)
        if ($relative.StartsWith('..') -or [IO.Path]::IsPathRooted($relative)) { throw 'Source inventory escaped its root.' }
        $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        foreach ($destination in $destinations) {
            $target = Join-Path $destination $relative
            [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target)) | Out-Null
            [IO.File]::Copy($file.FullName, $target, $false)
            if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant() -ne $hash) { throw "Game copy mismatch: $relative" }
        }
        if ((Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant() -ne $hash) { throw "Source changed during copy: $relative" }
        $rows.Add([pscustomobject]@{ path = $relative.Replace('\', '/'); length = $file.Length; sha256 = $hash })
        $progress.copiedFiles = $rows.Count
    }
    $progress.completed = $true
} catch {
    $progress.error = $_.Exception.Message
    throw
} finally {
    $progress.inventory = @($rows.ToArray())
    $progress['recordedAtUtc'] = [DateTime]::UtcNow.ToString('o')
    $progress | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $ReceiptPath -Encoding utf8
}
