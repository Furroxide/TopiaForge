[CmdletBinding()]
param([string]$CasePattern = ".")
Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$source = Get-Content -LiteralPath (Join-Path $PSScriptRoot "build-windows.ps1") -Raw
$start = $source.IndexOf('$windowsCertificatePin = ""', [StringComparison]::Ordinal)
$end = $source.IndexOf('if ((Get-Sha256 $canonical)', $start, [StringComparison]::Ordinal)
if ($start -lt 0 -or $end -le $start) { throw "Windows signing admission was not found." }
$admit = [scriptblock]::Create($source.Substring($start, $end - $start))
$failures = [Collections.Generic.List[string]]::new()
$passed = 0
# Admission must precede candidate-output writes: even a rejected policy must
# leave an existing candidate untouched and must not create an empty output.
$outputWrite = $source.IndexOf('New-Item -ItemType Directory -Force -Path $output', [StringComparison]::Ordinal)
if ($outputWrite -lt $end) {
    $failures.Add("Policy admission must precede the first candidate output directory write.")
} else { $passed++ }

# Execute only the real admission block in an owned child. Never read real
# signing credentials; strip inherited values or use visibly synthetic values.
$envNames = @("WINDOWS_CERTIFICATE_PFX", "WINDOWS_CERTIFICATE_PASSWORD", "WINDOWS_TIMESTAMP_URL")
$fixture = [scriptblock]::Create(@'
param($admitSource, $caseJson)
Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$case = $caseJson | ConvertFrom-Json
$identities = @{}
if ($case.ModePresent) { $identities.windowsDistribution = $case.Mode }
if ($case.PinPresent) { $identities.windowsCertificateSha256 = $case.Pin }
$policy = [pscustomobject]@{
    versioning = [pscustomobject]@{ productVersion = $case.Version }
    signingIdentities = [pscustomobject]$identities
    publication = [pscustomobject]@{ mode = "admin-staged-auto-publish" }
}
. ([scriptblock]::Create($admitSource))
'@)
$cases = [Collections.Generic.List[object]]::new()
function Add-AdmissionCase {
    param(
        [string]$Name,
        [AllowNull()][object]$Mode = "unsigned",
        [AllowNull()][object]$Pin = $null,
        [string]$Version = "0.1.0-rc.1",
        [bool]$Pass = $false,
        [string]$Message = "",
        [bool]$SyntheticCredentials = $false,
        [switch]$OmitMode,
        [switch]$OmitPin
    )
    $cases.Add(@{
        Name = $Name; Mode = $Mode; Pin = $Pin; Version = $Version
        ModePresent = -not $OmitMode
        PinPresent = $PSBoundParameters.ContainsKey('Pin') -and -not $OmitPin
        Pass = $Pass; Message = $Message; SyntheticCredentials = $SyntheticCredentials
    })
}
Add-AdmissionCase -Name "unsigned without a pin" -Pass $true
Add-AdmissionCase -Name "unsigned with explicit empty pin" -Pin "" -Message "certificate"
Add-AdmissionCase -Name "unsigned ignores synthetic ambient credentials" -Pass $true -SyntheticCredentials $true
foreach ($pinCase in @(
    @{ Name = "valid"; Value = ("a" * 64) },
    @{ Name = "invalid"; Value = "invalid-pin" },
    @{ Name = "zero"; Value = ("0" * 64) },
    @{ Name = "whitespace"; Value = " `t " },
    @{ Name = "trailing LF"; Value = (("a" * 64) + "`n") },
    @{ Name = "uppercase"; Value = ("A" * 64) }
)) {
    Add-AdmissionCase -Name "unsigned rejects $($pinCase.Name) pin" -Pin $pinCase.Value -Message "certificate"
}
foreach ($mode in @("signed", $null)) {
    $label = if ($null -eq $mode) { "default signed" } else { "explicit signed" }
    Add-AdmissionCase -Name "$label requires credentials" -Mode $mode -OmitMode:($null -eq $mode) -Pin ("a" * 64) -Message "WINDOWS_CERTIFICATE_PFX"
    Add-AdmissionCase -Name "$label permits stable versions" -Mode $mode -OmitMode:($null -eq $mode) -Pin ("a" * 64) -Version "1.0.0" -Message "WINDOWS_CERTIFICATE_PFX"
    foreach ($pinCase in @(
        @{ Name = "missing"; Value = $null },
        @{ Name = "empty"; Value = "" },
        @{ Name = "invalid"; Value = "invalid-pin" },
        @{ Name = "zero"; Value = ("0" * 64) },
        @{ Name = "whitespace"; Value = " `t " },
    @{ Name = "trailing LF"; Value = (("a" * 64) + "`n") }
    )) {
        Add-AdmissionCase -Name "$label rejects $($pinCase.Name) pin" -Mode $mode -OmitMode:($null -eq $mode) -Pin $pinCase.Value -OmitPin:($pinCase.Name -eq "missing") -Message "certificate"
    }
}
foreach ($mode in @("maybe", "UNSIGNED", "Signed", "", " unsigned ")) {
    Add-AdmissionCase -Name "unknown mode '$mode'" -Mode $mode -Message "distribution mode"
}
foreach ($rawCase in @(
    @{ Name = "explicit null"; Value = $null },
    @{ Name = "number"; Value = 7 },
    @{ Name = "boolean"; Value = $false },
    @{ Name = "array"; Value = @("unsigned") },
    @{ Name = "object"; Value = @{ value = "unsigned" } }
)) {
    Add-AdmissionCase -Name "rejects $($rawCase.Name) mode" -Mode $rawCase.Value -Message "distribution"
}
foreach ($rawCase in @(
    @{ Name = "explicit null"; Value = $null },
    @{ Name = "number"; Value = 7 },
    @{ Name = "boolean"; Value = $false },
    @{ Name = "array"; Value = @("a" * 64) },
    @{ Name = "object"; Value = @{ value = ("a" * 64) } }
)) {
    Add-AdmissionCase -Name "rejects $($rawCase.Name) pin" -Pin $rawCase.Value -Message "certificate"
}
# Match SemanticVersion.tryParse: metadata is allowed, numeric prerelease
# identifiers cannot have leading zeros, and alpha/hyphen identifiers can.
foreach ($version in @(
    "0.1.0", "0.1.0+build.1", "1.0.0-rc.1", "10.0.0-rc.1",
    "not-a-version", "00.1.0-rc.1", "0.01.0-rc.1", "0.1.00-rc.1",
    "0.1.0-01", "0.1.0-rc.01", "0.1.0-rc..1", "0.1.0-",
    "0.1.0-rc.1+", "0.1.0-rc.1+build..1", "0.1.0-rc_1",
    " 0.1.0-rc.1", "0.1.0-rc.1 ", "0.1.0-r$([char]0x00e9)lease",
    "0.1.0-rc.1`n", "0.1.0-rc.1`r`n"
)) {
    Add-AdmissionCase -Name "unsigned rejects version '$version'" -Version $version -Message "0.x prerelease"
}
foreach ($version in @(
    "0.0.0-0", "0.1.0-rc.1+build.01", "0.1.0-01a",
    "0.1.0-rc-01", "0.1.0--", "0.1.0-0.0+0",
    "0.999999999999999999999999.0-rc.1"
)) {
    Add-AdmissionCase -Name "unsigned accepts version '$version'" -Version $version -Pass $true
}
$selectedCases = @($cases | Where-Object { $_.Name -match $CasePattern })
if ($selectedCases.Count -eq 0) { throw "No signing admission cases match $CasePattern." }
foreach ($case in $selectedCases) {
    $child = [Diagnostics.ProcessStartInfo]::new()
    $child.FileName = (Get-Process -Id $PID).Path
    $child.UseShellExecute = $false
    $child.CreateNoWindow = $true
    $child.RedirectStandardOutput = $true
    $child.RedirectStandardError = $true
    foreach ($name in $envNames) {
        $child.Environment.Remove($name) | Out-Null
        if ($case.SyntheticCredentials) { $child.Environment[$name] = "test-only-not-a-credential" }
    }
    $json = $case | ConvertTo-Json -Compress -Depth 8
    $command = "& { $fixture } '" + $admit.ToString().Replace("'", "''") + "' '" + $json.Replace("'", "''") + "'"
    foreach ($arg in @("-NoLogo", "-NoProfile", "-NonInteractive", "-Command", $command)) {
        $child.ArgumentList.Add($arg)
    }
    $process = [Diagnostics.Process]::Start($child)
    try {
        $outTask = $process.StandardOutput.ReadToEndAsync()
        $errTask = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(10000)) {
            $process.Kill()
            $process.WaitForExit()
            throw "Signing admission fixture did not exit."
        }
        $output = $outTask.GetAwaiter().GetResult() + $errTask.GetAwaiter().GetResult()
        if (($process.ExitCode -eq 0) -ne $case.Pass) {
            $failures.Add("$($case.Name): unexpected admission: $output")
        } elseif (-not $case.Pass -and $case.Message -ne "WINDOWS_CERTIFICATE_PFX" -and
            @($envNames | Where-Object { $output.Contains($_) }).Count -gt 0) {
            $failures.Add("$($case.Name): policy refusal must precede credential lookup: $output")
        } elseif (-not $case.Pass -and $output.IndexOf($case.Message, [StringComparison]::OrdinalIgnoreCase) -lt 0) {
            $failures.Add("$($case.Name): wrong refusal, expected '$($case.Message)': $output")
        } else {
            $passed++
            Write-Output "PASS $($case.Name)"
        }
    } finally { $process.Dispose() }
}
foreach ($failure in $failures) { Write-Output "FAIL $failure" }
Write-Output "Windows signing admission: $passed passed, $($failures.Count) failed."
if ($failures.Count -gt 0) { throw "Windows signing admission regressions failed." }
