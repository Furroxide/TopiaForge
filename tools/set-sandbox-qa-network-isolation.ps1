#Requires -Version 7.0
#Requires -RunAsAdministrator
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$GameCopyReceipt,
    [Parameter(Mandatory)][string]$ReceiptPath,
    [string]$GameRoot = 'D:\TopiaForgeQA\game'
)
$ErrorActionPreference = 'Stop'
function ResolveQaProgram([string]$Root) {
    if ($Root -ceq 'D:\TopiaForgeQA\game') {
        return [pscustomobject]@{Executable='D:\TopiaForgeQA\game\Robotopia.exe';RuleName='TopiaForge-QA-Isolation-20260909'}
    }
    $match = [regex]::Match($Root,'^D:\\TopiaForgeQA\\game-provisioning-([0-9]{8}T[0-9]{6}Z)$',[Text.RegularExpressions.RegexOptions]::CultureInvariant)
    if (!$match.Success) { throw 'Use the original QA game or a fresh, timestamped provisioning-game child.' }
    $stamp = $match.Groups[1].Value
    [void][DateTime]::ParseExact($stamp,'yyyyMMddTHHmmssZ',[Globalization.CultureInfo]::InvariantCulture,[Globalization.DateTimeStyles]::AssumeUniversal)
    return [pscustomobject]@{Executable=($Root+'\Robotopia.exe');RuleName=('TopiaForge-QA-Provisioning-'+$stamp)}
}
$scope = ResolveQaProgram $GameRoot
$gameExecutable = $scope.Executable
$sourceExecutable = 'D:\TopiaForgeQA\source-game\Robotopia.exe'
$ruleName = $scope.RuleName
$result = [ordered]@{schemaVersion=1;kind='sandbox-qa-outbound-isolation-v1';startedAtUtc=[DateTime]::UtcNow.ToString('o');ruleName=$ruleName;program=$gameExecutable;ruleCreated=$false;verified=$false;gameExecuted=$false;isolationAdmitted=$false;qualifiesRelease=$false;errorType=$null}
function RequirePhysical([string]$Path) {
    $full = [IO.Path]::GetFullPath($Path)
    if ($full -cne $Path -or $full -notmatch '^[A-Z]:\\' -or $full.Substring(3).Contains(':')) { throw 'Nonphysical QA path.' }
    $parent = $full
    while ($parent) {
        if ((Test-Path -LiteralPath $parent) -and ((Get-Item -LiteralPath $parent -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Linked QA path.' }
        $parent = [IO.Path]::GetDirectoryName($parent)
    }
}
foreach ($path in @($gameExecutable,$sourceExecutable,$GameCopyReceipt,$ReceiptPath)) { RequirePhysical $path }
if (Test-Path -LiteralPath $ReceiptPath) { throw 'Refusing existing firewall receipt.' }
try {
    if ((Get-Item -LiteralPath $GameCopyReceipt).Length -gt 1MB) { throw 'Unbounded source copy receipt.' }
    $copy = Get-Content -LiteralPath $GameCopyReceipt -Raw | ConvertFrom-Json
    if ($copy.kind -ne 'sandbox-qa-game-copy-v1' -or !$copy.completed -or $copy.qaRoot -cne 'D:\TopiaForgeQA') { throw 'Verified approved source-copy receipt required.' }
    $expected = @($copy.inventory | Where-Object path -CEQ 'Robotopia.exe')
    if ($expected.Count -ne 1 -or $expected[0].sha256 -cnotmatch '^[a-f0-9]{64}$') { throw 'Expected game executable identity missing.' }
    foreach ($path in @($gameExecutable,$sourceExecutable)) {
        if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -cne $expected[0].sha256) { throw 'QA executable differs from the approved source.' }
    }
    if (@(Get-Service BFE,MpsSvc | Where-Object Status -NE Running).Count -gt 0 -or @(Get-NetFirewallProfile | Where-Object Enabled -NE True).Count -gt 0) { throw 'Existing filtering services/profiles must already be enabled.' }
    if (Get-NetFirewallRule -Name $ruleName -ErrorAction SilentlyContinue) { throw 'Refusing to overwrite an existing firewall rule.' }
    New-NetFirewallRule -Name $ruleName -DisplayName $ruleName -Description 'Outbound block for the isolated TopiaForge QA game only; retain until reviewed native QA network policy changes.' -PolicyStore PersistentStore -Direction Outbound -Action Block -Program $gameExecutable -Enabled True -Profile Any -Protocol Any -LocalAddress Any -RemoteAddress Any -InterfaceType Any | Out-Null
    $result.ruleCreated = $true
    $policy = New-Object -ComObject HNetCfg.FwPolicy2
    $rules = $null; $rule = $null
    try {
        $rules = $policy.Rules; $rule = $rules.Item($ruleName)
        if (!$rule.Enabled -or $rule.Direction -ne 2 -or $rule.Action -ne 0 -or $rule.Profiles -ne 2147483647 -or $rule.Protocol -ne 256 -or $rule.ApplicationName -cne $gameExecutable -or $rule.InterfaceTypes -ne 'All') { throw 'Actual firewall rule does not match the isolated scope.' }
        foreach ($value in @($rule.LocalAddresses,$rule.RemoteAddresses,$rule.LocalPorts,$rule.RemotePorts)) { if (![string]::IsNullOrEmpty($value) -and $value -ne '*') { throw 'Firewall endpoint/port scope is narrowed.' } }
        if (![string]::IsNullOrEmpty($rule.ServiceName) -or ($null -ne $rule.Interfaces -and @($rule.Interfaces).Count -ne 0)) { throw 'Firewall service/interface scope is narrowed.' }
        $result.verified = $true
        $result.programSha256 = $expected[0].sha256
        $result.scope = [ordered]@{direction='outbound';action='block';profiles='all';protocols='all';addresses='all';ports='all';interfaces='all'}
    } finally {
        if ($null -ne $rule) { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($rule) }
        if ($null -ne $rules) { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($rules) }
        [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($policy)
    }
}
catch { $result.errorType = $_.Exception.GetType().Name }
finally {
    $result.completedAtUtc = [DateTime]::UtcNow.ToString('o')
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes(($result | ConvertTo-Json -Depth 6))
    $stream = [IO.File]::Open($ReceiptPath,[IO.FileMode]::CreateNew)
    try { $stream.Write($bytes); $stream.Flush($true) } finally { $stream.Dispose() }
}
if (!$result.verified) { exit 1 }
