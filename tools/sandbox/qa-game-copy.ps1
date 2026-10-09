# Rules shared by the QA game-copy, fresh-provisioning-copy and firewall scripts.
# Name and receipt rules are pure and platform-neutral so CI can test them; only
# the two protected-directory functions touch the file system, and only on Windows.
# Dot-sourcing callers keep their own strict-mode setting.

$script:SandboxQaRoot = 'D:\TopiaForgeQA'

# Build-named siblings keep every earlier copy intact: a retarget adds
# source-game-<build> and game-<build> instead of reusing source-game and game.
function Get-SandboxQaBuildCopyNames {
    param([Parameter(Mandatory)][long]$BuildId)
    if ($BuildId -lt 1 -or $BuildId -gt 999999) { throw 'Robotopia build id is out of range.' }
    return [pscustomobject]@{
        SourceGame = "source-game-$BuildId"
        Game = "game-$BuildId"
        Receipt = "game-copy-$BuildId.json"
    }
}

function Test-SandboxQaCopyReceiptName {
    param([Parameter(Mandatory)][AllowEmptyString()][string]$Name)
    return $Name -cmatch '^game-copy(-[1-9][0-9]{0,5})?\.json$'
}

# Validates a completed copy receipt whose bytes the caller already bound to a
# trusted digest, and returns the source tree it describes. Version 1 is the
# original 2409 copy into source-game; version 2 names its build.
function Resolve-SandboxQaCopyReceipt {
    param([Parameter(Mandatory)]$Receipt, [string]$QaRoot = $script:SandboxQaRoot)
    if ($Receipt.completed -isnot [bool] -or !$Receipt.completed -or $Receipt.qaRoot -cne $QaRoot) { throw 'Completed QA copy receipt required.' }
    $rows = @($Receipt.inventory)
    if ($Receipt.fileCount -isnot [long] -and $Receipt.fileCount -isnot [int]) { throw 'QA copy receipt count is invalid.' }
    if ($Receipt.fileCount -lt 1 -or $Receipt.copiedFiles -ne $Receipt.fileCount -or $rows.Count -ne $Receipt.fileCount) { throw 'QA copy receipt inventory is incomplete.' }
    if ($Receipt.sourceBytes -isnot [long] -and $Receipt.sourceBytes -isnot [int]) { throw 'QA copy receipt size is invalid.' }
    if ($Receipt.kind -ceq 'sandbox-qa-game-copy-v1' -and $Receipt.schemaVersion -eq 1) {
        return [pscustomobject]@{ BuildId = $null; SourceGameRoot = "$QaRoot\source-game"; GameRoot = "$QaRoot\game"; FileCount = [long]$Receipt.fileCount; SourceBytes = [long]$Receipt.sourceBytes; Rows = $rows }
    }
    if ($Receipt.kind -ceq 'sandbox-qa-game-copy-v2' -and $Receipt.schemaVersion -eq 2) {
        if ($Receipt.buildId -isnot [long] -and $Receipt.buildId -isnot [int]) { throw 'QA copy receipt build id is invalid.' }
        $names = Get-SandboxQaBuildCopyNames ([long]$Receipt.buildId)
        if ($Receipt.sourceGameRoot -cne "$QaRoot\$($names.SourceGame)" -or $Receipt.gameRoot -cne "$QaRoot\$($names.Game)") { throw 'QA copy receipt roots do not match its build.' }
        foreach ($digest in @($Receipt.filesManifestSha256, $Receipt.gameExecutableSha256)) {
            if ($digest -isnot [string] -or $digest -cnotmatch '^[a-f0-9]{64}$') { throw 'QA copy receipt digests are invalid.' }
        }
        $executable = @($rows | Where-Object { $_.path -ceq 'Robotopia.exe' })
        if ($executable.Count -ne 1 -or $executable[0].sha256 -cne $Receipt.gameExecutableSha256) { throw 'QA copy receipt executable identity mismatch.' }
        return [pscustomobject]@{ BuildId = [long]$Receipt.buildId; SourceGameRoot = $Receipt.sourceGameRoot; GameRoot = $Receipt.gameRoot; FileCount = [long]$Receipt.fileCount; SourceBytes = [long]$Receipt.sourceBytes; Rows = $rows }
    }
    throw 'Unknown QA game copy receipt kind.'
}

# Creates one new directory with its protected ACL already applied (no window
# with inherited access): SYSTEM, Administrators and the creator get full
# control, the QA account gets the stated rights. Refuses an existing path and
# returns the expected access SDDL for Assert-SandboxQaProtectedDirectory, so a
# caller can record the creation before verification can fail.
function New-SandboxQaProtectedDirectory {
    [CmdletBinding(SupportsShouldProcess)]
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][Security.Principal.SecurityIdentifier]$CreatorSid,
        [Parameter(Mandatory)][Security.Principal.SecurityIdentifier]$QaSid,
        [Parameter(Mandatory)][ValidateSet('ReadAndExecute', 'Modify')][string]$QaRights
    )
    if (!$PSCmdlet.ShouldProcess($Path, 'Create the protected QA directory')) { throw 'Protected QA directory creation was not confirmed.' }
    if (Test-Path -LiteralPath $Path) { throw 'Refusing an existing destination, including a previous partial copy.' }
    $acl = [Security.AccessControl.DirectorySecurity]::new()
    $acl.SetAccessRuleProtection($true, $false)
    $acl.SetOwner($CreatorSid)
    $inherit = [Security.AccessControl.InheritanceFlags]'ContainerInherit,ObjectInherit'
    foreach ($sid in @($CreatorSid, [Security.Principal.SecurityIdentifier]::new('S-1-5-18'), [Security.Principal.SecurityIdentifier]::new('S-1-5-32-544'))) {
        $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($sid, [Security.AccessControl.FileSystemRights]::FullControl, $inherit, [Security.AccessControl.PropagationFlags]::None, [Security.AccessControl.AccessControlType]::Allow))
    }
    $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($QaSid, [Security.AccessControl.FileSystemRights]$QaRights, $inherit, [Security.AccessControl.PropagationFlags]::None, [Security.AccessControl.AccessControlType]::Allow))
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
    [SandboxFreshGameDirectory]::CreateNew($Path, $acl.GetSecurityDescriptorBinaryForm())
    return $acl.GetSecurityDescriptorSddlForm([Security.AccessControl.AccessControlSections]::Access)
}

function Assert-SandboxQaProtectedDirectory {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][Security.Principal.SecurityIdentifier]$CreatorSid,
        [Parameter(Mandatory)][string]$ExpectedAccessSddl
    )
    $actual = Get-Acl -LiteralPath $Path
    if (!$actual.AreAccessRulesProtected -or $actual.GetOwner([Security.Principal.SecurityIdentifier]).Value -cne $CreatorSid.Value -or $actual.GetSecurityDescriptorSddlForm([Security.AccessControl.AccessControlSections]::Access) -cne $ExpectedAccessSddl) { throw 'Protected QA directory ACL verification failed.' }
}
