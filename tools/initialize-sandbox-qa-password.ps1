#Requires -Version 7.0
#Requires -RunAsAdministrator
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ProvisioningReceipt,
    [Parameter(Mandatory)][string]$CompletionReceipt
)
$ErrorActionPreference = 'Stop'
$accountName = 'TopiaForgeQA'
$first = $null
$second = $null
try {
    $receipt = Get-Content -LiteralPath $ProvisioningReceipt -Raw | ConvertFrom-Json
    if (-not $receipt.completed -or $receipt.accountName -ne $accountName) {
        throw 'The successful provisioning receipt is required.'
    }
    $account = Get-LocalUser -Name $accountName
    if ($account.SID.Value -ne $receipt.userSid) { throw 'Provisioned account identity changed.' }
    if (Get-CimInstance Win32_UserProfile -Filter "SID='$($account.SID.Value)'") {
        throw 'The QA profile already exists. This initialization helper will not reset an initialized user password.'
    }
    if (Test-Path -LiteralPath $CompletionReceipt) { throw 'The completion receipt already exists.' }
    Write-Host 'Choose the password for the new TopiaForgeQA Windows account. It stays in this local secure prompt.'
    $first = Read-Host 'New QA password' -AsSecureString
    $second = Read-Host 'Confirm QA password' -AsSecureString
    if ($first.Length -lt 12 -or $first.Length -ne $second.Length) { throw 'Use matching passwords of at least 12 characters.' }
    $firstPointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($first)
    $secondPointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($second)
    try {
        $difference = 0
        for ($index = 0; $index -lt $first.Length; $index++) {
            $difference = $difference -bor ([Runtime.InteropServices.Marshal]::ReadInt16($firstPointer, $index * 2) -bxor [Runtime.InteropServices.Marshal]::ReadInt16($secondPointer, $index * 2))
        }
        if ($difference -ne 0) { throw 'The passwords do not match.' }
        Set-LocalUser -Name $accountName -Password $first
    }
    finally {
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($firstPointer)
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($secondPointer)
    }
    $result = [ordered]@{
        schemaVersion = 1
        kind = 'sandbox-qa-password-initialization-v1'
        accountName = $accountName
        completedAtUtc = [DateTime]::UtcNow.ToString('o')
        passwordSet = $true
        profileInitialized = $false
        isolationAdmitted = $false
    }
    $stream = [IO.File]::Open($CompletionReceipt, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try {
        $bytes = [Text.UTF8Encoding]::new($false).GetBytes(($result | ConvertTo-Json))
        $stream.Write($bytes); $stream.Flush($true)
    } finally { $stream.Dispose() }
    Write-Host 'Password set. Keep it private. The next step is a normal Windows sign-in to TopiaForgeQA.'
}
catch {
    Write-Host ('Initialization stopped: ' + $_.Exception.Message)
    Read-Host 'Press Enter to close' | Out-Null
    exit 1
}
finally {
    if ($null -ne $first) { $first.Dispose() }
    if ($null -ne $second) { $second.Dispose() }
}
Read-Host 'Press Enter to close' | Out-Null
