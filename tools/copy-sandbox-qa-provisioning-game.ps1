#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$GameCopyReceipt,
    [Parameter(Mandatory)][string]$GameRoot,
    [Parameter(Mandatory)][string]$ReceiptPath
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$qaRoot = 'D:\TopiaForgeQA'
$sourceRoot = 'D:\TopiaForgeQA\source-game'
$expectedCount = 409
$expectedBytes = 5428015421L
$trustedReceiptSha256 = 'c6d577dfe8a8fb714a7dcb7dabe9f169e81fde2372d06e319c6a831fdd160118'
$privateRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\.dart_tool\rc1-review\qa-provisioning-20260909'))
$leases = [Collections.Generic.List[IO.FileStream]]::new()
$targetLeases = [Collections.Generic.List[IO.FileStream]]::new()
$result = $null
$receiptLease = $null
$verified = [Collections.Generic.List[object]]::new()

function Assert-SafeRelativePath([string]$Path) {
    if (!$Path -or $Path.Length -gt 240 -or $Path.Contains('\') -or $Path.StartsWith('/') -or $Path.EndsWith('/') -or $Path -match '[\x00-\x1f\x7f:*?"<>|]') { throw 'Unsafe relative game path.' }
    foreach ($segment in $Path.Split('/')) {
        if (!$segment -or $segment -in @('.','..') -or $segment -match '[ .]$' -or $segment -match '^(?i:CON|PRN|AUX|NUL|CLOCK\$|CONIN\$|CONOUT\$|COM[0-9¹²³]|LPT[0-9¹²³])(?:\.|$)') { throw 'Unsafe game path segment.' }
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
function New-ProtectedGameRoot([string]$Path,$CreatorSid,$QaSid) {
    $acl = [Security.AccessControl.DirectorySecurity]::new()
    $acl.SetAccessRuleProtection($true,$false)
    $acl.SetOwner($CreatorSid)
    $inherit = [Security.AccessControl.InheritanceFlags]'ContainerInherit,ObjectInherit'
    foreach ($sid in @($CreatorSid,[Security.Principal.SecurityIdentifier]::new('S-1-5-18'),[Security.Principal.SecurityIdentifier]::new('S-1-5-32-544'))) {
        $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($sid,[Security.AccessControl.FileSystemRights]::FullControl,$inherit,[Security.AccessControl.PropagationFlags]::None,[Security.AccessControl.AccessControlType]::Allow))
    }
    $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($QaSid,[Security.AccessControl.FileSystemRights]::Modify,$inherit,[Security.AccessControl.PropagationFlags]::None,[Security.AccessControl.AccessControlType]::Allow))
    if (!('SandboxFreshGameDirectory' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
public static class SandboxFreshGameDirectory {
    [StructLayout(LayoutKind.Sequential)] struct SecurityAttributes {
        public int Length; public IntPtr Descriptor;
        [MarshalAs(UnmanagedType.Bool)] public bool InheritHandle;
    }
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool CreateDirectoryW(string path, ref SecurityAttributes attributes);
    public static void CreateNew(string path, byte[] descriptor) {
        var pinned = GCHandle.Alloc(descriptor, GCHandleType.Pinned);
        try {
            var attributes = new SecurityAttributes { Length=Marshal.SizeOf<SecurityAttributes>(), Descriptor=pinned.AddrOfPinnedObject(), InheritHandle=false };
            if (!CreateDirectoryW(path, ref attributes)) throw new Win32Exception(Marshal.GetLastWin32Error());
        } finally { pinned.Free(); }
    }
}
'@
    }
    Assert-FreshGameRoot $Path
    [SandboxFreshGameDirectory]::CreateNew($Path,$acl.GetSecurityDescriptorBinaryForm())
    $result.rootCreated = $true
    $actual = Get-Acl -LiteralPath $Path
    if (!$actual.AreAccessRulesProtected -or $actual.GetOwner([Security.Principal.SecurityIdentifier]).Value -cne $CreatorSid.Value -or $actual.GetSecurityDescriptorSddlForm([Security.AccessControl.AccessControlSections]::Access) -cne $acl.GetSecurityDescriptorSddlForm([Security.AccessControl.AccessControlSections]::Access)) { throw 'Fresh root protected ACL verification failed.' }
}

# Invalid destinations and receipts fail before any directory or output is created.
Assert-FreshGameRoot $GameRoot
foreach ($path in @($sourceRoot,$qaRoot,$privateRoot,$GameCopyReceipt,$ReceiptPath)) { Assert-PhysicalPath $path }
if ($GameCopyReceipt -cne (Join-Path $privateRoot 'game-copy.json')) { throw 'Only the original approved game-copy receipt is accepted.' }
if ([IO.Path]::GetDirectoryName($ReceiptPath) -cne $privateRoot -or [IO.Path]::GetFileName($ReceiptPath) -cnotmatch '^game-provisioning-copy-[0-9]{8}T[0-9]{6}Z\.json$') { throw 'Result receipt must use a fresh timestamped name in the private QA record directory.' }
if (!(Test-Path -LiteralPath $privateRoot -PathType Container) -or (Test-Path -LiteralPath $ReceiptPath)) { throw 'Receipt directory missing or immutable result receipt already exists.' }
try {
    $result = [ordered]@{schemaVersion=1;kind='sandbox-qa-fresh-provisioning-game-copy-v1';startedAtUtc=[DateTime]::UtcNow.ToString('o');qaRoot=$qaRoot;sourceGameRoot=$sourceRoot;gameRoot=$GameRoot;approvedInventorySha256=$trustedReceiptSha256;rootCreated=$false;completed=$false;copiedFiles=0;copiedBytes=0L;gameExecuted=$false;isolationAdmitted=$false;qualifiesRelease=$false;errorType=$null;inventory=@()}
    # Reserve the unique receipt atomically before creating any destination content.
    $receiptLease = [IO.FileStream]::new($ReceiptPath,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::Read)
    $receiptSource = [IO.FileStream]::new($GameCopyReceipt,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
    $leases.Add($receiptSource)
    if ($receiptSource.Length -gt 1MB) { throw 'Original inventory receipt is unbounded.' }
    if ([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($receiptSource)).ToLowerInvariant() -cne $trustedReceiptSha256) { throw 'Original inventory receipt changed.' }
    $receiptSource.Position = 0
    $reader = [IO.StreamReader]::new($receiptSource,[Text.Encoding]::UTF8,$true,4096,$true)
    try { $copy = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
    if ($copy.schemaVersion -ne 1 -or $copy.kind -cne 'sandbox-qa-game-copy-v1' -or $copy.completed -isnot [bool] -or !$copy.completed -or $copy.qaRoot -cne $qaRoot -or $copy.fileCount -ne $expectedCount -or $copy.copiedFiles -ne $expectedCount -or $copy.sourceBytes -ne $expectedBytes -or @($copy.inventory).Count -ne $expectedCount) { throw 'Approved inventory identity mismatch.' }
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
