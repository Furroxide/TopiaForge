#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9][A-Za-z0-9_-]{0,19}$')][string]$AccountName,
    [Parameter(Mandatory)][string]$QaRoot,
    [Parameter(Mandatory)][string]$ReceiptPath
)
$ErrorActionPreference = 'Stop'
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (!$principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this provisioning script through a Windows administrator prompt.'
}
$root = [IO.Path]::GetFullPath($QaRoot).TrimEnd('\')
if ($root -notmatch '^[A-Z]:\\TopiaForgeQA$') { throw 'QA root must be a fixed-drive TopiaForgeQA directory.' }
$volume = Get-Volume -DriveLetter $root.Substring(0, 1)
if ($volume.DriveType -ne 'Fixed' -or $volume.FileSystem -ne 'NTFS' -or $volume.HealthStatus -ne 'Healthy') {
    throw 'QA storage requires a healthy fixed NTFS volume.'
}
if (Test-Path -LiteralPath $root) { throw 'Refusing to reuse an existing QA root; review any partial provisioning first.' }
if (Get-LocalUser -Name $AccountName -ErrorAction SilentlyContinue) { throw 'Refusing to modify an existing account.' }
$receipt = [IO.Path]::GetFullPath($ReceiptPath)
if (Test-Path -LiteralPath $receipt) { throw 'Refusing to overwrite a provisioning receipt.' }
$receiptDirectory = [IO.Path]::GetDirectoryName($receipt)
if (!(Test-Path -LiteralPath $receiptDirectory -PathType Container)) { throw 'Create the private receipt parent before elevation.' }
$systemSid = [Security.Principal.SecurityIdentifier]::new('S-1-5-18')
$adminsSid = [Security.Principal.SecurityIdentifier]::new('S-1-5-32-544')
$creatorSid = $identity.User
$inheritance = [Security.AccessControl.InheritanceFlags]'ContainerInherit,ObjectInherit'
function Set-QADirectoryAcl([string]$Path, [Security.Principal.SecurityIdentifier]$QaSid, [string]$QaAccess) {
    $security = [Security.AccessControl.DirectorySecurity]::new()
    $security.SetAccessRuleProtection($true, $false)
    foreach ($sid in @($systemSid, $adminsSid, $creatorSid)) {
        $security.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new(
            $sid, 'FullControl', $inheritance, 'None', 'Allow'))
    }
    if ($QaSid) {
        $security.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new(
            $QaSid, $QaAccess, $inheritance, 'None', 'Allow'))
    }
    Set-Acl -LiteralPath $Path -AclObject $security
}
$record = [ordered]@{
    schemaVersion = 1; kind = 'sandbox-qa-provisioning-progress-v1'; startedAtUtc = [DateTime]::UtcNow.ToString('o')
    accountName = $AccountName; qaRoot = $root; userSid = ''; completed = $false
    storageFreeBytesBefore = $volume.SizeRemaining; profileInitialized = $false
    gameInstalled = $false; isolationAdmitted = $false; error = ''
}
function Save-QAProgress {
    $record | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $receipt -Encoding utf8
}
Save-QAProgress
try {
    # A generated bootstrap password never enters stdout, command arguments or Git.
    # Export-Clixml protects it with Windows DPAPI for this administrator identity.
    $randomBytes = [Security.Cryptography.RandomNumberGenerator]::GetBytes(48)
    $passwordText = [Convert]::ToBase64String($randomBytes) + '!aA7'
    $password = ConvertTo-SecureString -String $passwordText -AsPlainText -Force
    $passwordText = $null
    [Array]::Clear($randomBytes, 0, $randomBytes.Length)
    $user = New-LocalUser -Name $AccountName -Password $password -Disabled -AccountNeverExpires `
        -Description 'Isolated TopiaForge QA; no personal data'
    $record.userSid = $user.SID.Value
    Save-QAProgress
    $usersGroup = Get-LocalGroup -SID 'S-1-5-32-545'
    Add-LocalGroupMember -Group $usersGroup -Member $user
    [IO.Directory]::CreateDirectory($root) | Out-Null
    Set-QADirectoryAcl $root $user.SID 'ReadAndExecute'
    foreach ($name in @('game', 'launcher', 'evidence', 'state')) {
        $directory = Join-Path $root $name
        [IO.Directory]::CreateDirectory($directory) | Out-Null
        Set-QADirectoryAcl $directory $user.SID 'Modify'
    }
    foreach ($name in @('source-game', 'tools')) {
        $directory = Join-Path $root $name
        [IO.Directory]::CreateDirectory($directory) | Out-Null
        Set-QADirectoryAcl $directory $user.SID 'ReadAndExecute'
    }
    $privateDirectory = Join-Path $root 'private'
    [IO.Directory]::CreateDirectory($privateDirectory) | Out-Null
    Set-QADirectoryAcl $privateDirectory $null ''
    $credential = [Management.Automation.PSCredential]::new($AccountName, $password)
    $credential | Export-Clixml -LiteralPath (Join-Path $privateDirectory 'bootstrap-credential.clixml')
    Enable-LocalUser -Name $AccountName
    $record.completed = $true
    $record['completedAtUtc'] = [DateTime]::UtcNow.ToString('o')
    $record['nextStep'] = 'Set a chosen password privately and sign in normally to initialize the QA profile; do not spoof profile environment variables.'
    Save-QAProgress
} catch {
    # Preserve partial state for review. Never recursively delete a profile or
    # account as automatic rollback, and never overwrite existing data on retry.
    $record.error = $_.Exception.Message
    Save-QAProgress
    throw
} finally {
    if ($password) { $password.Dispose() }
}
