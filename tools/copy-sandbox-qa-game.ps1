#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$SourceGame,
    [Parameter(Mandatory)][string]$MetadataPath,
    [Parameter(Mandatory)][string]$QaRoot,
    [Parameter(Mandatory)][string]$ReceiptPath
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'sandbox/qa-game-copy.ps1')

# Copies the official install of the pinned Robotopia build into two new,
# build-named QA trees: source-game-<build> (QA read/execute) and game-<build>
# (QA modify). The official files manifest, whose digest the build metadata
# pins, is the exact inventory, so BepInEx, Doorstop configuration, winhttp.dll,
# launcher credentials, logs, saves and user profiles are never copied. Earlier
# copies, including the original source-game and game trees, stay untouched.
$source = [IO.Path]::GetFullPath($SourceGame).TrimEnd('\')
$qa = [IO.Path]::GetFullPath($QaRoot).TrimEnd('\')
if ($qa -cne $script:SandboxQaRoot) { throw 'Use the explicitly provisioned QA root.' }
foreach ($path in @($source, $qa)) {
    for ($cursor = $path; $cursor; $cursor = [IO.Path]::GetDirectoryName($cursor)) {
        if ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Source or QA path uses a reparse point.' }
    }
}
foreach ($entry in Get-ChildItem -LiteralPath $source -Force -Recurse) {
    if ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Source game tree contains a reparse point.' }
}
if (Test-Path -LiteralPath $ReceiptPath) { throw 'Refusing to overwrite a copy receipt.' }

# The release verifier checks the manifest digest and count against the pin,
# every listed file's size and digest, and the independently pinned executable.
$official = & (Join-Path (Split-Path $PSScriptRoot) 'tools/release/verify-robotopia-install.ps1') -GameDirectory $source -MetadataPath $MetadataPath | ConvertFrom-Json
if ($official.filesVerified -isnot [long] -or $official.filesManifestSha256 -cnotmatch '^[a-f0-9]{64}$' -or $official.gameExecutableSha256 -cnotmatch '^[a-f0-9]{64}$') { throw 'Official install verification did not complete.' }
$names = Get-SandboxQaBuildCopyNames ([long]$official.buildId)
$destinations = @((Join-Path $qa $names.SourceGame), (Join-Path $qa $names.Game))
foreach ($destination in $destinations) {
    if (Test-Path -LiteralPath $destination) { throw 'Refusing an existing build copy; earlier copies stay untouched.' }
}

# Bind the inventory to the same manifest bytes the verifier accepted.
$manifestBytes = [IO.File]::ReadAllBytes((Join-Path ([IO.Path]::GetDirectoryName($source)) 'filelist.json'))
if ([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($manifestBytes)).ToLowerInvariant() -cne $official.filesManifestSha256) { throw 'Files manifest changed after verification.' }
$manifest = [Text.UTF8Encoding]::new($false, $true).GetString($manifestBytes) | ConvertFrom-Json
$rows = @($manifest.files | ForEach-Object { [pscustomobject]@{ path = [string]$_.path; length = [long]$_.size; sha256 = [string]$_.sha256 } })
if ($rows.Count -ne $official.filesVerified) { throw 'Files manifest count changed after verification.' }
$totalBytes = [long]($rows | Measure-Object -Property length -Sum).Sum
$drive = [IO.DriveInfo]::new($qa.Substring(0, 3))
if (!$drive.IsReady -or $drive.AvailableFreeSpace -lt (2 * $totalBytes + 10GB)) { throw 'Insufficient measured disk space for both copies and a 10 GiB working reserve.' }

$qaUser = Get-LocalUser -Name 'TopiaForgeQA' -ErrorAction Stop
if (!$qaUser.Enabled) { throw 'The provisioned QA account must be enabled.' }
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
try { $creatorSid = $identity.User } finally { $identity.Dispose() }
if ($creatorSid.Value -ceq $qaUser.SID.Value) { throw 'Copy as the existing creator account, not the QA account.' }

$receipt = [ordered]@{
    schemaVersion = 2; kind = 'sandbox-qa-game-copy-v2'; startedAtUtc = [DateTime]::UtcNow.ToString('o')
    buildId = [long]$official.buildId; archiveSha256 = $official.archiveSha256
    filesManifestSha256 = $official.filesManifestSha256; gameExecutableSha256 = $official.gameExecutableSha256
    source = $source; qaRoot = $qa; sourceGameRoot = $destinations[0]; gameRoot = $destinations[1]
    completed = $false; fileCount = $rows.Count; sourceBytes = $totalBytes; copiedFiles = 0; copiedBytes = 0L
    rootsCreated = 0; freeBytesBefore = $drive.AvailableFreeSpace; error = ''
    scope = 'Exact-byte copies of the official files manifest pinned for this build; every file verified against its manifest digest in both copies; no game executed'
    inventory = @()
}
# Reserve the unique receipt before creating any destination content.
$receiptLease = [IO.FileStream]::new($ReceiptPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::Read)
$copied = [Collections.Generic.List[object]]::new()
try {
    $rights = @('ReadAndExecute', 'Modify')
    for ($index = 0; $index -lt $destinations.Count; $index++) {
        $expectedAccess = New-SandboxQaProtectedDirectory -Path $destinations[$index] -CreatorSid $creatorSid -QaSid $qaUser.SID -QaRights $rights[$index]
        $receipt.rootsCreated++
        Assert-SandboxQaProtectedDirectory -Path $destinations[$index] -CreatorSid $creatorSid -ExpectedAccessSddl $expectedAccess
    }
    $buffer = [byte[]]::new(1MB)
    foreach ($row in $rows) {
        $relative = $row.path.Replace('/', '\')
        $sourceStream = [IO.FileStream]::new((Join-Path $source $relative), [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
        $targets = [Collections.Generic.List[IO.FileStream]]::new()
        $hasher = [Security.Cryptography.IncrementalHash]::CreateHash([Security.Cryptography.HashAlgorithmName]::SHA256)
        try {
            foreach ($destination in $destinations) {
                $targetPath = Join-Path $destination $relative
                [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($targetPath))
                $targets.Add([IO.FileStream]::new($targetPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None))
            }
            while (($count = $sourceStream.Read($buffer, 0, $buffer.Length)) -gt 0) {
                foreach ($target in $targets) { $target.Write($buffer, 0, $count) }
                $hasher.AppendData($buffer, 0, $count)
            }
            $sourceHash = [Convert]::ToHexString($hasher.GetHashAndReset()).ToLowerInvariant()
            if ($sourceStream.Length -ne $row.length -or $sourceHash -cne $row.sha256) { throw "Source bytes differ from the official manifest: $($row.path)" }
            foreach ($target in $targets) {
                $target.Flush($true)
                $target.Position = 0
                if ($target.Length -ne $row.length -or [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($target)).ToLowerInvariant() -cne $row.sha256) { throw "Copied bytes failed verification: $($row.path)" }
            }
        } finally {
            $hasher.Dispose()
            foreach ($target in $targets) { $target.Dispose() }
            $sourceStream.Dispose()
        }
        $copied.Add([ordered]@{ path = $row.path; length = $row.length; sha256 = $row.sha256 })
        $receipt.copiedFiles = $copied.Count
        $receipt.copiedBytes += $row.length
    }
    # Closed trees: each copy holds exactly the manifest files and no link.
    $expectedFiles = [Collections.Generic.HashSet[string]]::new([string[]]@($rows | ForEach-Object path), [StringComparer]::Ordinal)
    foreach ($destination in $destinations) {
        $found = @(Get-ChildItem -LiteralPath $destination -Force -Recurse)
        if (@($found | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }).Count) { throw 'Copied tree contains a reparse point.' }
        $files = @($found | Where-Object { !$_.PSIsContainer } | ForEach-Object { [IO.Path]::GetRelativePath($destination, $_.FullName).Replace('\', '/') })
        if ($files.Count -ne $expectedFiles.Count -or @($files | Where-Object { !$expectedFiles.Contains($_) }).Count) { throw 'Copied tree has missing or extra files.' }
    }
    $receipt.completed = $true
} catch {
    $receipt.error = $_.Exception.Message
} finally {
    $receipt.inventory = $copied.ToArray()
    $receipt['recordedAtUtc'] = [DateTime]::UtcNow.ToString('o')
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes(($receipt | ConvertTo-Json -Depth 6))
    try { $receiptLease.Write($bytes); $receiptLease.Flush($true) } finally { $receiptLease.Dispose() }
}
if (!$receipt.completed) { throw 'QA game copy failed; preserve its receipt and any partial content for review.' }
[pscustomobject]$receipt | Select-Object kind, buildId, sourceGameRoot, gameRoot, completed, copiedFiles, copiedBytes
