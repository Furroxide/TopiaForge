#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$GameCopyReceipt,
    [Parameter(Mandatory)][string]$GameRoot,
    [Parameter(Mandatory)][string]$ReceiptPath,
    [Parameter(Mandatory)][string]$PrivateEvidenceRoot,
    # Digest of the reviewed copy receipt. The source tree, file count and byte
    # total all come from that receipt, so no game build needs constants here.
    [Parameter(Mandatory)][ValidatePattern('^[a-f0-9]{64}$')][string]$TrustedReceiptSha256
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'sandbox/qa-game-copy.ps1')
$qaRoot = 'D:\TopiaForgeQA'
$sourceRoot = $null
$privateRoot = $PrivateEvidenceRoot
$leases = [Collections.Generic.List[IO.FileStream]]::new()
$targetLeases = [Collections.Generic.List[IO.FileStream]]::new()
$result = $null
$receiptLease = $null
$verified = [Collections.Generic.List[object]]::new()

function Assert-SafeRelativePath([string]$Path) {
    if (!$Path -or $Path.Length -gt 240 -or $Path.Contains('\') -or $Path.StartsWith('/') -or $Path.EndsWith('/') -or $Path -match '[\x00-\x1f\x7f:*?"<>|]') { throw 'Unsafe relative game path.' }
    foreach ($segment in $Path.Split('/')) {
        if (!$segment -or $segment -in @('.','..') -or $segment -match '[ .]$' -or $segment -match '^(?i:CON|PRN|AUX|NUL|CLOCK\$|CONIN\$|CONOUT\$|COM[0-9\u00B9\u00B2\u00B3]|LPT[0-9\u00B9\u00B2\u00B3])(?:\.|$)') { throw 'Unsafe game path segment.' }
    }
}
function Assert-PhysicalPath([string]$Path) {
    $full = [IO.Path]::GetFullPath($Path)
    if ($full -cne $Path -or $full -notmatch '^[A-Z]:\\' -or $full.Substring(3).Contains(':')) { throw 'Canonical local physical path required.' }
    if ($full.Length -gt 3) { Assert-SafeRelativePath ($full.Substring(3).Replace('\','/')) }
    $parent = $full
    while ($parent) {
        if (Test-Path -LiteralPath $parent) {
            if ((Get-Item -LiteralPath $parent -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Reparse points are forbidden.' }
        }
        $parent = [IO.Path]::GetDirectoryName($parent)
    }
}
function Assert-FreshGameRoot([string]$Path) {
    Assert-PhysicalPath $Path
    if ([IO.Path]::GetDirectoryName($Path) -cne 'D:\TopiaForgeQA' -or [IO.Path]::GetFileName($Path) -cnotmatch '^game-provisioning-[0-9]{8}T[0-9]{6}Z$') { throw 'Fresh provisioning game must be one named child of the QA root.' }
    $timestamp = [IO.Path]::GetFileName($Path).Substring('game-provisioning-'.Length)
    [void][DateTime]::ParseExact($timestamp,'yyyyMMddTHHmmssZ',[Globalization.CultureInfo]::InvariantCulture,[Globalization.DateTimeStyles]::AssumeUniversal)
    if (Test-Path -LiteralPath $Path) { throw 'Refusing an existing destination, including a previous partial copy.' }
}
function Assert-NoAlternateStreams([string]$Path) {
    foreach ($stream in @(Get-Item -LiteralPath $Path -Stream * -ErrorAction Stop)) {
        if ($stream.Stream -cne ':$DATA') { throw 'Alternate data streams are forbidden.' }
    }
}
function Get-ClosedTree([string]$Root) {
    Assert-PhysicalPath $Root
    if (!(Test-Path -LiteralPath $Root -PathType Container)) { throw 'Game tree root missing.' }
    $files = [Collections.Generic.Dictionary[string,string]]::new([StringComparer]::OrdinalIgnoreCase)
    $directories = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $pending = [Collections.Generic.Stack[string]]::new()
    $pending.Push($Root)
    while ($pending.Count) {
        $directory = $pending.Pop()
        Assert-PhysicalPath $directory
        Assert-NoAlternateStreams $directory
        foreach ($entry in [IO.Directory]::EnumerateFileSystemEntries($directory)) {
            $relative = [IO.Path]::GetRelativePath($Root,$entry).Replace('\','/')
            Assert-SafeRelativePath $relative
            if ($files.Count + $directories.Count -gt 1024) { throw 'Unbounded game tree.' }
            $attributes = [IO.File]::GetAttributes($entry)
            if ($attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Linked game content forbidden.' }
            if ($attributes -band [IO.FileAttributes]::Directory) {
                if (!$directories.Add($relative)) { throw 'Duplicate game directory.' }
                $pending.Push($entry)
            } else {
                Assert-NoAlternateStreams $entry
                $files.Add($relative,$entry)
            }
        }
    }
    return [pscustomobject]@{files=$files;directories=$directories}
}
function Assert-TreeMatches($Tree,$Rows,$Directories) {
    if ($Tree.files.Count -ne $Rows.Count -or $Tree.directories.Count -ne $Directories.Count) { throw 'Game tree has missing or extra content.' }
    foreach ($row in $Rows) {
        if (!$Tree.files.ContainsKey($row.path) -or [IO.Path]::GetRelativePath($sourceRoot,$Tree.files[$row.path]).Replace('\','/') -cne $row.path) { throw 'Game file identity or casing mismatch.' }
    }
    foreach ($directory in $Directories) { if (!$Tree.directories.Contains($directory)) { throw 'Unexpected game directory.' } }
}
function New-ProtectedGameRoot {
    [CmdletBinding(SupportsShouldProcess)]
    param([string]$Path,$CreatorSid,$QaSid)
    if (!$PSCmdlet.ShouldProcess($Path,'Create the protected fresh game root')) { throw 'Protected game root creation was not confirmed.' }
    Assert-FreshGameRoot $Path
    $expectedAccess = New-SandboxQaProtectedDirectory -Path $Path -CreatorSid $CreatorSid -QaSid $QaSid -QaRights Modify -Confirm:$false
    $result.rootCreated = $true
    Assert-SandboxQaProtectedDirectory -Path $Path -CreatorSid $CreatorSid -ExpectedAccessSddl $expectedAccess
}

# Invalid destinations and receipts fail before any directory or output is created.
Assert-FreshGameRoot $GameRoot
foreach ($path in @($qaRoot,$privateRoot,$GameCopyReceipt,$ReceiptPath)) { Assert-PhysicalPath $path }
if ([IO.Path]::GetDirectoryName($GameCopyReceipt) -cne $privateRoot -or !(Test-SandboxQaCopyReceiptName ([IO.Path]::GetFileName($GameCopyReceipt)))) { throw 'Only a reviewed game-copy receipt in the private QA record directory is accepted.' }
if ([IO.Path]::GetDirectoryName($ReceiptPath) -cne $privateRoot -or [IO.Path]::GetFileName($ReceiptPath) -cnotmatch '^game-provisioning-copy-[0-9]{8}T[0-9]{6}Z\.json$') { throw 'Result receipt must use a fresh timestamped name in the private QA record directory.' }
if (!(Test-Path -LiteralPath $privateRoot -PathType Container) -or (Test-Path -LiteralPath $ReceiptPath)) { throw 'Receipt directory missing or immutable result receipt already exists.' }
try {
    $result = [ordered]@{schemaVersion=1;kind='sandbox-qa-fresh-provisioning-game-copy-v1';startedAtUtc=[DateTime]::UtcNow.ToString('o');qaRoot=$qaRoot;sourceGameRoot=$null;buildId=$null;gameRoot=$GameRoot;approvedInventorySha256=$TrustedReceiptSha256;rootCreated=$false;completed=$false;copiedFiles=0;copiedBytes=0L;gameExecuted=$false;isolationAdmitted=$false;qualifiesRelease=$false;errorType=$null;inventory=@()}
    # Reserve the unique receipt atomically before creating any destination content.
    $receiptLease = [IO.FileStream]::new($ReceiptPath,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::Read)
    $receiptSource = [IO.FileStream]::new($GameCopyReceipt,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
    $leases.Add($receiptSource)
    if ($receiptSource.Length -gt 1MB) { throw 'Original inventory receipt is unbounded.' }
    if ([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($receiptSource)).ToLowerInvariant() -cne $TrustedReceiptSha256) { throw 'Reviewed inventory receipt changed.' }
    $receiptSource.Position = 0
    $reader = [IO.StreamReader]::new($receiptSource,[Text.Encoding]::UTF8,$true,4096,$true)
    try { $copy = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
    $approved = Resolve-SandboxQaCopyReceipt -Receipt $copy -QaRoot $qaRoot
    $sourceRoot = $approved.SourceGameRoot
    $expectedCount = $approved.FileCount
    $expectedBytes = $approved.SourceBytes
    $result.sourceGameRoot = $sourceRoot
    $result.buildId = $approved.BuildId
    Assert-PhysicalPath $sourceRoot
    $rows = @($copy.inventory)
    $names = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $directories = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $total = 0L
    foreach ($row in $rows) {
        if (@(Compare-Object @($row.PSObject.Properties.Name) @('path','length','sha256')).Count) { throw 'Unexpected inventory fields.' }
        Assert-SafeRelativePath $row.path
        if (!$names.Add($row.path) -or $row.sha256 -cnotmatch '^[a-f0-9]{64}$' -or ($row.length -isnot [long] -and $row.length -isnot [int]) -or $row.length -lt 0 -or $row.length -gt 2GB) { throw 'Invalid or duplicate inventory row.' }
        $total += $row.length
        $directory = [IO.Path]::GetDirectoryName($row.path.Replace('/','\'))
        while ($directory) { [void]$directories.Add($directory.Replace('\','/')); $directory = [IO.Path]::GetDirectoryName($directory) }
    }
    if ($total -ne $expectedBytes) { throw 'Approved inventory total mismatch.' }
    Assert-TreeMatches (Get-ClosedTree $sourceRoot) $rows $directories
    $qaUser = Get-LocalUser -Name 'TopiaForgeQA' -ErrorAction Stop
    $originalProvisioning = Get-Content -LiteralPath (Join-Path $privateRoot 'provisioning-attempt2.json') -Raw | ConvertFrom-Json
    if (!$qaUser.Enabled -or $qaUser.SID.Value -cne $originalProvisioning.userSid) { throw 'Existing QA account identity mismatch.' }
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    try { $creatorSid = $identity.User } finally { $identity.Dispose() }
    if ($creatorSid.Value -ceq $qaUser.SID.Value) { throw 'Preparation requires the existing creator account, not the QA account.' }
    $drive = [IO.DriveInfo]::new('D:\')
    if (!$drive.IsReady -or $drive.AvailableFreeSpace -lt ($expectedBytes + 10GB)) { throw 'Fresh copy requires its full size plus a 10 GiB reserve.' }
    $result.freeBytesBefore = $drive.AvailableFreeSpace
    $result.qaUserSid = $qaUser.SID.Value
    $result.creatorSid = $creatorSid.Value
    foreach ($row in $rows) {
        $path = Join-Path $sourceRoot $row.path.Replace('/','\')
        Assert-PhysicalPath $path
        $stream = [IO.FileStream]::new($path,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
        $leases.Add($stream)
        if ($stream.Length -ne $row.length -or [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)).ToLowerInvariant() -cne $row.sha256) { throw 'Source bytes differ from the approved inventory.' }
        $stream.Position = 0
    }
    Assert-TreeMatches (Get-ClosedTree $sourceRoot) $rows $directories
    Assert-PhysicalPath $qaRoot
    New-ProtectedGameRoot $GameRoot $creatorSid $qaUser.SID
    $result.rootCreated = $true
    $result.aclVerified = $true
    foreach ($directory in @($directories | Sort-Object Length)) {
        $path = Join-Path $GameRoot $directory.Replace('/','\')
        Assert-PhysicalPath $path
        [void][IO.Directory]::CreateDirectory($path)
    }
    $buffer = [byte[]]::new(1MB)
    for ($index = 0; $index -lt $rows.Count; $index++) {
        $row = $rows[$index]
        $source = $leases[$index + 1]
        $targetPath = Join-Path $GameRoot $row.path.Replace('/','\')
        Assert-PhysicalPath $targetPath
        $target = [IO.FileStream]::new($targetPath,[IO.FileMode]::CreateNew,[IO.FileAccess]::ReadWrite,[IO.FileShare]::Read)
        $targetLeases.Add($target)
        $copyHasher = [Security.Cryptography.IncrementalHash]::CreateHash([Security.Cryptography.HashAlgorithmName]::SHA256)
        try {
            while (($count = $source.Read($buffer,0,$buffer.Length)) -gt 0) {
                $target.Write($buffer,0,$count)
                $copyHasher.AppendData($buffer,0,$count)
            }
            $target.Flush($true)
            $sourceHash = [Convert]::ToHexString($copyHasher.GetHashAndReset()).ToLowerInvariant()
            $target.Position = 0
            $targetHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($target)).ToLowerInvariant()
            if ($target.Length -ne $row.length -or $sourceHash -cne $row.sha256 -or $targetHash -cne $row.sha256) { throw 'Copied bytes failed independent target verification.' }
            $verified.Add([ordered]@{path=$row.path;length=$row.length;sourceSha256=$sourceHash;targetSha256=$targetHash})
            $result.copiedFiles++
            $result.copiedBytes += $row.length
        } finally { $copyHasher.Dispose() }
    }
    $result.inventory = $verified.ToArray()
    Assert-TreeMatches (Get-ClosedTree $sourceRoot) $rows $directories
    $targetTree = Get-ClosedTree $GameRoot
    if ($targetTree.files.Count -ne $expectedCount -or $targetTree.directories.Count -ne $directories.Count) { throw 'Final target contains unexpected content.' }
    foreach ($row in $rows) { if (!$targetTree.files.ContainsKey($row.path)) { throw 'Final target file missing.' } }
    $result.completed = $true
} catch {
    if ($null -ne $result) { $result.errorType = $_.Exception.GetType().Name; $result.error = $_.Exception.Message }
} finally {
    foreach ($lease in $targetLeases) { $lease.Dispose() }
    foreach ($lease in $leases) { $lease.Dispose() }
    if ($null -ne $receiptLease) {
        $result.inventory = $verified.ToArray()
        $result.completedAtUtc = [DateTime]::UtcNow.ToString('o')
        $bytes = [Text.UTF8Encoding]::new($false).GetBytes(($result | ConvertTo-Json -Depth 7))
        try { $receiptLease.Write($bytes); $receiptLease.Flush($true) } finally { $receiptLease.Dispose() }
    }
}
if (!$result.completed) { throw 'Fresh QA copy failed; preserve its receipt and all partial content for review.' }
[pscustomobject]$result | Select-Object kind,gameRoot,completed,copiedFiles,copiedBytes,gameExecuted,isolationAdmitted,qualifiesRelease
