[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [Parameter(Position = 0, Mandatory = $true)]
    [ValidateSet("preflight", "build", "stage", "dispatch", "resume", "all")]
    [string]$Command,

    [string]$Version,
    [string]$Repository = "furroxide/TopiaForge",
    [string]$WslDistribution = "Ubuntu-24.04",
    [string]$ProtonExecutable = $env:TOPIAFORGE_PROTON_EXECUTABLE,
    [string]$SteamRoot = $env:TOPIAFORGE_STEAM_ROOT,
    [string]$CompatDataRoot = $env:TOPIAFORGE_COMPAT_DATA_ROOT,
    [string]$WindowsCreatorEvidence,
    [string]$WindowsCreatorEvidenceBundle,
    [string]$UnityPath = "C:\Program Files\Unity\Hub\Editor\6000.0.23f1\Editor\Unity.exe",
    [string]$GameDirectory = "$env:LOCALAPPDATA\Tomato Cake\launcher\Robotopia",
    [string]$PythonPath = $env:TOPIAFORGE_PYTHON,
    [string]$StateRoot,
    [switch]$Rehearsal
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest
if ($WhatIfPreference) {
    throw "Use -Rehearsal for a non-publishing run; -WhatIf cannot persist a resumable release state."
}
$explicitParameters = @{}
foreach ($parameterName in $PSBoundParameters.Keys) {
    $explicitParameters[$parameterName] = $true
}

$repositoryRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")).Path
$policyPath = Join-Path $repositoryRoot "release/release-policy.json"
$platformToolchainsPath = Join-Path $repositoryRoot "release/platform-toolchains.json"
$policy = Get-Content -LiteralPath $policyPath -Raw | ConvertFrom-Json
if (-not (Test-Path -LiteralPath $platformToolchainsPath -PathType Leaf)) {
    throw "The pinned platform toolchain manifest is missing."
}
$platformToolchains = Get-Content -LiteralPath $platformToolchainsPath -Raw |
    ConvertFrom-Json
# Linux is descoped from 0.1.0-rc.1 and returns in rc.2, so the WSL build,
# Proton acceptance, and their preflight checks are gated on the policy rather
# than deleted. Re-adding the Linux archive to release-policy.json restores the
# whole path. See P0-LINUX-01 in docs/LaunchBlockers.md.
$targetsLinux =
    @($policy.artifactPolicy.platformArchives) -contains "TopiaForge-linux-x64.zip"
if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = [string]$policy.versioning.productVersion
}
if ($Version -notmatch "^[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?$") {
    throw "Version must be an unprefixed semantic version."
}
$tag = "v$Version"
if ([string]::IsNullOrWhiteSpace($StateRoot)) {
    $StateRoot = Join-Path $repositoryRoot ".release-local"
}
$stateDirectory = [System.IO.Path]::GetFullPath((Join-Path $StateRoot $Version))
$assetsDirectory = Join-Path $stateDirectory "assets"
$evidenceDirectory = Join-Path $stateDirectory "evidence"
$statePath = Join-Path $stateDirectory "state.json"
$handoffSignatureScript = Join-Path $PSScriptRoot `
    "release/handoff-signature.ps1"
$powerShellName = if ($IsWindows) { "pwsh.exe" } else { "pwsh" }
$powerShellExecutable = Join-Path $PSHOME $powerShellName
$finalizerRegistrationGraceAttempts = 30
$finalizerRegistrationPollDelayMilliseconds = 2000

function Enter-ReleaseLock {
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute(
        "PSUseShouldProcessForStateChangingFunctions",
        "",
        Justification = "The lock is a safety prerequisite for every state-changing release command."
    )]
    param()

    New-Item -ItemType Directory -Force -Path $stateDirectory | Out-Null
    $lockPath = Join-Path $stateDirectory "release-admin.lock"
    try {
        return [System.IO.File]::Open(
            $lockPath,
            [System.IO.FileMode]::OpenOrCreate,
            [System.IO.FileAccess]::ReadWrite,
            [System.IO.FileShare]::None
        )
    }
    catch [System.IO.IOException] {
        throw "Another release-admin process already owns the $Version release state."
    }
}

function Invoke-Checked {
    param(
        [Parameter(Mandatory = $true)]
        [string]$FilePath,
        [Parameter()]
        [string[]]$Arguments = @(),
        [Parameter()]
        [string]$WorkingDirectory = $repositoryRoot,
        [switch]$Capture
    )

    Push-Location $WorkingDirectory
    try {
        if ($Capture) {
            $result = & $FilePath @Arguments 2>&1
            if ($LASTEXITCODE -ne 0) {
                throw "'$FilePath' failed with exit code $LASTEXITCODE.`n$($result | Out-String)"
            }
            return ($result | Out-String).Trim()
        }
        & $FilePath @Arguments
        if ($LASTEXITCODE -ne 0) {
            throw "'$FilePath' failed with exit code $LASTEXITCODE."
        }
    }
    finally {
        Pop-Location
    }
}

function ConvertTo-PosixLiteral {
    param([AllowEmptyString()][string]$Value)
    if ($Value.Contains("`0") -or $Value.Contains("`r") -or $Value.Contains("`n")) {
        throw "Remote command arguments cannot contain NUL or newline characters."
    }
    $escaped = $Value.Replace("'", "'`"`"'`"`'")
    return "'$escaped'"
}

function Invoke-WslScript {
    param(
        [Parameter(Mandatory = $true)][string]$Script,
        [string[]]$Arguments = @(),
        [switch]$Capture
    )
    $encoded = [Convert]::ToBase64String(
        [System.Text.UTF8Encoding]::new($false).GetBytes($Script)
    )
    $remoteArguments = @($Arguments | ForEach-Object {
            ConvertTo-PosixLiteral ([string]$_)
        })
    $remoteCommand =
        "printf '%s' '$encoded' | base64 --decode | /bin/bash -s --"
    if ($remoteArguments.Count -gt 0) {
        $remoteCommand += " " + ($remoteArguments -join " ")
    }
    return Invoke-Checked wsl @(
        "--distribution", $WslDistribution, "--exec",
        "bash", "-lc", $remoteCommand
    ) -Capture:$Capture
}

function Require-Command {
    param([Parameter(Mandatory = $true)][string]$Name)
    $resolved = Get-Command $Name -CommandType Application -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if ($null -eq $resolved) {
        throw "Required command '$Name' was not found."
    }
    return $resolved.Source
}

function Assert-GitLfsMaterialized {
    param(
        [Parameter(Mandatory = $true)][string]$RepositoryPath,
        [Parameter(Mandatory = $true)]
        [ValidatePattern("^[0-9a-f]{40}$")]
        [string]$SourceSha
    )

    Invoke-Checked git @(
        "-C", $RepositoryPath, "lfs", "fsck", $SourceSha
    )
    $lfsInventory = Invoke-Checked git @(
        "-C", $RepositoryPath, "lfs", "ls-files", "-l"
    ) -Capture
    $lfsInventoryLines = @(
        $lfsInventory -split "\r?\n" |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
    )
    if ($lfsInventoryLines.Count -eq 0 -or
        @($lfsInventoryLines | Where-Object {
                $_ -cnotmatch "^[0-9a-f]{64} \* .+$"
            }).Count -ne 0) {
        throw "Every tracked Git LFS object must be materialized before release."
    }
}

function Assert-LatestRobotopiaBuild {
    Invoke-Checked dotnet @(
        "run",
        "--project",
        (Join-Path $repositoryRoot `
            "tools/TopiaForge.ManagedRefs/TopiaForge.ManagedRefs.csproj"),
        "--configuration",
        "Release",
        "--",
        "--probe",
        "--require-latest"
    )
}

function Resolve-GitHubCli {
    $resolved = Get-Command gh -CommandType Application -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if ($null -ne $resolved) {
        return $resolved.Source
    }
    if ($IsWindows -and -not [string]::IsNullOrWhiteSpace($env:ProgramFiles)) {
        $installed = Join-Path $env:ProgramFiles "GitHub CLI\gh.exe"
        if (Test-Path -LiteralPath $installed -PathType Leaf) {
            return $installed
        }
    }
    throw "Required command 'gh' was not found on PATH or at " +
        "C:\Program Files\GitHub CLI\gh.exe."
}

function Resolve-Bash {
    $resolved = Get-Command bash -CommandType Application -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if ($null -ne $resolved) {
        return $resolved.Source
    }
    if ($IsWindows) {
        $installed = Join-Path $env:ProgramFiles "Git\bin\bash.exe"
        if (Test-Path -LiteralPath $installed -PathType Leaf) {
            return $installed
        }
    }
    throw "Required command 'bash' was not found on PATH or in Git for Windows."
}

function Resolve-Jq {
    $resolved = Get-Command jq -CommandType Application -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if ($null -ne $resolved) {
        return $resolved.Source
    }
    if ($IsWindows) {
        foreach ($root in @($env:ProgramFiles, $env:LOCALAPPDATA, $env:ProgramData)) {
            if ([string]::IsNullOrWhiteSpace($root)) {
                continue
            }
            foreach ($relative in @(
                    "jq\jq.exe",
                    "Microsoft\WinGet\Links\jq.exe",
                    "chocolatey\bin\jq.exe"
                )) {
                $installed = Join-Path $root $relative
                if (Test-Path -LiteralPath $installed -PathType Leaf) {
                    return $installed
                }
            }
        }
    }
    throw "Required command 'jq' was not found on PATH or in a known " +
        "installation directory. The shell release verifiers parse GitHub " +
        "API responses with jq and stop without it. Install it with " +
        "'winget install jqlang.jq' and reopen the terminal."
}

function Resolve-Python {
    param(
        [AllowEmptyString()]
        [string]$ConfiguredPath = $PythonPath
    )

    $hasConfiguredPath = -not [string]::IsNullOrWhiteSpace($ConfiguredPath)
    $candidates = if ($hasConfiguredPath) {
        @($ConfiguredPath)
    }
    elseif ($IsWindows) {
        @("python", "python3")
    }
    else {
        @("python3", "python")
    }
    foreach ($candidate in $candidates) {
        $resolved = Get-Command $candidate -CommandType Application `
            -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($null -eq $resolved) {
            continue
        }
        $probe = & $resolved.Source -c (
            "import sys; print(f'{sys.version_info.major}.{sys.version_info.minor}')"
        ) 2>&1 | Out-String
        $probeExitCode = $LASTEXITCODE
        $version = [regex]::Match($probe.Trim(), "^([0-9]+)\.([0-9]+)$")
        if ($probeExitCode -eq 0 -and
            $version.Success -and
            [int]$version.Groups[1].Value -eq 3 -and
            [int]$version.Groups[2].Value -ge 11) {
            return $resolved.Source
        }
    }
    if ($hasConfiguredPath) {
        throw "Configured Python '$ConfiguredPath' is missing, unusable, or older than 3.11."
    }
    throw "A working Python 3.11 or newer was not found. Set -PythonPath or TOPIAFORGE_PYTHON."
}

function ConvertTo-SshPublicKeyIdentity {
    param([Parameter(Mandatory = $true)][string]$Value)
    if ($Value.Contains("`0") -or $Value.Contains("`r") -or
        $Value.Contains("`n")) {
        throw "SSH public keys must contain exactly one newline-free key."
    }
    $parts = @($Value.Trim() -split "\s+")
    if ($parts.Count -lt 2 -or
        $parts[0] -cnotmatch "^[A-Za-z0-9@._+-]+$" -or
        $parts[1] -cnotmatch "^[A-Za-z0-9+/]+={0,2}$") {
        throw "SSH public key syntax is invalid."
    }
    return "$($parts[0]) $($parts[1])"
}

function Assert-GitHubSshSigningKey {
    param(
        [Parameter(Mandatory = $true)][string]$SigningKey,
        [Parameter(Mandatory = $true)][string]$GitHubLogin
    )
    if ($GitHubLogin -cnotmatch "^[A-Za-z0-9-]+$") {
        throw "The authenticated GitHub login is invalid."
    }
    $publicKeyPath = "$SigningKey.pub"
    $null = Assert-BoundedRegularFile -Path $SigningKey -MaximumBytes 65536 `
        -Label "Configured SSH tag-signing private key"
    $null = Assert-BoundedRegularFile -Path $publicKeyPath -MaximumBytes 16384 `
        -Label "Configured SSH tag-signing public key"
    $privateFingerprint = Invoke-Checked ssh-keygen @(
        "-lf", $SigningKey
    ) -Capture
    $publicFingerprint = Invoke-Checked ssh-keygen @(
        "-lf", $publicKeyPath
    ) -Capture
    $fingerprintPattern = "\bSHA256:[A-Za-z0-9+/]+\b"
    $privateMatch = [regex]::Match($privateFingerprint, $fingerprintPattern)
    $publicMatch = [regex]::Match($publicFingerprint, $fingerprintPattern)
    if (-not $privateMatch.Success -or -not $publicMatch.Success -or
        $privateMatch.Value -cne $publicMatch.Value) {
        throw "The SSH tag-signing public key does not match its private key."
    }
    $expectedIdentity = ConvertTo-SshPublicKeyIdentity (
        Get-Content -LiteralPath $publicKeyPath -Raw
    ).Trim()
    $registeredText = Invoke-Checked $gitHubCli @(
        "api", "--paginate",
        "users/$GitHubLogin/ssh_signing_keys?per_page=100",
        "--jq", ".[].key"
    ) -Capture
    $registeredIdentities = @(
        $registeredText -split "\r?\n" |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
            ForEach-Object { ConvertTo-SshPublicKeyIdentity $_ }
    )
    if ($registeredIdentities -cnotcontains $expectedIdentity) {
        throw "The configured SSH tag-signing key is not registered as a " +
            "GitHub signing key for $GitHubLogin. Register $publicKeyPath " +
            "before creating an immutable version tag."
    }
}

function Assert-GitHubOpenPgpSigningKey {
    param(
        [Parameter(Mandatory = $true)][string]$SigningKey,
        [Parameter(Mandatory = $true)][string]$GitHubLogin
    )
    if ($GitHubLogin -cnotmatch "^[A-Za-z0-9-]+$") {
        throw "The authenticated GitHub login is invalid."
    }
    $details = Invoke-Checked gpg @(
        "--batch",
        "--with-colons",
        "--fingerprint",
        "--fingerprint",
        "--list-secret-keys",
        "--",
        $SigningKey
    ) -Capture
    $localKeyIds = @(
        $details -split "\r?\n" |
            ForEach-Object {
                $fields = @($_ -split ":")
                if ($fields.Count -gt 9 -and $fields[0] -ceq "fpr" -and
                    $fields[9] -cmatch "^[0-9A-Fa-f]{40,64}$") {
                    $fields[9].Substring($fields[9].Length - 16).
                        ToUpperInvariant()
                }
            } |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
            Sort-Object -Unique
    )
    if ($localKeyIds.Count -eq 0) {
        throw "The configured OpenPGP tag-signing key has no secret signing identity."
    }
    $registeredText = Invoke-Checked $gitHubCli @(
        "api",
        "users/$GitHubLogin/gpg_keys?per_page=100",
        "--jq",
        ".[] | ((select(.can_sign == true) | .key_id), (.subkeys[]? | select(.can_sign == true) | .key_id))"
    ) -Capture
    $registeredKeyIds = @(
        $registeredText -split "\r?\n" |
            ForEach-Object {
                $value = $_.Trim().ToUpperInvariant()
                if ($value -cmatch "^[0-9A-F]{16,64}$") {
                    $value.Substring($value.Length - 16)
                }
            } |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
            Sort-Object -Unique
    )
    if (-not @(
            $localKeyIds |
                Where-Object { $registeredKeyIds -ccontains $_ }
        )) {
        throw "The configured OpenPGP tag-signing key is not registered as a GitHub signing key for $GitHubLogin."
    }
}

function Assert-GitHubTagSigningIdentity {
    param(
        [Parameter(Mandatory = $true)][string]$GitHubLogin
    )
    $signingKey = Invoke-Checked git @(
        "-C", $repositoryRoot, "config", "--get", "user.signingkey"
    ) -Capture
    if ([string]::IsNullOrWhiteSpace($signingKey)) {
        throw "git user.signingKey must identify the key used for signed release tags."
    }
    $signingFormat = & git -C $repositoryRoot config --get gpg.format 2>$null
    if ($LASTEXITCODE -notin @(0, 1)) {
        throw "Could not inspect the Git tag signing format."
    }
    if ([string]::IsNullOrWhiteSpace($signingFormat) -or
        $signingFormat -eq "openpgp") {
        Require-Command gpg | Out-Null
        Assert-GitHubOpenPgpSigningKey -SigningKey $signingKey `
            -GitHubLogin $GitHubLogin
    }
    elseif ($signingFormat -eq "ssh") {
        Require-Command ssh-keygen | Out-Null
        Assert-GitHubSshSigningKey -SigningKey $signingKey `
            -GitHubLogin $GitHubLogin
    }
    else {
        throw "Unsupported git gpg.format '$signingFormat' for release tags."
    }
}

function Get-Sha256 {
    param([Parameter(Mandatory = $true)][string]$Path)
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-Utf8Sha256 {
    param([Parameter(Mandatory = $true)][AllowEmptyString()][string]$Value)
    $bytes = [System.Text.UTF8Encoding]::new($false).GetBytes($Value)
    $hash = [System.Security.Cryptography.SHA256]::HashData($bytes)
    return [Convert]::ToHexString($hash).ToLowerInvariant()
}

function Get-BytesSha256 {
    param([Parameter(Mandatory = $true)][byte[]]$Value)
    $hash = [System.Security.Cryptography.SHA256]::HashData($Value)
    return [Convert]::ToHexString($hash).ToLowerInvariant()
}

function Assert-ExactJsonProperties {
    param(
        [Parameter(Mandatory = $true)][psobject]$Value,
        [Parameter(Mandatory = $true)][string[]]$Expected,
        [Parameter(Mandatory = $true)][string]$Label
    )
    $actual = @($Value.PSObject.Properties.Name | Sort-Object)
    $expectedSorted = @($Expected | Sort-Object)
    if (Compare-Object $actual $expectedSorted -SyncWindow 0) {
        throw "$Label does not contain the exact expected fields."
    }
}

function Assert-ByteIdenticalMetadata {
    param(
        [Parameter(Mandatory = $true)][string]$ExpectedPath,
        [Parameter(Mandatory = $true)][string]$ActualPath,
        [Parameter(Mandatory = $true)][string]$Label
    )
    foreach ($path in @($ExpectedPath, $ActualPath)) {
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "$Label is missing."
        }
        $file = Get-Item -LiteralPath $path -Force
        if (($file.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0 -or
            $file.Length -le 0 -or $file.Length -gt 2097152) {
            throw "$Label is not a bounded regular metadata file."
        }
    }
    $expectedBytes = [System.IO.File]::ReadAllBytes($ExpectedPath)
    $actualBytes = [System.IO.File]::ReadAllBytes($ActualPath)
    if ($expectedBytes.Length -ne $actualBytes.Length) {
        throw "$Label no longer matches its frozen bytes."
    }
    for ($index = 0; $index -lt $expectedBytes.Length; $index++) {
        if ($expectedBytes[$index] -ne $actualBytes[$index]) {
            throw "$Label no longer matches its frozen bytes."
        }
    }
}

function Assert-BoundedRegularFile {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][Int64]$MaximumBytes,
        [Parameter(Mandatory = $true)][string]$Label
    )
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "$Label is missing."
    }
    $file = Get-Item -LiteralPath $Path -Force
    if (($file.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0 -or
        $file.Length -le 0 -or $file.Length -gt $MaximumBytes) {
        throw "$Label is not a bounded regular file."
    }
    return $file
}

function Get-GitBlobBytes {
    param(
        [Parameter(Mandatory = $true)][string]$SourceSha,
        [Parameter(Mandatory = $true)][string]$GitPath
    )
    if ($SourceSha -cnotmatch "^[0-9a-f]{40}$" -or
        $GitPath -cnotmatch "^[A-Za-z0-9._/-]+$" -or
        $GitPath.StartsWith("/") -or
        @($GitPath -split "/") -contains "..") {
        throw "Git blob identity is invalid."
    }
    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = "git"
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    foreach ($argument in @(
            "-C",
            $repositoryRoot,
            "cat-file",
            "blob",
            "$SourceSha`:$GitPath"
        )) {
        $startInfo.ArgumentList.Add($argument)
    }
    $process = [System.Diagnostics.Process]::new()
    $memory = [System.IO.MemoryStream]::new()
    try {
        $process.StartInfo = $startInfo
        if (-not $process.Start()) {
            throw "Could not start git to read the frozen blob."
        }
        $standardError = $process.StandardError.ReadToEndAsync()
        $process.StandardOutput.BaseStream.CopyTo($memory)
        $process.WaitForExit()
        $errorText = $standardError.GetAwaiter().GetResult()
        if ($process.ExitCode -ne 0) {
            throw "Could not read $GitPath at $SourceSha. $errorText"
        }
        return ,$memory.ToArray()
    }
    finally {
        $memory.Dispose()
        $process.Dispose()
    }
}

$gitHubCli = Resolve-GitHubCli
$bashCli = Resolve-Bash
$jqCli = Resolve-Jq

function Invoke-ReleaseGovernanceAudit {
    $pythonCli = Resolve-Python
    $hadOverride = Test-Path Env:TOPIAFORGE_GH_CLI
    $previousOverride = $env:TOPIAFORGE_GH_CLI
    # The shell verifiers invoke jq by name. Prepend its resolved directory so a
    # jq that exists only in a known installation directory still satisfies their
    # own 'command -v jq' guard.
    $previousPath = $env:PATH
    $jqDirectory = Split-Path -Parent $jqCli
    try {
        $env:TOPIAFORGE_GH_CLI = $gitHubCli
        if (-not [string]::IsNullOrWhiteSpace($jqDirectory)) {
            $env:PATH = "$jqDirectory$([System.IO.Path]::PathSeparator)$previousPath"
        }
        Invoke-Checked $bashCli @(
            "./tools/verify-release-governance.sh",
            $Repository
        )
        Invoke-Checked $pythonCli @(
            ".github/scripts/audit_repository_governance.py",
            "check",
            "--repo",
            $Repository
        )
    }
    finally {
        $env:PATH = $previousPath
        if ($hadOverride) {
            $env:TOPIAFORGE_GH_CLI = $previousOverride
        }
        else {
            Remove-Item Env:TOPIAFORGE_GH_CLI -ErrorAction SilentlyContinue
        }
    }
}

function Get-GitHubRepositoryFromRemote {
    param([Parameter(Mandatory = $true)][string]$RemoteUrl)
    $suffix = $null
    foreach ($prefix in @(
            "https://github.com/",
            "git@github.com:",
            "ssh://git@github.com/"
        )) {
        if ($RemoteUrl.StartsWith(
                $prefix,
                [System.StringComparison]::OrdinalIgnoreCase
            )) {
            $suffix = $RemoteUrl.Substring($prefix.Length).TrimEnd("/")
            break
        }
    }
    if ([string]::IsNullOrWhiteSpace($suffix)) {
        throw "Git origin must be a credential-free github.com repository URL."
    }
    if ($suffix.EndsWith(
            ".git",
            [System.StringComparison]::OrdinalIgnoreCase
        )) {
        $suffix = $suffix.Substring(0, $suffix.Length - 4)
    }
    if ($suffix -notmatch "^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$") {
        throw "Git origin must identify exactly one github.com owner/repository."
    }
    return $suffix
}

function Get-RobotopiaInstalledBuildId {
    param([Parameter(Mandatory = $true)][string]$GameRoot)
    $resolvedGameRoot = [System.IO.Path]::GetFullPath($GameRoot).TrimEnd("\", "/")
    $candidates = [System.Collections.Generic.List[string]]::new()
    $candidates.Add((Join-Path $resolvedGameRoot "installed-build.json"))
    if ((Split-Path -Leaf $resolvedGameRoot).Equals(
            "Robotopia",
            [System.StringComparison]::OrdinalIgnoreCase
        )) {
        $candidates.Add((Join-Path (Split-Path -Parent $resolvedGameRoot) `
                    "installed-build.json"))
    }
    foreach ($candidate in $candidates) {
        if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) {
            continue
        }
        $length = (Get-Item -LiteralPath $candidate).Length
        if ($length -le 0 -or $length -gt 4096) {
            throw "Robotopia installed-build.json is invalid."
        }
        try {
            $metadata = Get-Content -LiteralPath $candidate -Raw |
                ConvertFrom-Json
            if ($metadata.PSObject.Properties.Name -notcontains "id") {
                throw "missing id"
            }
            $idText = [string]$metadata.id
            if ($idText -notmatch "^[1-9][0-9]*$") {
                throw "invalid id"
            }
            return [int]$idText
        }
        catch {
            throw "Robotopia installed-build.json is invalid."
        }
    }
    throw "Robotopia installed-build.json is missing."
}

function Get-RobotopiaOfficialInstall {
    param([Parameter(Mandatory = $true)][string]$GameRoot)
    $verifier = Join-Path $repositoryRoot `
        "tools/release/verify-robotopia-install.ps1"
    $metadata = Join-Path $repositoryRoot `
        ([string]$policy.gameBuild.metadataFile)
    $result = & $verifier -GameDirectory $GameRoot -MetadataPath $metadata |
        Out-String
    if (-not $?) {
        throw "Official Robotopia installation verification failed."
    }
    try {
        return $result.Trim() | ConvertFrom-Json
    }
    catch {
        throw "Official Robotopia installation verifier returned invalid JSON."
    }
}

function Clear-ReleaseDirectory {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$AllowedParent
    )
    $resolvedPath = [System.IO.Path]::GetFullPath($Path)
    $resolvedParent = [System.IO.Path]::GetFullPath($AllowedParent)
    $prefix = $resolvedParent.TrimEnd("\", "/") +
        [System.IO.Path]::DirectorySeparatorChar
    if (-not $resolvedPath.StartsWith(
            $prefix,
            [System.StringComparison]::OrdinalIgnoreCase
        )) {
        throw "Refusing to clear a path outside the release state: $resolvedPath"
    }
    if (Test-Path -LiteralPath $resolvedPath) {
        Remove-Item -LiteralPath $resolvedPath -Recurse -Force
    }
    New-Item -ItemType Directory -Force -Path $resolvedPath | Out-Null
}

function Read-State {
    if (-not (Test-Path -LiteralPath $statePath -PathType Leaf)) {
        return $null
    }
    return Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
}

function Use-StateConfiguration {
    param([Parameter(Mandatory = $true)][psobject]$State)
    $immutableBindings = @(
        @("Repository", "repository"),
        @("WslDistribution", "wslDistribution"),
        @("ProtonExecutable", "protonExecutable"),
        @("SteamRoot", "steamRoot"),
        @("CompatDataRoot", "compatDataRoot"),
        @("UnityPath", "unityPath"),
        @("GameDirectory", "gameDirectory")
    )
    foreach ($binding in $immutableBindings) {
        $parameterName = $binding[0]
        $stateName = $binding[1]
        if ($State.PSObject.Properties.Name -notcontains $stateName) {
            continue
        }
        $storedValue = [string]$State.$stateName
        if ($explicitParameters.ContainsKey($parameterName)) {
            $requestedValue = [string](Get-Variable -Scope Script -Name $parameterName).Value
            if (-not [string]::Equals(
                    $requestedValue,
                    $storedValue,
                    [System.StringComparison]::Ordinal
                )) {
                throw "Cannot change $parameterName while resuming a frozen release."
            }
        }
        else {
            Set-Variable -Scope Script -Name $parameterName -Value $storedValue
        }
    }

    foreach ($binding in @(
            @("WindowsCreatorEvidence", "windowsCreatorEvidence"),
            @("WindowsCreatorEvidenceBundle", "windowsCreatorEvidenceBundle")
        )) {
        $parameterName = $binding[0]
        $stateName = $binding[1]
        $hasStoredValue = $State.PSObject.Properties.Name -contains $stateName -and
            -not [string]::IsNullOrWhiteSpace([string]$State.$stateName)
        $evidenceIsFrozen = [string]$State.phase -in @(
            "built", "staged", "dispatch-requested", "published"
        )
        if ($explicitParameters.ContainsKey($parameterName)) {
            $requestedValue = [string](Get-Variable -Scope Script -Name $parameterName).Value
            if ($evidenceIsFrozen -and $hasStoredValue -and
                -not [string]::Equals(
                    $requestedValue,
                    [string]$State.$stateName,
                    [System.StringComparison]::Ordinal
                )) {
                throw "Cannot replace $parameterName after it is frozen in release state."
            }
        }
        elseif ($hasStoredValue) {
            Set-Variable -Scope Script -Name $parameterName -Value $State.$stateName
        }
    }

    if ($State.PSObject.Properties.Name -contains "rehearsal") {
        $storedRehearsal = [bool]$State.rehearsal
        if ($explicitParameters.ContainsKey("Rehearsal") -and
            [bool]$Rehearsal -ne $storedRehearsal) {
            throw "Cannot change Rehearsal while resuming a frozen release."
        }
        $script:Rehearsal = $storedRehearsal
    }

}

function Write-State {
    param(
        [Parameter(Mandatory = $true)][string]$Phase,
        [Parameter(Mandatory = $true)][string]$SourceSha,
        [hashtable]$Additional = @{}
    )
    New-Item -ItemType Directory -Force -Path $stateDirectory | Out-Null
    $body = [ordered]@{
        schema = "release-admin-state-v1"
        version = $Version
        tag = $tag
        sourceSha = $SourceSha
        phase = $Phase
        rehearsal = [bool]$Rehearsal
        repository = $Repository
        wslDistribution = $WslDistribution
        protonExecutable = $ProtonExecutable
        steamRoot = $SteamRoot
        compatDataRoot = $CompatDataRoot
        unityPath = $UnityPath
        gameDirectory = $GameDirectory
        windowsCreatorEvidence = $WindowsCreatorEvidence
        windowsCreatorEvidenceBundle = $WindowsCreatorEvidenceBundle
    }
    foreach ($entry in $Additional.GetEnumerator()) {
        $body[$entry.Key] = $entry.Value
    }
    $temporary = "$statePath.tmp"
    $body | ConvertTo-Json -Depth 8 |
        Set-Content -LiteralPath $temporary -Encoding utf8NoBOM
    Move-Item -LiteralPath $temporary -Destination $statePath -Force
}

function Assert-SourceStillExact {
    param([Parameter(Mandatory = $true)][string]$SourceSha)
    $head = Invoke-Checked git @("-C", $repositoryRoot, "rev-parse", "HEAD") -Capture
    if ($head -ne $SourceSha) {
        throw "The checkout moved after preflight. Expected $SourceSha, found $head."
    }
    $status = Invoke-Checked git @(
        "-C", $repositoryRoot, "status", "--porcelain", "--untracked-files=all"
    ) -Capture
    if (-not [string]::IsNullOrWhiteSpace($status)) {
        throw "The checkout is no longer clean."
    }
}

function Assert-OriginStillExact {
    param([Parameter(Mandatory = $true)][string]$SourceSha)
    $originUrl = Invoke-Checked git @(
        "-C", $repositoryRoot, "remote", "get-url", "origin"
    ) -Capture
    $originRepository = Get-GitHubRepositoryFromRemote $originUrl
    if (-not [string]::Equals(
            $originRepository,
            $Repository,
            [System.StringComparison]::OrdinalIgnoreCase
        )) {
        throw "Git origin changed after preflight and no longer matches -Repository."
    }
    Invoke-Checked git @(
        "-C", $repositoryRoot, "fetch", "--prune", "origin", "main"
    )
    $originMain = Invoke-Checked git @(
        "-C", $repositoryRoot, "rev-parse", "refs/remotes/origin/main"
    ) -Capture
    if ($originMain -ne $SourceSha) {
        throw "origin/main moved after preflight; start a new release from $originMain."
    }
}

function Assert-ReleaseOwnedPath {
    param([Parameter(Mandatory = $true)][string]$Path)
    $resolvedPath = [System.IO.Path]::GetFullPath($Path)
    $resolvedState = [System.IO.Path]::GetFullPath($stateDirectory)
    $prefix = $resolvedState.TrimEnd("\", "/") +
        [System.IO.Path]::DirectorySeparatorChar
    if (-not $resolvedPath.StartsWith(
            $prefix,
            [System.StringComparison]::OrdinalIgnoreCase
        )) {
        throw "Release work path is outside the owned state directory: $resolvedPath"
    }
    return $resolvedPath
}

function Remove-ReleaseWorktree {
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute(
        "PSUseShouldProcessForStateChangingFunctions",
        "",
        Justification = "The release transaction rejects -WhatIf and validates every path as release-owned before this internal cleanup helper runs."
    )]
    param([Parameter(Mandatory = $true)][string]$Path)
    $resolvedPath = Assert-ReleaseOwnedPath $Path
    $worktreeList = Invoke-Checked git @(
        "-C", $repositoryRoot, "worktree", "list", "--porcelain"
    ) -Capture
    $registered = $false
    foreach ($line in @($worktreeList -split "\r?\n")) {
        if ($line.StartsWith("worktree ", [System.StringComparison]::Ordinal)) {
            $listedPath = [System.IO.Path]::GetFullPath($line.Substring(9))
            if ($listedPath.Equals(
                    $resolvedPath,
                    [System.StringComparison]::OrdinalIgnoreCase
                )) {
                $registered = $true
                break
            }
        }
    }
    if ($registered) {
        Invoke-Checked git @(
            "-C", $repositoryRoot, "worktree", "remove", "--force", $resolvedPath
        )
    }
    if (Test-Path -LiteralPath $resolvedPath) {
        Remove-Item -LiteralPath $resolvedPath -Recurse -Force
    }
    Invoke-Checked git @("-C", $repositoryRoot, "worktree", "prune")
}

function New-ReleaseWorktree {
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute(
        "PSUseShouldProcessForStateChangingFunctions",
        "",
        Justification = "The release transaction rejects -WhatIf; independently skipping this internal worktree step would leave an invalid partial build."
    )]
    param(
        [Parameter(Mandatory = $true)]
        [ValidatePattern("^[a-z0-9-]+$")]
        [string]$Name,
        [Parameter(Mandatory = $true)]
        [ValidatePattern("^[0-9a-f]{40}$")]
        [string]$SourceSha
    )
    $worktreesRoot = Join-Path $stateDirectory "worktrees"
    New-Item -ItemType Directory -Force -Path $worktreesRoot | Out-Null
    $path = Assert-ReleaseOwnedPath (Join-Path $worktreesRoot $Name)
    Remove-ReleaseWorktree $path
    Invoke-Checked git @(
        "-C", $repositoryRoot, "worktree", "add", "--detach", $path, $SourceSha
    )
    $head = Invoke-Checked git @("-C", $path, "rev-parse", "HEAD") -Capture
    $status = Invoke-Checked git @(
        "-C", $path, "status", "--porcelain", "--untracked-files=all"
    ) -Capture
    if ($head -ne $SourceSha -or -not [string]::IsNullOrWhiteSpace($status)) {
        throw "Detached release worktree '$Name' is not a clean exact-SHA checkout."
    }
    Assert-GitLfsMaterialized -RepositoryPath $path -SourceSha $SourceSha
    return $path
}

function Get-DartAndFlutter {
    . (Join-Path $repositoryRoot "tools/flutter-sdk.ps1")
    return @{
        Dart = Resolve-TopiaForgeSdkCommand -Tool dart -RepositoryRoot $repositoryRoot
        Flutter = Resolve-TopiaForgeSdkCommand -Tool flutter -RepositoryRoot $repositoryRoot
    }
}

function Invoke-Preflight {
    $existingState = Read-State
    if ($null -ne $existingState) {
        if ($existingState.schema -ne "release-admin-state-v1" -or
            $existingState.version -ne $Version -or
            $existingState.tag -ne $tag -or
            $existingState.phase -notin @(
                "preflight", "platforms-built", "built", "staged",
                "dispatch-requested", "published"
            )) {
            throw "Existing release state is invalid or belongs to another candidate."
        }
        Use-StateConfiguration $existingState
    }
    Require-Command git | Out-Null
    Require-Command git-lfs | Out-Null

    $branch = Invoke-Checked git @(
        "-C", $repositoryRoot, "branch", "--show-current"
    ) -Capture
    if ($branch -ne "main") {
        throw "Production release preflight requires the main branch."
    }
    $status = Invoke-Checked git @(
        "-C", $repositoryRoot, "status", "--porcelain", "--untracked-files=all"
    ) -Capture
    if (-not [string]::IsNullOrWhiteSpace($status)) {
        throw "Production release preflight requires a clean checkout."
    }
    $head = Invoke-Checked git @(
        "-C", $repositoryRoot, "rev-parse", "HEAD"
    ) -Capture
    Assert-GitLfsMaterialized -RepositoryPath $repositoryRoot `
        -SourceSha $head
    if ($Repository -notmatch "^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$") {
        throw "Repository must be a GitHub owner/repository name."
    }
    $originUrl = Invoke-Checked git @(
        "-C", $repositoryRoot, "remote", "get-url", "origin"
    ) -Capture
    $originRepository = Get-GitHubRepositoryFromRemote $originUrl
    if (-not [string]::Equals(
            $originRepository,
            $Repository,
            [System.StringComparison]::OrdinalIgnoreCase
        )) {
        throw "Git origin '$originRepository' does not match -Repository '$Repository'."
    }
    Require-Command dotnet | Out-Null
    Require-Command node | Out-Null
    Require-Command tar | Out-Null
    Require-Command wsl | Out-Null
    Invoke-Checked git @("-C", $repositoryRoot, "fetch", "--prune", "origin", "main")
    $head = Invoke-Checked git @("-C", $repositoryRoot, "rev-parse", "HEAD") -Capture
    $originMain = Invoke-Checked git @(
        "-C", $repositoryRoot, "rev-parse", "refs/remotes/origin/main"
    ) -Capture
    if ($head -ne $originMain) {
        throw "Local main must exactly match origin/main."
    }
    if ($null -ne $existingState -and
        [string]$existingState.sourceSha -ne $head) {
        throw "Existing release state is frozen to a different source SHA."
    }
    $policyAtHead = Invoke-Checked git @(
        "-C", $repositoryRoot, "show", "$head`:release/release-policy.json"
    ) -Capture | ConvertFrom-Json
    if ($policyAtHead.versioning.productVersion -ne $Version) {
        throw "Version $Version does not match release policy at $head."
    }
    # The version is already checked against policy immediately above; the rule here is about signing
    # exceptions, not about which release line is being cut, so it must not be gated on a version literal.
    if ($policyAtHead.publication.PSObject.Properties.Name -contains
            "codeSigningException") {
        throw "Production release forbids every code-signing exception."
    }
    $sdk = Get-DartAndFlutter
    Invoke-Checked $sdk.Dart @(
        "run",
        "bin/topiaforge.dart",
        "release",
        "validate-readiness",
        "--version",
        $Version,
        "--target-sha",
        $head
    ) -WorkingDirectory (Join-Path $repositoryRoot "apps/topiaforge_cli")
    $windowsCertificatePin = ""
    if ($policyAtHead.signingIdentities.PSObject.Properties.Name -contains
        "windowsCertificateSha256") {
        $windowsCertificatePin =
            [string]$policyAtHead.signingIdentities.windowsCertificateSha256
    }
    if ($windowsCertificatePin -cnotmatch "^(?!0{64}$)[0-9a-f]{64}$") {
        throw "RC1 requires a reviewed nonzero Windows certificate SHA-256 pin."
    }
    Invoke-Checked $powerShellExecutable @(
        "-NoLogo",
        "-NoProfile",
        "-NonInteractive",
        "-File",
        $handoffSignatureScript,
        "-Mode",
        "ValidateCredentials",
        "-ExpectedCertificateSha256",
        $windowsCertificatePin
    )

    Invoke-Checked $gitHubCli @("auth", "status", "--hostname", "github.com")
    $githubLogin = Invoke-Checked $gitHubCli @(
        "api", "user", "--jq", ".login"
    ) -Capture
    $isAdmin = Invoke-Checked $gitHubCli @(
        "api", "repos/$Repository", "--jq", ".permissions.admin"
    ) -Capture
    if ($isAdmin -ne "true") {
        throw "The authenticated GitHub account must have repository admin permission."
    }
    Invoke-ReleaseGovernanceAudit

    Assert-GitHubTagSigningIdentity -GitHubLogin $githubLogin

    if ((Invoke-Checked dotnet @("--version") -Capture) -ne $policy.toolchains.dotnetSdk) {
        throw "Expected .NET SDK $($policy.toolchains.dotnetSdk)."
    }
    $nodeVersion = Invoke-Checked node @("--version") -Capture
    $expectedNodeVersion = "v$([string]$policy.toolchains.node)"
    if ($nodeVersion -cne $expectedNodeVersion) {
        throw "Expected Node $expectedNodeVersion; found $nodeVersion."
    }
    Assert-LatestRobotopiaBuild
    $flutterJson = Invoke-Checked $sdk.Flutter @("--version", "--machine") -Capture |
        ConvertFrom-Json
    if ($flutterJson.frameworkVersion -ne $policy.toolchains.flutter) {
        throw "Expected Flutter $($policy.toolchains.flutter)."
    }
    $dartVersion = Invoke-Checked $sdk.Dart @("--version") -Capture
    if ($dartVersion -notmatch [regex]::Escape(
            "Dart SDK version: $($policy.toolchains.dart)"
        )) {
        throw "Expected Dart $($policy.toolchains.dart)."
    }

    if (-not (Test-Path -LiteralPath $UnityPath -PathType Leaf)) {
        throw "The exact Unity editor was not found: $UnityPath"
    }
    $unityVersion = Invoke-Checked $UnityPath @("-version") -Capture
    if ($unityVersion -notmatch [regex]::Escape($policy.toolchains.unity)) {
        throw "Unity must be exactly $($policy.toolchains.unity)."
    }
    $managedDirectory = Join-Path $GameDirectory "Robotopia_Data/Managed"
    if (-not (Test-Path -LiteralPath $GameDirectory -PathType Container) -or
        -not (Test-Path -LiteralPath $managedDirectory -PathType Container)) {
        throw "The Windows Robotopia installation is missing or incomplete."
    }
    $installedGameBuildId = Get-RobotopiaInstalledBuildId $GameDirectory
    if ($installedGameBuildId -ne [int]$policy.gameBuild.id) {
        throw "Robotopia must be the pinned build $($policy.gameBuild.id); " +
            "found build $installedGameBuildId."
    }
    $officialInstall = Get-RobotopiaOfficialInstall -GameRoot $GameDirectory
    if ($officialInstall.buildId -ne [Int64]$policy.gameBuild.id -or
        [string]$officialInstall.archiveSha256 -cne
            [string]$((Get-Content -LiteralPath (
                Join-Path $repositoryRoot $policy.gameBuild.metadataFile
            ) -Raw | ConvertFrom-Json).archives.windows.sha256)) {
        throw "Robotopia official-install identity does not match release policy."
    }

    $vswhere = Join-Path ${env:ProgramFiles(x86)} `
        "Microsoft Visual Studio/Installer/vswhere.exe"
    if (-not (Test-Path -LiteralPath $vswhere -PathType Leaf)) {
        throw "Visual Studio Installer's vswhere.exe is required."
    }
    $visualStudio = Invoke-Checked $vswhere @(
        "-latest", "-products", "*",
        "-requires", "Microsoft.VisualStudio.Component.VC.Tools.x86.x64",
        "-property", "installationPath"
    ) -Capture
    $msvcVersionFile = Join-Path $visualStudio `
        "VC/Auxiliary/Build/Microsoft.VCToolsVersion.default.txt"
    if (-not (Test-Path -LiteralPath $msvcVersionFile -PathType Leaf) -or
        (Get-Content -LiteralPath $msvcVersionFile -Raw).Trim() -ne
            [string]$platformToolchains.windows.msvc) {
        throw "MSVC must be exactly $($platformToolchains.windows.msvc)."
    }
    $windowsSdkRoot = Join-Path ${env:ProgramFiles(x86)} "Windows Kits/10"
    $installedWindowsSdks = @(
        Get-ChildItem -LiteralPath (Join-Path $windowsSdkRoot "Include") `
            -Directory -ErrorAction SilentlyContinue |
            Where-Object {
                $_.Name -match "^[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+$"
            } |
            Sort-Object { [version]$_.Name }
    )
    $pinnedWindowsSdk = [string]$platformToolchains.windows.windowsSdk
    if ($installedWindowsSdks.Count -eq 0 -or
        [string]$installedWindowsSdks[-1].Name -ne $pinnedWindowsSdk -or
        -not (Test-Path -LiteralPath `
            (Join-Path $windowsSdkRoot "Lib/$pinnedWindowsSdk") `
            -PathType Container)) {
        $foundWindowsSdk = if ($installedWindowsSdks.Count -eq 0) {
            "none"
        }
        else {
            [string]$installedWindowsSdks[-1].Name
        }
        throw "Expected Windows SDK $pinnedWindowsSdk as the selected latest " +
            "SDK; found $foundWindowsSdk."
    }

    # The entire WSL builder and Proton preflight is skipped when Linux is not a
    # target platform. Re-adding TopiaForge-linux-x64.zip to the policy turns it
    # back on unchanged.
    if ($targetsLinux) {
    $wslInventory = Invoke-Checked wsl @("--list", "--verbose") -Capture
    if ($wslInventory -notmatch "(?m)^\s*\*?\s*$([regex]::Escape($WslDistribution))\s+\S+\s+2\s*$") {
        throw "$WslDistribution must be installed and configured as WSL2."
    }
    $wslRelease = Invoke-Checked wsl @(
        "--distribution", $WslDistribution, "--exec", "bash", "-lc",
        ". /etc/os-release && printf '%s %s %s' `"`$ID`" `"`$VERSION_ID`" `"`$(uname -m)`""
    ) -Capture
    if ($wslRelease -ne "ubuntu 24.04 x86_64") {
        throw "The WSL builder must be Ubuntu 24.04 x86_64."
    }
    $wslToolProbe = @'
set -euo pipefail
for command_name in git git-lfs jq tar unzip sha256sum dotnet node flutter dart clang cmake ninja pkg-config; do
  command -v "$command_name" >/dev/null
done
git lfs version >/dev/null
test "$(dotnet --version)" = "__DOTNET__"
test "$(node --version)" = "v__NODE__"
test "$(flutter --version --machine | jq -r .frameworkVersion)" = "__FLUTTER__"
dart --version 2>&1 | grep -F "Dart SDK version: __DART__" >/dev/null
'@
    $wslToolProbe = $wslToolProbe.
        Replace("__DOTNET__", [string]$policy.toolchains.dotnetSdk).
        Replace("__NODE__", [string]$policy.toolchains.node).
        Replace("__FLUTTER__", [string]$policy.toolchains.flutter).
        Replace("__DART__", [string]$policy.toolchains.dart)
    Invoke-Checked wsl @(
        "--distribution", $WslDistribution, "--exec", "bash", "-lc", $wslToolProbe
    )
    $wslPlatformVersions = Invoke-Checked wsl @(
        "--distribution", $WslDistribution, "--exec", "bash", "-lc",
        "set -euo pipefail; " +
        "clang --version | sed -nE '1s/.*clang version ([0-9.]+).*/\1/p'; " +
        "cmake --version | sed -nE '1s/.* ([0-9.]+)$/\1/p'; " +
        "ninja --version; pkg-config --modversion gtk+-3.0"
    ) -Capture
    $wslPlatformVersionLines = @(
        $wslPlatformVersions -split "\r?\n" |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
    )
    $expectedWslPlatformVersions = @(
        [string]$platformToolchains.linux.clang,
        [string]$platformToolchains.linux.cmake,
        [string]$platformToolchains.linux.ninja,
        [string]$platformToolchains.linux.gtk
    )
    if ($wslPlatformVersionLines.Count -ne 4 -or
        (Compare-Object $wslPlatformVersionLines $expectedWslPlatformVersions `
            -SyncWindow 0)) {
        throw "The WSL platform toolchains do not match release/platform-toolchains.json."
    }
    $repositoryWslPath = Invoke-Checked wsl @(
        "--distribution", $WslDistribution, "--exec",
        "wslpath", "-a", "-u", $repositoryRoot
    ) -Capture
    Invoke-Checked wsl @(
        "--distribution", $WslDistribution, "--exec",
        "git", "-c", "safe.directory=$repositoryWslPath",
        "-C", $repositoryWslPath, "cat-file", "-e", "$head^{commit}"
    )
    $wslLfsProbe = @'
set -euo pipefail
repository=$1
source_sha=$2
git -c "safe.directory=$repository" -C "$repository" lfs fsck "$source_sha"
inventory=$(git -c "safe.directory=$repository" -C "$repository" lfs ls-files -l)
[[ -n "$inventory" ]]
if grep -Ev '^[0-9a-f]{64} \* .+$' <<<"$inventory" | grep -q .; then
  printf 'Every tracked Git LFS object must be materialized in WSL.\n' >&2
  exit 1
fi
'@
    Invoke-WslScript -Script $wslLfsProbe -Arguments @(
        $repositoryWslPath,
        $head
    )

    foreach ($binding in @(
            @("ProtonExecutable", $ProtonExecutable),
            @("SteamRoot", $SteamRoot),
            @("CompatDataRoot", $CompatDataRoot)
        )) {
        if ([string]::IsNullOrWhiteSpace([string]$binding[1]) -or
            -not ([string]$binding[1]).StartsWith("/") -or
            ([string]$binding[1]).Contains("`0") -or
            ([string]$binding[1]).Contains("`r") -or
            ([string]$binding[1]).Contains("`n")) {
            throw "$($binding[0]) must be an absolute newline-free Linux path."
        }
    }
    if ([string]$platformToolchains.linux.proton -ne "10.0-4" -or
        [string]$platformToolchains.linux.executionEnvironment -ne "wsl2-wslg") {
        throw "Linux acceptance must pin Proton 10.0-4 on WSL2/WSLg."
    }
    $gameDirectoryWslPath = Invoke-Checked wsl @(
        "--distribution", $WslDistribution, "--exec",
        "wslpath", "-a", "-u", $GameDirectory
    ) -Capture
    Invoke-Checked wsl @(
        "--distribution", $WslDistribution, "--exec", "/bin/bash",
        "$repositoryWslPath/tools/release/test-proton.sh",
        "--preflight-only",
        "--repo", $repositoryWslPath,
        "--source-sha", $head,
        "--version", $Version,
        "--game-dir", $gameDirectoryWslPath,
        "--game-build-id", ([string]$policy.gameBuild.id),
        "--proton-executable", $ProtonExecutable,
        "--steam-root", $SteamRoot,
        "--compat-data-root", $CompatDataRoot
    )
    }

    New-Item -ItemType Directory -Force -Path $stateDirectory | Out-Null
    $unityLicenseLog = Join-Path $stateDirectory "preflight-unity-license.log"
    Invoke-Checked $UnityPath @(
        "-batchmode", "-quit",
        "-buildTarget", "StandaloneWindows64",
        "-projectPath", (Join-Path $repositoryRoot "tools/unity-ui-bundle"),
        "-logFile", $unityLicenseLog
    )
    $postUnityStatus = Invoke-Checked git @(
        "-C", $repositoryRoot, "status", "--porcelain", "--untracked-files=all"
    ) -Capture
    if (-not [string]::IsNullOrWhiteSpace($postUnityStatus)) {
        throw "Unity preflight changed the tracked checkout; restore it before release."
    }

    if ($null -eq $existingState) {
        Write-State -Phase "preflight" -SourceSha $head
    }
    Write-Host "Preflight passed for $tag at $head without regressing release state."
    return $head
}

function New-CanonicalEcosystem {
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute(
        "PSUseShouldProcessForStateChangingFunctions",
        "",
        Justification = "The release transaction rejects -WhatIf; this internal deterministic build must either complete in its release-owned output or fail."
    )]
    param(
        [Parameter(Mandatory = $true)][string]$SourceRoot,
        [Parameter(Mandatory = $true)][string]$Output,
        [Parameter(Mandatory = $true)][string]$Dart
    )
    Clear-ReleaseDirectory -Path $Output -AllowedParent $stateDirectory
    $cliProject = Join-Path $SourceRoot "apps/topiaforge_cli"
    Invoke-Checked $Dart @("pub", "get", "--enforce-lockfile") `
        -WorkingDirectory $cliProject
    Invoke-Checked $Dart @(
        "run", "bin/topiaforge.dart", "pack", "--all",
        "--output", $Output, "--configuration", "Release"
    ) -WorkingDirectory $cliProject
    Invoke-Checked $Dart @(
        "run", "bin/topiaforge.dart", "pack",
        "--project", (Join-Path $SourceRoot "mods/TopiaForge.CreatorTools"),
        "--output", $Output, "--configuration", "Release"
    ) -WorkingDirectory $cliProject
    Invoke-Checked $Dart @(
        "run", "bin/topiaforge.dart", "unity", "pack-packages",
        "--output", (Join-Path $Output "vpm")
    ) -WorkingDirectory $cliProject

    $packages = @(Get-ChildItem -LiteralPath $Output -File -Filter "*.topiaforgemod")
    $vpmPackages = @(Get-ChildItem -LiteralPath (Join-Path $Output "vpm") -File -Filter "*.zip")
    if ($packages.Count -ne 15 -or $vpmPackages.Count -ne 3) {
        throw "Canonical ecosystem must contain exactly 15 mod and 3 VPM packages."
    }
    foreach ($package in $packages) {
        Invoke-Checked $Dart @(
            "run", "bin/topiaforge.dart", "check", "package", $package.FullName
        ) -WorkingDirectory $cliProject
    }
}

function Get-TreeManifest {
    param([Parameter(Mandatory = $true)][string]$Root)
    $rootPath = [System.IO.Path]::GetFullPath($Root).TrimEnd("\", "/")
    $filesByRelativePath = [System.Collections.Generic.Dictionary[string, string]]::new(
        [System.StringComparer]::Ordinal
    )
    foreach ($file in Get-ChildItem -LiteralPath $rootPath -File -Recurse) {
        $relative = $file.FullName.Substring($rootPath.Length + 1).Replace("\", "/")
        $filesByRelativePath.Add($relative, $file.FullName)
    }
    $relativePaths = [string[]]@($filesByRelativePath.Keys)
    [System.Array]::Sort($relativePaths, [System.StringComparer]::Ordinal)
    return @(
        foreach ($relative in $relativePaths) {
            "$((Get-Sha256 $filesByRelativePath[$relative]))  ./$relative"
        }
    )
}

function Invoke-WslBuild {
    param(
        [Parameter(Mandatory = $true)][string]$SourceSha,
        [Parameter(Mandatory = $true)][string]$CanonicalArchive,
        [Parameter(Mandatory = $true)][string]$CanonicalEcosystemSha,
        [Parameter(Mandatory = $true)][string]$CanonicalArchiveSha
    )
    $repoWsl = Invoke-Checked wsl @(
        "-d", $WslDistribution, "--", "wslpath", "-a", "-u", $repositoryRoot
    ) -Capture
    $canonicalWsl = Invoke-Checked wsl @(
        "-d", $WslDistribution, "--", "wslpath", "-a", "-u", $CanonicalArchive
    ) -Capture
    $assetsWsl = Invoke-Checked wsl @(
        "-d", $WslDistribution, "--", "wslpath", "-a", "-u", $assetsDirectory
    ) -Capture
    $remoteRoot = "/tmp/topiaforge-release-$($SourceSha.Substring(0, 16))-linux"
    $linuxScript = @'
set -euo pipefail
source_repository=$1
source_sha=$2
version=$3
canonical_archive=$4
canonical_ecosystem_sha=$5
canonical_archive_sha=$6
output=$7
remote_root=$8
case "$remote_root" in
  /tmp/topiaforge-release-[0-9a-f]*-linux) ;;
  *) printf 'Unsafe WSL release root.\n' >&2; exit 1 ;;
esac
checkout="$remote_root/repo"
cleanup() {
  rm -rf -- "$remote_root"
}
trap cleanup EXIT
cleanup
mkdir -p "$remote_root"
git -c "safe.directory=$source_repository" clone \
  --no-hardlinks --no-checkout -- "$source_repository" "$checkout"
git -C "$checkout" checkout --detach "$source_sha"
test "$(git -C "$checkout" rev-parse HEAD)" = "$source_sha"
test -z "$(git -C "$checkout" status --porcelain --untracked-files=all)"
git -C "$checkout" lfs fsck "$source_sha"
lfs_inventory=$(git -C "$checkout" lfs ls-files -l)
[[ -n "$lfs_inventory" ]]
if grep -Ev '^[0-9a-f]{64} \* .+$' <<<"$lfs_inventory" | grep -q .; then
  printf 'The WSL clone contains unmaterialized Git LFS pointers.\n' >&2
  exit 1
fi
/bin/bash "$checkout/tools/release/build-platform.sh" \
  --platform linux \
  --repo "$checkout" \
  --sha "$source_sha" \
  --version "$version" \
  --canonical-archive "$canonical_archive" \
  --canonical-ecosystem-sha256 "$canonical_ecosystem_sha" \
  --canonical-archive-sha256 "$canonical_archive_sha" \
  --output "$output"
'@
    Invoke-WslScript -Script $linuxScript -Arguments @(
        $repoWsl,
        $SourceSha,
        $Version,
        $canonicalWsl,
        $CanonicalEcosystemSha,
        $CanonicalArchiveSha,
        $assetsWsl,
        $remoteRoot
    )
}

function Assert-LiveAcceptancePackageReceipt {
    param(
        [Parameter(Mandatory = $true)][psobject]$Receipt,
        [Parameter(Mandatory = $true)][string]$Label
    )
    Assert-ExactJsonProperties -Value $Receipt `
        -Expected @("criticalFiles", "sourceSha256") -Label $Label
    if ([string]$Receipt.sourceSha256 -cnotmatch "^[0-9a-f]{64}$") {
        throw "$Label has an invalid source SHA-256."
    }
    $criticalFiles = @($Receipt.criticalFiles)
    if ($criticalFiles.Count -lt 1 -or $criticalFiles.Count -gt 8192) {
        throw "$Label has an invalid critical-file inventory."
    }
    $previousPath = $null
    $paths = [System.Collections.Generic.HashSet[string]]::new(
        [System.StringComparer]::Ordinal
    )
    foreach ($criticalFile in $criticalFiles) {
        Assert-ExactJsonProperties -Value $criticalFile `
            -Expected @("path", "sha256") -Label "$Label critical file"
        $criticalPath = [string]$criticalFile.path
        $segments = @($criticalPath -split "/")
        if ($criticalPath.Length -gt 512 -or
            $criticalPath -cnotmatch "^[A-Za-z0-9][A-Za-z0-9._/-]*$" -or
            $criticalPath.Contains("\") -or
            $criticalPath.StartsWith("/") -or
            $criticalPath.EndsWith("/") -or
            $criticalPath.Contains("//") -or
            @($segments | Where-Object { $_ -in @(".", "..") }).Count -ne 0 -or
            [string]$criticalFile.sha256 -cnotmatch "^[0-9a-f]{64}$" -or
            -not $paths.Add($criticalPath) -or
            ($null -ne $previousPath -and
                [StringComparer]::Ordinal.Compare(
                    $previousPath,
                    $criticalPath
                ) -ge 0)) {
            throw "$Label has an unsafe or non-deterministic critical-file receipt."
        }
        $previousPath = $criticalPath
    }
}

function Assert-WindowsRuntimeEvidence {
    param(
        [Parameter(Mandatory = $true)][string]$SourceSha,
        [Parameter(Mandatory = $true)][psobject]$Validation
    )
    Assert-ExactJsonProperties -Value $Validation.evidenceSha256 `
        -Expected @("robotopia", "unity") `
        -Label "Windows validation evidenceSha256"
    $gameMetadataBytes = Get-GitBlobBytes -SourceSha $SourceSha `
        -GitPath ([string]$policy.gameBuild.metadataFile)
    try {
        $gameMetadata = [System.Text.UTF8Encoding]::new(
            $false,
            $true
        ).GetString($gameMetadataBytes) | ConvertFrom-Json
    }
    catch {
        throw "The source-SHA Robotopia build metadata is not valid UTF-8 JSON."
    }
    if ([string]$Validation.gameArchiveSha256 -cne
            [string]$gameMetadata.archives.windows.sha256 -or
        [string]$Validation.gameFilesManifestSha256 -cne
            [string]$gameMetadata.windowsFilesManifest.sha256 -or
        $Validation.gameFilesVerified -isnot [Int64] -or
        [Int64]$Validation.gameFilesVerified -ne
            [Int64]$gameMetadata.windowsFilesManifest.fileCount -or
        [string]$Validation.gameExecutableSha256 -cne
            [string]$gameMetadata.windowsFilesManifest.gameExecutableSha256) {
        throw "Windows validation did not bind the pinned official Robotopia bytes."
    }

    $unityPath = Join-Path $evidenceDirectory "windows/unity/lifecycle.json"
    $null = Assert-BoundedRegularFile -Path $unityPath -MaximumBytes 2097152 `
        -Label "Retained Unity lifecycle evidence"
    $unity = Get-Content -LiteralPath $unityPath -Raw | ConvertFrom-Json
    Assert-ExactJsonProperties -Value $unity -Expected @(
        "cycles",
        "editorVersion",
        "result",
        "schemaVersion",
        "uiAssemblyVersion",
        "validatorSmoke",
        "worldsAssemblyVersion"
    ) -Label "Retained Unity lifecycle evidence"
    if ($unity.schemaVersion -isnot [Int64] -or
        [Int64]$unity.schemaVersion -ne 1 -or
        [string]$unity.result -cne "pass" -or
        [string]$unity.editorVersion -cne [string]$policy.toolchains.unity -or
        $unity.cycles -isnot [Int64] -or [Int64]$unity.cycles -ne 16 -or
        $unity.validatorSmoke -ne $true -or
        [string]$unity.worldsAssemblyVersion -cnotmatch
            "^[0-9]+(?:\.[0-9]+){1,3}$" -or
        [string]$unity.uiAssemblyVersion -cnotmatch
            "^[0-9]+(?:\.[0-9]+){1,3}$" -or
        [string]$Validation.evidenceSha256.unity -cne (Get-Sha256 $unityPath)) {
        throw "Retained Unity lifecycle evidence does not match the exact release."
    }

    $robotopiaPath = Join-Path $evidenceDirectory `
        "windows/robotopia/acceptance-result.json"
    $null = Assert-BoundedRegularFile -Path $robotopiaPath `
        -MaximumBytes 16777216 -Label "Retained Robotopia acceptance evidence"
    $robotopia = Get-Content -LiteralPath $robotopiaPath -Raw |
        ConvertFrom-Json -DateKind String
    Assert-ExactJsonProperties -Value $robotopia -Expected @(
        "acceptanceChallenge",
        "acceptancePackageReceipt",
        "acceptancePackageStatus",
        "completedAtUtc",
        "failures",
        "gameDirectory",
        "lastRunSessionId",
        "missingCases",
        "package",
        "passedCases",
        "releaseJourneyAuthoringCommandCount",
        "releaseJourneyCli",
        "releaseJourneyEnabled",
        "releaseJourneyProject",
        "requiredCases",
        "requiredLoadedPackageId",
        "requiredLoadedPackageReceipt",
        "requiredLoadedPackageStatus",
        "requiredLogMarker",
        "requiredLogMarkerObserved",
        "schemaVersion",
        "startedAtUtc",
        "succeeded"
    ) -Label "Retained Robotopia acceptance evidence"
    Assert-LiveAcceptancePackageReceipt `
        -Receipt $robotopia.acceptancePackageReceipt `
        -Label "Retained Robotopia acceptance-package receipt"
    Assert-LiveAcceptancePackageReceipt `
        -Receipt $robotopia.requiredLoadedPackageReceipt `
        -Label "Retained Robotopia journey-package receipt"
    $caseInventoryBytes = Get-GitBlobBytes -SourceSha $SourceSha `
        -GitPath "tests/live-game-acceptance.json"
    try {
        $caseInventory = [System.Text.UTF8Encoding]::new(
            $false,
            $true
        ).GetString($caseInventoryBytes) | ConvertFrom-Json
    }
    catch {
        throw "The source-SHA Robotopia case inventory is not valid UTF-8 JSON."
    }
    $expectedCases = @(
        $caseInventory.cases | ForEach-Object { [string]$_.id }
    )
    if ($expectedCases.Count -eq 0 -or
        @($expectedCases | Sort-Object -Unique).Count -ne $expectedCases.Count) {
        throw "The Robotopia acceptance case inventory is invalid."
    }
    $requiredCases = @(
        $robotopia.requiredCases | ForEach-Object { [string]$_ }
    )
    $passedCases = @(
        $robotopia.passedCases | ForEach-Object { [string]$_ }
    )
    $expectedPassedCases = @($expectedCases | Sort-Object)
    $caseSetsMatch =
        $requiredCases.Count -eq $expectedCases.Count -and
        $passedCases.Count -eq $expectedCases.Count -and
        -not (Compare-Object $requiredCases $expectedCases -SyncWindow 0) -and
        -not (Compare-Object $passedCases $expectedPassedCases -SyncWindow 0)
    $journeyId = "dev.topiaforge.release-$($SourceSha.Substring(0, 12))"
    $journeyName = "TopiaForge release $Version"
    $expectedMarker = "$journeyName loaded. Run '$journeyId`:greet' to try its command."
    try {
        $started = [DateTimeOffset]::Parse([string]$robotopia.startedAtUtc)
        $completed = [DateTimeOffset]::Parse([string]$robotopia.completedAtUtc)
    }
    catch {
        throw "Retained Robotopia acceptance timestamps are invalid."
    }
    $storedGameDirectory = [System.IO.Path]::GetFullPath(
        [string]$robotopia.gameDirectory
    )
    $expectedGameDirectory = [System.IO.Path]::GetFullPath($GameDirectory)
    if ($robotopia.schemaVersion -isnot [Int64] -or
        [Int64]$robotopia.schemaVersion -ne 2 -or
        [string]$robotopia.acceptanceChallenge -cnotmatch
            "^[0-9a-f]{64}$" -or
        $robotopia.succeeded -ne $true -or
        @($robotopia.missingCases).Count -ne 0 -or
        @($robotopia.failures).Count -ne 0 -or
        -not $caseSetsMatch -or
        [string]$robotopia.acceptancePackageStatus -cne "loaded" -or
        [string]::IsNullOrWhiteSpace([string]$robotopia.lastRunSessionId) -or
        $robotopia.releaseJourneyEnabled -ne $true -or
        $robotopia.releaseJourneyAuthoringCommandCount -isnot [Int64] -or
        [Int64]$robotopia.releaseJourneyAuthoringCommandCount -ne 2 -or
        [string]$robotopia.requiredLoadedPackageId -cne $journeyId -or
        [string]$robotopia.requiredLoadedPackageStatus -cne "loaded" -or
        [string]$robotopia.requiredLogMarker -cne $expectedMarker -or
        $robotopia.requiredLogMarkerObserved -ne $true -or
        (Split-Path -Leaf ([string]$robotopia.releaseJourneyCli)) -cne
            "topiaforge.exe" -or
        (Split-Path -Leaf ([string]$robotopia.releaseJourneyProject)) -cne
            $journeyId -or
        (Split-Path -Leaf ([string]$robotopia.package)) -cnotmatch
            "^dev\.topiaforge\.sdk-acceptance-.+\.topiaforgemod$" -or
        -not $storedGameDirectory.Equals(
            $expectedGameDirectory,
            [System.StringComparison]::OrdinalIgnoreCase
        ) -or
        [string]$robotopia.startedAtUtc -cnotmatch "Z$" -or
        [string]$robotopia.completedAtUtc -cnotmatch "Z$" -or
        $completed -lt $started -or
        [string]$Validation.evidenceSha256.robotopia -cne
            (Get-Sha256 $robotopiaPath)) {
        throw "Retained Robotopia acceptance evidence does not match the exact release."
    }
}

function Assert-NoDuplicateJsonProperties {
    param(
        [Parameter(Mandatory = $true)]
        [System.Text.Json.JsonElement]$Element,
        [Parameter(Mandatory = $true)][string]$Label
    )
    if ($Element.ValueKind -eq [System.Text.Json.JsonValueKind]::Object) {
        $names = [System.Collections.Generic.HashSet[string]]::new(
            [System.StringComparer]::Ordinal
        )
        foreach ($property in $Element.EnumerateObject()) {
            if (-not $names.Add($property.Name)) {
                throw "$Label contains a duplicate JSON property."
            }
            Assert-NoDuplicateJsonProperties -Element $property.Value `
                -Label $Label
        }
    }
    elseif ($Element.ValueKind -eq [System.Text.Json.JsonValueKind]::Array) {
        foreach ($item in $Element.EnumerateArray()) {
            Assert-NoDuplicateJsonProperties -Element $item -Label $Label
        }
    }
}

function ConvertFrom-StrictJsonBytes {
    param(
        [Parameter(Mandatory = $true)][byte[]]$Bytes,
        [Parameter(Mandatory = $true)][string]$Label,
        [Parameter(Mandatory = $true)][Int64]$MaximumBytes
    )
    if ($Bytes.Length -le 0 -or $Bytes.Length -gt $MaximumBytes -or
        ($Bytes.Length -ge 3 -and
            $Bytes[0] -eq 0xef -and
            $Bytes[1] -eq 0xbb -and
            $Bytes[2] -eq 0xbf)) {
        throw "$Label must be bounded UTF-8 JSON without a byte-order mark."
    }
    try {
        $text = [System.Text.UTF8Encoding]::new($false, $true).
            GetString($Bytes)
        $document = [System.Text.Json.JsonDocument]::Parse(
            $text,
            [System.Text.Json.JsonDocumentOptions]@{
                AllowTrailingCommas = $false
                CommentHandling =
                    [System.Text.Json.JsonCommentHandling]::Disallow
                MaxDepth = 24
            }
        )
    }
    catch {
        throw "$Label is not strict JSON."
    }
    try {
        if ($document.RootElement.ValueKind -ne
            [System.Text.Json.JsonValueKind]::Object) {
            throw "$Label must be a JSON object."
        }
        Assert-NoDuplicateJsonProperties -Element $document.RootElement `
            -Label $Label
    }
    finally {
        $document.Dispose()
    }
    try {
        return $text | ConvertFrom-Json
    }
    catch {
        throw "$Label is not valid UTF-8 JSON."
    }
}

function Get-ZipEntrySha256 {
    param(
        [Parameter(Mandatory = $true)]
        [System.IO.Compression.ZipArchiveEntry]$Entry
    )
    $stream = $Entry.Open()
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        return [Convert]::ToHexString(
            $sha.ComputeHash($stream)
        ).ToLowerInvariant()
    }
    finally {
        $sha.Dispose()
        $stream.Dispose()
    }
}

function Assert-WindowsCreatorEvidencePair {
    param(
        [Parameter(Mandatory = $true)][string]$SourceSha,
        [Parameter(Mandatory = $true)][string]$WindowsArchive,
        [Parameter(Mandatory = $true)][string]$CanonicalSha,
        [Parameter(Mandatory = $true)][string]$DescriptorPath,
        [Parameter(Mandatory = $true)][string]$BundlePath
    )
    $descriptorFile = Assert-BoundedRegularFile -Path $DescriptorPath `
        -MaximumBytes 2097152 -Label "Windows Creator acceptance descriptor"
    $bundleFile = Assert-BoundedRegularFile -Path $BundlePath `
        -MaximumBytes 268435456 -Label "Windows Creator acceptance bundle"
    $descriptorBytes = [System.IO.File]::ReadAllBytes(
        $descriptorFile.FullName
    )
    $descriptor = ConvertFrom-StrictJsonBytes -Bytes $descriptorBytes `
        -Label "Windows Creator acceptance descriptor" `
        -MaximumBytes 2097152
    $descriptorText = [System.Text.UTF8Encoding]::new($false, $true).
        GetString($descriptorBytes)
    Assert-ExactJsonProperties -Value $descriptor -Expected @(
        "acceptanceChallenge",
        "acceptanceResultSha256",
        "archiveSha256",
        "archiveSize",
        "canonicalEcosystemSha256",
        "caseInventorySha256",
        "caseResults",
        "checkpointStateUnchanged",
        "creatorPackageReceipt",
        "evidenceSha256",
        "evidenceSize",
        "gameBuildId",
        "lastRunSessionId",
        "lifecycleCycles",
        "platform",
        "result",
        "saveStateUnchanged",
        "schema",
        "suite",
        "targetSha",
        "version"
    ) -Label "Windows Creator acceptance descriptor"
    Assert-ExactJsonProperties -Value $descriptor.creatorPackageReceipt `
        -Expected @("criticalFiles", "sourceSha256") `
        -Label "Windows Creator package receipt"
    $windowsSha = Get-Sha256 $WindowsArchive
    $windowsSize = (Get-Item -LiteralPath $WindowsArchive).Length
    $bundleSha = Get-Sha256 $BundlePath
    $bundleSize = $bundleFile.Length
    $caseInventoryBytes = Get-GitBlobBytes -SourceSha $SourceSha `
        -GitPath "tests/live-game-acceptance.json"
    $caseInventorySha = Get-BytesSha256 $caseInventoryBytes
    $caseInventory = ConvertFrom-StrictJsonBytes -Bytes $caseInventoryBytes `
        -Label "Source-SHA Creator case inventory" -MaximumBytes 2097152
    $sourcePolicyBytes = Get-GitBlobBytes -SourceSha $SourceSha `
        -GitPath "release/release-policy.json"
    $sourcePolicy = ConvertFrom-StrictJsonBytes -Bytes $sourcePolicyBytes `
        -Label "Source-SHA release policy" -MaximumBytes 2097152
    $expectedGameBuildId = [string]$sourcePolicy.gameBuild.id
    $expectedCaseIds = @(
        $caseInventory.creatorAcceptance.cases |
            ForEach-Object { [string]$_.id }
    )
    $uniqueExpectedCaseIds = @($expectedCaseIds | Sort-Object -Unique)
    if ($expectedCaseIds.Count -eq 0 -or
        $uniqueExpectedCaseIds.Count -ne $expectedCaseIds.Count -or
        @($expectedCaseIds | Where-Object {
                $_ -cnotmatch "^[a-z0-9][a-z0-9._-]{0,127}$"
            }).Count -ne 0 -or
        [string]$caseInventory.creatorAcceptance.gameBuild -ne
            $expectedGameBuildId) {
        throw "The Creator acceptance inventory is invalid for this release."
    }
    # v2 binds the descriptor to one interactive run: a one-run challenge the
    # recorder had to echo, the exact manager session that loaded the mod, the
    # exact CreatorTools payload receipt, and the acceptance result digest.
    # None of these can be produced by inspecting a directory of artifacts.
    $creatorReceipt = $descriptor.creatorPackageReceipt
    $receiptFiles = @($creatorReceipt.criticalFiles)
    $receiptValid = $receiptFiles.Count -ge 1 -and $receiptFiles.Count -le 8192
    $previousReceiptPath = $null
    foreach ($receiptFile in $receiptFiles) {
        try {
            Assert-ExactJsonProperties -Value $receiptFile `
                -Expected @("path", "sha256") `
                -Label "Windows Creator package receipt file"
        }
        catch {
            $receiptValid = $false
            break
        }
        $receiptPath = [string]$receiptFile.path
        if ([string]$receiptFile.sha256 -cnotmatch "^[0-9a-f]{64}$" -or
            $receiptPath -cnotmatch "^[A-Za-z0-9][A-Za-z0-9._/-]{0,511}$" -or
            $receiptPath.Contains("//") -or
            @($receiptPath -split "/" | Where-Object {
                    $_ -in @(".", "..")
                }).Count -ne 0 -or
            ($null -ne $previousReceiptPath -and
                [StringComparer]::Ordinal.Compare(
                    $previousReceiptPath,
                    $receiptPath
                ) -ge 0)) {
            $receiptValid = $false
            break
        }
        $previousReceiptPath = $receiptPath
    }

    $actualCaseResults = @($descriptor.caseResults)
    $caseResultsMatch = $actualCaseResults.Count -eq $expectedCaseIds.Count
    if ($caseResultsMatch) {
        foreach ($caseResult in $actualCaseResults) {
            try {
                Assert-ExactJsonProperties -Value $caseResult `
                    -Expected @("id", "result") `
                    -Label "Windows Creator case result"
            }
            catch {
                $caseResultsMatch = $false
                break
            }
        }
    }
    if ($caseResultsMatch) {
        for ($caseIndex = 0;
            $caseIndex -lt $expectedCaseIds.Count;
            $caseIndex++) {
            if ([string]$actualCaseResults[$caseIndex].id -cne
                    $expectedCaseIds[$caseIndex] -or
                [string]$actualCaseResults[$caseIndex].result -cne "pass") {
                $caseResultsMatch = $false
                break
            }
        }
    }
    if ($descriptor.schema -ne "release-windows-creator-evidence-v2" -or
        [string]$descriptor.acceptanceChallenge -cnotmatch "^[0-9a-f]{64}$" -or
        [string]$descriptor.acceptanceResultSha256 -cnotmatch
            "^[0-9a-f]{64}$" -or
        [string]$creatorReceipt.sourceSha256 -cnotmatch "^[0-9a-f]{64}$" -or
        -not $receiptValid -or
        [string]$descriptor.lastRunSessionId -cnotmatch
            "^[A-Za-z0-9][A-Za-z0-9._:-]{0,255}$" -or
        $descriptor.version -ne $Version -or
        $descriptor.targetSha -ne $SourceSha -or
        $descriptor.platform -ne "windows" -or
        $descriptor.archiveSha256 -ne $windowsSha -or
        $descriptor.archiveSize -isnot [Int64] -or
        [Int64]$descriptor.archiveSize -ne $windowsSize -or
        $descriptor.canonicalEcosystemSha256 -ne $CanonicalSha -or
        [string]$descriptor.gameBuildId -ne $expectedGameBuildId -or
        $descriptor.result -ne "pass" -or
        $descriptor.suite -ne "creator-full" -or
        $descriptor.caseInventorySha256 -ne $caseInventorySha -or
        -not $caseResultsMatch -or
        $descriptor.lifecycleCycles -isnot [Int64] -or
        [int]$descriptor.lifecycleCycles -lt
            [int]$caseInventory.creatorAcceptance.minimumLifecycleCycles -or
        $descriptor.saveStateUnchanged -ne $true -or
        $descriptor.checkpointStateUnchanged -ne $true -or
        $descriptor.evidenceSha256 -ne $bundleSha -or
        $descriptor.evidenceSize -isnot [Int64] -or
        [Int64]$descriptor.evidenceSize -ne $bundleSize) {
        throw "Windows Creator acceptance evidence does not match this exact candidate."
    }
    if ($descriptorText -match
        '(?i)"(?:username|hostname|machine|localPath|timestamp|startedAt|completedAt|credential|password|secret|token|rawLog)"\s*:') {
        throw "Windows Creator evidence contains forbidden machine-specific or sensitive fields."
    }

    $bundleStream = [System.IO.File]::Open(
        $bundleFile.FullName,
        [System.IO.FileMode]::Open,
        [System.IO.FileAccess]::Read,
        [System.IO.FileShare]::Read
    )
    try {
        $lockedBundleSha = [System.Security.Cryptography.SHA256]::HashData(
            $bundleStream
        )
        if ($bundleStream.Length -ne $bundleSize -or
            [Convert]::ToHexString($lockedBundleSha).ToLowerInvariant() -cne
                $bundleSha) {
            throw "Windows Creator evidence bundle changed while it was being validated."
        }
        $bundleStream.Position = 0
        try {
            $zip = [System.IO.Compression.ZipArchive]::new(
                $bundleStream,
                [System.IO.Compression.ZipArchiveMode]::Read,
                $false,
                [System.Text.UTF8Encoding]::new($false, $true)
            )
        }
        catch {
            throw "Windows Creator evidence bundle is not a valid ZIP."
        }
        try {
            $zipEntries = @($zip.Entries)
            if ($zipEntries.Count -lt 6 -or $zipEntries.Count -gt 518) {
                throw "Windows Creator evidence bundle entry count is invalid."
            }
            $entryByName = @{}
            $caseInsensitiveNames =
                [System.Collections.Generic.HashSet[string]]::new(
                    [System.StringComparer]::OrdinalIgnoreCase
                )
            $totalUncompressedBytes = [Int64]0
            foreach ($entry in $zipEntries) {
                $entryName = [string]$entry.FullName
                $segments = @($entryName -split "/")
                if ($entryName.Length -gt 240 -or
                    $entryName.Contains("\") -or
                    $entryName.StartsWith("/") -or
                    $entryName.EndsWith("/") -or
                    $entryName.Contains("//") -or
                    @($segments | Where-Object {
                            $_ -in @(".", "..") -or
                            $_ -cnotmatch
                                "^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$"
                        }).Count -ne 0 -or
                    $entry.ExternalAttributes -ne 0 -or
                    $entry.Length -le 0 -or
                    $entry.Length -gt 67108864 -or
                    $entry.CompressedLength -ne $entry.Length -or
                    $entry.LastWriteTime.Year -ne 1980 -or
                    $entry.LastWriteTime.Month -ne 1 -or
                    $entry.LastWriteTime.Day -ne 1 -or
                    $entry.LastWriteTime.Hour -ne 0 -or
                    $entry.LastWriteTime.Minute -ne 0 -or
                    $entry.LastWriteTime.Second -ne 0) {
                    throw "Windows Creator evidence bundle contains a non-deterministic, unsafe, compressed, reparse-like, or oversized entry."
                }
                if ($entryByName.ContainsKey($entryName) -or
                    -not $caseInsensitiveNames.Add($entryName)) {
                    throw "Windows Creator evidence bundle contains duplicate or case-colliding entries."
                }
                $entryByName[$entryName] = $entry
                $totalUncompressedBytes += [Int64]$entry.Length
                if ($totalUncompressedBytes -gt 250000000) {
                    throw "Windows Creator evidence bundle exceeds its uncompressed size limit."
                }
            }

            if (-not $entryByName.ContainsKey("bundle-manifest.json") -or
                $entryByName["bundle-manifest.json"].Length -gt 2097152) {
                throw "Windows Creator evidence bundle manifest is missing or oversized."
            }
            $manifestStream = $entryByName["bundle-manifest.json"].Open()
            $manifestMemory = [System.IO.MemoryStream]::new()
            try {
                $manifestStream.CopyTo($manifestMemory)
                $manifestBytes = $manifestMemory.ToArray()
            }
            finally {
                $manifestMemory.Dispose()
                $manifestStream.Dispose()
            }
            $bundleManifest = ConvertFrom-StrictJsonBytes `
                -Bytes $manifestBytes `
                -Label "Windows Creator evidence bundle manifest" `
                -MaximumBytes 2097152
            Assert-ExactJsonProperties -Value $bundleManifest -Expected @(
                "acceptanceChallenge",
                "acceptanceResult",
                "archiveSha256",
                "archiveSize",
                "canonicalEcosystemSha256",
                "caseInventorySha256",
                "cases",
                "gameBuildId",
                "lastRunSessionId",
                "lifecycleCycles",
                "platform",
                "schema",
                "stateSnapshots",
                "targetSha",
                "version"
            ) -Label "Windows Creator evidence bundle manifest"
            Assert-ExactJsonProperties -Value $bundleManifest.stateSnapshots `
                -Expected @("checkpoint", "layout", "save") `
                -Label "Windows Creator state-snapshot inventory"
            # The declared persistence layout travels with the evidence so a
            # game build that relocates or renames its persisted state is
            # rejected instead of silently reported as "unchanged".
            Assert-ExactJsonProperties `
                -Value $bundleManifest.stateSnapshots.layout `
                -Expected @("exclusions", "roots", "version") `
                -Label "Windows Creator persistence layout"
            Assert-ExactJsonProperties -Value $bundleManifest.acceptanceResult `
                -Expected @("entry", "sha256", "size") `
                -Label "Windows Creator acceptance result"
            foreach ($stateKind in @("save", "checkpoint")) {
                $statePair = $bundleManifest.stateSnapshots.$stateKind
                Assert-ExactJsonProperties -Value $statePair `
                    -Expected @("after", "before", "unchanged") `
                    -Label "Windows Creator $stateKind state inventory"
                foreach ($stateMoment in @("before", "after")) {
                    Assert-ExactJsonProperties `
                        -Value $statePair.$stateMoment `
                        -Expected @("entry", "sha256", "size") `
                        -Label "Windows Creator $stateKind-$stateMoment snapshot"
                }
            }
            $layoutRoots = @($bundleManifest.stateSnapshots.layout.roots)
            $layoutExclusions = @(
                $bundleManifest.stateSnapshots.layout.exclusions
            )
            if ($bundleManifest.schema -cne
                    "release-windows-creator-evidence-bundle-v2" -or
                [string]$bundleManifest.acceptanceChallenge -cne
                    [string]$descriptor.acceptanceChallenge -or
                [string]$bundleManifest.lastRunSessionId -cne
                    [string]$descriptor.lastRunSessionId -or
                [string]$bundleManifest.acceptanceResult.entry -cne
                    "acceptance/creator-acceptance-result.json" -or
                [string]$bundleManifest.acceptanceResult.sha256 -cne
                    [string]$descriptor.acceptanceResultSha256 -or
                $bundleManifest.acceptanceResult.size -isnot [Int64] -or
                [Int64]$bundleManifest.acceptanceResult.size -le 0 -or
                [Int64]$bundleManifest.acceptanceResult.size -gt 8388608 -or
                $bundleManifest.stateSnapshots.layout.version -isnot [Int64] -or
                [Int64]$bundleManifest.stateSnapshots.layout.version -lt 1 -or
                $layoutRoots.Count -lt 1 -or $layoutRoots.Count -gt 64 -or
                $layoutExclusions.Count -gt 256 -or
                @($layoutRoots | Where-Object {
                        [string]::IsNullOrWhiteSpace([string]$_)
                    }).Count -ne 0 -or
                @($layoutExclusions | Where-Object {
                        [string]::IsNullOrWhiteSpace([string]$_)
                    }).Count -ne 0 -or
                $bundleManifest.version -cne [string]$descriptor.version -or
                $bundleManifest.targetSha -cne [string]$descriptor.targetSha -or
                $bundleManifest.platform -cne "windows" -or
                $bundleManifest.archiveSha256 -cne
                    [string]$descriptor.archiveSha256 -or
                $bundleManifest.archiveSize -isnot [Int64] -or
                [Int64]$bundleManifest.archiveSize -ne
                    [Int64]$descriptor.archiveSize -or
                $bundleManifest.canonicalEcosystemSha256 -cne
                    [string]$descriptor.canonicalEcosystemSha256 -or
                [string]$bundleManifest.gameBuildId -cne
                    [string]$descriptor.gameBuildId -or
                $bundleManifest.caseInventorySha256 -cne
                    $caseInventorySha -or
                $bundleManifest.lifecycleCycles -isnot [Int64] -or
                [Int64]$bundleManifest.lifecycleCycles -ne
                    [Int64]$descriptor.lifecycleCycles) {
                throw "Windows Creator evidence bundle manifest does not match the public descriptor and exact candidate."
            }

            $referencedEntries = @{}
            $expectedCaseBundles = @($bundleManifest.cases)
            if ($expectedCaseBundles.Count -ne $expectedCaseIds.Count) {
                throw "Windows Creator evidence bundle has an incomplete case inventory."
            }
            for ($caseIndex = 0;
                $caseIndex -lt $expectedCaseIds.Count;
                $caseIndex++) {
                $caseBundle = $expectedCaseBundles[$caseIndex]
                Assert-ExactJsonProperties -Value $caseBundle `
                    -Expected @("artifacts", "id", "result") `
                    -Label "Windows Creator bundle case"
                if ([string]$caseBundle.id -cne
                        $expectedCaseIds[$caseIndex] -or
                    [string]$caseBundle.result -cne "pass" -or
                    [string]$caseBundle.id -cne
                        [string]$actualCaseResults[$caseIndex].id -or
                    [string]$caseBundle.result -cne
                        [string]$actualCaseResults[$caseIndex].result) {
                    throw "Windows Creator bundle case does not match the source-SHA inventory and public descriptor."
                }
                $artifacts = @($caseBundle.artifacts)
                if ($artifacts.Count -eq 0) {
                    throw "Every Windows Creator case must retain at least one evidence artifact."
                }
                $previousArtifactEntry = $null
                foreach ($artifact in $artifacts) {
                    Assert-ExactJsonProperties -Value $artifact `
                        -Expected @("entry", "sha256", "size") `
                        -Label "Windows Creator case artifact"
                    $artifactEntry = [string]$artifact.entry
                    $artifactPrefix = "artifacts/$($caseBundle.id)/"
                    $artifactSuffix = if ($artifactEntry.StartsWith(
                            $artifactPrefix,
                            [System.StringComparison]::Ordinal
                        )) {
                        $artifactEntry.Substring($artifactPrefix.Length)
                    }
                    else {
                        ""
                    }
                    if ([string]::IsNullOrWhiteSpace($artifactSuffix) -or
                        $artifact.sha256 -cnotmatch "^[0-9a-f]{64}$" -or
                        $artifact.size -isnot [Int64] -or
                        [Int64]$artifact.size -le 0 -or
                        [Int64]$artifact.size -gt 33554432 -or
                        $null -ne $previousArtifactEntry -and
                        [StringComparer]::Ordinal.Compare(
                            $previousArtifactEntry,
                            $artifactEntry
                        ) -ge 0 -or
                        $referencedEntries.ContainsKey($artifactEntry)) {
                        throw "Windows Creator case artifact inventory is unsafe or non-deterministic."
                    }
                    $artifactSegments = @($artifactSuffix -split "/")
                    if (@($artifactSegments | Where-Object {
                                $_ -in @(".", "..") -or
                                $_ -cnotmatch
                                    "^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$"
                            }).Count -ne 0) {
                        throw "Windows Creator case artifact path is unsafe."
                    }
                    $referencedEntries[$artifactEntry] = $artifact
                    $previousArtifactEntry = $artifactEntry
                }
            }

            $expectedStateEntries = [ordered]@{
                "state/checkpoint-after.bin" =
                    $bundleManifest.stateSnapshots.checkpoint.after
                "state/checkpoint-before.bin" =
                    $bundleManifest.stateSnapshots.checkpoint.before
                "state/save-after.bin" =
                    $bundleManifest.stateSnapshots.save.after
                "state/save-before.bin" =
                    $bundleManifest.stateSnapshots.save.before
            }
            foreach ($stateEntryName in $expectedStateEntries.Keys) {
                $stateRecord = $expectedStateEntries[$stateEntryName]
                if ([string]$stateRecord.entry -cne $stateEntryName -or
                    $stateRecord.sha256 -cnotmatch "^[0-9a-f]{64}$" -or
                    $stateRecord.size -isnot [Int64] -or
                    [Int64]$stateRecord.size -le 0 -or
                    [Int64]$stateRecord.size -gt 67108864) {
                    throw "Windows Creator state snapshot inventory is invalid."
                }
                $referencedEntries[$stateEntryName] = $stateRecord
            }
            $referencedEntries["acceptance/creator-acceptance-result.json"] =
                $bundleManifest.acceptanceResult
            $saveSnapshots = $bundleManifest.stateSnapshots.save
            $checkpointSnapshots =
                $bundleManifest.stateSnapshots.checkpoint
            if ($saveSnapshots.unchanged -ne $true -or
                $checkpointSnapshots.unchanged -ne $true -or
                $saveSnapshots.before.sha256 -cne
                    [string]$saveSnapshots.after.sha256 -or
                [Int64]$saveSnapshots.before.size -ne
                    [Int64]$saveSnapshots.after.size -or
                $checkpointSnapshots.before.sha256 -cne
                    [string]$checkpointSnapshots.after.sha256 -or
                [Int64]$checkpointSnapshots.before.size -ne
                    [Int64]$checkpointSnapshots.after.size -or
                $descriptor.saveStateUnchanged -ne $true -or
                $descriptor.checkpointStateUnchanged -ne $true) {
                throw "Windows Creator save or checkpoint state changed."
            }

            foreach ($referencedEntryName in $referencedEntries.Keys) {
                if (-not $entryByName.ContainsKey($referencedEntryName)) {
                    throw "Windows Creator evidence bundle is missing an inventoried entry."
                }
                $record = $referencedEntries[$referencedEntryName]
                $entry = $entryByName[$referencedEntryName]
                if ([Int64]$entry.Length -ne [Int64]$record.size -or
                    (Get-ZipEntrySha256 $entry) -cne
                        [string]$record.sha256) {
                    throw "Windows Creator embedded evidence bytes do not match their exact digest inventory."
                }
            }

            # The acceptance result is the recorder's own output. Re-derive its
            # claims here so the public descriptor cannot assert a pass the
            # interactive run never produced.
            $acceptanceEntry =
                $entryByName["acceptance/creator-acceptance-result.json"]
            $acceptanceStream = $acceptanceEntry.Open()
            $acceptanceMemory = [System.IO.MemoryStream]::new()
            try {
                $acceptanceStream.CopyTo($acceptanceMemory)
                $acceptanceBytes = $acceptanceMemory.ToArray()
            }
            finally {
                $acceptanceMemory.Dispose()
                $acceptanceStream.Dispose()
            }
            $acceptanceResult = ConvertFrom-StrictJsonBytes `
                -Bytes $acceptanceBytes `
                -Label "Windows Creator acceptance result" `
                -MaximumBytes 8388608
            $acceptancePassed = @(
                $acceptanceResult.passedCases | ForEach-Object { [string]$_ }
            )
            $acceptanceRequired = @(
                $acceptanceResult.requiredCases | ForEach-Object { [string]$_ }
            )
            $acceptanceReceiptFiles = @(
                $acceptanceResult.creatorPackageReceipt.criticalFiles |
                    ForEach-Object {
                        [string]$_.path + "=" + [string]$_.sha256
                    }
            )
            $descriptorReceiptFiles = @(
                $receiptFiles | ForEach-Object {
                    [string]$_.path + "=" + [string]$_.sha256
                }
            )
            if ($acceptanceResult.schemaVersion -ne 1 -or
                [string]$acceptanceResult.suite -cne "creator-full" -or
                $acceptanceResult.succeeded -ne $true -or
                [string]$acceptanceResult.acceptanceChallenge -cne
                    [string]$descriptor.acceptanceChallenge -or
                [string]$acceptanceResult.lastRunSessionId -cne
                    [string]$descriptor.lastRunSessionId -or
                [string]$acceptanceResult.creatorPackageReceipt.sourceSha256 `
                    -cne [string]$creatorReceipt.sourceSha256 -or
                $acceptanceReceiptFiles.Count -ne
                    $descriptorReceiptFiles.Count -or
                $null -ne (Compare-Object $acceptanceReceiptFiles `
                        $descriptorReceiptFiles -SyncWindow 0) -or
                [string]$acceptanceResult.gameBuild -cne
                    $expectedGameBuildId -or
                $acceptanceResult.lifecycleCycles -isnot [Int64] -or
                [Int64]$acceptanceResult.lifecycleCycles -ne
                    [Int64]$descriptor.lifecycleCycles -or
                $acceptanceResult.saveStateUnchanged -ne $true -or
                $acceptanceResult.checkpointStateUnchanged -ne $true -or
                @($acceptanceResult.failures).Count -ne 0 -or
                @($acceptanceResult.missingCases).Count -ne 0 -or
                $acceptancePassed.Count -ne $expectedCaseIds.Count -or
                $acceptanceRequired.Count -ne $expectedCaseIds.Count -or
                $null -ne (Compare-Object $acceptancePassed `
                        @($expectedCaseIds | Sort-Object) -SyncWindow 0) -or
                $null -ne (Compare-Object $acceptanceRequired `
                        @($expectedCaseIds | Sort-Object) -SyncWindow 0)) {
                throw "The retained Creator acceptance result does not prove this exact challenge-bound interactive run."
            }

            $expectedZipEntryNames = [string[]]@(
                @("bundle-manifest.json") + @($referencedEntries.Keys)
            )
            $sortedPayloadNames = [string[]]@($expectedZipEntryNames[1..(
                        $expectedZipEntryNames.Count - 1
                    )])
            [Array]::Sort($sortedPayloadNames, [StringComparer]::Ordinal)
            $expectedZipEntryNames =
                [string[]]@("bundle-manifest.json") + $sortedPayloadNames
            if ($zipEntries.Count -ne $expectedZipEntryNames.Count) {
                throw "Windows Creator evidence bundle contains unexpected entries."
            }
            for ($entryIndex = 0;
                $entryIndex -lt $expectedZipEntryNames.Count;
                $entryIndex++) {
                if ([string]$zipEntries[$entryIndex].FullName -cne
                    $expectedZipEntryNames[$entryIndex]) {
                    throw "Windows Creator evidence bundle contains unexpected or non-deterministically ordered entries."
                }
            }
        }
        finally {
            $zip.Dispose()
        }
    }
    finally {
        $bundleStream.Dispose()
    }
    return $descriptor
}

function Assert-WindowsCreatorEvidence {
    param(
        [Parameter(Mandatory = $true)][string]$SourceSha,
        [Parameter(Mandatory = $true)][string]$WindowsArchive,
        [Parameter(Mandatory = $true)][string]$CanonicalSha
    )
    if ([string]::IsNullOrWhiteSpace($WindowsCreatorEvidence) -or
        -not (Test-Path -LiteralPath $WindowsCreatorEvidence -PathType Leaf)) {
        throw "The local Windows Creator acceptance descriptor is required. " +
            "Builds are retained; provide -WindowsCreatorEvidence and run resume."
    }
    if ([string]::IsNullOrWhiteSpace($WindowsCreatorEvidenceBundle) -or
        -not (Test-Path -LiteralPath $WindowsCreatorEvidenceBundle -PathType Leaf)) {
        throw "The retained Windows Creator acceptance bundle is required. " +
            "Provide -WindowsCreatorEvidenceBundle and run resume."
    }
    $null = Assert-WindowsCreatorEvidencePair -SourceSha $SourceSha `
        -WindowsArchive $WindowsArchive -CanonicalSha $CanonicalSha `
        -DescriptorPath $WindowsCreatorEvidence `
        -BundlePath $WindowsCreatorEvidenceBundle
    $destination = Join-Path $evidenceDirectory "windows-creator"
    New-Item -ItemType Directory -Force -Path $destination | Out-Null
    $retainedDescriptor = Join-Path $destination "creator-evidence.json"
    $retainedBundle = Join-Path $destination "creator-evidence.bundle"
    Copy-Item -LiteralPath $WindowsCreatorEvidence `
        -Destination $retainedDescriptor -Force
    Copy-Item -LiteralPath $WindowsCreatorEvidenceBundle `
        -Destination $retainedBundle -Force
    Assert-ByteIdenticalMetadata -ExpectedPath $WindowsCreatorEvidence `
        -ActualPath $retainedDescriptor `
        -Label "Retained Windows Creator acceptance descriptor"
    if ((Get-Sha256 $WindowsCreatorEvidenceBundle) -cne
            (Get-Sha256 $retainedBundle) -or
        (Get-Item -LiteralPath $WindowsCreatorEvidenceBundle).Length -ne
            (Get-Item -LiteralPath $retainedBundle).Length) {
        throw "Retained Windows Creator acceptance bundle changed while it was copied."
    }
    $null = Assert-WindowsCreatorEvidencePair -SourceSha $SourceSha `
        -WindowsArchive $WindowsArchive -CanonicalSha $CanonicalSha `
        -DescriptorPath $retainedDescriptor -BundlePath $retainedBundle
}

function Assert-RetainedWindowsCreatorEvidence {
    param(
        [Parameter(Mandatory = $true)][string]$SourceSha,
        [Parameter(Mandatory = $true)][string]$WindowsArchive,
        [Parameter(Mandatory = $true)][string]$CanonicalSha
    )
    $destination = Join-Path $evidenceDirectory "windows-creator"
    return Assert-WindowsCreatorEvidencePair -SourceSha $SourceSha `
        -WindowsArchive $WindowsArchive -CanonicalSha $CanonicalSha `
        -DescriptorPath (Join-Path $destination "creator-evidence.json") `
        -BundlePath (Join-Path $destination "creator-evidence.bundle")
}

function Assert-ProtonEvidence {
    param(
        [Parameter(Mandatory = $true)][string]$SourceSha,
        [Parameter(Mandatory = $true)][string]$LinuxArchive,
        [Parameter(Mandatory = $true)][string]$CanonicalSha
    )
    $protonDirectory = Join-Path $evidenceDirectory "proton"
    $descriptorPath = Join-Path $protonDirectory "proton-evidence.json"
    $bundlePath = Join-Path $protonDirectory "proton-evidence.bundle"
    $null = Assert-BoundedRegularFile -Path $descriptorPath `
        -MaximumBytes 2097152 `
        -Label "Automatic same-host WSL2/WSLg Proton descriptor"
    $bundleFile = Assert-BoundedRegularFile -Path $bundlePath `
        -MaximumBytes 268435456 `
        -Label "Automatic same-host WSL2/WSLg Proton evidence bundle"
    $descriptorText = Get-Content -LiteralPath $descriptorPath -Raw
    $descriptor = $descriptorText | ConvertFrom-Json
    $linuxSha = Get-Sha256 $LinuxArchive
    $linuxSize = (Get-Item -LiteralPath $LinuxArchive).Length
    $bundleSha = Get-Sha256 $bundlePath
    $bundleSize = $bundleFile.Length
    $caseInventoryBytes = Get-GitBlobBytes -SourceSha $SourceSha `
        -GitPath "tests/live-game-acceptance.json"
    $caseInventorySha = Get-BytesSha256 $caseInventoryBytes
    try {
        $caseInventory = [System.Text.UTF8Encoding]::new(
            $false,
            $true
        ).GetString($caseInventoryBytes) | ConvertFrom-Json
    }
    catch {
        throw "The source-SHA Proton case inventory is not valid UTF-8 JSON."
    }
    $expectedCases = @(
        $caseInventory.cases |
            ForEach-Object { [string]$_.id } |
            Sort-Object
    )
    if ($expectedCases.Count -eq 0 -or
        @($expectedCases | Sort-Object -Unique).Count -ne $expectedCases.Count) {
        throw "The Proton acceptance case inventory is invalid."
    }
    $caseSetText = ($expectedCases -join "`n") + "`n"
    $caseSetSha = Get-Utf8Sha256 $caseSetText
    $requiredCases = @($descriptor.requiredCases | ForEach-Object { [string]$_ })
    $passedCases = @($descriptor.passedCases | ForEach-Object { [string]$_ })
    $caseSetsMatch =
        $requiredCases.Count -eq $expectedCases.Count -and
        $passedCases.Count -eq $expectedCases.Count -and
        -not (Compare-Object $requiredCases $expectedCases -SyncWindow 0) -and
        -not (Compare-Object $passedCases $expectedCases -SyncWindow 0)
    $expectedDescriptorKeys = @(
        "acceptanceResultSha256",
        "archiveSha256",
        "archiveSize",
        "canonicalEcosystemSha256",
        "caseInventorySha256",
        "evidenceSha256",
        "evidenceSize",
        "executionEnvironment",
        "failures",
        "gameArchiveSha256",
        "gameBuildId",
        "gameExecutableSha256",
        "gameFilesManifestSha256",
        "gameFilesVerified",
        "independentQa",
        "passedCases",
        "passedCasesSha256",
        "platform",
        "protonAppId",
        "protonBuildId",
        "protonDepotId",
        "protonManifestId",
        "protonRuntimeSha256",
        "protonSourceCommit",
        "protonVersion",
        "releaseJourney",
        "requiredCases",
        "requiredCasesSha256",
        "result",
        "runtime",
        "runtimeConfigurationSha256",
        "schema",
        "suite",
        "targetSha",
        "version",
        "winDllOverrides",
        "wineCommandSha256"
    ) | Sort-Object
    $actualDescriptorKeys = @($descriptor.PSObject.Properties.Name | Sort-Object)
    $descriptorKeysMatch =
        -not (Compare-Object $actualDescriptorKeys $expectedDescriptorKeys -SyncWindow 0)
    $digestNames = @(
        "acceptanceResultSha256",
        "archiveSha256",
        "canonicalEcosystemSha256",
        "caseInventorySha256",
        "evidenceSha256",
        "gameArchiveSha256",
        "gameExecutableSha256",
        "gameFilesManifestSha256",
        "passedCasesSha256",
        "protonRuntimeSha256",
        "requiredCasesSha256",
        "runtimeConfigurationSha256",
        "wineCommandSha256"
    )
    $digestsValid = $true
    foreach ($digestName in $digestNames) {
        if ([string]$descriptor.$digestName -cnotmatch "^[0-9a-f]{64}$") {
            $digestsValid = $false
        }
    }
    $journeyKeys = @($descriptor.releaseJourney.PSObject.Properties.Name | Sort-Object)
    $expectedJourneyKeys = @(
        "authoringCommandCount",
        "enabled",
        "loadedPackageStatus",
        "logMarkerObserved"
    ) | Sort-Object
    $journeyMatches =
        -not (Compare-Object $journeyKeys $expectedJourneyKeys -SyncWindow 0) -and
        $descriptor.releaseJourney.enabled -eq $true -and
        [int]$descriptor.releaseJourney.authoringCommandCount -eq 2 -and
        [string]$descriptor.releaseJourney.loadedPackageStatus -ceq "loaded" -and
        $descriptor.releaseJourney.logMarkerObserved -eq $true
    $gameMetadataBytes = Get-GitBlobBytes -SourceSha $SourceSha `
        -GitPath ([string]$policy.gameBuild.metadataFile)
    try {
        $gameMetadata = [System.Text.UTF8Encoding]::new(
            $false,
            $true
        ).GetString($gameMetadataBytes) | ConvertFrom-Json
    }
    catch {
        throw "The source-SHA Robotopia build metadata is not valid UTF-8 JSON."
    }
    if ($descriptor.schema -ne "release-proton-evidence-v1" -or
        $descriptor.version -ne $Version -or
        $descriptor.targetSha -ne $SourceSha -or
        $descriptor.platform -ne "linux-proton" -or
        $descriptor.archiveSha256 -ne $linuxSha -or
        $descriptor.archiveSize -isnot [Int64] -or
        [Int64]$descriptor.archiveSize -ne $linuxSize -or
        $descriptor.canonicalEcosystemSha256 -ne $CanonicalSha -or
        $descriptor.gameBuildId -isnot [Int64] -or
        [Int64]$descriptor.gameBuildId -ne [Int64]$policy.gameBuild.id -or
        [string]$descriptor.gameArchiveSha256 -cne
            [string]$gameMetadata.archives.windows.sha256 -or
        [string]$descriptor.gameFilesManifestSha256 -cne
            [string]$gameMetadata.windowsFilesManifest.sha256 -or
        $descriptor.gameFilesVerified -isnot [Int64] -or
        [Int64]$descriptor.gameFilesVerified -ne
            [Int64]$gameMetadata.windowsFilesManifest.fileCount -or
        [string]$descriptor.gameExecutableSha256 -cne
            [string]$gameMetadata.windowsFilesManifest.gameExecutableSha256 -or
        $descriptor.result -ne "pass" -or
        $descriptor.suite -ne "full" -or
        $descriptor.protonVersion -ne
            [string]$platformToolchains.linux.proton -or
        $descriptor.protonAppId -isnot [Int64] -or
        [Int64]$descriptor.protonAppId -ne
            [Int64]$platformToolchains.linux.protonSteamAppId -or
        $descriptor.protonDepotId -isnot [Int64] -or
        [Int64]$descriptor.protonDepotId -ne
            [Int64]$platformToolchains.linux.protonSteamDepotId -or
        [string]$descriptor.protonManifestId -cne
            [string]$platformToolchains.linux.protonSteamManifestId -or
        $descriptor.protonBuildId -isnot [Int64] -or
        [Int64]$descriptor.protonBuildId -ne
            [Int64]$platformToolchains.linux.protonSteamBuildId -or
        [string]$descriptor.protonSourceCommit -cne
            [string]$platformToolchains.linux.protonSourceCommit -or
        $descriptor.executionEnvironment -ne
            [string]$platformToolchains.linux.executionEnvironment -or
        $descriptor.runtime -ne "windows-x64-via-proton" -or
        $descriptor.winDllOverrides -ne "winhttp=n,b" -or
        $descriptor.independentQa -ne $false -or
        $descriptor.caseInventorySha256 -ne $caseInventorySha -or
        $descriptor.requiredCasesSha256 -ne $caseSetSha -or
        $descriptor.passedCasesSha256 -ne $caseSetSha -or
        -not $descriptorKeysMatch -or
        -not $digestsValid -or
        -not $caseSetsMatch -or
        @($descriptor.failures).Count -ne 0 -or
        -not $journeyMatches -or
        $descriptor.evidenceSha256 -ne $bundleSha -or
        $descriptor.evidenceSize -isnot [Int64] -or
        [Int64]$descriptor.evidenceSize -ne $bundleSize) {
        throw "Same-host WSL2/WSLg Proton evidence does not match this exact candidate."
    }
    if ($descriptorText -match
        '(?i)"(?:username|hostname|path|timestamp|credential|password|rawLog)"\s*:') {
        throw "The public Proton descriptor contains machine-specific or sensitive fields."
    }

    $bundleEntries = @(
        (Invoke-Checked tar @("-tf", $bundlePath) -Capture) -split "\r?\n" |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
    )
    $expectedBundleEntries = @(
        "acceptance-result.json",
        "cli-help.txt",
        "game-build-marker.json",
        "last-run.json",
        "manager.log",
        "new-mod.txt",
        "proton-version.txt",
        "runtime-context.txt"
    )
    if ($bundleEntries.Count -ne $expectedBundleEntries.Count -or
        (Compare-Object $bundleEntries $expectedBundleEntries -SyncWindow 0)) {
        throw "The private Proton evidence bundle inventory is invalid."
    }
    $inspectionDirectory = Join-Path $stateDirectory "proton-evidence-check"
    Clear-ReleaseDirectory -Path $inspectionDirectory -AllowedParent $stateDirectory
    try {
        Invoke-Checked tar @("-xf", $bundlePath, "-C", $inspectionDirectory)
        $acceptancePath = Join-Path $inspectionDirectory "acceptance-result.json"
        if ((Get-Sha256 $acceptancePath) -ne
            [string]$descriptor.acceptanceResultSha256) {
            throw "The bundled Proton acceptance result digest does not match."
        }
        $acceptance = Get-Content -LiteralPath $acceptancePath -Raw |
            ConvertFrom-Json -DateKind String
        Assert-ExactJsonProperties -Value $acceptance -Expected @(
            "acceptanceChallenge",
            "acceptancePackageReceipt",
            "acceptancePackageStatus",
            "completedAtUtc",
            "failures",
            "gameDirectory",
            "lastRunSessionId",
            "missingCases",
            "package",
            "passedCases",
            "releaseJourneyAuthoringCommandCount",
            "releaseJourneyCli",
            "releaseJourneyEnabled",
            "releaseJourneyProject",
            "requiredCases",
            "requiredLoadedPackageId",
            "requiredLoadedPackageReceipt",
            "requiredLoadedPackageStatus",
            "requiredLogMarker",
            "requiredLogMarkerObserved",
            "schemaVersion",
            "startedAtUtc",
            "succeeded"
        ) -Label "Bundled Proton acceptance result"
        Assert-LiveAcceptancePackageReceipt `
            -Receipt $acceptance.acceptancePackageReceipt `
            -Label "Bundled Proton acceptance-package receipt"
        Assert-LiveAcceptancePackageReceipt `
            -Receipt $acceptance.requiredLoadedPackageReceipt `
            -Label "Bundled Proton journey-package receipt"
        $acceptanceRequired = @(
            $acceptance.requiredCases | ForEach-Object { [string]$_ } | Sort-Object
        )
        $acceptancePassed = @(
            $acceptance.passedCases | ForEach-Object { [string]$_ } | Sort-Object
        )
        $journeyId = "dev.topiaforge.release-$($SourceSha.Substring(0, 12))"
        $journeyName = "TopiaForge release $Version"
        $expectedMarker =
            "$journeyName loaded. Run '$journeyId`:greet' to try its command."
        try {
            $started = [DateTimeOffset]::Parse([string]$acceptance.startedAtUtc)
            $completed = [DateTimeOffset]::Parse([string]$acceptance.completedAtUtc)
        }
        catch {
            throw "The bundled Proton acceptance timestamps are invalid."
        }
        if ($acceptance.schemaVersion -isnot [Int64] -or
            [Int64]$acceptance.schemaVersion -ne 2 -or
            [string]$acceptance.acceptanceChallenge -cnotmatch
                "^[0-9a-f]{64}$" -or
            $acceptance.succeeded -ne $true -or
            @($acceptance.missingCases).Count -ne 0 -or
            @($acceptance.failures).Count -ne 0 -or
            @($acceptanceRequired).Count -ne $expectedCases.Count -or
            @($acceptancePassed).Count -ne $expectedCases.Count -or
            (Compare-Object $acceptanceRequired $expectedCases -SyncWindow 0) -or
            (Compare-Object $acceptancePassed $expectedCases -SyncWindow 0) -or
            $acceptance.releaseJourneyEnabled -ne $true -or
            [int]$acceptance.releaseJourneyAuthoringCommandCount -ne 2 -or
            [string]$acceptance.requiredLoadedPackageStatus -cne "loaded" -or
            $acceptance.requiredLogMarkerObserved -ne $true -or
            [string]$acceptance.acceptancePackageStatus -cne "loaded" -or
            [string]::IsNullOrWhiteSpace([string]$acceptance.lastRunSessionId) -or
            [string]$acceptance.requiredLoadedPackageId -cne $journeyId -or
            [string]$acceptance.requiredLogMarker -cne $expectedMarker -or
            (Split-Path -Leaf ([string]$acceptance.releaseJourneyCli)) -cne
                "topiaforge" -or
            (Split-Path -Leaf ([string]$acceptance.releaseJourneyProject)) -cne
                $journeyId -or
            [string]$acceptance.startedAtUtc -cnotmatch "Z$" -or
            [string]$acceptance.completedAtUtc -cnotmatch "Z$" -or
            $completed -lt $started) {
            throw "The bundled Proton acceptance result is incomplete."
        }
        $runtimeContext = @(
            "executionEnvironment=$($descriptor.executionEnvironment)",
            "gameBuildId=$($descriptor.gameBuildId)",
            "gameArchiveSha256=$($descriptor.gameArchiveSha256)",
            "gameExecutableSha256=$($descriptor.gameExecutableSha256)",
            "gameFilesManifestSha256=$($descriptor.gameFilesManifestSha256)",
            "gameFilesVerified=$($descriptor.gameFilesVerified)",
            "independentQa=false",
            "protonRuntimeSha256=$($descriptor.protonRuntimeSha256)",
            "protonVersion=$($descriptor.protonVersion)",
            "runtime=$($descriptor.runtime)",
            "winDllOverrides=$($descriptor.winDllOverrides)",
            "wineCommandSha256=$($descriptor.wineCommandSha256)"
        ) -join "`n"
        $runtimeContext += "`n"
        $runtimeContextPath = Join-Path $inspectionDirectory "runtime-context.txt"
        $actualRuntimeContext = [System.IO.File]::ReadAllText($runtimeContextPath).
            Replace("`r`n", "`n").
            Replace("`r", "`n")
        if ($actualRuntimeContext -cne $runtimeContext) {
            throw "The bundled Proton runtime configuration content is invalid."
        }
        if ((Get-Utf8Sha256 $runtimeContext) -ne
            [string]$descriptor.runtimeConfigurationSha256) {
            throw "The bundled Proton runtime configuration digest is invalid."
        }
        $gameMarker = Get-Content -LiteralPath (
            Join-Path $inspectionDirectory "game-build-marker.json"
        ) -Raw | ConvertFrom-Json
        if ([string]$gameMarker.id -ne [string]$policy.gameBuild.id) {
            throw "The bundled Proton game-build marker is invalid."
        }
        $protonVersion = [System.IO.File]::ReadAllText(
            (Join-Path $inspectionDirectory "proton-version.txt")
        ).Replace("`r`n", "`n").Replace("`r", "`n")
        if ($protonVersion -cne "Proton $($descriptor.protonVersion)`n") {
            throw "The bundled Proton version evidence is invalid."
        }
        $lastRun = Get-Content -LiteralPath (
            Join-Path $inspectionDirectory "last-run.json"
        ) -Raw | ConvertFrom-Json
        $acceptanceRunPackage = @(
            $lastRun.packages | Where-Object {
                [string]$_.id -ceq "dev.topiaforge.sdk-acceptance"
            }
        )
        $journeyRunPackage = @(
            $lastRun.packages | Where-Object {
                [string]$_.id -ceq $journeyId
            }
        )
        if ($lastRun.schemaVersion -isnot [Int64] -or
            [Int64]$lastRun.schemaVersion -ne 1 -or
            [string]$lastRun.sessionId -cne
                [string]$acceptance.lastRunSessionId -or
            $acceptanceRunPackage.Count -ne 1 -or
            $journeyRunPackage.Count -ne 1 -or
            [string]$acceptanceRunPackage[0].sourceSha256 -cne
                [string]$acceptance.acceptancePackageReceipt.sourceSha256 -or
            (($acceptanceRunPackage[0].criticalFiles |
                    ConvertTo-Json -Compress) -cne
                ($acceptance.acceptancePackageReceipt.criticalFiles |
                    ConvertTo-Json -Compress)) -or
            [string]$journeyRunPackage[0].sourceSha256 -cne
                [string]$acceptance.requiredLoadedPackageReceipt.sourceSha256 -or
            (($journeyRunPackage[0].criticalFiles |
                    ConvertTo-Json -Compress) -cne
                ($acceptance.requiredLoadedPackageReceipt.criticalFiles |
                    ConvertTo-Json -Compress))) {
            throw "The bundled Proton last-run package receipts are for a different session or package."
        }
        foreach ($requiredNonemptyEntry in @(
                "cli-help.txt",
                "manager.log",
                "new-mod.txt"
            )) {
            $null = Assert-BoundedRegularFile -Path (
                Join-Path $inspectionDirectory $requiredNonemptyEntry
            ) -MaximumBytes 134217728 `
                -Label "Bundled Proton $requiredNonemptyEntry evidence"
        }
    }
    finally {
        if (Test-Path -LiteralPath $inspectionDirectory) {
            Remove-Item -LiteralPath $inspectionDirectory -Recurse -Force
        }
    }
}

function Repair-PartialProtonEvidence {
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute(
        "PSUseShouldProcessForStateChangingFunctions",
        "",
        Justification = "Only an unfrozen one-sided evidence pair in the release-owned directory is removed so the exact acceptance run can restart."
    )]
    param([Parameter(Mandatory = $true)][string]$ProtonDirectory)

    $protonDirectory = [System.IO.Path]::GetFullPath($ProtonDirectory)
    $descriptorPath = Join-Path $protonDirectory "proton-evidence.json"
    $bundlePath = Join-Path $protonDirectory "proton-evidence.bundle"
    $hasDescriptor = Test-Path -LiteralPath $descriptorPath -PathType Leaf
    $hasBundle = Test-Path -LiteralPath $bundlePath -PathType Leaf
    if ($hasDescriptor -xor $hasBundle) {
        $orphanPath = if ($hasDescriptor) { $descriptorPath } else { $bundlePath }
        $maximumOrphanBytes = if ($hasDescriptor) { 2097152 } else { 536870912 }
        $null = Assert-BoundedRegularFile -Path $orphanPath `
            -MaximumBytes $maximumOrphanBytes `
            -Label "Interrupted Proton evidence"
        Remove-Item -LiteralPath $orphanPath -Force
        Write-Host (
            "Removed one unfrozen Proton evidence file left by an interrupted " +
            "publication; the exact acceptance run will be repeated."
        )
        $hasDescriptor = $false
        $hasBundle = $false
    }
    return [pscustomobject]@{
        HasDescriptor = $hasDescriptor
        HasBundle = $hasBundle
    }
}

function Invoke-WslProtonAcceptance {
    param(
        [Parameter(Mandatory = $true)][string]$SourceSha,
        [Parameter(Mandatory = $true)][string]$LinuxArchive,
        [Parameter(Mandatory = $true)][string]$CanonicalSha
    )
    $protonDirectory = Join-Path $evidenceDirectory "proton"
    $evidenceState = Repair-PartialProtonEvidence `
        -ProtonDirectory $protonDirectory
    $hasDescriptor = [bool]$evidenceState.HasDescriptor
    $hasBundle = [bool]$evidenceState.HasBundle
    if ($hasDescriptor -and $hasBundle) {
        Assert-ProtonEvidence -SourceSha $SourceSha -LinuxArchive $LinuxArchive `
            -CanonicalSha $CanonicalSha
        Write-Host "Existing same-host WSL2/WSLg Proton evidence verifies."
        return
    }
    New-Item -ItemType Directory -Force -Path $protonDirectory | Out-Null
    $repositoryWslPath = Invoke-Checked wsl @(
        "--distribution", $WslDistribution, "--exec",
        "wslpath", "-a", "-u", $repositoryRoot
    ) -Capture
    $archiveWslPath = Invoke-Checked wsl @(
        "--distribution", $WslDistribution, "--exec",
        "wslpath", "-a", "-u", $LinuxArchive
    ) -Capture
    $gameDirectoryWslPath = Invoke-Checked wsl @(
        "--distribution", $WslDistribution, "--exec",
        "wslpath", "-a", "-u", $GameDirectory
    ) -Capture
    $outputWslPath = Invoke-Checked wsl @(
        "--distribution", $WslDistribution, "--exec",
        "wslpath", "-a", "-u", $protonDirectory
    ) -Capture
    Invoke-Checked wsl @(
        "--distribution", $WslDistribution, "--exec", "/bin/bash",
        "$repositoryWslPath/tools/release/test-proton.sh",
        "--repo", $repositoryWslPath,
        "--source-sha", $SourceSha,
        "--version", $Version,
        "--archive", $archiveWslPath,
        "--canonical-ecosystem-sha256", $CanonicalSha,
        "--game-dir", $gameDirectoryWslPath,
        "--game-build-id", ([string]$policy.gameBuild.id),
        "--proton-executable", $ProtonExecutable,
        "--steam-root", $SteamRoot,
        "--compat-data-root", $CompatDataRoot,
        "--output", $outputWslPath
    )
    Assert-ProtonEvidence -SourceSha $SourceSha -LinuxArchive $LinuxArchive `
        -CanonicalSha $CanonicalSha
}

function New-WindowsQaSummary {
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute(
        "PSUseShouldProcessForStateChangingFunctions",
        "",
        Justification = "This internal helper only writes deterministic metadata inside the validated release-owned evidence or verification directory."
    )]
    param(
        [Parameter(Mandatory = $true)][string]$SourceSha,
        [Parameter(Mandatory = $true)][string]$CanonicalSha,
        [Parameter(Mandatory = $true)][string]$WindowsArchive,
        [Parameter(Mandatory = $true)][string]$ValidationPath,
        [Parameter(Mandatory = $true)][psobject]$Validation,
        [Parameter(Mandatory = $true)][string]$OutputPath
    )
    Assert-WindowsRuntimeEvidence -SourceSha $SourceSha `
        -Validation $Validation
    $creator = Assert-RetainedWindowsCreatorEvidence -SourceSha $SourceSha `
        -WindowsArchive $WindowsArchive -CanonicalSha $CanonicalSha
    $unityPath = Join-Path $evidenceDirectory "windows/unity/lifecycle.json"
    $robotopiaPath = Join-Path $evidenceDirectory `
        "windows/robotopia/acceptance-result.json"
    $creatorPath = Join-Path $evidenceDirectory `
        "windows-creator/creator-evidence.json"
    $unity = Get-Content -LiteralPath $unityPath -Raw | ConvertFrom-Json
    $robotopia = Get-Content -LiteralPath $robotopiaPath -Raw |
        ConvertFrom-Json -DateKind String
    $caseInventoryBytes = Get-GitBlobBytes -SourceSha $SourceSha `
        -GitPath "tests/live-game-acceptance.json"
    $caseInventorySha = Get-BytesSha256 $caseInventoryBytes
    try {
        $caseInventory = [System.Text.UTF8Encoding]::new(
            $false,
            $true
        ).GetString($caseInventoryBytes) | ConvertFrom-Json
    }
    catch {
        throw "The source-SHA Windows QA case inventory is not valid UTF-8 JSON."
    }
    $liveCases = @(
        $caseInventory.cases |
            ForEach-Object { [string]$_.id } |
            Sort-Object
    )
    $creatorCases = @(
        $caseInventory.creatorAcceptance.cases |
            ForEach-Object { [string]$_.id } |
            Sort-Object
    )
    $liveCasesSha = Get-Utf8Sha256 (($liveCases -join "`n") + "`n")
    $creatorCasesSha = Get-Utf8Sha256 (($creatorCases -join "`n") + "`n")
    $summary = [ordered]@{
        schema = "release-windows-qa-summary-v1"
        version = $Version
        targetSha = $SourceSha
        platform = "windows"
        archiveSha256 = Get-Sha256 $WindowsArchive
        archiveSize = (Get-Item -LiteralPath $WindowsArchive).Length
        canonicalEcosystemSha256 = $CanonicalSha
        signingState = [string]$Validation.signingState
        toolchains = [ordered]@{
            dart = [string]$policy.toolchains.dart
            dotnetRuntime = [string]$policy.toolchains.dotnetRuntime
            dotnetSdk = [string]$policy.toolchains.dotnetSdk
            flutter = [string]$policy.toolchains.flutter
            node = [string]$Validation.platformToolchains.node
            unity = [string]$policy.toolchains.unity
            msvc = [string]$Validation.platformToolchains.msvc
            windowsSdk = [string]$Validation.platformToolchains.windowsSdk
        }
        gameBuildId = [Int64]$Validation.gameBuildId
        validationDescriptorSha256 = Get-Sha256 $ValidationPath
        unity = [ordered]@{
            result = [string]$unity.result
            editorVersion = [string]$unity.editorVersion
            cycles = [Int64]$unity.cycles
            validatorSmoke = [bool]$unity.validatorSmoke
            evidenceSha256 = Get-Sha256 $unityPath
        }
        robotopia = [ordered]@{
            result = "pass"
            suite = "full"
            gameArchiveSha256 = [string]$Validation.gameArchiveSha256
            gameExecutableSha256 = [string]$Validation.gameExecutableSha256
            gameFilesManifestSha256 =
                [string]$Validation.gameFilesManifestSha256
            gameFilesVerified = [Int64]$Validation.gameFilesVerified
            caseInventorySha256 = $caseInventorySha
            requiredCases = $liveCases
            requiredCasesSha256 = $liveCasesSha
            passedCases = $liveCases
            passedCasesSha256 = $liveCasesSha
            missingCases = @()
            failures = @()
            releaseJourney = [ordered]@{
                enabled = [bool]$robotopia.releaseJourneyEnabled
                authoringCommandCount =
                    [Int64]$robotopia.releaseJourneyAuthoringCommandCount
                loadedPackageStatus =
                    [string]$robotopia.requiredLoadedPackageStatus
                logMarkerObserved =
                    [bool]$robotopia.requiredLogMarkerObserved
            }
            evidenceSha256 = Get-Sha256 $robotopiaPath
        }
        creator = [ordered]@{
            result = [string]$creator.result
            suite = [string]$creator.suite
            caseInventorySha256 = $caseInventorySha
            requiredCases = $creatorCases
            requiredCasesSha256 = $creatorCasesSha
            passedCases = $creatorCases
            passedCasesSha256 = $creatorCasesSha
            lifecycleCycles = [Int64]$creator.lifecycleCycles
            saveStateUnchanged = [bool]$creator.saveStateUnchanged
            checkpointStateUnchanged =
                [bool]$creator.checkpointStateUnchanged
            failures = @()
            descriptorSha256 = Get-Sha256 $creatorPath
            evidenceSha256 = [string]$creator.evidenceSha256
            evidenceSize = [Int64]$creator.evidenceSize
        }
    }
    $parent = Split-Path -Parent $OutputPath
    New-Item -ItemType Directory -Force -Path $parent | Out-Null
    $summary | ConvertTo-Json -Depth 12 -Compress |
        Set-Content -LiteralPath $OutputPath -Encoding utf8NoBOM
}

function Build-Handoff {
    param(
        [Parameter(Mandatory = $true)][string]$SourceSha,
        [Parameter(Mandatory = $true)][string]$CanonicalSha,
        [Parameter(Mandatory = $true)][string]$CanonicalArchiveSha,
        [Parameter(Mandatory = $true)][string]$EcosystemEvidenceSha,
        [switch]$VerifyOnly
    )
    $windowsCertificatePin = ""
    if ($policy.signingIdentities.PSObject.Properties.Name -contains
        "windowsCertificateSha256") {
        $windowsCertificatePin =
            [string]$policy.signingIdentities.windowsCertificateSha256
    }
    if ($windowsCertificatePin -cnotmatch "^(?!0{64}$)[0-9a-f]{64}$") {
        throw "A reviewed nonzero Windows certificate SHA-256 pin is required."
    }
    if (-not $VerifyOnly) {
        Invoke-Checked $powerShellExecutable @(
            "-NoLogo",
            "-NoProfile",
            "-NonInteractive",
            "-File",
            $handoffSignatureScript,
            "-Mode",
            "ValidateCredentials",
            "-ExpectedCertificateSha256",
            $windowsCertificatePin
        )
    }
    $sdk = Get-DartAndFlutter
    $cliProject = Join-Path $repositoryRoot "apps/topiaforge_cli"
    $verificationDirectory = Join-Path $stateDirectory "handoff-metadata-check"
    if ($VerifyOnly) {
        Clear-ReleaseDirectory -Path $verificationDirectory `
            -AllowedParent $stateDirectory
    }
    $ecosystemManifestPath = Join-Path $stateDirectory "ecosystem.sha256"
    if (-not (Test-Path -LiteralPath $ecosystemManifestPath -PathType Leaf) -or
        (Get-Sha256 $ecosystemManifestPath) -ne $CanonicalSha -or
        $EcosystemEvidenceSha -ne $CanonicalSha) {
        throw "Canonical ecosystem identity or reproducibility evidence changed."
    }
    $platforms = @(
        @{
            Name = "windows-x64"
            ValidationPlatform = "windows"
            Archive = "TopiaForge-windows-x64.zip"
            Validation = "validation-windows.json"
            SigningState = "authenticode-timestamped"
            ExpectedChecks = @(
                "archive-smoke",
                "embedded-cli",
                "packaged-launcher-health",
                "canonical-ecosystem",
                "authenticode",
                "unity-reproducibility",
                "unity-lifecycle",
                "official-game-bytes",
                "robotopia-acceptance"
            )
        }
    )
    # Linux is descoped from 0.1.0-rc.1, so the handoff platform set follows
    # the policy instead of assuming both archives exist. Re-adding the Linux
    # archive to release-policy.json restores this entry. See P0-LINUX-01.
    if ($targetsLinux) {
        $platforms += @{
            Name = "linux-x64"
            ValidationPlatform = "linux"
            Archive = "TopiaForge-linux-x64.zip"
            Validation = "validation-linux.json"
            SigningState = "not-applicable"
            ExpectedChecks = @(
                "archive-smoke",
                "embedded-cli",
                "canonical-ecosystem",
                "packaged-launcher-health"
            )
        }
    }
    foreach ($platform in $platforms) {
        $validationPath = Join-Path $assetsDirectory $platform.Validation
        if (-not (Test-Path -LiteralPath $validationPath -PathType Leaf)) {
            $validationPath = Join-Path $evidenceDirectory $platform.Validation
        }
        $validation = Get-Content -LiteralPath $validationPath -Raw | ConvertFrom-Json
        $archivePath = Join-Path $assetsDirectory $platform.Archive
        $expectedValidationProperties = @(
            "archiveSha256",
            "canonicalArchiveSha256",
            "canonicalEcosystemSha256",
            "checks",
            "passed",
            "platform",
            "platformToolchains",
            "schema",
            "signingState",
            "targetSha",
            "version"
        )
        if ($platform.Name -eq "windows-x64") {
            $expectedValidationProperties += @(
                "evidenceSha256",
                "gameArchiveSha256",
                "gameBuildId",
                "gameExecutableSha256",
                "gameFilesManifestSha256",
                "gameFilesVerified"
            )
        }
        Assert-ExactJsonProperties -Value $validation `
            -Expected $expectedValidationProperties `
            -Label "$($platform.Name) validation summary"
        if ($validation.schema -ne "release-local-validation-v1" -or
            $validation.platform -ne $platform.ValidationPlatform -or
            $validation.version -ne $Version -or
            $validation.targetSha -ne $SourceSha -or
            $validation.archiveSha256 -ne (Get-Sha256 $archivePath) -or
            $validation.canonicalEcosystemSha256 -ne $CanonicalSha -or
            $validation.canonicalArchiveSha256 -ne $CanonicalArchiveSha -or
            $validation.signingState -ne $platform.SigningState -or
            [string]::Join("`n", @($validation.checks)) -cne
                [string]::Join("`n", @($platform.ExpectedChecks)) -or
            $validation.passed -ne $true) {
            throw "$($platform.Name) validation summary does not match the exact release."
        }
        if ($platform.Name -eq "windows-x64") {
            Assert-ExactJsonProperties -Value $validation.platformToolchains `
                -Expected @("msvc", "node", "windowsSdk") `
                -Label "Windows validation platformToolchains"
            if ($validation.platformToolchains.node -ne
                    $policy.toolchains.node -or
                $validation.platformToolchains.msvc -ne
                    $platformToolchains.windows.msvc -or
                $validation.platformToolchains.windowsSdk -ne
                    $platformToolchains.windows.windowsSdk -or
                [string]$validation.gameBuildId -ne
                    [string]$policy.gameBuild.id) {
                throw "Windows validation did not bind the pinned toolchains and game build."
            }
        }
        elseif ($platform.Name -eq "linux-x64") {
            Assert-ExactJsonProperties -Value $validation.platformToolchains `
                -Expected @(
                    "clang",
                    "cmake",
                    "executionEnvironment",
                    "gtk",
                    "ninja",
                    "node",
                    "proton",
                    "protonSourceCommit",
                    "protonSteamAppId",
                    "protonSteamBuildId",
                    "protonSteamDepotId",
                    "protonSteamManifestId"
                ) -Label "Linux validation platformToolchains"
            foreach ($name in @(
                    "node",
                    "clang",
                    "cmake",
                    "ninja",
                    "gtk",
                    "proton",
                    "executionEnvironment"
                )) {
                $expectedValue = if ($name -eq "node") {
                    $policy.toolchains.node
                }
                else {
                    $platformToolchains.linux.$name
                }
                if ($validation.platformToolchains.$name -ne $expectedValue) {
                    throw "Linux validation did not measure the pinned $name toolchain."
                }
            }
        }
        $validationSha = Get-Sha256 $validationPath
        $manifestDirectory = if ($VerifyOnly) {
            $verificationDirectory
        }
        else {
            $assetsDirectory
        }
        $manifestOutput = Join-Path $manifestDirectory `
            "release-platform-bundle-v1-$($platform.Name).json"
        $arguments = @(
            "run", "bin/topiaforge.dart", "release", "build-platform-bundle",
            "--version", $Version, "--target-sha", $SourceSha,
            "--platform", $platform.Name,
            "--archive", $archivePath,
            "--canonical-ecosystem-sha256", $CanonicalSha,
            "--evidence", "ecosystem-reproducibility=$EcosystemEvidenceSha",
            "--evidence", "package=$validationSha",
            "--evidence", "toolchains=$validationSha",
            "--output", $manifestOutput
        )
        if ($platform.Name -eq "windows-x64") {
            Assert-WindowsRuntimeEvidence -SourceSha $SourceSha `
                -Validation $validation
            $creatorPath = Join-Path $evidenceDirectory `
                "windows-creator/creator-evidence.json"
            $null = Assert-RetainedWindowsCreatorEvidence `
                -SourceSha $SourceSha -WindowsArchive $archivePath `
                -CanonicalSha $CanonicalSha
            $retainedQaPath = Join-Path $evidenceDirectory `
                "windows/windows-qa-summary.json"
            $qaPath = if ($VerifyOnly) {
                Join-Path $verificationDirectory "windows-qa-summary.json"
            }
            else {
                $retainedQaPath
            }
            New-WindowsQaSummary -SourceSha $SourceSha `
                -CanonicalSha $CanonicalSha -WindowsArchive $archivePath `
                -ValidationPath $validationPath -Validation $validation `
                -OutputPath $qaPath
            if ($VerifyOnly) {
                Assert-ByteIdenticalMetadata -ExpectedPath $qaPath `
                    -ActualPath $retainedQaPath `
                    -Label "Frozen Windows QA summary"
            }
            $arguments += @(
                "--qa", $qaPath,
                "--evidence", "authenticode=$validationSha",
                "--evidence", "unity=$($validation.evidenceSha256.unity)",
                "--evidence", "robotopia=$($validation.evidenceSha256.robotopia)",
                "--evidence", "creator=$(Get-Sha256 $creatorPath)"
            )
        }
        elseif ($platform.Name -eq "linux-x64") {
            $protonPath = Join-Path $evidenceDirectory "proton/proton-evidence.json"
            Assert-ProtonEvidence -SourceSha $SourceSha `
                -LinuxArchive $archivePath -CanonicalSha $CanonicalSha
            $arguments += @(
                "--qa", $protonPath,
                "--evidence", "proton=$(Get-Sha256 $protonPath)"
            )
        }
        Invoke-Checked $sdk.Dart $arguments -WorkingDirectory $cliProject
        if ($VerifyOnly) {
            Assert-ByteIdenticalMetadata -ExpectedPath $manifestOutput `
                -ActualPath (Join-Path $assetsDirectory (
                    "release-platform-bundle-v1-$($platform.Name).json"
                )) -Label "Frozen $($platform.Name) platform manifest"
        }
        $validationDestination = Join-Path $evidenceDirectory $platform.Validation
        if (-not [System.IO.Path]::GetFullPath($validationPath).Equals(
                [System.IO.Path]::GetFullPath($validationDestination),
                [System.StringComparison]::OrdinalIgnoreCase
            )) {
            if ($VerifyOnly) {
                throw "Validation evidence must already be isolated from public assets."
            }
            else {
                Move-Item -LiteralPath $validationPath `
                    -Destination $validationDestination -Force
            }
        }
    }
    if ($VerifyOnly) {
        # The verification copy mirrors the policy-derived platform set
        # so a Windows-only RC1 does not demand a Linux archive.
        $verificationArchives = @(
            $platforms | ForEach-Object { $_.Archive }
        )
        foreach ($archiveName in $verificationArchives) {
            New-Item -ItemType HardLink `
                -Path (Join-Path $verificationDirectory $archiveName) `
                -Target (Join-Path $assetsDirectory $archiveName) | Out-Null
        }
        Invoke-Checked $sdk.Dart @(
            "run", "bin/topiaforge.dart", "release", "build-handoff",
            "--version", $Version, "--target-sha", $SourceSha,
            "--assets", $verificationDirectory
        ) -WorkingDirectory $cliProject
        Assert-ByteIdenticalMetadata -ExpectedPath (
            Join-Path $verificationDirectory "release-handoff-v1.json"
        ) -ActualPath (Join-Path $assetsDirectory "release-handoff-v1.json") `
            -Label "Frozen release handoff manifest"
    }
    else {
        Invoke-Checked $sdk.Dart @(
            "run", "bin/topiaforge.dart", "release", "build-handoff",
            "--version", $Version, "--target-sha", $SourceSha,
            "--assets", $assetsDirectory
        ) -WorkingDirectory $cliProject
    }
    $handoffPath = Join-Path $assetsDirectory "release-handoff-v1.json"
    $handoffSignaturePath = Join-Path $assetsDirectory `
        "release-handoff-v1.json.p7s"
    $handoffSignatureMode = if ($VerifyOnly) { "Verify" } else { "Sign" }
    Invoke-Checked $powerShellExecutable @(
        "-NoLogo",
        "-NoProfile",
        "-NonInteractive",
        "-File",
        $handoffSignatureScript,
        "-Mode",
        $handoffSignatureMode,
        "-ExpectedCertificateSha256",
        $windowsCertificatePin,
        "-HandoffPath",
        $handoffPath,
        "-SignaturePath",
        $handoffSignaturePath
    )
    Invoke-Checked $sdk.Dart @(
        "run", "bin/topiaforge.dart", "release", "verify-handoff",
        "--version", $Version, "--target-sha", $SourceSha,
        "--assets", $assetsDirectory, "--verify-embedded-ecosystem"
    ) -WorkingDirectory $cliProject
    if ($VerifyOnly -and (Test-Path -LiteralPath $verificationDirectory)) {
        Remove-Item -LiteralPath $verificationDirectory -Recurse -Force
    }
}

function Invoke-Build {
    $state = Read-State
    if ($null -eq $state -or $state.phase -notin @(
            "preflight", "platforms-built", "built", "staged",
            "dispatch-requested", "published"
        )) {
        throw "Run release-admin.ps1 preflight before build."
    }
    Use-StateConfiguration $state
    $sourceSha = [string]$state.sourceSha
    Assert-SourceStillExact $sourceSha
    Assert-OriginStillExact $sourceSha
    if ($state.phase -in @(
            "built", "staged", "dispatch-requested", "published"
        )) {
        Build-Handoff -SourceSha $sourceSha -CanonicalSha $state.canonicalSha256 `
            -CanonicalArchiveSha $state.canonicalArchiveSha256 `
            -EcosystemEvidenceSha $state.ecosystemEvidenceSha256 -VerifyOnly
        Write-Host "Exact release build already exists and verifies."
        return $sourceSha
    }

    New-Item -ItemType Directory -Force -Path `
        $stateDirectory, $assetsDirectory, $evidenceDirectory | Out-Null
    $canonicalArchive = Join-Path $stateDirectory "ecosystem-dist.tar"
    if ($state.phase -eq "preflight") {
        Clear-ReleaseDirectory -Path $assetsDirectory -AllowedParent $stateDirectory
        Clear-ReleaseDirectory -Path $evidenceDirectory -AllowedParent $stateDirectory
        $sdk = Get-DartAndFlutter
        $env:ROBOTOPIA_GAME_DIR = $GameDirectory
        $first = Join-Path $stateDirectory "ecosystem-a"
        $second = Join-Path $stateDirectory "ecosystem-b"
        $releaseWorktrees = [System.Collections.Generic.List[string]]::new()
        try {
            $canonicalAWorktree = New-ReleaseWorktree `
                -Name "canonical-a" -SourceSha $sourceSha
            $releaseWorktrees.Add($canonicalAWorktree)
            $canonicalBWorktree = New-ReleaseWorktree `
                -Name "canonical-b" -SourceSha $sourceSha
            $releaseWorktrees.Add($canonicalBWorktree)
            New-CanonicalEcosystem -SourceRoot $canonicalAWorktree `
                -Output $first -Dart $sdk.Dart
            New-CanonicalEcosystem -SourceRoot $canonicalBWorktree `
                -Output $second -Dart $sdk.Dart
            $firstManifest = Get-TreeManifest $first
            $secondManifest = Get-TreeManifest $second
            if (Compare-Object -CaseSensitive $firstManifest $secondManifest) {
                throw "The two canonical ecosystem builds are not byte-identical."
            }
            [System.IO.File]::WriteAllText(
                (Join-Path $stateDirectory "ecosystem.sha256"),
                (($firstManifest -join "`n") + "`n"),
                [System.Text.UTF8Encoding]::new($false)
            )
            $ecosystemEvidenceSha = Get-Sha256 (
                Join-Path $stateDirectory "ecosystem.sha256"
            )
            $canonicalSha = $ecosystemEvidenceSha
            if (Test-Path -LiteralPath $canonicalArchive) {
                Remove-Item -LiteralPath $canonicalArchive -Force
            }
            Invoke-Checked tar @("-cf", $canonicalArchive, "-C", $first, ".")
            $canonicalArchiveSha = Get-Sha256 $canonicalArchive
            foreach ($package in Get-ChildItem -LiteralPath $first -File `
                    -Filter "*.topiaforgemod") {
                Copy-Item -LiteralPath $package.FullName `
                    -Destination $assetsDirectory -Force
            }

            $windowsWorktree = New-ReleaseWorktree `
                -Name "windows" -SourceSha $sourceSha
            $releaseWorktrees.Add($windowsWorktree)
            Invoke-Checked (
                Join-Path $windowsWorktree "tools/release/build-windows.ps1"
            ) @(
                "-RepositoryRoot", $windowsWorktree,
                "-SourceSha", $sourceSha,
                "-Version", $Version,
                "-CanonicalArchive", $canonicalArchive,
                "-CanonicalEcosystemSha256", $canonicalSha,
                "-CanonicalArchiveSha256", $canonicalArchiveSha,
                "-OutputDirectory", $assetsDirectory,
                "-PrivateEvidenceDirectory", $evidenceDirectory,
                "-UnityPath", $UnityPath,
                "-GameDirectory", $GameDirectory,
                "-DartPath", $sdk.Dart,
                "-FlutterPath", $sdk.Flutter
            )

            if ($targetsLinux) {
                Invoke-WslBuild -SourceSha $sourceSha `
                    -CanonicalArchive $canonicalArchive `
                    -CanonicalEcosystemSha $canonicalSha `
                    -CanonicalArchiveSha $canonicalArchiveSha
            }
        }
        finally {
            $cleanupWorktrees = @($releaseWorktrees)
            [array]::Reverse($cleanupWorktrees)
            $cleanupFailures = @()
            foreach ($releaseWorktree in $cleanupWorktrees) {
                try {
                    Remove-ReleaseWorktree $releaseWorktree
                }
                catch {
                    $cleanupFailures += $_.Exception.Message
                }
            }
            if ($cleanupFailures.Count -gt 0) {
                throw "Could not clean release worktrees: $($cleanupFailures -join '; ')"
            }
        }
        Remove-Item -LiteralPath $first, $second -Recurse -Force
        Write-State -Phase "platforms-built" -SourceSha $sourceSha -Additional @{
            canonicalSha256 = $canonicalSha
            canonicalArchiveSha256 = $canonicalArchiveSha
            ecosystemEvidenceSha256 = $ecosystemEvidenceSha
        }
        $state = Read-State
    }

    if ($targetsLinux) {
        $linuxArchive = Join-Path $assetsDirectory "TopiaForge-linux-x64.zip"
        Invoke-WslProtonAcceptance -SourceSha $sourceSha `
            -LinuxArchive $linuxArchive `
            -CanonicalSha ([string]$state.canonicalSha256)
    }
    $windowsArchive = Join-Path $assetsDirectory "TopiaForge-windows-x64.zip"
    Assert-WindowsCreatorEvidence -SourceSha $sourceSha `
        -WindowsArchive $windowsArchive `
        -CanonicalSha ([string]$state.canonicalSha256)
    Build-Handoff -SourceSha $sourceSha -CanonicalSha $state.canonicalSha256 `
        -CanonicalArchiveSha $state.canonicalArchiveSha256 `
        -EcosystemEvidenceSha $state.ecosystemEvidenceSha256
    Write-State -Phase "built" -SourceSha $sourceSha -Additional @{
        canonicalSha256 = [string]$state.canonicalSha256
        canonicalArchiveSha256 = [string]$state.canonicalArchiveSha256
        ecosystemEvidenceSha256 = [string]$state.ecosystemEvidenceSha256
    }
    Write-Host "Both production packages and their handoff manifests verify."
    return $sourceSha
}

function Get-ReleaseCatalogEntry {
    $catalog = Get-Content -LiteralPath (Join-Path $repositoryRoot "release/catalog.json") `
        -Raw | ConvertFrom-Json
    $release = @($catalog.releases | Where-Object { $_.version -eq $Version })
    if ($release.Count -ne 1) {
        throw "release/catalog.json must contain exactly one entry for $Version."
    }
    if ($release[0].tag -ne $tag -or
        $release[0].prerelease -isnot [bool]) {
        throw "The release catalog entry does not match the requested tag."
    }
    return $release[0]
}

function Get-StagedAssetPaths {
    $release = Get-ReleaseCatalogEntry
    $names = @($release.artifacts) + @(
        "release-platform-bundle-v1-windows-x64.json",
        "release-platform-bundle-v1-linux-x64.json",
        "release-handoff-v1.json",
        "release-handoff-v1.json.p7s"
    )
    return @($names | ForEach-Object {
            $path = Join-Path $assetsDirectory $_
            if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
                throw "Required staged asset is missing: $_"
            }
            $path
        })
}

function Assert-RemoteAssetMatches {
    param(
        [Parameter(Mandatory = $true)][string]$AssetName,
        [Parameter(Mandatory = $true)][string]$LocalPath
    )
    $download = Join-Path $stateDirectory "remote-asset-check"
    Clear-ReleaseDirectory -Path $download -AllowedParent $stateDirectory
    Invoke-Checked $gitHubCli @(
        "release", "download", $tag, "--repo", $Repository,
        "--pattern", $AssetName, "--dir", $download, "--clobber"
    )
    $remotePath = Join-Path $download $AssetName
    if (-not (Test-Path -LiteralPath $remotePath -PathType Leaf) -or
        (Get-Sha256 $remotePath) -ne (Get-Sha256 $LocalPath)) {
        throw "GitHub asset '$AssetName' does not match the local handoff byte-for-byte."
    }
}

function Get-RemoteTagInfo {
    $output = Invoke-Checked git @(
        "-C", $repositoryRoot, "ls-remote", "--tags", "origin",
        "refs/tags/$tag", "refs/tags/$tag^{}"
    ) -Capture
    $result = [ordered]@{
        ObjectSha = $null
        CommitSha = $null
    }
    foreach ($line in @($output -split "\r?\n")) {
        if ([string]::IsNullOrWhiteSpace($line)) {
            continue
        }
        $parts = $line -split "\s+", 2
        if ($parts.Count -ne 2) {
            throw "Could not parse the remote tag reference."
        }
        if ($parts[1] -eq "refs/tags/$tag") {
            $result.ObjectSha = $parts[0]
        }
        elseif ($parts[1] -eq "refs/tags/$tag^{}") {
            $result.CommitSha = $parts[0]
        }
    }
    return [pscustomobject]$result
}

function Assert-ExactSignedTag {
    [CmdletBinding(SupportsShouldProcess = $true)]
    param(
        [Parameter(Mandatory = $true)][string]$SourceSha,
        [Parameter(Mandatory = $true)][bool]$AllowCreation
    )
    & git -C $repositoryRoot rev-parse --verify `
        "refs/tags/$tag" 2>$null | Out-Null
    $hasLocalTag = $LASTEXITCODE -eq 0
    $remote = Get-RemoteTagInfo

    if (-not $hasLocalTag -and $null -ne $remote.ObjectSha) {
        Invoke-Checked git @(
            "-C", $repositoryRoot, "fetch", "origin",
            "refs/tags/$tag`:refs/tags/$tag"
        )
        Invoke-Checked git @(
            "-C", $repositoryRoot, "rev-parse", "--verify", "refs/tags/$tag"
        ) -Capture | Out-Null
        $hasLocalTag = $true
    }
    if (-not $hasLocalTag) {
        if (-not $AllowCreation) {
            throw "The exact signed release tag is missing."
        }
        if ($PSCmdlet.ShouldProcess($tag, "create signed annotated tag")) {
            Invoke-Checked git @(
                "-C", $repositoryRoot, "tag", "-s", "-a", $tag, $SourceSha,
                "-m", "TopiaForge $Version"
            )
        }
    }

    $localObjectSha = Invoke-Checked git @(
        "-C", $repositoryRoot, "rev-parse", "--verify", "refs/tags/$tag"
    ) -Capture
    $localType = Invoke-Checked git @(
        "-C", $repositoryRoot, "cat-file", "-t", $localObjectSha
    ) -Capture
    $localCommitSha = Invoke-Checked git @(
        "-C", $repositoryRoot, "rev-parse", "refs/tags/$tag^{}"
    ) -Capture
    if ($localType -ne "tag" -or $localCommitSha -ne $SourceSha) {
        throw "Release tag $tag must be annotated and point to the frozen candidate."
    }
    Invoke-Checked git @("-C", $repositoryRoot, "verify-tag", $tag)

    $remote = Get-RemoteTagInfo
    if ($null -eq $remote.ObjectSha) {
        if (-not $AllowCreation) {
            throw "The signed release tag is absent from origin."
        }
        if ($PSCmdlet.ShouldProcess($tag, "push exact signed tag")) {
            Invoke-Checked git @(
                "-C", $repositoryRoot, "push", "origin", "refs/tags/$tag"
            )
        }
        $remote = Get-RemoteTagInfo
    }
    if ($remote.ObjectSha -ne $localObjectSha -or
        $remote.CommitSha -ne $SourceSha) {
        throw "Remote tag $tag does not exactly match the signed local tag."
    }
}

function Invoke-Stage {
    [CmdletBinding(SupportsShouldProcess = $true)]
    param()

    $state = Read-State
    if ($null -eq $state -or $state.phase -notin @(
            "built", "staged", "dispatch-requested", "published"
        )) {
        throw "A fully verified local build is required before staging."
    }
    Use-StateConfiguration $state
    if ($Rehearsal -or [bool]$state.rehearsal) {
        throw "A rehearsal can never create tags or mutate GitHub releases."
    }
    $originalPhase = [string]$state.phase
    $allowMutation = $originalPhase -eq "built"
    $sourceSha = [string]$state.sourceSha
    Assert-SourceStillExact $sourceSha
    Assert-OriginStillExact $sourceSha
    Build-Handoff -SourceSha $sourceSha -CanonicalSha $state.canonicalSha256 `
        -CanonicalArchiveSha $state.canonicalArchiveSha256 `
        -EcosystemEvidenceSha $state.ecosystemEvidenceSha256 -VerifyOnly
    Invoke-Checked $gitHubCli @("auth", "status", "--hostname", "github.com")
    $isAdmin = Invoke-Checked $gitHubCli @(
        "api", "repos/$Repository", "--jq", ".permissions.admin"
    ) -Capture
    if ($isAdmin -ne "true") {
        throw "Staging requires an authenticated repository administrator."
    }

    $catalogEntry = Get-ReleaseCatalogEntry
    $expectedNotesRelative = "release/notes/$tag.md"
    if ([string]$catalogEntry.notesFile -ne $expectedNotesRelative) {
        throw "The release catalog must use $expectedNotesRelative."
    }
    $notes = Join-Path $repositoryRoot $expectedNotesRelative
    if (-not (Test-Path -LiteralPath $notes -PathType Leaf)) {
        throw "Release notes are missing: $expectedNotesRelative"
    }
    $expectedNotes = [System.IO.File]::ReadAllText($notes).
        Replace("`r`n", "`n").
        Replace("`r", "`n").
        TrimEnd([char[]]@("`n"))
    if ([string]::IsNullOrWhiteSpace($expectedNotes)) {
        throw "Release notes cannot be empty."
    }
    $sdk = Get-DartAndFlutter
    Invoke-Checked $sdk.Dart @(
        "run",
        "bin/topiaforge.dart",
        "release",
        "validate-policy",
        "--version",
        $Version
    ) -WorkingDirectory (Join-Path $repositoryRoot "apps/topiaforge_cli")
    $localAssets = Get-StagedAssetPaths
    Assert-LatestRobotopiaBuild
    Invoke-ReleaseGovernanceAudit
    $stageGitHubLogin = Invoke-Checked $gitHubCli @(
        "api", "user", "--jq", ".login"
    ) -Capture
    Assert-GitHubTagSigningIdentity -GitHubLogin $stageGitHubLogin
    Invoke-Checked $sdk.Dart @(
        "run",
        "bin/topiaforge.dart",
        "release",
        "validate-readiness",
        "--version",
        $Version,
        "--target-sha",
        $sourceSha
    ) -WorkingDirectory (Join-Path $repositoryRoot "apps/topiaforge_cli")
    Assert-ExactSignedTag -SourceSha $sourceSha -AllowCreation $allowMutation

    $releaseJson = & $gitHubCli release view $tag --repo $Repository `
        --json tagName,isDraft,isImmutable,isPrerelease,name,body,assets,targetCommitish,author `
        2>$null
    if ($LASTEXITCODE -ne 0) {
        if (-not $allowMutation) {
            throw "The exact staged GitHub release is missing."
        }
        if ($PSCmdlet.ShouldProcess($tag, "create exact draft release")) {
            $createArguments = @(
                "release", "create", $tag, "--repo", $Repository, "--draft",
                "--verify-tag", "--target", $sourceSha, "--title", "TopiaForge $Version",
                "--notes", $expectedNotes
            )
            if ([bool]$catalogEntry.prerelease) {
                $createArguments += "--prerelease"
            }
            Invoke-Checked $gitHubCli $createArguments
        }
        $releaseJson = Invoke-Checked $gitHubCli @(
            "release", "view", $tag, "--repo", $Repository,
            "--json",
            "tagName,isDraft,isImmutable,isPrerelease,name,body,assets,targetCommitish,author"
        ) -Capture
    }
    $release = ($releaseJson | Out-String) | ConvertFrom-Json
    if ($release.tagName -ne $tag -or
        $release.name -ne "TopiaForge $Version" -or
        $release.body -ne $expectedNotes -or
        [bool]$release.isPrerelease -ne [bool]$catalogEntry.prerelease) {
        throw "GitHub release metadata does not exactly match the catalog."
    }
    $resolvedTarget = Invoke-Checked git @(
        "-C", $repositoryRoot, "rev-parse", "$($release.targetCommitish)^{commit}"
    ) -Capture
    if ($resolvedTarget -ne $sourceSha) {
        throw "GitHub release target does not resolve to the frozen candidate."
    }
    if (-not $release.isDraft -and -not $release.isImmutable) {
        throw "A published release must be immutable."
    }
    $releaseAuthor = [string]$release.author.login
    if ($releaseAuthor -notmatch "^[A-Za-z0-9-]+$") {
        throw "The existing GitHub release has no verifiable human author."
    }
    $authorIsAdmin = Invoke-Checked $gitHubCli @(
        "api", "repos/$Repository/collaborators/$releaseAuthor/permission",
        "--jq", ".permission"
    ) -Capture
    if ($authorIsAdmin -ne "admin") {
        throw "The existing GitHub release was not staged by a repository administrator."
    }

    $allowedFinalizerMetadata = if (
        $originalPhase -in @("dispatch-requested", "published") -or
        (-not $release.isDraft -and $release.isImmutable)
    ) {
        @($policy.artifactPolicy.generatedMetadata)
    }
    else {
        @()
    }
    $governance = Get-Content -LiteralPath (
        Join-Path $repositoryRoot ".github/repository-governance.json"
    ) -Raw | ConvertFrom-Json
    if ([int]$governance.schema_version -ne 2 -or
        [string]$governance.repository_full_name -cne $Repository) {
        throw "Release asset authority governance is invalid."
    }
    $workflowPrincipal = $governance.release_workflow_principal
    $workflowIntegrationId = [string]$governance.github_actions_integration_id
    if ($null -eq $workflowPrincipal -or
        [string]$workflowPrincipal.login -cne "github-actions[bot]" -or
        [string]$workflowPrincipal.actor_id -cne "41898282" -or
        [string]$workflowPrincipal.type -cne "Bot" -or
        $workflowIntegrationId -cne "15368") {
        throw "Release workflow asset authority governance is invalid."
    }
    $releaseAssetsJson = Invoke-Checked $gitHubCli @(
        "api", "repos/$Repository/releases/tags/$tag",
        "--jq", ".assets"
    ) -Capture
    $assetDocument = $null
    try {
        $assetDocument = [System.Text.Json.JsonDocument]::Parse(
            $releaseAssetsJson
        )
        if ($assetDocument.RootElement.ValueKind -ne
            [System.Text.Json.JsonValueKind]::Array) {
            throw "asset inventory is not an array"
        }
        $releaseAssets = @($releaseAssetsJson | ConvertFrom-Json)
    }
    catch {
        throw "Could not parse the exact GitHub release asset inventory."
    }
    finally {
        if ($null -ne $assetDocument) {
            $assetDocument.Dispose()
        }
    }
    $localNames = @($localAssets | ForEach-Object { Split-Path -Leaf $_ })
    $releaseAssetNames = @(
        $releaseAssets | ForEach-Object { [string]$_.name }
    )
    if (@($releaseAssetNames | Sort-Object -Unique).Count -ne
        $releaseAssetNames.Count) {
        throw "The release contains duplicate asset names."
    }
    foreach ($asset in $releaseAssets) {
        $assetName = [string]$asset.name
        $assetState = [string]$asset.state
        if ($assetName -cnotin $localNames -and
            $assetName -cnotin $allowedFinalizerMetadata) {
            throw "The release contains an unexpected asset: $($asset.name)"
        }
        $isAllowedFinalizerMetadata =
            $assetName -cin $allowedFinalizerMetadata
        if ($isAllowedFinalizerMetadata) {
            if ($asset.PSObject.Properties.Name -notcontains "uploader" -or
                $null -eq $asset.uploader -or
                [string]$asset.uploader.login -cne
                    [string]$workflowPrincipal.login -or
                [string]$asset.uploader.id -cne
                    [string]$workflowPrincipal.actor_id -or
                [string]$asset.uploader.type -cne
                    [string]$workflowPrincipal.type) {
                throw (
                    "Generated release asset '$assetName' was not uploaded " +
                    "by the pinned GitHub Actions principal."
                )
            }
            $hasPerformingApp = $asset.PSObject.Properties.Name -contains
                "performed_via_github_app"
            if ($hasPerformingApp -and
                $null -ne $asset.performed_via_github_app -and
                (
                    $asset.performed_via_github_app.PSObject.Properties.Name `
                        -notcontains "id" -or
                    [string]$asset.performed_via_github_app.id -cne
                        $workflowIntegrationId
                )) {
                throw (
                    "Generated release asset '$assetName' has the wrong " +
                    "performing GitHub App."
                )
            }
        }
        if ($assetState -cnotin @("uploaded", "starter")) {
            throw "Release asset '$assetName' has unsupported state '$assetState'."
        }
        $isStrandedFinalizerMetadata = (
            $isAllowedFinalizerMetadata -and
            $release.isDraft -and
            $originalPhase -eq "dispatch-requested"
        )
        if ($assetState -ceq "starter" -and
            (-not $release.isDraft -or -not $allowMutation) -and
            -not $isStrandedFinalizerMetadata) {
            throw "Starter asset '$assetName' cannot be repaired in this release phase."
        }
    }
    foreach ($localAsset in $localAssets) {
        $name = Split-Path -Leaf $localAsset
        $matchingAssets = @(
            $releaseAssets |
                Where-Object { [string]$_.name -ceq $name }
        )
        if ($matchingAssets.Count -eq 1 -and
            [string]$matchingAssets[0].state -ceq "starter") {
            $assetId = [string]$matchingAssets[0].id
            if ($assetId -cnotmatch "^[1-9][0-9]*$") {
                throw "Starter asset '$name' has no safe REST asset identifier."
            }
            $currentAssetState = Invoke-Checked $gitHubCli @(
                "api", "repos/$Repository/releases/assets/$assetId",
                "--jq", ".state"
            ) -Capture
            if ($currentAssetState -cne "starter") {
                throw "Starter asset '$name' changed state before safe repair."
            }
            if (-not $PSCmdlet.ShouldProcess(
                    $tag,
                    "delete incomplete starter asset $name"
                )) {
                throw "Starter asset repair was declined for '$name'."
            }
            Invoke-Checked $gitHubCli @(
                "api", "--method", "DELETE",
                "repos/$Repository/releases/assets/$assetId"
            )
            $releaseAssets = @(
                $releaseAssets |
                    Where-Object { [string]$_.id -cne $assetId }
            )
            $matchingAssets = @()
        }
        if ($matchingAssets.Count -eq 1) {
            Assert-RemoteAssetMatches -AssetName $name -LocalPath $localAsset
        }
        elseif (-not $release.isDraft) {
            throw "Published release is missing handoff asset '$name'."
        }
        elseif (-not $allowMutation) {
            throw "The already-staged draft is missing handoff asset '$name'."
        }
        else {
            if (-not $PSCmdlet.ShouldProcess($tag, "upload $name")) {
                throw "Asset upload was declined for '$name'."
            }
            Invoke-Checked $gitHubCli @(
                "release", "upload", $tag, $localAsset, "--repo", $Repository
            )
            Assert-RemoteAssetMatches -AssetName $name -LocalPath $localAsset
        }
    }
    if ($originalPhase -eq "built") {
        Write-State -Phase "staged" -SourceSha $sourceSha -Additional @{
            canonicalSha256 = [string]$state.canonicalSha256
            canonicalArchiveSha256 = [string]$state.canonicalArchiveSha256
            ecosystemEvidenceSha256 = [string]$state.ecosystemEvidenceSha256
        }
    }
    Write-Host "Exact local handoff is staged on the GitHub draft."
    return $sourceSha
}

function New-FinalizerRequestId {
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute(
        "PSUseShouldProcessForStateChangingFunctions",
        "",
        Justification = "This helper only returns an in-memory random identifier."
    )]
    param()

    return "release-admin-$([Guid]::NewGuid().ToString('N'))"
}

function Get-FinalizerRequestId {
    param([Parameter(Mandatory = $true)][psobject]$State)

    if ($State.PSObject.Properties.Name -notcontains "finalizerRequestId") {
        throw "The durable finalizer request ID is missing."
    }
    $requestId = [string]$State.finalizerRequestId
    if ($requestId -cnotmatch "^release-admin-[0-9a-f]{32}$") {
        throw "The durable finalizer request ID is invalid."
    }
    return $requestId
}

function Get-FinalizerRunId {
    param(
        [Parameter(Mandatory = $true)][psobject]$State,
        [switch]$Required
    )

    $runId = ""
    if ($State.PSObject.Properties.Name -contains "finalizerRunId" -and
        $null -ne $State.finalizerRunId) {
        $runId = [string]$State.finalizerRunId
    }
    if (-not [string]::IsNullOrWhiteSpace($runId) -and
        $runId -cnotmatch "^[1-9][0-9]*$") {
        throw "The durable finalizer run ID is invalid."
    }
    if ($Required -and [string]::IsNullOrWhiteSpace($runId)) {
        throw "The durable finalizer run ID is missing."
    }
    return $runId
}

function Get-FinalizerDispatchAttempt {
    param([Parameter(Mandatory = $true)][psobject]$State)

    if ($State.PSObject.Properties.Name -notcontains
        "finalizerDispatchAttempt") {
        throw "The durable finalizer dispatch-attempt journal is missing."
    }
    $attempt = $State.finalizerDispatchAttempt
    if ($attempt -isnot [int] -and $attempt -isnot [long]) {
        throw "The durable finalizer dispatch-attempt journal is invalid."
    }
    if ([long]$attempt -lt 0 -or [long]$attempt -gt [int]::MaxValue) {
        throw "The durable finalizer dispatch-attempt journal is invalid."
    }
    return [int]$attempt
}

function Write-FinalizerState {
    param(
        [Parameter(Mandatory = $true)]
        [ValidateSet("dispatch-requested", "published")]
        [string]$Phase,
        [Parameter(Mandatory = $true)][psobject]$State,
        [Parameter(Mandatory = $true)]
        [ValidatePattern("^release-admin-[0-9a-f]{32}$")]
        [string]$RequestId,
        [AllowEmptyString()][string]$RunId = "",
        [Parameter(Mandatory = $true)]
        [ValidateRange(0, [int]::MaxValue)]
        [int]$DispatchAttempt
    )

    if (-not [string]::IsNullOrWhiteSpace($RunId) -and
        $RunId -cnotmatch "^[1-9][0-9]*$") {
        throw "The finalizer run ID cannot be persisted."
    }
    if ($Phase -ceq "published" -and
        ($DispatchAttempt -lt 1 -or
            [string]::IsNullOrWhiteSpace($RunId))) {
        throw (
            "Published state requires a journaled finalizer dispatch attempt " +
            "and its exact GitHub run ID."
        )
    }
    if (-not [string]::IsNullOrWhiteSpace($RunId) -and
        $DispatchAttempt -lt 1) {
        throw "A finalizer run ID requires a journaled dispatch attempt."
    }
    Write-State -Phase $Phase -SourceSha ([string]$State.sourceSha) `
        -Additional @{
            canonicalSha256 = [string]$State.canonicalSha256
            canonicalArchiveSha256 =
                [string]$State.canonicalArchiveSha256
            ecosystemEvidenceSha256 =
                [string]$State.ecosystemEvidenceSha256
            finalizerRequestId = $RequestId
            finalizerRunId = $RunId
            finalizerDispatchAttempt = $DispatchAttempt
        }
}

function Assert-FinalizerRunIdentity {
    param(
        [Parameter(Mandatory = $true)][psobject]$Run,
        [Parameter(Mandatory = $true)]
        [ValidatePattern("^release-admin-[0-9a-f]{32}$")]
        [string]$RequestId,
        [Parameter(Mandatory = $true)]
        [ValidatePattern("^[0-9a-f]{40}$")]
        [string]$SourceSha
    )

    $requiredProperties = @(
        "databaseId", "displayTitle", "event", "headBranch", "headSha",
        "status", "conclusion", "repository", "workflowPath"
    )
    foreach ($property in $requiredProperties) {
        if ($Run.PSObject.Properties.Name -notcontains $property) {
            throw "The GitHub finalizer run response is missing '$property'."
        }
    }
    $runId = [string]$Run.databaseId
    if ($runId -cnotmatch "^[1-9][0-9]*$") {
        throw "The GitHub finalizer run has an invalid database ID."
    }
    $expectedTitle = "Finalize $tag ($RequestId)"
    if ([string]$Run.displayTitle -cne $expectedTitle -or
        [string]$Run.event -cne "workflow_dispatch" -or
        [string]$Run.headBranch -cne $tag -or
        [string]$Run.headSha -cne $SourceSha -or
        [string]$Run.repository -cne $Repository -or
        [string]$Run.workflowPath -cne ".github/workflows/release.yml") {
        throw (
            "GitHub finalizer run $runId does not exactly match request " +
            "$RequestId, repository $Repository, tag $tag, authoritative " +
            "workflow path, and source SHA $SourceSha."
        )
    }
    $status = [string]$Run.status
    if ($status -cnotin @(
            "queued", "in_progress", "pending", "requested", "waiting",
            "completed"
        )) {
        throw "GitHub finalizer run $runId has unsupported status '$status'."
    }
    $conclusion = [string]$Run.conclusion
    if ($status -cne "completed" -and
        -not [string]::IsNullOrWhiteSpace($conclusion)) {
        throw (
            "GitHub finalizer run $runId has a conclusion before completion."
        )
    }
    if ($status -ceq "completed" -and
        [string]::IsNullOrWhiteSpace($conclusion)) {
        throw "GitHub finalizer run $runId completed without a conclusion."
    }
    return $Run
}

function Find-ExactFinalizerRun {
    param(
        [Parameter(Mandatory = $true)]
        [ValidatePattern("^release-admin-[0-9a-f]{32}$")]
        [string]$RequestId,
        [Parameter(Mandatory = $true)]
        [ValidatePattern("^[0-9a-f]{40}$")]
        [string]$SourceSha
    )

    $fields = (
        "databaseId,displayTitle,event,headBranch,headSha,status," +
        "conclusion,url"
    )
    $runsJson = Invoke-Checked $gitHubCli @(
        "run", "list",
        "--repo", $Repository,
        "--workflow", "release.yml",
        "--event", "workflow_dispatch",
        "--branch", $tag,
        "--limit", "100",
        "--json", $fields
    ) -Capture
    $document = $null
    try {
        $document = [System.Text.Json.JsonDocument]::Parse($runsJson)
        if ($document.RootElement.ValueKind -ne
            [System.Text.Json.JsonValueKind]::Array) {
            throw "run inventory is not an array"
        }
        $runs = @($runsJson | ConvertFrom-Json)
    }
    catch {
        throw "Could not parse the GitHub finalizer run inventory."
    }
    finally {
        if ($null -ne $document) {
            $document.Dispose()
        }
    }
    $expectedTitle = "Finalize $tag ($RequestId)"
    $matching = @(
        $runs |
            Where-Object { [string]$_.displayTitle -ceq $expectedTitle }
    )
    if ($matching.Count -eq 0) {
        return $null
    }
    if ($matching.Count -ne 1) {
        throw "The unique finalizer request ID matched multiple GitHub runs."
    }
    $runId = [string]$matching[0].databaseId
    if ($runId -cnotmatch "^[1-9][0-9]*$") {
        throw "The discovered GitHub finalizer run has an invalid database ID."
    }
    return Get-ExactFinalizerRunById `
        -RunId $runId -RequestId $RequestId -SourceSha $SourceSha
}

function Wait-ExactFinalizerRunRegistration {
    param(
        [Parameter(Mandatory = $true)]
        [ValidatePattern("^release-admin-[0-9a-f]{32}$")]
        [string]$RequestId,
        [Parameter(Mandatory = $true)]
        [ValidatePattern("^[0-9a-f]{40}$")]
        [string]$SourceSha,
        [ValidateRange(1, 120)][int]$MaximumAttempts = 30,
        [ValidateRange(0, 10000)][int]$PollDelayMilliseconds = 2000,
        [switch]$AllowMissing
    )

    for ($attempt = 1; $attempt -le $MaximumAttempts; $attempt++) {
        $run = Find-ExactFinalizerRun `
            -RequestId $RequestId -SourceSha $SourceSha
        if ($null -ne $run) {
            return $run
        }
        if ($attempt -lt $MaximumAttempts -and $PollDelayMilliseconds -gt 0) {
            Start-Sleep -Milliseconds $PollDelayMilliseconds
        }
    }
    if ($AllowMissing) {
        return $null
    }
    throw (
        "GitHub did not expose the exact finalizer run for request " +
        "$RequestId. Resume later; do not dispatch a replacement manually."
    )
}

function Get-ExactFinalizerRunById {
    param(
        [Parameter(Mandatory = $true)]
        [ValidatePattern("^[1-9][0-9]*$")]
        [string]$RunId,
        [Parameter(Mandatory = $true)]
        [ValidatePattern("^release-admin-[0-9a-f]{32}$")]
        [string]$RequestId,
        [Parameter(Mandatory = $true)]
        [ValidatePattern("^[0-9a-f]{40}$")]
        [string]$SourceSha
    )

    $runJson = Invoke-Checked $gitHubCli @(
        "api", "repos/$Repository/actions/runs/$RunId"
    ) -Capture
    try {
        $restRun = $runJson | ConvertFrom-Json
    }
    catch {
        throw "Could not parse GitHub finalizer run $RunId."
    }
    if ($null -eq $restRun -or $restRun -is [array]) {
        throw "GitHub finalizer run $RunId did not return one run object."
    }
    foreach ($property in @(
            "id", "display_title", "event", "head_branch", "head_sha",
            "status", "conclusion", "path", "repository"
        )) {
        if ($restRun.PSObject.Properties.Name -notcontains $property) {
            throw (
                "The authoritative GitHub finalizer run response is missing " +
                "'$property'."
            )
        }
    }
    if ($null -eq $restRun.repository -or
        $restRun.repository.PSObject.Properties.Name -notcontains
            "full_name") {
        throw (
            "The authoritative GitHub finalizer run response has no " +
            "repository identity."
        )
    }
    $run = [pscustomobject][ordered]@{
        databaseId = [string]$restRun.id
        displayTitle = [string]$restRun.display_title
        event = [string]$restRun.event
        headBranch = [string]$restRun.head_branch
        headSha = [string]$restRun.head_sha
        status = [string]$restRun.status
        conclusion = if ($null -eq $restRun.conclusion) {
            $null
        }
        else {
            [string]$restRun.conclusion
        }
        repository = [string]$restRun.repository.full_name
        workflowPath = [string]$restRun.path
    }
    $run = Assert-FinalizerRunIdentity `
        -Run $run -RequestId $RequestId -SourceSha $SourceSha
    if ([string]$run.databaseId -cne $RunId) {
        throw "GitHub returned the wrong finalizer run ID."
    }
    return $run
}

function Wait-ExactFinalizerRunCompletion {
    param(
        [Parameter(Mandatory = $true)][psobject]$Run,
        [Parameter(Mandatory = $true)]
        [ValidatePattern("^release-admin-[0-9a-f]{32}$")]
        [string]$RequestId,
        [Parameter(Mandatory = $true)]
        [ValidatePattern("^[0-9a-f]{40}$")]
        [string]$SourceSha
    )

    $run = Assert-FinalizerRunIdentity `
        -Run $Run -RequestId $RequestId -SourceSha $SourceSha
    $runId = [string]$run.databaseId
    if ([string]$run.status -cne "completed") {
        Invoke-Checked $gitHubCli @(
            "run", "watch", $runId, "--repo", $Repository, "--interval", "10"
        )
        $run = Get-ExactFinalizerRunById `
            -RunId $runId -RequestId $RequestId -SourceSha $SourceSha
    }

    if ([string]$run.status -ceq "completed" -and
        [string]$run.conclusion -cin @("failure", "cancelled")) {
        Invoke-Checked $gitHubCli @(
            "run", "rerun", $runId, "--repo", $Repository
        )
        Invoke-Checked $gitHubCli @(
            "run", "watch", $runId, "--repo", $Repository, "--interval", "10"
        )
        $run = Get-ExactFinalizerRunById `
            -RunId $runId -RequestId $RequestId -SourceSha $SourceSha
    }

    if ([string]$run.status -cne "completed" -or
        [string]$run.conclusion -cne "success") {
        throw (
            "GitHub finalizer run $runId did not finish successfully: " +
            "status=$($run.status), conclusion=$($run.conclusion)."
        )
    }
    return $run
}

function Assert-ExactPublishedPrerelease {
    param(
        [Parameter(Mandatory = $true)]
        [ValidatePattern("^[0-9a-f]{40}$")]
        [string]$SourceSha
    )

    $releaseJson = Invoke-Checked $gitHubCli @(
        "release", "view", $tag,
        "--repo", $Repository,
        "--json",
        "tagName,isDraft,isImmutable,isPrerelease,targetCommitish"
    ) -Capture
    try {
        $release = $releaseJson | ConvertFrom-Json
    }
    catch {
        throw "Could not parse the published GitHub release."
    }
    foreach ($property in @(
            "tagName", "isDraft", "isImmutable", "isPrerelease",
            "targetCommitish"
        )) {
        if ($null -eq $release -or
            $release.PSObject.Properties.Name -notcontains $property) {
            throw "The published GitHub release is missing '$property'."
        }
    }
    if ([string]$release.tagName -cne $tag -or
        [bool]$release.isDraft -or
        -not [bool]$release.isImmutable -or
        -not [bool]$release.isPrerelease) {
        throw (
            "The finalizer must publish the exact immutable prerelease " +
            "for $tag."
        )
    }
    $resolvedTarget = Invoke-Checked git @(
        "-C", $repositoryRoot,
        "rev-parse", "$($release.targetCommitish)^{commit}"
    ) -Capture
    if ($resolvedTarget -cne $SourceSha) {
        throw "The published prerelease does not target the frozen source SHA."
    }
}

function Invoke-Dispatch {
    [CmdletBinding(SupportsShouldProcess = $true)]
    param()

    $state = Read-State
    if ($null -eq $state -or $state.phase -notin @(
            "staged", "dispatch-requested", "published"
        )) {
        throw "Stage the exact draft before dispatching the finalizer."
    }
    Use-StateConfiguration $state
    if ($Rehearsal -or [bool]$state.rehearsal) {
        throw "A rehearsal can never dispatch the protected publisher."
    }

    if ($state.phase -eq "published") {
        $publishedRequestId = Get-FinalizerRequestId -State $state
        $publishedRunId = Get-FinalizerRunId -State $state -Required
        if ((Get-FinalizerDispatchAttempt -State $state) -lt 1) {
            throw "Published state has no journaled finalizer dispatch attempt."
        }
        $publishedRun = Get-ExactFinalizerRunById `
            -RunId $publishedRunId `
            -RequestId $publishedRequestId `
            -SourceSha ([string]$state.sourceSha)
        if ([string]$publishedRun.status -cne "completed" -or
            [string]$publishedRun.conclusion -cne "success") {
            throw "The recorded published finalizer run is no longer successful."
        }
        Invoke-Stage | Out-Null
        Assert-ExactPublishedPrerelease -SourceSha ([string]$state.sourceSha)
        Write-Host (
            "Exact rerun verified without mutation; finalizer run " +
            "$publishedRunId published the immutable prerelease."
        )
        return
    }

    if ($state.phase -eq "staged") {
        Invoke-Stage | Out-Null
        $state = Read-State
        $requestId = New-FinalizerRequestId
        Write-FinalizerState -Phase "dispatch-requested" -State $state `
            -RequestId $requestId -DispatchAttempt 0
        $state = Read-State
    }
    else {
        $requestId = Get-FinalizerRequestId -State $state
    }

    $runId = Get-FinalizerRunId -State $state
    $dispatchAttempt = Get-FinalizerDispatchAttempt -State $state
    $run = $null
    if (-not [string]::IsNullOrWhiteSpace($runId)) {
        $run = Get-ExactFinalizerRunById `
            -RunId $runId `
            -RequestId $requestId `
            -SourceSha ([string]$state.sourceSha)
    }
    else {
        $run = Find-ExactFinalizerRun `
            -RequestId $requestId `
            -SourceSha ([string]$state.sourceSha)
        if ($null -eq $run -and $dispatchAttempt -gt 0) {
            $run = Wait-ExactFinalizerRunRegistration `
                -RequestId $requestId `
                -SourceSha ([string]$state.sourceSha) `
                -MaximumAttempts $finalizerRegistrationGraceAttempts `
                -PollDelayMilliseconds `
                    $finalizerRegistrationPollDelayMilliseconds `
                -AllowMissing
            if ($null -eq $run) {
                throw (
                    "No exact GitHub finalizer run is visible for journaled " +
                    "request $requestId. Resume later; automatic redispatch " +
                    "is forbidden because workflow_dispatch has no " +
                    "idempotency key."
                )
            }
        }
        if ($null -eq $run) {
            Invoke-Stage | Out-Null
            if (-not $PSCmdlet.ShouldProcess(
                    $tag,
                    "dispatch protected GitHub finalizer request $requestId"
                )) {
                throw "Protected finalizer dispatch was declined."
            }
            if ($dispatchAttempt -eq [int]::MaxValue) {
                throw "The finalizer dispatch-attempt journal is exhausted."
            }
            $dispatchAttempt++
            Write-FinalizerState -Phase "dispatch-requested" -State $state `
                -RequestId $requestId -DispatchAttempt $dispatchAttempt
            $state = Read-State
            Invoke-Checked $gitHubCli @(
                "workflow", "run", "release.yml", "--repo", $Repository,
                "--ref", $tag,
                "-f", "tag=$tag",
                "-f", "request_id=$requestId"
            )
            $run = Wait-ExactFinalizerRunRegistration `
                -RequestId $requestId `
                -SourceSha ([string]$state.sourceSha) `
                -MaximumAttempts $finalizerRegistrationGraceAttempts `
                -PollDelayMilliseconds `
                    $finalizerRegistrationPollDelayMilliseconds
        }
        if ($dispatchAttempt -lt 1) {
            throw (
                "An exact finalizer run exists without a journaled dispatch " +
                "attempt."
            )
        }
        $runId = [string]$run.databaseId
        Write-FinalizerState -Phase "dispatch-requested" -State $state `
            -RequestId $requestId -RunId $runId `
            -DispatchAttempt $dispatchAttempt
        $state = Read-State
    }

    $run = Wait-ExactFinalizerRunCompletion `
        -Run $run `
        -RequestId $requestId `
        -SourceSha ([string]$state.sourceSha)
    Invoke-Stage | Out-Null
    Assert-ExactPublishedPrerelease -SourceSha ([string]$state.sourceSha)
    Write-FinalizerState -Phase "published" -State $state `
        -RequestId $requestId -RunId ([string]$run.databaseId) `
        -DispatchAttempt $dispatchAttempt
    Write-Host (
        "Protected GitHub finalizer run $($run.databaseId) published " +
        "the immutable prerelease for $tag."
    )
}

function Invoke-All {
    $state = Read-State
    if ($null -eq $state) {
        Invoke-Preflight | Out-Null
        $state = Read-State
    }
    Use-StateConfiguration $state
    if ($state.phase -in @("preflight", "platforms-built")) {
        Invoke-Build | Out-Null
        $state = Read-State
    }
    if ($Rehearsal) {
        if ($state.phase -ne "built") {
            throw "The non-publishing rehearsal did not reach a verified build."
        }
        Write-Host "Non-publishing two-platform rehearsal passed."
        return
    }
    if ($state.phase -eq "built") {
        Invoke-Stage | Out-Null
        $state = Read-State
    }
    if ($state.phase -in @(
            "staged", "dispatch-requested", "published"
        )) {
        Invoke-Dispatch
    }
}

if ($env:TOPIAFORGE_RELEASE_TEST_IMPORT -ne "1") {
    $releaseLock = Enter-ReleaseLock
    try {
        switch ($Command) {
            "preflight" { Invoke-Preflight | Out-Null }
            "build" { Invoke-Build | Out-Null }
            "stage" { Invoke-Stage | Out-Null }
            "dispatch" { Invoke-Dispatch }
            "resume" { Invoke-All }
            "all" { Invoke-All }
        }
    }
    finally {
        $releaseLock.Dispose()
    }
}
