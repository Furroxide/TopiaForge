[CmdletBinding()]
param(
    [switch]$CreatorEvidenceOnly
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

function Assert-True {
    param(
        [Parameter(Mandatory = $true)][bool]$Condition,
        [Parameter(Mandatory = $true)][string]$Message
    )
    if (-not $Condition) {
        throw $Message
    }
}

function Assert-ThrowsMatch {
    param(
        [Parameter(Mandatory = $true)][scriptblock]$Action,
        [Parameter(Mandatory = $true)][string]$Pattern,
        [Parameter(Mandatory = $true)][string]$Message
    )
    try {
        & $Action
    }
    catch {
        if ($_.Exception.Message -match $Pattern) {
            return
        }
        throw "$Message Unexpected error: $($_.Exception.Message)"
    }
    throw $Message
}

function Copy-CreatorEvidenceZipForTest {
    param(
        [Parameter(Mandatory = $true)][string]$Source,
        [Parameter(Mandatory = $true)][string]$Destination,
        [switch]$TamperFirstArtifact,
        [switch]$AddUnexpectedEntry,
        [switch]$AddUnsafeEntry,
        [switch]$MarkFirstArtifactReparse
    )
    $sourceZip = [System.IO.Compression.ZipFile]::OpenRead($Source)
    try {
        $entries = @(
            $sourceZip.Entries | ForEach-Object {
                $memory = [System.IO.MemoryStream]::new()
                $stream = $_.Open()
                try {
                    $stream.CopyTo($memory)
                    $bytes = $memory.ToArray()
                }
                finally {
                    $stream.Dispose()
                    $memory.Dispose()
                }
                [pscustomobject]@{
                    Name = $_.FullName
                    Bytes = $bytes
                }
            }
        )
    }
    finally {
        $sourceZip.Dispose()
    }
    if ($TamperFirstArtifact) {
        $artifact = @(
            $entries | Where-Object { $_.Name.StartsWith("artifacts/") }
        )[0]
        [Array]::Fill[byte]($artifact.Bytes, 0x58)
    }
    if ($AddUnexpectedEntry) {
        $entries += [pscustomobject]@{
            Name = "unexpected.bin"
            Bytes = [byte[]]@(0x58)
        }
    }
    if ($AddUnsafeEntry) {
        $entries += [pscustomobject]@{
            Name = "../escape.bin"
            Bytes = [byte[]]@(0x58)
        }
    }
    $destinationStream = [System.IO.File]::Open(
        $Destination,
        [System.IO.FileMode]::CreateNew,
        [System.IO.FileAccess]::Write,
        [System.IO.FileShare]::None
    )
    $destinationZip = [System.IO.Compression.ZipArchive]::new(
        $destinationStream,
        [System.IO.Compression.ZipArchiveMode]::Create,
        $false
    )
    try {
        $markedReparse = $false
        foreach ($sourceEntry in $entries) {
            $entry = $destinationZip.CreateEntry(
                $sourceEntry.Name,
                [System.IO.Compression.CompressionLevel]::NoCompression
            )
            $entry.LastWriteTime = [DateTimeOffset]::new(
                1980,
                1,
                1,
                0,
                0,
                0,
                [TimeSpan]::Zero
            )
            if ($MarkFirstArtifactReparse -and
                -not $markedReparse -and
                $sourceEntry.Name.StartsWith("artifacts/")) {
                $entry.ExternalAttributes =
                    [int][System.IO.FileAttributes]::ReparsePoint
                $markedReparse = $true
            }
            else {
                $entry.ExternalAttributes = 0
            }
            $entryStream = $entry.Open()
            try {
                $entryStream.Write(
                    $sourceEntry.Bytes,
                    0,
                    $sourceEntry.Bytes.Length
                )
            }
            finally {
                $entryStream.Dispose()
            }
        }
    }
    finally {
        $destinationZip.Dispose()
        $destinationStream.Dispose()
    }
}

$repositoryRootForTest = (Resolve-Path -LiteralPath (
        Join-Path $PSScriptRoot ".."
    )).Path
$scriptPath = Join-Path $PSScriptRoot "release-admin.ps1"
$tokens = $null
$parseErrors = $null
$adminAst = [System.Management.Automation.Language.Parser]::ParseFile(
    $scriptPath,
    [ref]$tokens,
    [ref]$parseErrors
)
Assert-True ($parseErrors.Count -eq 0) (
    "release-admin.ps1 has parser errors: " +
    (@($parseErrors | ForEach-Object { $_.Message }) -join "; ")
)

$adminSource = Get-Content -LiteralPath $scriptPath -Raw
$parameterNames = @(
    $adminAst.ParamBlock.Parameters |
        ForEach-Object { $_.Name.VariablePath.UserPath }
)
foreach ($requiredParameter in @(
        "WslDistribution",
        "ProtonExecutable",
        "SteamRoot",
        "CompatDataRoot",
        "WindowsCreatorEvidence",
        "WindowsCreatorEvidenceBundle",
        "PythonPath"
    )) {
    Assert-True ($parameterNames -contains $requiredParameter) (
        "release-admin.ps1 is missing -$requiredParameter."
    )
}
foreach ($forbiddenParameter in @(
        "MacHost",
        "MacRepositoryPath",
        "MacEnvironmentFile",
        "MacKnownHostsFile",
        "MacExpectedTeamId",
        "ProtonEvidence",
        "ProtonEvidenceBundle"
    )) {
    Assert-True ($parameterNames -notcontains $forbiddenParameter) (
        "release-admin.ps1 retained obsolete -$forbiddenParameter."
    )
}
foreach ($forbiddenSource in @(
        "Invoke-MacBuild",
        "Invoke-SshScript",
        "TopiaForge-macos-universal.zip",
        "release-platform-bundle-v1-macos",
        "validation-macos.json",
        "scp ",
        "three-platform"
    )) {
    Assert-True (-not $adminSource.Contains($forbiddenSource)) (
        "release-admin.ps1 retained an obsolete production assumption: " +
        $forbiddenSource
    )
}
foreach ($requiredSource in @(
        "C:\Program Files\GitHub CLI\gh.exe",
        "test-proton.sh",
        "--preflight-only",
        "--proton-executable",
        "--steam-root",
        "--compat-data-root",
        "authenticode-timestamped",
        "release-handoff-v1.json.p7s",
        "handoff-signature.ps1",
        "RC1 production forbids every code-signing exception",
        "verify-release-governance.sh",
        ".github/scripts/audit_repository_governance.py",
        "Invoke-ReleaseGovernanceAudit",
        "function Resolve-Jq",
        '$jqCli = Resolve-Jq',
        "Require-Command git-lfs",
        "lfs fsck",
        "Every tracked Git LFS object must be materialized",
        "The WSL clone contains unmaterialized Git LFS pointers",
        "Assert-GitHubSshSigningKey",
        "ssh_signing_keys",
        'request_id=$requestId',
        '"--branch", $tag',
        '"api", "repos/$Repository/actions/runs/$RunId"',
        '".github/workflows/release.yml"',
        "dispatch-requested",
        "finalizerDispatchAttempt",
        "Wait-ExactFinalizerRunCompletion",
        "Assert-ExactPublishedPrerelease",
        "Non-publishing two-platform rehearsal passed."
    )) {
    Assert-True ($adminSource.Contains($requiredSource)) (
        "release-admin.ps1 is missing the required contract: $requiredSource"
    )
}

$releaseWorkflowSource = Get-Content -LiteralPath (
    Join-Path $repositoryRootForTest ".github/workflows/release.yml"
) -Raw
foreach ($requiredWorkflowSource in @(
        'run-name: Finalize ${{ inputs.tag }} (${{ inputs.request_id }})',
        "request_id:",
        "DISPATCH_REQUEST_ID:",
        "^release-admin-[0-9a-f]{32}$"
    )) {
    Assert-True ($releaseWorkflowSource.Contains($requiredWorkflowSource)) (
        "release.yml is missing durable dispatch identity: " +
        $requiredWorkflowSource
    )
}
$handoffSignatureSource = Get-Content -LiteralPath (
    Join-Path $PSScriptRoot "release/handoff-signature.ps1"
) -Raw
foreach ($requiredSigningSource in @(
        "WINDOWS_CERTIFICATE_PFX",
        "WINDOWS_CERTIFICATE_PASSWORD",
        "WINDOWS_TIMESTAMP_URL",
        "SignedCms",
        "CheckSignature",
        "ExpectedCertificateSha256"
    )) {
    Assert-True ($handoffSignatureSource.Contains($requiredSigningSource)) (
        "Handoff signature helper is missing: $requiredSigningSource"
    )
}

$toolchainPath = Join-Path $repositoryRootForTest `
    "release/platform-toolchains.json"
$toolchains = Get-Content -LiteralPath $toolchainPath -Raw | ConvertFrom-Json
Assert-True ([string]$toolchains.linux.proton -ceq "10.0-4") (
    "The Linux toolchain contract must pin Proton 10.0-4."
)
Assert-True (
    [string]$toolchains.linux.executionEnvironment -ceq "wsl2-wslg"
) "The Linux toolchain contract must pin WSL2/WSLg."

$releasePolicy = Get-Content -LiteralPath (
    Join-Path $repositoryRootForTest "release/release-policy.json"
) -Raw | ConvertFrom-Json
Assert-True (
    $releasePolicy.publication.PSObject.Properties.Name -cnotcontains
        "codeSigningException" -and
    @($releasePolicy.signingIdentities.PSObject.Properties).Count -eq 0
) "Release policy must forbid RC1 signing exceptions and remain blocked until the reviewed certificate pin is configured."
Assert-True (
    [string]$releasePolicy.toolchains.node -ceq "24.18.0" -and
    $releasePolicy.toolchains.PSObject.Properties.Name -cnotcontains
        "nodeMinimum"
) "Release policy must pin the exact Node 24.18.0 toolchain."

$platformBuilderPath = Join-Path $PSScriptRoot "release/build-platform.sh"
$platformBuilderSource = Get-Content -LiteralPath $platformBuilderPath -Raw
foreach ($requiredBuilderSource in @(
        '[[ "$node_version" == "v$node_pin" ]]',
        "Expected Node v%s",
        ".linux.proton",
        ".linux.executionEnvironment",
        "executionEnvironment:",
        "--topiaforge-update-health-nonce",
        "packaged-launcher-health"
    )) {
    Assert-True ($platformBuilderSource.Contains($requiredBuilderSource)) (
        "build-platform.sh is missing: $requiredBuilderSource"
    )
}
Assert-True (
    -not $platformBuilderSource.Contains("semantic_version_at_least") -and
    -not $platformBuilderSource.Contains("nodeMinimum")
) "build-platform.sh must reject every Node version except the exact pin."
Assert-True (
    $platformBuilderSource.Contains(
        'packaged_launcher="$health_root/launcher/topiaforge_launcher"'
    ) -and
    -not $platformBuilderSource.Contains(
        '$health_root/TopiaForge-linux-x64/launcher'
    )
) "Linux launcher health must use the archive-root launcher directory."

$windowsBuilderPath = Join-Path $PSScriptRoot "release/build-windows.ps1"
$windowsBuilderSource = Get-Content -LiteralPath $windowsBuilderPath -Raw
Assert-True (
    $windowsBuilderSource.Contains(
        'Invoke-Checked -FilePath $packagedCli -WorkingDirectory $repository'
    ) -and
    $windowsBuilderSource.Contains("Invoke-PackagedLauncherHealthCheck") -and
    $windowsBuilderSource.Contains("--topiaforge-update-health-nonce") -and
    $windowsBuilderSource.Contains('"packaged-launcher-health"') -and
    -not $windowsBuilderSource.Contains(
        '"run", "bin/topiaforge.dart", "acceptance", "run"'
    )
) "Windows acceptance is not launched by the exact packaged CLI."
Assert-True (
    $windowsBuilderSource.Contains(
        'Join-Path $extract "launcher/topiaforge_launcher.exe"'
    ) -and
    -not $windowsBuilderSource.Contains(
        "TopiaForge-windows-x64/launcher/topiaforge_launcher.exe"
    )
) "Windows launcher health must use the archive-root launcher directory."

$protonRunnerPath = Join-Path $PSScriptRoot "release/test-proton.sh"
Assert-True (
    Test-Path -LiteralPath $protonRunnerPath -PathType Leaf
) "tools/release/test-proton.sh is missing."
$protonRunnerSource = Get-Content -LiteralPath $protonRunnerPath -Raw
foreach ($requiredRunnerSource in @(
        "--preflight-only",
        "--source-sha",
        "--canonical-ecosystem-sha256",
        "--game-build-id",
        "--proton-executable",
        "--steam-root",
        "--compat-data-root",
        "proton-evidence.json",
        "proton-evidence.bundle",
        "independentQa"
    )) {
    Assert-True ($protonRunnerSource.Contains($requiredRunnerSource)) (
        "test-proton.sh is missing: $requiredRunnerSource"
    )
}

$bashPath = $null
if (Test-Path -LiteralPath "C:\Program Files\Git\bin\bash.exe") {
    $bashPath = "C:\Program Files\Git\bin\bash.exe"
}
elseif (-not $IsWindows) {
    $bashPath = (Get-Command bash -CommandType Application `
            -ErrorAction SilentlyContinue | Select-Object -First 1).Source
}
if (-not [string]::IsNullOrWhiteSpace($bashPath)) {
    foreach ($shellPath in @($platformBuilderPath, $protonRunnerPath)) {
        & $bashPath -n $shellPath
        if ($LASTEXITCODE -ne 0) {
            throw "$shellPath failed bash syntax validation."
        }
    }
}

$testRoot = Join-Path ([System.IO.Path]::GetTempPath()) `
    "topiaforge-release-admin-test-$([Guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Force -Path $testRoot | Out-Null
try {
    $fakeBin = Join-Path $testRoot "bin"
    $fakeLog = Join-Path $testRoot "native.log"
    $testState = Join-Path $testRoot "state"
    $testAssets = Join-Path $testState "assets"
    $testEvidence = Join-Path $testState "evidence"
    New-Item -ItemType Directory -Force -Path `
        $fakeBin, $testAssets, $testEvidence | Out-Null
    $env:FAKE_RELEASE_LOG = $fakeLog

    if ($IsWindows) {
        @'
@echo off
echo gh %*>>"%FAKE_RELEASE_LOG%"
set "outdir="
set "pattern="
:parse
if "%~1"=="" goto done
if "%~1"=="--dir" (
  set "outdir=%~2"
  shift
  shift
  goto parse
)
if "%~1"=="--pattern" (
  set "pattern=%~2"
  shift
  shift
  goto parse
)
shift
goto parse
:done
if defined outdir copy /y "%FAKE_RELEASE_ASSET_SOURCE%" "%outdir%\%pattern%" >nul
exit /b 0
'@ | Set-Content -LiteralPath (Join-Path $fakeBin "gh.cmd") -Encoding ascii

        @'
@echo off
setlocal EnableDelayedExpansion
set "iswslpath=0"
:args
if "%~1"=="" goto done
set "arg=%~1"
echo wsl-arg !arg!>>"%FAKE_RELEASE_LOG%"
if "!arg!"=="wslpath" set "iswslpath=1"
shift
goto args
:done
if "!iswslpath!"=="1" echo /mnt/c/topiaforge-test
exit /b 0
'@ | Set-Content -LiteralPath (Join-Path $fakeBin "wsl.cmd") -Encoding ascii

        @'
@echo off
if "%~1"=="-c" echo 3.12
exit /b 0
'@ | Set-Content -LiteralPath (Join-Path $fakeBin "python.cmd") -Encoding ascii

        @'
@echo off
if "%~1"=="-c" echo 3.10
exit /b 0
'@ | Set-Content -LiteralPath (
            Join-Path $fakeBin "python-too-old.cmd"
        ) -Encoding ascii

        @'
@echo off
exit /b 0
'@ | Set-Content -LiteralPath (Join-Path $fakeBin "jq.cmd") -Encoding ascii
    }
    else {
        @'
#!/usr/bin/env bash
printf 'gh %s\n' "$*" >>"$FAKE_RELEASE_LOG"
outdir=''
pattern=''
while (($#)); do
  case "$1" in
    --dir) outdir=$2; shift 2 ;;
    --pattern) pattern=$2; shift 2 ;;
    *) shift ;;
  esac
done
if [[ -n "$outdir" ]]; then
  cp -- "$FAKE_RELEASE_ASSET_SOURCE" "$outdir/$pattern"
fi
'@ | Set-Content -LiteralPath (Join-Path $fakeBin "gh") -Encoding utf8NoBOM

        @'
#!/usr/bin/env bash
is_wslpath=false
for argument in "$@"; do
  printf 'wsl-arg %s\n' "$argument" >>"$FAKE_RELEASE_LOG"
  [[ "$argument" == wslpath ]] && is_wslpath=true
done
if [[ "$is_wslpath" == true ]]; then
  printf '/mnt/c/topiaforge-test\n'
fi
'@ | Set-Content -LiteralPath (Join-Path $fakeBin "wsl") -Encoding utf8NoBOM

        @'
#!/usr/bin/env bash
if [[ ${1:-} == -c ]]; then
  printf '3.12\n'
fi
'@ | Set-Content -LiteralPath (Join-Path $fakeBin "python") -Encoding utf8NoBOM

        @'
#!/usr/bin/env bash
if [[ ${1:-} == -c ]]; then
  printf '3.10\n'
fi
'@ | Set-Content -LiteralPath (
            Join-Path $fakeBin "python-too-old"
        ) -Encoding utf8NoBOM

        @'
#!/usr/bin/env bash
exit 0
'@ | Set-Content -LiteralPath (Join-Path $fakeBin "jq") -Encoding utf8NoBOM
        & chmod +x (Join-Path $fakeBin "gh") (Join-Path $fakeBin "wsl") `
            (Join-Path $fakeBin "python") (
                Join-Path $fakeBin "python-too-old"
            ) (Join-Path $fakeBin "jq")
        if ($LASTEXITCODE -ne 0) {
            throw "Could not mark fake orchestration tools executable."
        }
    }

    $oldPath = $env:PATH
    $oldProgramFiles = $env:ProgramFiles
    $env:PATH = "$fakeBin$([System.IO.Path]::PathSeparator)$oldPath"
    $env:TOPIAFORGE_RELEASE_TEST_IMPORT = "1"
    try {
        . $scriptPath -Command preflight `
            -StateRoot $testRoot `
            -GameDirectory (Join-Path $testRoot "Robotopia") `
            -ProtonExecutable "/opt/proton-10.0-4/proton" `
            -SteamRoot "/mnt/c/Program Files (x86)/Steam" `
            -CompatDataRoot "/var/tmp/topiaforge-compat"
        $firstReleaseLock = Enter-ReleaseLock
        try {
            Assert-ThrowsMatch -Action {
                $secondReleaseLock = Enter-ReleaseLock
                $secondReleaseLock.Dispose()
            } -Pattern "already owns" `
                -Message "Concurrent release-admin ownership was not rejected."
        }
        finally {
            $firstReleaseLock.Dispose()
        }
        $releasedLock = Enter-ReleaseLock
        $releasedLock.Dispose()

        $partialProton = Join-Path $testRoot "partial-proton-evidence"
        New-Item -ItemType Directory -Path $partialProton | Out-Null
        $partialDescriptor = Join-Path $partialProton "proton-evidence.json"
        Set-Content -LiteralPath $partialDescriptor -Value "{}" `
            -Encoding utf8NoBOM -NoNewline
        $repairedProton = Repair-PartialProtonEvidence `
            -ProtonDirectory $partialProton
        Assert-True (
            -not (Test-Path -LiteralPath $partialDescriptor) -and
            -not [bool]$repairedProton.HasDescriptor -and
            -not [bool]$repairedProton.HasBundle
        ) "An interrupted one-sided Proton publication was not recoverable."

        $fakePythonName = if ($IsWindows) { "python.cmd" } else { "python" }
        $oldPythonName = if ($IsWindows) {
            "python-too-old.cmd"
        }
        else {
            "python-too-old"
        }
        $fakePython = Join-Path $fakeBin $fakePythonName
        $oldPython = Join-Path $fakeBin $oldPythonName
        Assert-True (
            (Resolve-Python -ConfiguredPath $fakePython) -ceq $fakePython
        ) "Resolve-Python did not execute and accept a working configured runtime."
        Assert-ThrowsMatch -Action {
            Resolve-Python -ConfiguredPath $oldPython
        } -Pattern "older than 3.11" `
            -Message "Resolve-Python accepted an unsupported configured runtime."

        $lfsRepository = Join-Path $testRoot "lfs-materialization"
        Invoke-Checked git @("init", "--quiet", $lfsRepository)
        Invoke-Checked git @(
            "-C", $lfsRepository, "config", "user.name", "Release LFS Test"
        )
        Invoke-Checked git @(
            "-C", $lfsRepository, "config", "user.email",
            "release-lfs-test@example.invalid"
        )
        Invoke-Checked git @(
            "-C", $lfsRepository, "config", "core.autocrlf", "false"
        )
        Invoke-Checked git @("-C", $lfsRepository, "lfs", "install", "--local")
        [System.IO.File]::WriteAllText(
            (Join-Path $lfsRepository ".gitattributes"),
            "*.bin filter=lfs diff=lfs merge=lfs -text`n",
            [System.Text.UTF8Encoding]::new($false)
        )
        [System.IO.File]::WriteAllBytes(
            (Join-Path $lfsRepository "payload.bin"),
            [byte[]](0..255)
        )
        Invoke-Checked git @("-C", $lfsRepository, "add", ".")
        Invoke-Checked git @(
            "-C", $lfsRepository, "commit", "--quiet", "-m", "LFS fixture"
        )
        $lfsSourceSha = Invoke-Checked git @(
            "-C", $lfsRepository, "rev-parse", "HEAD"
        ) -Capture
        Assert-GitLfsMaterialized -RepositoryPath $lfsRepository `
            -SourceSha $lfsSourceSha

        $payloadPath = Join-Path $lfsRepository "payload.bin"
        $hadSkipSmudge = Test-Path Env:GIT_LFS_SKIP_SMUDGE
        $previousSkipSmudge = $env:GIT_LFS_SKIP_SMUDGE
        try {
            $env:GIT_LFS_SKIP_SMUDGE = "1"
            Remove-Item -LiteralPath $payloadPath -Force
            Invoke-Checked git @(
                "-C", $lfsRepository, "checkout", "--", "payload.bin"
            )
        }
        finally {
            if ($hadSkipSmudge) {
                $env:GIT_LFS_SKIP_SMUDGE = $previousSkipSmudge
            }
            else {
                Remove-Item Env:GIT_LFS_SKIP_SMUDGE -ErrorAction SilentlyContinue
            }
        }
        Assert-ThrowsMatch -Action {
            Assert-GitLfsMaterialized -RepositoryPath $lfsRepository `
                -SourceSha $lfsSourceSha
        } -Pattern "must be materialized" `
            -Message "A clean Git LFS pointer-file checkout was accepted."

        $gameBuildMetadata = Get-Content -LiteralPath (
            Join-Path $repositoryRootForTest ".github/robotopia-game-build.json"
        ) -Raw | ConvertFrom-Json

        Assert-True (
            (ConvertTo-SshPublicKeyIdentity `
                "ssh-ed25519 YWJjZA== release-test") -ceq
                "ssh-ed25519 YWJjZA=="
        ) "SSH signing key identity did not ignore the optional comment."
        Assert-ThrowsMatch -Action {
            ConvertTo-SshPublicKeyIdentity "ssh-ed25519`nYWJjZA=="
        } -Pattern "one newline-free key" `
            -Message "A multiline SSH signing key was accepted."
        Assert-ThrowsMatch -Action {
            ConvertTo-SshPublicKeyIdentity "not-a-key"
        } -Pattern "syntax is invalid" `
            -Message "An invalid SSH signing key was accepted."

        if ($IsWindows) {
            $fallbackProgramFiles = Join-Path $testRoot "program-files"
            $fallbackGh = Join-Path $fallbackProgramFiles "GitHub CLI/gh.exe"
            New-Item -ItemType Directory -Force -Path (
                Split-Path -Parent $fallbackGh
            ) | Out-Null
            Set-Content -LiteralPath $fallbackGh -Value "test" -Encoding ascii
            $env:ProgramFiles = $fallbackProgramFiles
            $env:PATH = "$env:SystemRoot\System32"
            Assert-True (
                (Resolve-GitHubCli) -ceq $fallbackGh
            ) "Resolve-GitHubCli did not use the Program Files fallback."
            # Resolve-Jq also searches LOCALAPPDATA and ProgramData, where the
            # winget and Chocolatey shims live. Point every searched root at the
            # empty test tree so this asserts the fallback, not the host.
            $oldLocalAppData = $env:LOCALAPPDATA
            $oldProgramData = $env:ProgramData
            $env:LOCALAPPDATA = $fallbackProgramFiles
            $env:ProgramData = $fallbackProgramFiles
            Assert-ThrowsMatch -Action {
                Resolve-Jq
            } -Pattern "winget install jqlang.jq" `
                -Message "A missing jq did not report an actionable install step."
            $fallbackJq = Join-Path $fallbackProgramFiles "jq/jq.exe"
            New-Item -ItemType Directory -Force -Path (
                Split-Path -Parent $fallbackJq
            ) | Out-Null
            Set-Content -LiteralPath $fallbackJq -Value "test" -Encoding ascii
            Assert-True (
                (Resolve-Jq) -ceq $fallbackJq
            ) "Resolve-Jq did not use the Program Files fallback."
            $env:LOCALAPPDATA = $oldLocalAppData
            $env:ProgramData = $oldProgramData
            $env:PATH = "$fakeBin$([System.IO.Path]::PathSeparator)$oldPath"
            $env:ProgramFiles = $oldProgramFiles
        }

        $script:stateDirectory = $testState
        $script:assetsDirectory = $testAssets
        $script:evidenceDirectory = $testEvidence
        $script:statePath = Join-Path $testState "state.json"

        $preflightRepository = Join-Path $testRoot "preflight-repository"
        New-Item -ItemType Directory -Force -Path $preflightRepository | Out-Null
        & git -C $preflightRepository init -b feature | Out-Null
        & git -C $preflightRepository config user.name "Release test"
        & git -C $preflightRepository config user.email `
            "release-test@example.invalid"
        Set-Content -LiteralPath (Join-Path $preflightRepository "tracked.txt") `
            -Value "tracked" -Encoding ascii
        & git -C $preflightRepository add tracked.txt
        & git -C $preflightRepository commit -m "test" | Out-Null
        if ($LASTEXITCODE -ne 0) {
            throw "Could not create the preflight test repository."
        }
        $script:repositoryRoot = $preflightRepository
        $script:statePath = Join-Path $testState "no-state.json"
        Assert-ThrowsMatch -Action { Invoke-Preflight | Out-Null } `
            -Pattern "main branch" `
            -Message "Release preflight accepted a non-main checkout."
        & git -C $preflightRepository branch -m main
        Set-Content -LiteralPath (Join-Path $preflightRepository "dirty.txt") `
            -Value "dirty" -Encoding ascii
        Assert-ThrowsMatch -Action { Invoke-Preflight | Out-Null } `
            -Pattern "clean checkout" `
            -Message "Release preflight accepted a dirty checkout."
        $script:repositoryRoot = $repositoryRootForTest
        $script:statePath = Join-Path $testState "state.json"

        $gameLauncher = Join-Path $testRoot "game-launcher"
        $gameRoot = Join-Path $gameLauncher "Robotopia"
        New-Item -ItemType Directory -Force -Path $gameRoot | Out-Null
        Set-Content -LiteralPath (Join-Path $gameLauncher "installed-build.json") `
            -Value "{`"id`":`"$($policy.gameBuild.id)`"}" -Encoding ascii
        Assert-True (
            (Get-RobotopiaInstalledBuildId $gameRoot) -eq
                [int]$policy.gameBuild.id
        ) "The launcher-owned Robotopia build marker was not recognized."

        $canonical = Join-Path $testState "ecosystem-dist.tar"
        Set-Content -LiteralPath $canonical -Value "canonical" -Encoding ascii
        $testIndex = Join-Path $testRoot "source-sha.index"
        $previousIndex = $env:GIT_INDEX_FILE
        try {
            $env:GIT_INDEX_FILE = $testIndex
            & git -C $repositoryRootForTest read-tree HEAD
            & git -C $repositoryRootForTest add -- `
                .github/robotopia-game-build.json `
                release/release-policy.json `
                release/platform-toolchains.json `
                release/catalog.json `
                release/notes/v1.0.0-rc.1.md `
                tests/live-game-acceptance.json
            $testTree = (& git -C $repositoryRootForTest write-tree).Trim()
            $sourceSha = (& git -C $repositoryRootForTest `
                    -c "user.name=Release test" `
                    -c "user.email=release-test@example.invalid" `
                    commit-tree `
                    $testTree -p HEAD -m "release-admin source fixture").Trim()
        }
        finally {
            if ([string]::IsNullOrEmpty($previousIndex)) {
                Remove-Item Env:GIT_INDEX_FILE -ErrorAction SilentlyContinue
            }
            else {
                $env:GIT_INDEX_FILE = $previousIndex
            }
        }
        Assert-True ($sourceSha -cmatch "^[0-9a-f]{40}$") (
            "The test checkout did not resolve to an exact source SHA."
        )
        $canonicalSha = Get-Sha256 $canonical

        Invoke-WslBuild -SourceSha $sourceSha -CanonicalArchive $canonical `
            -CanonicalEcosystemSha $canonicalSha `
            -CanonicalArchiveSha $canonicalSha
        $wslLog = Get-Content -LiteralPath $fakeLog -Raw
        foreach ($expectedLogText in @(
                "wsl-arg Ubuntu-24.04",
                "wsl-arg wslpath",
                "base64 --decode"
            )) {
            Assert-True ($wslLog.Contains($expectedLogText)) (
                "WSL orchestration did not observe: $expectedLogText"
            )
        }
        $encodedScript = [regex]::Match(
            $wslLog,
            "printf '%s' '([A-Za-z0-9+/=]+)'"
        )
        Assert-True $encodedScript.Success (
            "The WSL build script was not transported with safe base64 framing."
        )
        $decodedScript = [System.Text.Encoding]::UTF8.GetString(
            [Convert]::FromBase64String($encodedScript.Groups[1].Value)
        )
        foreach ($expectedScriptText in @(
                "set -euo pipefail",
                "--no-hardlinks --no-checkout",
                "--platform linux",
                "--canonical-ecosystem-sha256",
                "--canonical-archive-sha256"
            )) {
            Assert-True ($decodedScript.Contains($expectedScriptText)) (
                "The WSL build script is missing: $expectedScriptText"
            )
        }
        Assert-True (-not $decodedScript.Contains("--platform macos")) (
            "The production WSL build still assumes a macOS package."
        )

        $localRemoteCheck = Join-Path $testRoot "local-asset.bin"
        $remoteRemoteCheck = Join-Path $testRoot "remote-asset.bin"
        Set-Content -LiteralPath $localRemoteCheck -Value "local" -Encoding ascii
        Set-Content -LiteralPath $remoteRemoteCheck -Value "remote" -Encoding ascii
        $env:FAKE_RELEASE_ASSET_SOURCE = $remoteRemoteCheck
        Assert-ThrowsMatch -Action {
            Assert-RemoteAssetMatches -AssetName "asset.bin" `
                -LocalPath $localRemoteCheck
        } -Pattern "does not match" `
            -Message "An uploaded asset mismatch did not fail closed."
        Remove-Item Env:FAKE_RELEASE_ASSET_SOURCE -ErrorAction SilentlyContinue

        Write-State -Phase "preflight" -SourceSha $sourceSha
        $storedState = Read-State
        Assert-True (
            [string]$storedState.protonExecutable -ceq
                "/opt/proton-10.0-4/proton"
        ) "Release state did not freeze the Proton executable."
        Assert-True (
            [string]$storedState.steamRoot -ceq
                "/mnt/c/Program Files (x86)/Steam"
        ) "Release state did not freeze the Steam root."
        Assert-True (
            [string]$storedState.compatDataRoot -ceq
                "/var/tmp/topiaforge-compat"
        ) "Release state did not freeze the compat-data root."
        foreach ($obsoleteStateProperty in @(
                "macHost",
                "macRepositoryPath",
                "macEnvironmentFile",
                "macKnownHostsFile",
                "macExpectedTeamId",
                "protonEvidence",
                "protonEvidenceBundle"
            )) {
            Assert-True (
                $storedState.PSObject.Properties.Name -notcontains
                    $obsoleteStateProperty
            ) "Release state retained $obsoleteStateProperty."
        }
        $script:ProtonExecutable = "/different/proton"
        Assert-ThrowsMatch -Action { Use-StateConfiguration $storedState } `
            -Pattern "Cannot change ProtonExecutable" `
            -Message "Frozen Proton configuration could be replaced."
        $script:ProtonExecutable = "/opt/proton-10.0-4/proton"

        $windowsArchive = Join-Path $testAssets "TopiaForge-windows-x64.zip"
        $creatorBundle = Join-Path $testRoot "creator-evidence.bundle"
        $creatorDescriptor = Join-Path $testRoot "creator-evidence.json"
        Set-Content -LiteralPath $windowsArchive -Value "windows candidate" `
            -Encoding ascii
        $script:WindowsCreatorEvidence = $creatorDescriptor
        $script:WindowsCreatorEvidenceBundle = $creatorBundle
        $acceptanceInventoryPath = Join-Path $repositoryRootForTest `
            "tests/live-game-acceptance.json"
        $acceptanceInventory = Get-Content -LiteralPath $acceptanceInventoryPath `
            -Raw | ConvertFrom-Json
        $creatorCaseEvidence = Join-Path $testRoot "creator-cases"
        New-Item -ItemType Directory -Force -Path $creatorCaseEvidence |
            Out-Null
        foreach ($creatorCase in $acceptanceInventory.creatorAcceptance.cases) {
            $caseDirectory = Join-Path $creatorCaseEvidence `
                ([string]$creatorCase.id)
            New-Item -ItemType Directory -Force -Path $caseDirectory |
                Out-Null
            Set-Content -LiteralPath (
                Join-Path $caseDirectory "observation.txt"
            ) -Value "retained evidence for $($creatorCase.id)" `
                -Encoding utf8NoBOM -NoNewline
        }
        # The native collector is exercised through its real artifact: a
        # challenge-bound acceptance result plus the canonical state pre-images
        # the CLI retains. Nothing here can be produced by inspecting a
        # directory, which is what the retired generator wrongly accepted.
        $creatorStateDirectory = Join-Path $testRoot "creator-state"
        New-Item -ItemType Directory -Force -Path $creatorStateDirectory |
            Out-Null
        $creatorSaveDocument =
            '{"layoutVersion":1,"members":[{"path":"player_data.json.gz",' +
            '"sha256":"' + ("3" * 64) + '","size":218}]}'
        $creatorCheckpointDocument =
            '{"saveVersion":2,"layoutVersion":1,"checkpoint":' +
            '{"CURRENT_CHECKPOINT":"F0Ql0Uceu2E","SGJzGz9Pevo_reached":true}}'
        function Write-CreatorStateForTest {
            param(
                [Parameter(Mandatory = $true)][string]$Directory,
                [Parameter(Mandatory = $true)][string]$SaveBeforeText,
                [Parameter(Mandatory = $true)][string]$SaveAfterText,
                [Parameter(Mandatory = $true)][string]$CheckpointBeforeText,
                [Parameter(Mandatory = $true)][string]$CheckpointAfterText
            )
            $pairs = [ordered]@{
                "save-before.bin" = $SaveBeforeText
                "save-after.bin" = $SaveAfterText
                "checkpoint-before.bin" = $CheckpointBeforeText
                "checkpoint-after.bin" = $CheckpointAfterText
            }
            foreach ($name in $pairs.Keys) {
                [System.IO.File]::WriteAllBytes(
                    (Join-Path $Directory $name),
                    [System.Text.Encoding]::UTF8.GetBytes($pairs[$name])
                )
            }
        }
        Write-CreatorStateForTest -Directory $creatorStateDirectory `
            -SaveBeforeText $creatorSaveDocument `
            -SaveAfterText $creatorSaveDocument `
            -CheckpointBeforeText $creatorCheckpointDocument `
            -CheckpointAfterText $creatorCheckpointDocument

        $creatorChallenge = "ab" * 32
        $creatorSessionId = "creator-session-0001"
        $creatorReceiptSha = "5" * 64
        $creatorMinimumCycles =
            [Int64]$acceptanceInventory.creatorAcceptance.minimumLifecycleCycles
        function Write-CreatorAcceptanceResultForTest {
            param(
                [Parameter(Mandatory = $true)][string]$Path,
                [Parameter(Mandatory = $true)][string]$StateDirectory,
                [Parameter(Mandatory = $true)][string]$Challenge,
                [Parameter(Mandatory = $true)][string]$SessionId,
                [Parameter(Mandatory = $true)][string]$ReceiptSha,
                [Parameter(Mandatory = $true)][string[]]$ExpectedCases,
                [Parameter(Mandatory = $true)][string]$GameBuildId,
                [Parameter(Mandatory = $true)][Int64]$MinimumCycles,
                [string[]]$PassedCases = @(),
                [Int64]$Cycles = 0
            )
            if ($PassedCases.Count -eq 0) { $PassedCases = $ExpectedCases }
            if ($Cycles -eq 0) { $Cycles = $MinimumCycles }
            $saveBeforeSha = Get-Sha256 (
                Join-Path $StateDirectory "save-before.bin"
            )
            $checkpointBeforeSha = Get-Sha256 (
                Join-Path $StateDirectory "checkpoint-before.bin"
            )
            $SaveAfterSha = Get-Sha256 (
                Join-Path $StateDirectory "save-after.bin"
            )
            $CheckpointAfterSha = Get-Sha256 (
                Join-Path $StateDirectory "checkpoint-after.bin"
            )
            $body = [ordered]@{
                schemaVersion = 1
                suite = "creator-full"
                startedAtUtc = "2026-01-01T00:00:00.000Z"
                completedAtUtc = "2026-01-01T01:00:00.000Z"
                gameDirectory = "C:/Robotopia"
                gameBuild = $GameBuildId
                acceptanceChallenge = $Challenge
                lastRunSessionId = $SessionId
                creatorPackageStatus = "loaded"
                creatorPackageReceipt = [ordered]@{
                    sourceSha256 = $ReceiptSha
                    criticalFiles = @(
                        [ordered]@{
                            path = "TopiaForge.CreatorTools.dll"
                            sha256 = "6" * 64
                        },
                        [ordered]@{
                            path = "topiaforge.mod.json"
                            sha256 = "7" * 64
                        }
                    )
                }
                requiredCases = @($ExpectedCases)
                passedCases = @($PassedCases)
                missingCases = @()
                failures = @()
                lifecycleCycles = $Cycles
                minimumLifecycleCycles = $MinimumCycles
                persistence = [ordered]@{
                    layout = [ordered]@{
                        version = 1
                        roots = @("%LOCALAPPDATA%/../LocalLow/Tomato Cake/robotopia")
                        exclusions = @("Player.log", "PostHog/", "Sentry/")
                    }
                    save = [ordered]@{
                        before = [ordered]@{ sha256 = $saveBeforeSha; size = 1 }
                        after = [ordered]@{ sha256 = $SaveAfterSha; size = 1 }
                        unchanged = ($saveBeforeSha -ceq $SaveAfterSha)
                    }
                    checkpoint = [ordered]@{
                        before = [ordered]@{
                            sha256 = $checkpointBeforeSha
                            size = 1
                        }
                        after = [ordered]@{
                            sha256 = $CheckpointAfterSha
                            size = 1
                        }
                        unchanged = (
                            $checkpointBeforeSha -ceq $CheckpointAfterSha
                        )
                    }
                }
                saveStateUnchanged = ($saveBeforeSha -ceq $SaveAfterSha)
                checkpointStateUnchanged = (
                    $checkpointBeforeSha -ceq $CheckpointAfterSha
                )
                succeeded = $true
            }
            [System.IO.File]::WriteAllBytes(
                $Path,
                [System.Text.Encoding]::UTF8.GetBytes(
                    ($body | ConvertTo-Json -Depth 32 -Compress)
                )
            )
            return $Path
        }
        $creatorExpectedCases = @(
            $acceptanceInventory.creatorAcceptance.cases |
                ForEach-Object { [string]$_.id } | Sort-Object
        )
        $releasePolicyForTest = Get-Content -LiteralPath (
            Join-Path $repositoryRootForTest "release/release-policy.json"
        ) -Raw | ConvertFrom-Json
        $creatorGameBuildId = [string]$releasePolicyForTest.gameBuild.id
        $creatorResultDefaults = @{
            StateDirectory = $creatorStateDirectory
            Challenge = $creatorChallenge
            SessionId = $creatorSessionId
            ReceiptSha = $creatorReceiptSha
            ExpectedCases = $creatorExpectedCases
            GameBuildId = $creatorGameBuildId
            MinimumCycles = $creatorMinimumCycles
        }

        $creatorAcceptanceResult = Join-Path $testRoot `
            "creator-acceptance-result.json"
        $null = Write-CreatorAcceptanceResultForTest @creatorResultDefaults `
            -Path $creatorAcceptanceResult
        $creatorGenerator = Join-Path $repositoryRootForTest `
            "tools/release/new-windows-creator-evidence.ps1"
        & $creatorGenerator -SourceSha $sourceSha -Version $Version `
            -WindowsArchive $windowsArchive `
            -CanonicalEcosystemSha256 $canonicalSha `
            -AcceptanceResult $creatorAcceptanceResult `
            -StateDirectory $creatorStateDirectory `
            -CaseEvidenceDirectory $creatorCaseEvidence `
            -OutputBundle $creatorBundle `
            -OutputDescriptor $creatorDescriptor `
            -RepositoryRoot $repositoryRootForTest | Out-Null

        $deterministicBundle = Join-Path $testRoot `
            "creator-evidence-deterministic.bundle"
        $deterministicDescriptor = Join-Path $testRoot `
            "creator-evidence-deterministic.json"
        & $creatorGenerator -SourceSha $sourceSha -Version $Version `
            -WindowsArchive $windowsArchive `
            -CanonicalEcosystemSha256 $canonicalSha `
            -AcceptanceResult $creatorAcceptanceResult `
            -StateDirectory $creatorStateDirectory `
            -CaseEvidenceDirectory $creatorCaseEvidence `
            -OutputBundle $deterministicBundle `
            -OutputDescriptor $deterministicDescriptor `
            -RepositoryRoot $repositoryRootForTest | Out-Null
        Assert-True (
            (Get-Sha256 $creatorBundle) -ceq
                (Get-Sha256 $deterministicBundle) -and
            (Get-Sha256 $creatorDescriptor) -ceq
                (Get-Sha256 $deterministicDescriptor)
        ) "Identical Creator inputs did not produce byte-identical evidence."

        Assert-WindowsCreatorEvidence -SourceSha $sourceSha `
            -WindowsArchive $windowsArchive -CanonicalSha $canonicalSha
        Assert-True (
            Test-Path -LiteralPath (
                Join-Path $testEvidence `
                    "windows-creator/creator-evidence.bundle"
            )
        ) "Manual same-host Windows Creator evidence was not retained."

        $creatorBundleBackup = Join-Path $testRoot `
            "creator-evidence-positive.bundle"
        $creatorDescriptorBackup = Join-Path $testRoot `
            "creator-evidence-positive.json"
        Copy-Item -LiteralPath $creatorBundle -Destination $creatorBundleBackup
        Copy-Item -LiteralPath $creatorDescriptor `
            -Destination $creatorDescriptorBackup
        Set-Content -LiteralPath $creatorBundle `
            -Value "opaque creator evidence" -Encoding ascii
        $opaqueDescriptor = Get-Content -LiteralPath $creatorDescriptor -Raw |
            ConvertFrom-Json
        $opaqueDescriptor.evidenceSha256 = Get-Sha256 $creatorBundle
        $opaqueDescriptor.evidenceSize =
            (Get-Item -LiteralPath $creatorBundle).Length
        $opaqueDescriptor | ConvertTo-Json -Depth 8 |
            Set-Content -LiteralPath $creatorDescriptor -Encoding utf8NoBOM
        Assert-ThrowsMatch -Action {
            Assert-WindowsCreatorEvidencePair -SourceSha $sourceSha `
                -WindowsArchive $windowsArchive `
                -CanonicalSha $canonicalSha `
                -DescriptorPath $creatorDescriptor `
                -BundlePath $creatorBundle
        } -Pattern "not a valid ZIP" `
            -Message "Opaque Creator evidence bytes were accepted."
        Copy-Item -LiteralPath $creatorBundleBackup `
            -Destination $creatorBundle -Force
        Copy-Item -LiteralPath $creatorDescriptorBackup `
            -Destination $creatorDescriptor -Force

        $tamperedBytesBundle = Join-Path $testRoot `
            "creator-evidence-tampered-bytes.bundle"
        $tamperedBytesDescriptor = Join-Path $testRoot `
            "creator-evidence-tampered-bytes.json"
        Copy-CreatorEvidenceZipForTest -Source $creatorBundleBackup `
            -Destination $tamperedBytesBundle -TamperFirstArtifact
        Copy-Item -LiteralPath $creatorDescriptorBackup `
            -Destination $tamperedBytesDescriptor
        $tamperedDescriptor = Get-Content `
            -LiteralPath $tamperedBytesDescriptor -Raw | ConvertFrom-Json
        $tamperedDescriptor.evidenceSha256 = Get-Sha256 $tamperedBytesBundle
        $tamperedDescriptor.evidenceSize =
            (Get-Item -LiteralPath $tamperedBytesBundle).Length
        $tamperedDescriptor | ConvertTo-Json -Depth 8 |
            Set-Content -LiteralPath $tamperedBytesDescriptor `
                -Encoding utf8NoBOM
        Assert-ThrowsMatch -Action {
            Assert-WindowsCreatorEvidencePair -SourceSha $sourceSha `
                -WindowsArchive $windowsArchive `
                -CanonicalSha $canonicalSha `
                -DescriptorPath $tamperedBytesDescriptor `
                -BundlePath $tamperedBytesBundle
        } -Pattern "embedded evidence bytes" `
            -Message "Tampered embedded Creator evidence bytes were accepted."

        $extraEntryBundle = Join-Path $testRoot `
            "creator-evidence-extra-entry.bundle"
        $extraEntryDescriptor = Join-Path $testRoot `
            "creator-evidence-extra-entry.json"
        Copy-CreatorEvidenceZipForTest -Source $creatorBundleBackup `
            -Destination $extraEntryBundle -AddUnexpectedEntry
        Copy-Item -LiteralPath $creatorDescriptorBackup `
            -Destination $extraEntryDescriptor
        $extraDescriptor = Get-Content -LiteralPath $extraEntryDescriptor `
            -Raw | ConvertFrom-Json
        $extraDescriptor.evidenceSha256 = Get-Sha256 $extraEntryBundle
        $extraDescriptor.evidenceSize =
            (Get-Item -LiteralPath $extraEntryBundle).Length
        $extraDescriptor | ConvertTo-Json -Depth 8 |
            Set-Content -LiteralPath $extraEntryDescriptor `
                -Encoding utf8NoBOM
        Assert-ThrowsMatch -Action {
            Assert-WindowsCreatorEvidencePair -SourceSha $sourceSha `
                -WindowsArchive $windowsArchive `
                -CanonicalSha $canonicalSha `
                -DescriptorPath $extraEntryDescriptor `
                -BundlePath $extraEntryBundle
        } -Pattern "unexpected entries" `
            -Message "An uninventoried Creator evidence entry was accepted."

        $unsafeEntryBundle = Join-Path $testRoot `
            "creator-evidence-unsafe-entry.bundle"
        $unsafeEntryDescriptor = Join-Path $testRoot `
            "creator-evidence-unsafe-entry.json"
        Copy-CreatorEvidenceZipForTest -Source $creatorBundleBackup `
            -Destination $unsafeEntryBundle -AddUnsafeEntry
        Copy-Item -LiteralPath $creatorDescriptorBackup `
            -Destination $unsafeEntryDescriptor
        $unsafeDescriptor = Get-Content -LiteralPath $unsafeEntryDescriptor `
            -Raw | ConvertFrom-Json
        $unsafeDescriptor.evidenceSha256 = Get-Sha256 $unsafeEntryBundle
        $unsafeDescriptor.evidenceSize =
            (Get-Item -LiteralPath $unsafeEntryBundle).Length
        $unsafeDescriptor | ConvertTo-Json -Depth 8 |
            Set-Content -LiteralPath $unsafeEntryDescriptor `
                -Encoding utf8NoBOM
        Assert-ThrowsMatch -Action {
            Assert-WindowsCreatorEvidencePair -SourceSha $sourceSha `
                -WindowsArchive $windowsArchive `
                -CanonicalSha $canonicalSha `
                -DescriptorPath $unsafeEntryDescriptor `
                -BundlePath $unsafeEntryBundle
        } -Pattern "unsafe" `
            -Message "An unsafe Creator evidence ZIP path was accepted."

        $reparseEntryBundle = Join-Path $testRoot `
            "creator-evidence-reparse-entry.bundle"
        $reparseEntryDescriptor = Join-Path $testRoot `
            "creator-evidence-reparse-entry.json"
        Copy-CreatorEvidenceZipForTest -Source $creatorBundleBackup `
            -Destination $reparseEntryBundle -MarkFirstArtifactReparse
        Copy-Item -LiteralPath $creatorDescriptorBackup `
            -Destination $reparseEntryDescriptor
        $reparseDescriptor = Get-Content `
            -LiteralPath $reparseEntryDescriptor -Raw | ConvertFrom-Json
        $reparseDescriptor.evidenceSha256 = Get-Sha256 $reparseEntryBundle
        $reparseDescriptor.evidenceSize =
            (Get-Item -LiteralPath $reparseEntryBundle).Length
        $reparseDescriptor | ConvertTo-Json -Depth 8 |
            Set-Content -LiteralPath $reparseEntryDescriptor `
                -Encoding utf8NoBOM
        Assert-ThrowsMatch -Action {
            Assert-WindowsCreatorEvidencePair -SourceSha $sourceSha `
                -WindowsArchive $windowsArchive `
                -CanonicalSha $canonicalSha `
                -DescriptorPath $reparseEntryDescriptor `
                -BundlePath $reparseEntryBundle
        } -Pattern "reparse-like" `
            -Message "A reparse-like Creator evidence entry was accepted."

        $sensitiveDescriptorPath = Join-Path $testRoot `
            "creator-evidence-sensitive.json"
        $sensitiveDescriptor = Get-Content `
            -LiteralPath $creatorDescriptorBackup -Raw | ConvertFrom-Json
        $sensitiveDescriptor | Add-Member -NotePropertyName "hostname" `
            -NotePropertyValue "release-workstation"
        $sensitiveDescriptor | ConvertTo-Json -Depth 8 |
            Set-Content -LiteralPath $sensitiveDescriptorPath `
                -Encoding utf8NoBOM
        Assert-ThrowsMatch -Action {
            Assert-WindowsCreatorEvidencePair -SourceSha $sourceSha `
                -WindowsArchive $windowsArchive `
                -CanonicalSha $canonicalSha `
                -DescriptorPath $sensitiveDescriptorPath `
                -BundlePath $creatorBundleBackup
        } -Pattern "exact expected fields|forbidden" `
            -Message "Sensitive public Creator metadata was accepted."

        # --- Adversarial: state mutated across End Session -----------------
        $mutatedStateDirectory = Join-Path $testRoot "creator-state-mutated"
        New-Item -ItemType Directory -Force -Path $mutatedStateDirectory |
            Out-Null
        Write-CreatorStateForTest -Directory $mutatedStateDirectory `
            -SaveBeforeText $creatorSaveDocument `
            -SaveAfterText ($creatorSaveDocument -replace '"size":218',
                '"size":219') `
            -CheckpointBeforeText $creatorCheckpointDocument `
            -CheckpointAfterText $creatorCheckpointDocument
        $mutatedSaveArgs = $creatorResultDefaults.Clone()
        $mutatedSaveArgs.StateDirectory = $mutatedStateDirectory
        $mutatedSaveResult = Write-CreatorAcceptanceResultForTest `
            @mutatedSaveArgs `
            -Path (Join-Path $testRoot "mutated-save-result.json")
        Assert-ThrowsMatch -Action {
            & $creatorGenerator -SourceSha $sourceSha -Version $Version `
                -WindowsArchive $windowsArchive `
                -CanonicalEcosystemSha256 $canonicalSha `
                -AcceptanceResult $mutatedSaveResult `
                -StateDirectory $mutatedStateDirectory `
                -CaseEvidenceDirectory $creatorCaseEvidence `
                -OutputBundle (Join-Path $testRoot "changed-state.bundle") `
                -OutputDescriptor (Join-Path $testRoot "changed-state.json") `
                -RepositoryRoot $repositoryRootForTest | Out-Null
        } -Pattern "challenge-bound interactive run|Save state changed" `
            -Message "Changed Creator save-state bytes were accepted."

        $mutatedCheckpointDirectory = Join-Path $testRoot `
            "creator-state-checkpoint"
        New-Item -ItemType Directory -Force `
            -Path $mutatedCheckpointDirectory | Out-Null
        Write-CreatorStateForTest -Directory $mutatedCheckpointDirectory `
            -SaveBeforeText $creatorSaveDocument `
            -SaveAfterText $creatorSaveDocument `
            -CheckpointBeforeText $creatorCheckpointDocument `
            -CheckpointAfterText ($creatorCheckpointDocument -replace
                'F0Ql0Uceu2E', 'ZZZZZZZZZZZ')
        $mutatedCheckpointArgs = $creatorResultDefaults.Clone()
        $mutatedCheckpointArgs.StateDirectory = $mutatedCheckpointDirectory
        $mutatedCheckpointResult = Write-CreatorAcceptanceResultForTest `
            @mutatedCheckpointArgs `
            -Path (Join-Path $testRoot "mutated-checkpoint-result.json")
        Assert-ThrowsMatch -Action {
            & $creatorGenerator -SourceSha $sourceSha -Version $Version `
                -WindowsArchive $windowsArchive `
                -CanonicalEcosystemSha256 $canonicalSha `
                -AcceptanceResult $mutatedCheckpointResult `
                -StateDirectory $mutatedCheckpointDirectory `
                -CaseEvidenceDirectory $creatorCaseEvidence `
                -OutputBundle (Join-Path $testRoot "changed-checkpoint.bundle") `
                -OutputDescriptor (
                    Join-Path $testRoot "changed-checkpoint.json"
                ) `
                -RepositoryRoot $repositoryRootForTest | Out-Null
        } -Pattern "challenge-bound interactive run|Checkpoint state changed" `
            -Message "Changed Creator checkpoint bytes were accepted."

        # --- Adversarial: incomplete or overstated case sets ----------------
        $shortCaseResult = Write-CreatorAcceptanceResultForTest `
            @creatorResultDefaults `
            -Path (Join-Path $testRoot "short-cases-result.json") `
            -PassedCases @($creatorExpectedCases | Select-Object -Skip 1)
        Assert-ThrowsMatch -Action {
            & $creatorGenerator -SourceSha $sourceSha -Version $Version `
                -WindowsArchive $windowsArchive `
                -CanonicalEcosystemSha256 $canonicalSha `
                -AcceptanceResult $shortCaseResult `
                -StateDirectory $creatorStateDirectory `
                -CaseEvidenceDirectory $creatorCaseEvidence `
                -OutputBundle (Join-Path $testRoot "short-cases.bundle") `
                -OutputDescriptor (Join-Path $testRoot "short-cases.json") `
                -RepositoryRoot $repositoryRootForTest | Out-Null
        } -Pattern "challenge-bound interactive run" `
            -Message "A Creator run missing a case was accepted."

        $shortCycleResult = Write-CreatorAcceptanceResultForTest `
            @creatorResultDefaults `
            -Path (Join-Path $testRoot "short-cycles-result.json") `
            -Cycles ([Int64]($creatorMinimumCycles - 1))
        Assert-ThrowsMatch -Action {
            & $creatorGenerator -SourceSha $sourceSha -Version $Version `
                -WindowsArchive $windowsArchive `
                -CanonicalEcosystemSha256 $canonicalSha `
                -AcceptanceResult $shortCycleResult `
                -StateDirectory $creatorStateDirectory `
                -CaseEvidenceDirectory $creatorCaseEvidence `
                -OutputBundle (Join-Path $testRoot "short-cycles.bundle") `
                -OutputDescriptor (Join-Path $testRoot "short-cycles.json") `
                -RepositoryRoot $repositoryRootForTest | Out-Null
        } -Pattern "challenge-bound interactive run" `
            -Message "A short Creator lifecycle cycle count was accepted."

        # --- Adversarial: spoofed, replayed, or rebound descriptors ---------
        foreach ($spoof in @(
                @{ Field = "acceptanceChallenge"; Value = "cd" * 32
                    Label = "challenge" },
                @{ Field = "lastRunSessionId"; Value = "other-session"
                    Label = "session" },
                @{ Field = "acceptanceResultSha256"; Value = "9" * 64
                    Label = "acceptance result digest" }
            )) {
            $spoofDescriptorPath = Join-Path $testRoot `
                "creator-spoof-$($spoof.Label -replace '\s', '-').json"
            $spoofDescriptor = Get-Content `
                -LiteralPath $creatorDescriptorBackup -Raw | ConvertFrom-Json
            $spoofDescriptor.($spoof.Field) = $spoof.Value
            $spoofDescriptor | ConvertTo-Json -Depth 32 -Compress |
                Set-Content -LiteralPath $spoofDescriptorPath `
                    -Encoding utf8NoBOM -NoNewline
            Assert-ThrowsMatch -Action {
                Assert-WindowsCreatorEvidencePair -SourceSha $sourceSha `
                    -WindowsArchive $windowsArchive `
                    -CanonicalSha $canonicalSha `
                    -DescriptorPath $spoofDescriptorPath `
                    -BundlePath $creatorBundleBackup
            } -Pattern "does not match this exact candidate|challenge-bound interactive run|manifest does not match" `
                -Message "A spoofed Creator $($spoof.Label) was accepted."
        }

        $wrongReceiptDescriptorPath = Join-Path $testRoot `
            "creator-wrong-receipt.json"
        $wrongReceiptDescriptor = Get-Content `
            -LiteralPath $creatorDescriptorBackup -Raw | ConvertFrom-Json
        $wrongReceiptDescriptor.creatorPackageReceipt.sourceSha256 = "8" * 64
        $wrongReceiptDescriptor | ConvertTo-Json -Depth 32 -Compress |
            Set-Content -LiteralPath $wrongReceiptDescriptorPath `
                -Encoding utf8NoBOM -NoNewline
        Assert-ThrowsMatch -Action {
            Assert-WindowsCreatorEvidencePair -SourceSha $sourceSha `
                -WindowsArchive $windowsArchive `
                -CanonicalSha $canonicalSha `
                -DescriptorPath $wrongReceiptDescriptorPath `
                -BundlePath $creatorBundleBackup
        } -Pattern "challenge-bound interactive run" `
            -Message "A wrong Creator package receipt was accepted."

        # A complete prior-run bundle replayed under a fresh challenge must
        # fail: the embedded acceptance result still carries the old challenge.
        $replayArgs = $creatorResultDefaults.Clone()
        $replayArgs.Challenge = "ef" * 32
        $replayArgs.SessionId = "creator-session-0002"
        $replayResult = Write-CreatorAcceptanceResultForTest @replayArgs `
            -Path (Join-Path $testRoot "replay-result.json")
        $replayBundle = Join-Path $testRoot "creator-replay.bundle"
        $replayDescriptor = Join-Path $testRoot "creator-replay.json"
        & $creatorGenerator -SourceSha $sourceSha -Version $Version `
            -WindowsArchive $windowsArchive `
            -CanonicalEcosystemSha256 $canonicalSha `
            -AcceptanceResult $replayResult `
            -StateDirectory $creatorStateDirectory `
            -CaseEvidenceDirectory $creatorCaseEvidence `
            -OutputBundle $replayBundle -OutputDescriptor $replayDescriptor `
            -RepositoryRoot $repositoryRootForTest | Out-Null
        Assert-ThrowsMatch -Action {
            Assert-WindowsCreatorEvidencePair -SourceSha $sourceSha `
                -WindowsArchive $windowsArchive `
                -CanonicalSha $canonicalSha `
                -DescriptorPath $creatorDescriptorBackup `
                -BundlePath $replayBundle
        } -Pattern "does not match this exact candidate|manifest does not match|changed while it was being validated" `
            -Message "A replayed prior-run Creator bundle was accepted."

        $extraCaseDirectory = Join-Path $creatorCaseEvidence "unexpected-case"
        New-Item -ItemType Directory -Path $extraCaseDirectory | Out-Null
        Set-Content -LiteralPath (
            Join-Path $extraCaseDirectory "observation.txt"
        ) -Value "unexpected" -Encoding ascii -NoNewline
        Assert-ThrowsMatch -Action {
            & $creatorGenerator -SourceSha $sourceSha -Version $Version `
                -WindowsArchive $windowsArchive `
                -CanonicalEcosystemSha256 $canonicalSha `
                -AcceptanceResult $creatorAcceptanceResult `
                -StateDirectory $creatorStateDirectory `
                -CaseEvidenceDirectory $creatorCaseEvidence `
                -OutputBundle (Join-Path $testRoot "extra-case.bundle") `
                -OutputDescriptor (Join-Path $testRoot "extra-case.json") `
                -RepositoryRoot $repositoryRootForTest | Out-Null
        } -Pattern "exact source-SHA cases" `
            -Message "An extra Creator evidence case was accepted."
        Remove-Item -LiteralPath $extraCaseDirectory -Recurse -Force

        if ($CreatorEvidenceOnly) {
            Write-Host (
                "Windows Creator deterministic evidence regression passed."
            )
            return
        }

        $windowsEvidence = Join-Path $testEvidence "windows"
        $unityEvidenceDirectory = Join-Path $windowsEvidence "unity"
        $robotopiaEvidenceDirectory = Join-Path $windowsEvidence "robotopia"
        New-Item -ItemType Directory -Force -Path `
            $unityEvidenceDirectory, $robotopiaEvidenceDirectory | Out-Null
        $unityEvidencePath = Join-Path $unityEvidenceDirectory "lifecycle.json"
        $unityBody = [ordered]@{
            schemaVersion = [Int64]1
            result = "pass"
            editorVersion = [string]$policy.toolchains.unity
            cycles = [Int64]16
            validatorSmoke = $true
            worldsAssemblyVersion = "1.0.0.0"
            uiAssemblyVersion = "1.0.0.0"
        }
        $unityBody | ConvertTo-Json -Depth 4 |
            Set-Content -LiteralPath $unityEvidencePath -Encoding utf8NoBOM
        $windowsRequiredCases = @(
            $acceptanceInventory.cases | ForEach-Object { [string]$_.id }
        )
        $windowsPassedCases = @($windowsRequiredCases | Sort-Object)
        $windowsJourneyId =
            "dev.topiaforge.release-$($sourceSha.Substring(0, 12))"
        $windowsJourneyName = "TopiaForge release $Version"
        $acceptanceChallenge = (("c" * 64) -join "")
        $acceptancePackageReceipt = [ordered]@{
            sourceSha256 = (("a" * 64) -join "")
            criticalFiles = @(
                [ordered]@{
                    path = "Mod.dll"
                    sha256 = (("b" * 64) -join "")
                },
                [ordered]@{
                    path = "topiaforge.mod.json"
                    sha256 = (("d" * 64) -join "")
                }
            )
        }
        $journeyPackageReceipt = [ordered]@{
            sourceSha256 = (("e" * 64) -join "")
            criticalFiles = @(
                [ordered]@{
                    path = "Mod.dll"
                    sha256 = (("f" * 64) -join "")
                },
                [ordered]@{
                    path = "topiaforge.mod.json"
                    sha256 = (("1" * 64) -join "")
                }
            )
        }
        $robotopiaEvidencePath = Join-Path $robotopiaEvidenceDirectory `
            "acceptance-result.json"
        $robotopiaBody = [ordered]@{
            schemaVersion = [Int64]2
            startedAtUtc = "2026-07-31T09:00:00.000Z"
            completedAtUtc = "2026-07-31T09:01:00.000Z"
            gameDirectory = $GameDirectory
            package = Join-Path $testRoot `
                "dev.topiaforge.sdk-acceptance-1.0.0-rc.1.topiaforgemod"
            requiredCases = $windowsRequiredCases
            passedCases = $windowsPassedCases
            missingCases = @()
            failures = @()
            acceptanceChallenge = $acceptanceChallenge
            lastRunSessionId = "windows-session-test"
            acceptancePackageStatus = "loaded"
            acceptancePackageReceipt = $acceptancePackageReceipt
            releaseJourneyEnabled = $true
            releaseJourneyAuthoringCommandCount = [Int64]2
            releaseJourneyCli = Join-Path $testRoot "topiaforge.exe"
            releaseJourneyProject = Join-Path $testRoot $windowsJourneyId
            requiredLoadedPackageId = $windowsJourneyId
            requiredLoadedPackageStatus = "loaded"
            requiredLoadedPackageReceipt = $journeyPackageReceipt
            requiredLogMarker = "$windowsJourneyName loaded. Run " +
                "'$windowsJourneyId`:greet' to try its command."
            requiredLogMarkerObserved = $true
            succeeded = $true
        }
        $robotopiaBody | ConvertTo-Json -Depth 8 |
            Set-Content -LiteralPath $robotopiaEvidencePath -Encoding utf8NoBOM
        $windowsValidation = [ordered]@{
            schema = "release-local-validation-v1"
            platform = "windows"
            version = $Version
            targetSha = $sourceSha
            archiveSha256 = Get-Sha256 $windowsArchive
            canonicalEcosystemSha256 = $canonicalSha
            canonicalArchiveSha256 = $canonicalSha
            signingState = "authenticode-timestamped"
            platformToolchains = [ordered]@{
                msvc = [string]$platformToolchains.windows.msvc
                node = [string]$policy.toolchains.node
                windowsSdk = [string]$platformToolchains.windows.windowsSdk
            }
            gameBuildId = [Int64]$policy.gameBuild.id
            gameArchiveSha256 =
                [string]$gameBuildMetadata.archives.windows.sha256
            gameFilesManifestSha256 =
                [string]$gameBuildMetadata.windowsFilesManifest.sha256
            gameFilesVerified =
                [Int64]$gameBuildMetadata.windowsFilesManifest.fileCount
            gameExecutableSha256 =
                [string]$gameBuildMetadata.windowsFilesManifest.gameExecutableSha256
            checks = @(
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
            evidenceSha256 = [ordered]@{
                unity = Get-Sha256 $unityEvidencePath
                robotopia = Get-Sha256 $robotopiaEvidencePath
            }
            passed = $true
        }
        $windowsValidationObject = $windowsValidation |
            ConvertTo-Json -Depth 8 | ConvertFrom-Json
        Assert-WindowsRuntimeEvidence -SourceSha $sourceSha `
            -Validation $windowsValidationObject

        $linuxArchive = Join-Path $testAssets "TopiaForge-linux-x64.zip"
        Set-Content -LiteralPath $linuxArchive -Value "linux candidate" `
            -Encoding ascii
        $protonDirectory = Join-Path $testEvidence "proton"
        $bundleStage = Join-Path $testRoot "proton-bundle"
        New-Item -ItemType Directory -Force -Path `
            $protonDirectory, $bundleStage | Out-Null
        $expectedCases = @(
            $acceptanceInventory.cases |
                ForEach-Object { [string]$_.id } |
                Sort-Object
        )
        $caseSetText = ($expectedCases -join "`n") + "`n"
        $caseSetSha = Get-Utf8Sha256 $caseSetText
        $gameExecutableSha =
            [string]$gameBuildMetadata.windowsFilesManifest.gameExecutableSha256
        $protonRuntimeSha = Get-Utf8Sha256 "Proton 10.0-4 runtime tree"
        $wineCommandSha = Get-Utf8Sha256 "private wine wrapper"
        $runtimeContext = @(
            "executionEnvironment=wsl2-wslg",
            "gameBuildId=$($policy.gameBuild.id)",
            "gameArchiveSha256=$($gameBuildMetadata.archives.windows.sha256)",
            "gameExecutableSha256=$gameExecutableSha",
            ("gameFilesManifestSha256={0}" -f
                $gameBuildMetadata.windowsFilesManifest.sha256),
            ("gameFilesVerified={0}" -f
                $gameBuildMetadata.windowsFilesManifest.fileCount),
            "independentQa=false",
            "protonRuntimeSha256=$protonRuntimeSha",
            "protonVersion=10.0-4",
            "runtime=windows-x64-via-proton",
            "winDllOverrides=winhttp=n,b",
            "wineCommandSha256=$wineCommandSha"
        ) -join "`n"
        $runtimeContext += "`n"
        [System.IO.File]::WriteAllText(
            (Join-Path $bundleStage "runtime-context.txt"),
            $runtimeContext,
            [System.Text.UTF8Encoding]::new($false)
        )
        $acceptanceResult = [ordered]@{
            schemaVersion = [Int64]2
            startedAtUtc = "2026-07-31T10:00:00.000Z"
            completedAtUtc = "2026-07-31T10:01:00.000Z"
            gameDirectory = "/mnt/c/Robotopia"
            package = "/tmp/dev.topiaforge.sdk-acceptance-1.0.0-rc.1.topiaforgemod"
            succeeded = $true
            requiredCases = $expectedCases
            passedCases = $expectedCases
            missingCases = @()
            failures = @()
            acceptanceChallenge = $acceptanceChallenge
            lastRunSessionId = "session-test"
            acceptancePackageStatus = "loaded"
            acceptancePackageReceipt = $acceptancePackageReceipt
            releaseJourneyEnabled = $true
            releaseJourneyAuthoringCommandCount = 2
            releaseJourneyCli = "/tmp/topiaforge"
            releaseJourneyProject =
                "/tmp/dev.topiaforge.release-$($sourceSha.Substring(0, 12))"
            requiredLoadedPackageId =
                "dev.topiaforge.release-$($sourceSha.Substring(0, 12))"
            requiredLoadedPackageStatus = "loaded"
            requiredLoadedPackageReceipt = $journeyPackageReceipt
            requiredLogMarker = "TopiaForge release $Version loaded. Run " +
                "'dev.topiaforge.release-$($sourceSha.Substring(0, 12)):greet' " +
                "to try its command."
            requiredLogMarkerObserved = $true
        }
        $acceptanceResult | ConvertTo-Json -Depth 8 |
            Set-Content -LiteralPath (
                Join-Path $bundleStage "acceptance-result.json"
            ) -Encoding utf8NoBOM
        Set-Content -LiteralPath (Join-Path $bundleStage "cli-help.txt") `
            -Value "TopiaForge CLI help" -Encoding utf8NoBOM
        Set-Content -LiteralPath (
            Join-Path $bundleStage "game-build-marker.json"
        ) -Value "{`"id`":$($policy.gameBuild.id)}" -Encoding utf8NoBOM
        $lastRunFixture = [ordered]@{
            schemaVersion = [Int64]1
            sessionId = "session-test"
            packages = @(
                [ordered]@{
                    id = "dev.topiaforge.sdk-acceptance"
                    sourceSha256 = $acceptancePackageReceipt.sourceSha256
                    criticalFiles = $acceptancePackageReceipt.criticalFiles
                },
                [ordered]@{
                    id = "dev.topiaforge.release-$($sourceSha.Substring(0, 12))"
                    sourceSha256 = $journeyPackageReceipt.sourceSha256
                    criticalFiles = $journeyPackageReceipt.criticalFiles
                }
            )
        }
        $lastRunFixture | ConvertTo-Json -Depth 8 |
            Set-Content -LiteralPath (
                Join-Path $bundleStage "last-run.json"
            ) -Encoding utf8NoBOM
        Set-Content -LiteralPath (Join-Path $bundleStage "manager.log") `
            -Value "TopiaForge release $Version loaded." -Encoding utf8NoBOM
        Set-Content -LiteralPath (Join-Path $bundleStage "new-mod.txt") `
            -Value "dev.topiaforge.release-test" -Encoding utf8NoBOM
        Set-Content -LiteralPath (Join-Path $bundleStage "proton-version.txt") `
            -Value "Proton 10.0-4" -Encoding utf8NoBOM

        $bundleEntries = @(
            "acceptance-result.json",
            "cli-help.txt",
            "game-build-marker.json",
            "last-run.json",
            "manager.log",
            "new-mod.txt",
            "proton-version.txt",
            "runtime-context.txt"
        )
        $protonBundle = Join-Path $protonDirectory "proton-evidence.bundle"
        & tar -cf $protonBundle -C $bundleStage @bundleEntries
        if ($LASTEXITCODE -ne 0) {
            throw "Could not create the Proton evidence fixture."
        }
        $acceptanceResultPath = Join-Path $bundleStage "acceptance-result.json"
        $protonDescriptor = Join-Path $protonDirectory "proton-evidence.json"
        $protonBody = [ordered]@{
            schema = "release-proton-evidence-v1"
            version = $Version
            targetSha = $sourceSha
            platform = "linux-proton"
            archiveSha256 = Get-Sha256 $linuxArchive
            archiveSize = (Get-Item -LiteralPath $linuxArchive).Length
            canonicalEcosystemSha256 = $canonicalSha
            gameBuildId = [Int64]$policy.gameBuild.id
            gameArchiveSha256 =
                [string]$gameBuildMetadata.archives.windows.sha256
            gameFilesManifestSha256 =
                [string]$gameBuildMetadata.windowsFilesManifest.sha256
            gameFilesVerified =
                [Int64]$gameBuildMetadata.windowsFilesManifest.fileCount
            result = "pass"
            suite = "full"
            protonVersion = "10.0-4"
            protonAppId = [Int64]$platformToolchains.linux.protonSteamAppId
            protonDepotId = [Int64]$platformToolchains.linux.protonSteamDepotId
            protonManifestId =
                [string]$platformToolchains.linux.protonSteamManifestId
            protonBuildId =
                [Int64]$platformToolchains.linux.protonSteamBuildId
            protonSourceCommit =
                [string]$platformToolchains.linux.protonSourceCommit
            protonRuntimeSha256 = $protonRuntimeSha
            executionEnvironment = "wsl2-wslg"
            runtime = "windows-x64-via-proton"
            runtimeConfigurationSha256 = Get-Utf8Sha256 $runtimeContext
            gameExecutableSha256 = $gameExecutableSha
            wineCommandSha256 = $wineCommandSha
            winDllOverrides = "winhttp=n,b"
            independentQa = $false
            caseInventorySha256 = Get-BytesSha256 (
                Get-GitBlobBytes -SourceSha $sourceSha `
                    -GitPath "tests/live-game-acceptance.json"
            )
            requiredCases = $expectedCases
            requiredCasesSha256 = $caseSetSha
            passedCases = $expectedCases
            passedCasesSha256 = $caseSetSha
            failures = @()
            releaseJourney = [ordered]@{
                enabled = $true
                authoringCommandCount = 2
                loadedPackageStatus = "loaded"
                logMarkerObserved = $true
            }
            acceptanceResultSha256 = Get-Sha256 $acceptanceResultPath
            evidenceSha256 = Get-Sha256 $protonBundle
            evidenceSize = (Get-Item -LiteralPath $protonBundle).Length
        }
        $protonBody | ConvertTo-Json -Depth 8 |
            Set-Content -LiteralPath $protonDescriptor -Encoding utf8NoBOM
        Assert-ProtonEvidence -SourceSha $sourceSha `
            -LinuxArchive $linuxArchive -CanonicalSha $canonicalSha

        $protonBundleBackup = Join-Path $testRoot "proton-evidence.valid.bundle"
        Copy-Item -LiteralPath $protonBundle -Destination $protonBundleBackup
        Add-Content -LiteralPath $protonBundle -Value "tampered"
        Assert-ThrowsMatch -Action {
            Assert-ProtonEvidence -SourceSha $sourceSha `
                -LinuxArchive $linuxArchive -CanonicalSha $canonicalSha
        } -Pattern "does not match" `
            -Message "Tampered Proton evidence bytes were accepted."
        Copy-Item -LiteralPath $protonBundleBackup -Destination $protonBundle -Force

        $windowsValidationPath = Join-Path $testEvidence "validation-windows.json"
        $windowsValidation | ConvertTo-Json -Depth 8 |
            Set-Content -LiteralPath $windowsValidationPath -Encoding utf8NoBOM
        $frozenQa = Join-Path $testEvidence "windows/windows-qa-summary.json"
        $recomputedQa = Join-Path $testRoot "windows-qa-recomputed.json"
        New-WindowsQaSummary -SourceSha $sourceSha `
            -CanonicalSha $canonicalSha -WindowsArchive $windowsArchive `
            -ValidationPath $windowsValidationPath `
            -Validation $windowsValidationObject -OutputPath $frozenQa
        New-WindowsQaSummary -SourceSha $sourceSha `
            -CanonicalSha $canonicalSha -WindowsArchive $windowsArchive `
            -ValidationPath $windowsValidationPath `
            -Validation $windowsValidationObject -OutputPath $recomputedQa
        Assert-ByteIdenticalMetadata -ExpectedPath $recomputedQa `
            -ActualPath $frozenQa -Label "Exact-rerun Windows QA summary"

        $retainedCreatorDescriptor = Join-Path $testEvidence `
            "windows-creator/creator-evidence.json"
        $retainedCreatorBundle = Join-Path $testEvidence `
            "windows-creator/creator-evidence.bundle"
        $creatorDescriptorBackup = Join-Path $testRoot "creator-descriptor.backup"
        $creatorBundleBackup = Join-Path $testRoot "creator-bundle.backup"
        Copy-Item $retainedCreatorDescriptor $creatorDescriptorBackup
        Copy-Item $retainedCreatorBundle $creatorBundleBackup
        # A replacement Creator pair that is internally consistent but is not
        # the frozen one must still be rejected: the QA summary is byte-frozen.
        $firstCreatorCaseId = [string]$acceptanceInventory.
            creatorAcceptance.cases[0].id
        Set-Content -LiteralPath (
            Join-Path (
                Join-Path $creatorCaseEvidence $firstCreatorCaseId
            ) "observation.txt"
        ) -Value "different but valid retained evidence" `
            -Encoding utf8NoBOM -NoNewline
        $replacementCreatorBundle = Join-Path $testRoot `
            "creator-replacement.bundle"
        $replacementCreatorDescriptor = Join-Path $testRoot `
            "creator-replacement.json"
        & $creatorGenerator -SourceSha $sourceSha -Version $Version `
            -WindowsArchive $windowsArchive `
            -CanonicalEcosystemSha256 $canonicalSha `
            -AcceptanceResult $creatorAcceptanceResult `
            -StateDirectory $creatorStateDirectory `
            -CaseEvidenceDirectory $creatorCaseEvidence `
            -OutputBundle $replacementCreatorBundle `
            -OutputDescriptor $replacementCreatorDescriptor `
            -RepositoryRoot $repositoryRootForTest | Out-Null
        Copy-Item -LiteralPath $replacementCreatorBundle `
            -Destination $retainedCreatorBundle -Force
        Copy-Item -LiteralPath $replacementCreatorDescriptor `
            -Destination $retainedCreatorDescriptor -Force
        New-WindowsQaSummary -SourceSha $sourceSha `
            -CanonicalSha $canonicalSha -WindowsArchive $windowsArchive `
            -ValidationPath $windowsValidationPath `
            -Validation $windowsValidationObject -OutputPath $recomputedQa
        Assert-ThrowsMatch -Action {
            Assert-ByteIdenticalMetadata -ExpectedPath $recomputedQa `
                -ActualPath $frozenQa -Label "Frozen Windows QA summary"
        } -Pattern "frozen bytes" `
            -Message "An internally consistent replacement Creator pair was accepted."
        Copy-Item $creatorDescriptorBackup $retainedCreatorDescriptor -Force
        Copy-Item $creatorBundleBackup $retainedCreatorBundle -Force

        $stageRepository = Join-Path $testRoot "stage-repository"
        $stageAssets = Join-Path $testRoot "stage-assets"
        $stageState = Join-Path $testRoot "stage-state"
        $stageEvidence = Join-Path $stageState "evidence"
        $stageNotesDirectory = Join-Path $stageRepository "release/notes"
        $stageCliDirectory = Join-Path $stageRepository "apps/topiaforge_cli"
        $stageGovernanceDirectory = Join-Path $stageRepository ".github"
        New-Item -ItemType Directory -Force -Path `
            $stageRepository, $stageAssets, $stageEvidence,
            $stageNotesDirectory, $stageCliDirectory,
            $stageGovernanceDirectory | Out-Null
        Copy-Item -LiteralPath (
            Join-Path $repositoryRootForTest `
                ".github/repository-governance.json"
        ) -Destination $stageGovernanceDirectory
        Set-Content -LiteralPath (
            Join-Path $stageNotesDirectory "v1.0.0-rc.1.md"
        ) -Value "stage notes" -Encoding utf8NoBOM
        Set-Content -LiteralPath (Join-Path $stageRepository "tracked.txt") `
            -Value "tracked" -Encoding ascii
        & git -C $stageRepository init -b main | Out-Null
        & git -C $stageRepository config user.name "Release test"
        & git -C $stageRepository config user.email `
            "release-test@example.invalid"
        & git -C $stageRepository config core.autocrlf false
        & git -C $stageRepository add .
        & git -C $stageRepository commit -m "stage fixture" | Out-Null
        if ($LASTEXITCODE -ne 0) {
            throw "Could not create the stage test repository."
        }
        $stageSourceSha = (& git -C $stageRepository rev-parse HEAD).Trim()
        $stageAsset = Join-Path $stageAssets "asset.bin"
        Set-Content -LiteralPath $stageAsset -Value "exact asset" -Encoding ascii
        $stageGhName = if ($IsWindows) { "stage-gh.cmd" } else { "stage-gh" }
        $stageDartName = if ($IsWindows) {
            "stage-dart.cmd"
        }
        else {
            "stage-dart"
        }
        $stageGh = Join-Path $fakeBin $stageGhName
        $stageDart = Join-Path $fakeBin $stageDartName
        $stageLog = Join-Path $testRoot "stage-gh.log"
        $stageDeleteMarker = Join-Path $testRoot "stage-delete.marker"
        $stageUploadMarker = Join-Path $testRoot "stage-upload.marker"
        $env:FAKE_STAGE_LOG = $stageLog
        $env:FAKE_STAGE_DELETE_MARKER = $stageDeleteMarker
        $env:FAKE_STAGE_UPLOAD_MARKER = $stageUploadMarker
        $env:FAKE_STAGE_ASSET_JSON = ConvertTo-Json -Compress -InputObject (
            [object[]]@(
                [ordered]@{
                    id = 123
                    name = "asset.bin"
                    state = "starter"
                }
            )
        )
        $env:FAKE_STAGE_RELEASE_JSON = (
            [ordered]@{
                tagName = "v1.0.0-rc.1"
                isDraft = $true
                isImmutable = $false
                isPrerelease = $true
                name = "TopiaForge 1.0.0-rc.1"
                body = "stage notes"
                assets = @()
                targetCommitish = $stageSourceSha
                author = [ordered]@{ login = "admin" }
            } | ConvertTo-Json -Compress
        )
        if ($IsWindows) {
            @'
@echo off
setlocal EnableExtensions
echo gh %*>>"%FAKE_STAGE_LOG%"
if "%~1"=="auth" exit /b 0
if "%~1"=="release" if "%~2"=="view" (
  echo %FAKE_STAGE_RELEASE_JSON%
  exit /b 0
)
if "%~1"=="release" if "%~2"=="upload" (
  if not exist "%FAKE_STAGE_DELETE_MARKER%" exit /b 61
  >"%FAKE_STAGE_UPLOAD_MARKER%" echo uploaded
  exit /b 0
)
if "%~1"=="api" if "%~2"=="--method" (
  if not "%~3"=="DELETE" exit /b 62
  if not "%~4"=="repos/furroxide/TopiaForge/releases/assets/123" exit /b 63
  >"%FAKE_STAGE_DELETE_MARKER%" echo deleted
  exit /b 0
)
if "%~1"=="api" if "%~2"=="repos/furroxide/TopiaForge" (
  echo true
  exit /b 0
)
if "%~1"=="api" if "%~2"=="user" (
  echo admin
  exit /b 0
)
if "%~1"=="api" if "%~2"=="repos/furroxide/TopiaForge/collaborators/admin/permission" (
  echo admin
  exit /b 0
)
if "%~1"=="api" if "%~2"=="repos/furroxide/TopiaForge/releases/tags/v1.0.0-rc.1" (
  echo %FAKE_STAGE_ASSET_JSON%
  exit /b 0
)
if "%~1"=="api" if "%~2"=="repos/furroxide/TopiaForge/releases/assets/123" (
  echo starter
  exit /b 0
)
exit /b 64
'@ | Set-Content -LiteralPath $stageGh -Encoding ascii
            @'
@echo off
echo dart %*>>"%FAKE_STAGE_LOG%"
exit /b 0
'@ | Set-Content -LiteralPath $stageDart -Encoding ascii
        }
        else {
            @'
#!/usr/bin/env bash
printf 'gh %s\n' "$*" >>"$FAKE_STAGE_LOG"
if [[ ${1:-} == auth ]]; then
  exit 0
fi
if [[ ${1:-} == release && ${2:-} == view ]]; then
  printf '%s\n' "$FAKE_STAGE_RELEASE_JSON"
  exit 0
fi
if [[ ${1:-} == release && ${2:-} == upload ]]; then
  [[ -f "$FAKE_STAGE_DELETE_MARKER" ]] || exit 61
  printf 'uploaded\n' >"$FAKE_STAGE_UPLOAD_MARKER"
  exit 0
fi
if [[ ${1:-} == api && ${2:-} == --method ]]; then
  [[ ${3:-} == DELETE ]] || exit 62
  [[ ${4:-} == repos/furroxide/TopiaForge/releases/assets/123 ]] || exit 63
  printf 'deleted\n' >"$FAKE_STAGE_DELETE_MARKER"
  exit 0
fi
if [[ ${1:-} == api && ${2:-} == repos/furroxide/TopiaForge ]]; then
  printf 'true\n'
  exit 0
fi
if [[ ${1:-} == api && ${2:-} == user ]]; then
  printf 'admin\n'
  exit 0
fi
if [[ ${1:-} == api &&
      ${2:-} == repos/furroxide/TopiaForge/collaborators/admin/permission ]]; then
  printf 'admin\n'
  exit 0
fi
if [[ ${1:-} == api &&
      ${2:-} == repos/furroxide/TopiaForge/releases/tags/v1.0.0-rc.1 ]]; then
  printf '%s\n' "$FAKE_STAGE_ASSET_JSON"
  exit 0
fi
if [[ ${1:-} == api &&
      ${2:-} == repos/furroxide/TopiaForge/releases/assets/123 ]]; then
  printf 'starter\n'
  exit 0
fi
exit 64
'@ | Set-Content -LiteralPath $stageGh -Encoding utf8NoBOM
            @'
#!/usr/bin/env bash
printf 'dart %s\n' "$*" >>"$FAKE_STAGE_LOG"
'@ | Set-Content -LiteralPath $stageDart -Encoding utf8NoBOM
            & chmod +x $stageGh $stageDart
            if ($LASTEXITCODE -ne 0) {
                throw "Could not mark fake staging tools executable."
            }
        }

        $originalRepositoryRoot = $script:repositoryRoot
        $originalStateDirectory = $script:stateDirectory
        $originalAssetsDirectory = $script:assetsDirectory
        $originalEvidenceDirectory = $script:evidenceDirectory
        $originalStatePath = $script:statePath
        $originalGitHubCli = $script:gitHubCli
        $originalAssertSourceStillExact = ${function:Assert-SourceStillExact}
        $originalAssertOriginStillExact = ${function:Assert-OriginStillExact}
        $originalBuildHandoff = ${function:Build-Handoff}
        $originalAssertExactSignedTag = ${function:Assert-ExactSignedTag}
        $originalAssertLatestRobotopiaBuild =
            ${function:Assert-LatestRobotopiaBuild}
        $originalInvokeReleaseGovernanceAudit =
            ${function:Invoke-ReleaseGovernanceAudit}
        $originalAssertGitHubTagSigningIdentity =
            ${function:Assert-GitHubTagSigningIdentity}
        $originalGetDartAndFlutter = ${function:Get-DartAndFlutter}
        $originalGetReleaseCatalogEntry = ${function:Get-ReleaseCatalogEntry}
        $originalGetStagedAssetPaths = ${function:Get-StagedAssetPaths}
        $originalAssertRemoteAssetMatches =
            ${function:Assert-RemoteAssetMatches}
        try {
            $script:repositoryRoot = $stageRepository
            $script:stateDirectory = $stageState
            $script:assetsDirectory = $stageAssets
            $script:evidenceDirectory = $stageEvidence
            $script:statePath = Join-Path $stageState "state.json"
            $script:gitHubCli = $stageGh
            function Assert-SourceStillExact {
                param([string]$SourceSha)
                $null = $SourceSha
            }
            function Assert-OriginStillExact {
                param([string]$SourceSha)
                $null = $SourceSha
            }
            function Build-Handoff {
                param(
                    [string]$SourceSha,
                    [string]$CanonicalSha,
                    [string]$CanonicalArchiveSha,
                    [string]$EcosystemEvidenceSha,
                    [switch]$VerifyOnly
                )
                $null = @(
                    $SourceSha,
                    $CanonicalSha,
                    $CanonicalArchiveSha,
                    $EcosystemEvidenceSha,
                    $VerifyOnly
                )
            }
            function Assert-ExactSignedTag {
                param([string]$SourceSha, [bool]$AllowCreation)
                $null = @($SourceSha, $AllowCreation)
                Add-Content -LiteralPath $stageLog -Value "signed-tag"
            }
            function Assert-LatestRobotopiaBuild {
                Add-Content -LiteralPath $stageLog -Value "latest-game"
            }
            function Invoke-ReleaseGovernanceAudit {
                Add-Content -LiteralPath $stageLog -Value "governance-audit"
            }
            function Assert-GitHubTagSigningIdentity {
                param([string]$GitHubLogin)
                Assert-True ($GitHubLogin -ceq "admin") (
                    "Stage signing-key revalidation used the wrong GitHub login."
                )
                Add-Content -LiteralPath $stageLog -Value "signing-identity"
            }
            function Get-DartAndFlutter {
                return @{
                    Dart = $stageDart
                    Flutter = $stageDart
                }
            }
            function Get-ReleaseCatalogEntry {
                return [pscustomobject]@{
                    notesFile = "release/notes/v1.0.0-rc.1.md"
                    prerelease = $true
                    artifacts = @("asset.bin")
                }
            }
            function Get-StagedAssetPaths {
                return ,$stageAsset
            }
            function Assert-RemoteAssetMatches {
                param([string]$AssetName, [string]$LocalPath)
                Assert-True (
                    $AssetName -ceq "asset.bin" -and
                    $LocalPath -ceq $stageAsset
                ) "Stage attempted to verify the wrong local asset."
            }

            $stageStateBody = [ordered]@{
                schema = "release-admin-state-v1"
                version = $Version
                tag = $tag
                sourceSha = $stageSourceSha
                phase = "built"
                rehearsal = $false
                repository = "furroxide/TopiaForge"
                wslDistribution = $WslDistribution
                protonExecutable = $ProtonExecutable
                steamRoot = $SteamRoot
                compatDataRoot = $CompatDataRoot
                unityPath = $UnityPath
                gameDirectory = $GameDirectory
                windowsCreatorEvidence = $WindowsCreatorEvidence
                windowsCreatorEvidenceBundle =
                    $WindowsCreatorEvidenceBundle
                canonicalSha256 = $canonicalSha
                canonicalArchiveSha256 = $canonicalSha
                ecosystemEvidenceSha256 = $canonicalSha
            }
            $stageStateBody | ConvertTo-Json -Depth 8 |
                Set-Content -LiteralPath $script:statePath `
                    -Encoding utf8NoBOM
            Invoke-Stage | Out-Null

            Assert-True (
                Test-Path -LiteralPath $stageDeleteMarker -PathType Leaf
            ) "Invoke-Stage did not delete the exact starter asset."
            Assert-True (
                Test-Path -LiteralPath $stageUploadMarker -PathType Leaf
            ) "Invoke-Stage did not resume the partial draft upload."
            $stageNativeLog = Get-Content -LiteralPath $stageLog -Raw
            $deleteIndex = $stageNativeLog.IndexOf(
                "api --method DELETE " +
                "repos/furroxide/TopiaForge/releases/assets/123"
            )
            $policyIndex = $stageNativeLog.IndexOf(
                "dart run bin/topiaforge.dart release validate-policy"
            )
            $latestIndex = $stageNativeLog.IndexOf("latest-game")
            $governanceIndex = $stageNativeLog.IndexOf("governance-audit")
            $identityIndex = $stageNativeLog.IndexOf("signing-identity")
            $tagIndex = $stageNativeLog.IndexOf("signed-tag")
            $uploadIndex = $stageNativeLog.IndexOf(
                "release upload v1.0.0-rc.1"
            )
            Assert-True (
                $policyIndex -ge 0 -and
                $latestIndex -gt $policyIndex -and
                $governanceIndex -gt $latestIndex -and
                $identityIndex -gt $governanceIndex -and
                $tagIndex -gt $identityIndex -and
                $deleteIndex -gt $tagIndex -and
                $uploadIndex -gt $deleteIndex
            ) (
                "Policy, latest-game, governance, signing identity, tag, " +
                "deletion, and upload checks did not run in the required order."
            )
            Assert-True (
                [string](Read-State).phase -ceq "staged"
            ) "A repaired partial draft did not advance to staged."

            $stagedState = Read-State
            Write-FinalizerState -Phase "dispatch-requested" `
                -State $stagedState `
                -RequestId (
                    "release-admin-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
                ) `
                -DispatchAttempt 1
            $strandedStageAssetsJson = ConvertTo-Json -Compress `
                -InputObject (
                [object[]]@(
                    [ordered]@{
                        id = 124
                        name = "asset.bin"
                        state = "uploaded"
                    },
                    [ordered]@{
                        id = 125
                        name = "release-bom.json"
                        state = "uploaded"
                        uploader = [ordered]@{
                            login = "github-actions[bot]"
                            id = 41898282
                            type = "Bot"
                        }
                        performed_via_github_app = [ordered]@{
                            id = 15368
                        }
                    },
                    [ordered]@{
                        id = 126
                        name = "release-sbom.spdx.json"
                        state = "starter"
                        uploader = [ordered]@{
                            login = "github-actions[bot]"
                            id = 41898282
                            type = "Bot"
                        }
                        performed_via_github_app = [ordered]@{
                            id = 15368
                        }
                    }
                )
            )
            $env:FAKE_STAGE_ASSET_JSON = $strandedStageAssetsJson
            Invoke-Stage | Out-Null
            Assert-True (
                [string](Read-State).phase -ceq "dispatch-requested"
            ) (
                "Invoke-Stage did not tolerate stranded generated metadata " +
                "after a journaled finalizer dispatch."
            )
            $wrongGeneratedUploader = @(
                $strandedStageAssetsJson | ConvertFrom-Json
            )
            @(
                $wrongGeneratedUploader |
                    Where-Object {
                        [string]$_.name -ceq "release-bom.json"
                    }
            )[0].uploader.id = 999
            $env:FAKE_STAGE_ASSET_JSON = ConvertTo-Json -Compress `
                -InputObject ([object[]]$wrongGeneratedUploader)
            Assert-ThrowsMatch -Action {
                Invoke-Stage | Out-Null
            } -Pattern "pinned GitHub Actions principal" -Message (
                "Invoke-Stage trusted stranded generated metadata from the " +
                "wrong uploader."
            )
            $env:FAKE_STAGE_ASSET_JSON = $strandedStageAssetsJson
        }
        finally {
            Set-Item Function:Assert-SourceStillExact `
                -Value $originalAssertSourceStillExact
            Set-Item Function:Assert-OriginStillExact `
                -Value $originalAssertOriginStillExact
            Set-Item Function:Build-Handoff -Value $originalBuildHandoff
            Set-Item Function:Assert-ExactSignedTag `
                -Value $originalAssertExactSignedTag
            Set-Item Function:Assert-LatestRobotopiaBuild `
                -Value $originalAssertLatestRobotopiaBuild
            Set-Item Function:Invoke-ReleaseGovernanceAudit `
                -Value $originalInvokeReleaseGovernanceAudit
            Set-Item Function:Assert-GitHubTagSigningIdentity `
                -Value $originalAssertGitHubTagSigningIdentity
            Set-Item Function:Get-DartAndFlutter `
                -Value $originalGetDartAndFlutter
            Set-Item Function:Get-ReleaseCatalogEntry `
                -Value $originalGetReleaseCatalogEntry
            Set-Item Function:Get-StagedAssetPaths `
                -Value $originalGetStagedAssetPaths
            Set-Item Function:Assert-RemoteAssetMatches `
                -Value $originalAssertRemoteAssetMatches
            $script:repositoryRoot = $originalRepositoryRoot
            $script:stateDirectory = $originalStateDirectory
            $script:assetsDirectory = $originalAssetsDirectory
            $script:evidenceDirectory = $originalEvidenceDirectory
            $script:statePath = $originalStatePath
            $script:gitHubCli = $originalGitHubCli
            Remove-Item Env:FAKE_STAGE_LOG -ErrorAction SilentlyContinue
            Remove-Item Env:FAKE_STAGE_DELETE_MARKER `
                -ErrorAction SilentlyContinue
            Remove-Item Env:FAKE_STAGE_UPLOAD_MARKER `
                -ErrorAction SilentlyContinue
            Remove-Item Env:FAKE_STAGE_RELEASE_JSON `
                -ErrorAction SilentlyContinue
            Remove-Item Env:FAKE_STAGE_ASSET_JSON `
                -ErrorAction SilentlyContinue
        }

        $dispatchStateDirectory = Join-Path $testRoot "dispatch-state"
        $dispatchAssetsDirectory = Join-Path $dispatchStateDirectory "assets"
        $dispatchEvidenceDirectory = Join-Path $dispatchStateDirectory "evidence"
        New-Item -ItemType Directory -Force -Path `
            $dispatchAssetsDirectory, $dispatchEvidenceDirectory | Out-Null
        $dispatchGh = Join-Path $fakeBin "dispatch-gh.ps1"
        $dispatchLog = Join-Path $testRoot "dispatch-gh.log"
        $dispatchWorkflowMarker = Join-Path $testRoot "dispatch-workflow.marker"
        $dispatchWatchMarker = Join-Path $testRoot "dispatch-watch.marker"
        $dispatchRerunMarker = Join-Path $testRoot "dispatch-rerun.marker"
        $dispatchListCount = Join-Path $testRoot "dispatch-list-count.txt"
        @'
$ErrorActionPreference = "Stop"
$cli = @($args)
[System.IO.File]::AppendAllText(
    $env:FAKE_DISPATCH_LOG,
    "gh $($cli -join ' ')" + [Environment]::NewLine
)

function New-FakeRun {
    param(
        [Parameter(Mandatory = $true)][string]$Status,
        [AllowNull()][string]$Conclusion,
        [switch]$WrongSha
    )
    $headSha = if ($WrongSha) {
        "0000000000000000000000000000000000000000"
    }
    else {
        $env:FAKE_DISPATCH_SOURCE_SHA
    }
    return [pscustomobject][ordered]@{
        databaseId = [Int64]$env:FAKE_DISPATCH_RUN_ID
        displayTitle = "Finalize v1.0.0-rc.1 " +
            "($($env:FAKE_DISPATCH_REQUEST_ID))"
        event = "workflow_dispatch"
        headBranch = "v1.0.0-rc.1"
        headSha = $headSha
        status = $Status
        conclusion = $Conclusion
        url = "https://example.invalid/actions/runs/" +
            $env:FAKE_DISPATCH_RUN_ID
        workflowName = "Finalize verified release"
    }
}

if ($cli.Count -ge 2 -and
    $cli[0] -ceq "workflow" -and $cli[1] -ceq "run") {
    $journal = Get-Content -LiteralPath $env:FAKE_DISPATCH_STATE_PATH -Raw |
        ConvertFrom-Json
    if ([int]$journal.finalizerDispatchAttempt -ne
        [int]$env:FAKE_DISPATCH_EXPECTED_ATTEMPT) {
        exit 65
    }
    [System.IO.File]::AppendAllText(
        $env:FAKE_DISPATCH_LOG,
        "journal-attempt $($journal.finalizerDispatchAttempt)" +
            [Environment]::NewLine
    )
    Set-Content -LiteralPath $env:FAKE_DISPATCH_WORKFLOW_MARKER `
        -Value "submitted" -Encoding ascii
    exit 0
}

if ($cli.Count -ge 2 -and
    $cli[0] -ceq "run" -and $cli[1] -ceq "list") {
    $listCount = if (Test-Path -LiteralPath `
        $env:FAKE_DISPATCH_LIST_COUNT) {
        [int](Get-Content -LiteralPath $env:FAKE_DISPATCH_LIST_COUNT -Raw)
    }
    else {
        0
    }
    Set-Content -LiteralPath $env:FAKE_DISPATCH_LIST_COUNT `
        -Value ($listCount + 1) -Encoding ascii
    $exposeRun = $env:FAKE_DISPATCH_SCENARIO -cin @(
        "wrong", "wrong-path", "crash-visible"
    ) -or
        (Test-Path -LiteralPath $env:FAKE_DISPATCH_WORKFLOW_MARKER)
    if (-not $exposeRun) {
        Write-Output "[]"
        exit 0
    }
    $wrongSha = $env:FAKE_DISPATCH_SCENARIO -ceq "wrong"
    $run = New-FakeRun -Status "completed" -Conclusion "success" `
        -WrongSha:$wrongSha
    $inventory = [object[]]::new(1)
    $inventory[0] = $run
    ConvertTo-Json -InputObject $inventory -Compress
    exit 0
}

if ($cli.Count -ge 2 -and
    $cli[0] -ceq "api" -and
    $cli[1] -ceq (
        "repos/furroxide/TopiaForge/actions/runs/" +
        $env:FAKE_DISPATCH_RUN_ID
    )) {
    $scenario = $env:FAKE_DISPATCH_SCENARIO
    $watched = Test-Path -LiteralPath $env:FAKE_DISPATCH_WATCH_MARKER
    $rerun = Test-Path -LiteralPath $env:FAKE_DISPATCH_RERUN_MARKER
    if ($scenario -ceq "queued" -and -not $watched) {
        $run = New-FakeRun -Status "queued" -Conclusion $null
    }
    elseif ($scenario -ceq "failure" -and -not ($rerun -and $watched)) {
        $run = New-FakeRun -Status "completed" -Conclusion "failure"
    }
    elseif ($scenario -ceq "cancelled" -and -not ($rerun -and $watched)) {
        $run = New-FakeRun -Status "completed" -Conclusion "cancelled"
    }
    else {
        $run = New-FakeRun -Status "completed" -Conclusion "success" `
            -WrongSha:($scenario -ceq "wrong")
    }
    $workflowPath = if ($scenario -ceq "wrong-path") {
        ".github/workflows/untrusted-release.yml"
    }
    else {
        ".github/workflows/release.yml"
    }
    $restRun = [pscustomobject][ordered]@{
        id = $run.databaseId
        display_title = $run.displayTitle
        event = $run.event
        head_branch = $run.headBranch
        head_sha = $run.headSha
        status = $run.status
        conclusion = $run.conclusion
        path = $workflowPath
        repository = [pscustomobject]@{
            full_name = "furroxide/TopiaForge"
        }
    }
    ConvertTo-Json -InputObject $restRun -Compress
    exit 0
}

if ($cli.Count -ge 2 -and
    $cli[0] -ceq "run" -and $cli[1] -ceq "watch") {
    Set-Content -LiteralPath $env:FAKE_DISPATCH_WATCH_MARKER `
        -Value "watched" -Encoding ascii
    exit 0
}

if ($cli.Count -ge 2 -and
    $cli[0] -ceq "run" -and $cli[1] -ceq "rerun") {
    Set-Content -LiteralPath $env:FAKE_DISPATCH_RERUN_MARKER `
        -Value "rerun" -Encoding ascii
    exit 0
}

if ($cli.Count -ge 2 -and
    $cli[0] -ceq "release" -and $cli[1] -ceq "view") {
    Write-Output $env:FAKE_DISPATCH_RELEASE_JSON
    exit 0
}

exit 64
'@ | Set-Content -LiteralPath $dispatchGh -Encoding utf8NoBOM

        $env:FAKE_DISPATCH_LOG = $dispatchLog
        $env:FAKE_DISPATCH_SOURCE_SHA = $stageSourceSha
        $env:FAKE_DISPATCH_WORKFLOW_MARKER = $dispatchWorkflowMarker
        $env:FAKE_DISPATCH_WATCH_MARKER = $dispatchWatchMarker
        $env:FAKE_DISPATCH_RERUN_MARKER = $dispatchRerunMarker
        $env:FAKE_DISPATCH_LIST_COUNT = $dispatchListCount
        $env:FAKE_DISPATCH_RELEASE_JSON = (
            [ordered]@{
                tagName = "v1.0.0-rc.1"
                isDraft = $false
                isImmutable = $true
                isPrerelease = $true
                targetCommitish = $stageSourceSha
            } | ConvertTo-Json -Compress
        )

        function Set-DispatchFixtureState {
            [Diagnostics.CodeAnalysis.SuppressMessageAttribute(
                "PSUseShouldProcessForStateChangingFunctions",
                "",
                Justification = "This test helper writes only its isolated fixture."
            )]
            param(
                [Parameter(Mandatory = $true)][string]$Phase,
                [string]$RequestId = "",
                [string]$RunId = "",
                [ValidateRange(0, [int]::MaxValue)]
                [int]$DispatchAttempt = 0
            )
            $body = [ordered]@{
                schema = "release-admin-state-v1"
                version = $Version
                tag = $tag
                sourceSha = $stageSourceSha
                phase = $Phase
                rehearsal = $false
                repository = "furroxide/TopiaForge"
                wslDistribution = $WslDistribution
                protonExecutable = $ProtonExecutable
                steamRoot = $SteamRoot
                compatDataRoot = $CompatDataRoot
                unityPath = $UnityPath
                gameDirectory = $GameDirectory
                windowsCreatorEvidence = $WindowsCreatorEvidence
                windowsCreatorEvidenceBundle =
                    $WindowsCreatorEvidenceBundle
                canonicalSha256 = $canonicalSha
                canonicalArchiveSha256 = $canonicalSha
                ecosystemEvidenceSha256 = $canonicalSha
            }
            if (-not [string]::IsNullOrWhiteSpace($RequestId)) {
                $body.finalizerRequestId = $RequestId
                $body.finalizerRunId = $RunId
                $body.finalizerDispatchAttempt = $DispatchAttempt
            }
            $body | ConvertTo-Json -Depth 8 |
                Set-Content -LiteralPath $script:statePath `
                    -Encoding utf8NoBOM
        }

        function Set-DispatchFakeScenario {
            [Diagnostics.CodeAnalysis.SuppressMessageAttribute(
                "PSUseShouldProcessForStateChangingFunctions",
                "",
                Justification = "This test helper resets only isolated fake state."
            )]
            param(
                [Parameter(Mandatory = $true)][string]$Scenario,
                [Parameter(Mandatory = $true)][string]$RequestId,
                [Parameter(Mandatory = $true)][string]$RunId
            )
            $env:FAKE_DISPATCH_SCENARIO = $Scenario
            $env:FAKE_DISPATCH_REQUEST_ID = $RequestId
            $env:FAKE_DISPATCH_RUN_ID = $RunId
            $env:FAKE_DISPATCH_EXPECTED_ATTEMPT = "1"
            Set-Content -LiteralPath $dispatchLog -Value "" -Encoding ascii
            Remove-Item -LiteralPath `
                $dispatchWorkflowMarker, $dispatchWatchMarker,
                $dispatchRerunMarker, $dispatchListCount `
                -Force -ErrorAction SilentlyContinue
        }

        $originalDispatchRepositoryRoot = $script:repositoryRoot
        $originalDispatchStateDirectory = $script:stateDirectory
        $originalDispatchAssetsDirectory = $script:assetsDirectory
        $originalDispatchEvidenceDirectory = $script:evidenceDirectory
        $originalDispatchStatePath = $script:statePath
        $originalDispatchGitHubCli = $script:gitHubCli
        $originalRegistrationGraceAttempts =
            $script:finalizerRegistrationGraceAttempts
        $originalRegistrationPollDelay =
            $script:finalizerRegistrationPollDelayMilliseconds
        $originalInvokeStage = ${function:Invoke-Stage}
        $originalNewFinalizerRequestId = ${function:New-FinalizerRequestId}
        try {
            $script:repositoryRoot = $stageRepository
            $script:stateDirectory = $dispatchStateDirectory
            $script:assetsDirectory = $dispatchAssetsDirectory
            $script:evidenceDirectory = $dispatchEvidenceDirectory
            $script:statePath = Join-Path $dispatchStateDirectory "state.json"
            $script:gitHubCli = $dispatchGh
            $script:finalizerRegistrationGraceAttempts = 2
            $script:finalizerRegistrationPollDelayMilliseconds = 0
            $env:FAKE_DISPATCH_STATE_PATH = $script:statePath
            function Invoke-Stage {
                [System.IO.File]::AppendAllText(
                    $env:FAKE_DISPATCH_LOG,
                    "stage-verify" + [Environment]::NewLine
                )
                return $stageSourceSha
            }
            function New-FinalizerRequestId {
                [Diagnostics.CodeAnalysis.SuppressMessageAttribute(
                    "PSUseShouldProcessForStateChangingFunctions",
                    "",
                    Justification = "This test double returns a fixture identifier."
                )]
                param()

                return $env:FAKE_DISPATCH_REQUEST_ID
            }

            $successRequestId =
                "release-admin-11111111111111111111111111111111"
            Set-DispatchFakeScenario -Scenario "success" `
                -RequestId $successRequestId -RunId "1001"
            Set-DispatchFixtureState -Phase "staged"
            Invoke-Dispatch
            $successState = Read-State
            Assert-True (
                [string]$successState.phase -ceq "published" -and
                [string]$successState.finalizerRequestId -ceq
                    $successRequestId -and
                [string]$successState.finalizerRunId -ceq "1001" -and
                [int]$successState.finalizerDispatchAttempt -eq 1
            ) "A successful dispatch did not persist its exact run identity."
            $successLog = Get-Content -LiteralPath $dispatchLog -Raw
            Assert-True (
                $successLog.Contains(
                    "workflow run release.yml --repo " +
                    "furroxide/TopiaForge --ref v1.0.0-rc.1 " +
                    "-f tag=v1.0.0-rc.1 -f request_id=$successRequestId"
                ) -and
                $successLog.Contains("journal-attempt 1")
            ) (
                "Dispatch did not journal the attempt before sending the " +
                "unique request ID to release.yml."
            )

            $crashRequestId =
                "release-admin-77777777777777777777777777777777"
            Set-DispatchFakeScenario -Scenario "crash-visible" `
                -RequestId $crashRequestId -RunId "1007"
            Set-DispatchFixtureState -Phase "dispatch-requested" `
                -RequestId $crashRequestId -DispatchAttempt 1
            Invoke-Dispatch
            $crashLog = Get-Content -LiteralPath $dispatchLog -Raw
            Assert-True (
                [string](Read-State).phase -ceq "published" -and
                [string](Read-State).finalizerRunId -ceq "1007" -and
                [int](Read-State).finalizerDispatchAttempt -eq 1 -and
                -not $crashLog.Contains("workflow run")
            ) (
                "Crash recovery dispatched a duplicate instead of binding " +
                "the already-visible remote run."
            )

            $noRunRequestId =
                "release-admin-88888888888888888888888888888888"
            Set-DispatchFakeScenario -Scenario "no-run" `
                -RequestId $noRunRequestId -RunId "1008"
            Set-DispatchFixtureState -Phase "dispatch-requested" `
                -RequestId $noRunRequestId -DispatchAttempt 1
            Assert-ThrowsMatch -Action {
                Invoke-Dispatch
            } -Pattern "automatic redispatch is forbidden" -Message (
                "An attempted request with no visible run was redispatched."
            )
            $noRunState = Read-State
            $noRunLog = Get-Content -LiteralPath $dispatchLog -Raw
            Assert-True (
                [string]$noRunState.phase -ceq "dispatch-requested" -and
                [string]$noRunState.finalizerRequestId -ceq
                    $noRunRequestId -and
                [string]$noRunState.finalizerRunId -ceq "" -and
                [int]$noRunState.finalizerDispatchAttempt -eq 1 -and
                [int](Get-Content -LiteralPath $dispatchListCount -Raw) -eq
                    3 -and
                -not $noRunLog.Contains("workflow run") -and
                -not $noRunLog.Contains("journal-attempt")
            ) (
                "Attempted/no-run recovery did not exhaust its bounded grace " +
                "and preserve the journal without redispatch."
            )

            $queuedRequestId =
                "release-admin-22222222222222222222222222222222"
            Set-DispatchFakeScenario -Scenario "queued" `
                -RequestId $queuedRequestId -RunId "1002"
            Set-DispatchFixtureState -Phase "dispatch-requested" `
                -RequestId $queuedRequestId -RunId "1002" `
                -DispatchAttempt 1
            Invoke-Dispatch
            $queuedLog = Get-Content -LiteralPath $dispatchLog -Raw
            Assert-True (
                [string](Read-State).phase -ceq "published" -and
                $queuedLog.Contains("run watch 1002") -and
                -not $queuedLog.Contains("run rerun 1002") -and
                -not $queuedLog.Contains("workflow run")
            ) "Resume did not wait for the exact queued finalizer run."

            foreach ($failedScenario in @("failure", "cancelled")) {
                $scenarioDigit = if ($failedScenario -ceq "failure") {
                    "3"
                }
                else {
                    "4"
                }
                $failedRequestId = "release-admin-" +
                    ($scenarioDigit * 32)
                $failedRunId = if ($failedScenario -ceq "failure") {
                    "1003"
                }
                else {
                    "1004"
                }
                Set-DispatchFakeScenario -Scenario $failedScenario `
                    -RequestId $failedRequestId -RunId $failedRunId
                Set-DispatchFixtureState -Phase "dispatch-requested" `
                    -RequestId $failedRequestId -RunId $failedRunId `
                    -DispatchAttempt 1
                Invoke-Dispatch
                $failedLog = Get-Content -LiteralPath $dispatchLog -Raw
                Assert-True (
                    [string](Read-State).phase -ceq "published" -and
                    $failedLog.Contains("run rerun $failedRunId") -and
                    $failedLog.Contains("run watch $failedRunId") -and
                    -not $failedLog.Contains("workflow run")
                ) (
                    "Resume did not safely rerun the exact " +
                    "$failedScenario finalizer run."
                )
            }

            $wrongRequestId =
                "release-admin-55555555555555555555555555555555"
            Set-DispatchFakeScenario -Scenario "wrong" `
                -RequestId $wrongRequestId -RunId "1005"
            Set-DispatchFixtureState -Phase "dispatch-requested" `
                -RequestId $wrongRequestId -DispatchAttempt 1
            Assert-ThrowsMatch -Action {
                Invoke-Dispatch
            } -Pattern "does not exactly match" -Message (
                "Resume accepted a request-ID match from the wrong source SHA."
            )
            $wrongLog = Get-Content -LiteralPath $dispatchLog -Raw
            Assert-True (
                [string](Read-State).phase -ceq "dispatch-requested" -and
                -not $wrongLog.Contains("workflow run") -and
                -not $wrongLog.Contains("run rerun")
            ) "A wrong finalizer run caused a mutation or replacement dispatch."

            $wrongPathRequestId =
                "release-admin-99999999999999999999999999999999"
            Set-DispatchFakeScenario -Scenario "wrong-path" `
                -RequestId $wrongPathRequestId -RunId "1009"
            Set-DispatchFixtureState -Phase "dispatch-requested" `
                -RequestId $wrongPathRequestId -DispatchAttempt 1
            Assert-ThrowsMatch -Action {
                Invoke-Dispatch
            } -Pattern "authoritative workflow path" -Message (
                "Resume accepted a same-name run from the wrong workflow path."
            )
            $wrongPathLog = Get-Content -LiteralPath $dispatchLog -Raw
            Assert-True (
                [string](Read-State).phase -ceq "dispatch-requested" -and
                -not $wrongPathLog.Contains("workflow run") -and
                -not $wrongPathLog.Contains("run rerun")
            ) "A wrong-path run caused a mutation or replacement dispatch."

            $publishedRequestId =
                "release-admin-66666666666666666666666666666666"
            Set-DispatchFakeScenario -Scenario "published" `
                -RequestId $publishedRequestId -RunId "1006"
            Set-DispatchFixtureState -Phase "published" `
                -RequestId $publishedRequestId -RunId "1006" `
                -DispatchAttempt 1
            $publishedStateBefore = [Convert]::ToBase64String(
                [System.IO.File]::ReadAllBytes($script:statePath)
            )
            Invoke-Dispatch
            $publishedStateAfter = [Convert]::ToBase64String(
                [System.IO.File]::ReadAllBytes($script:statePath)
            )
            $publishedLog = Get-Content -LiteralPath $dispatchLog -Raw
            Assert-True (
                $publishedStateAfter -ceq $publishedStateBefore -and
                -not $publishedLog.Contains("workflow run") -and
                -not $publishedLog.Contains("run watch") -and
                -not $publishedLog.Contains("run rerun")
            ) "An exact published rerun mutated state or GitHub Actions."
        }
        finally {
            Set-Item Function:Invoke-Stage -Value $originalInvokeStage
            Set-Item Function:New-FinalizerRequestId `
                -Value $originalNewFinalizerRequestId
            $script:repositoryRoot = $originalDispatchRepositoryRoot
            $script:stateDirectory = $originalDispatchStateDirectory
            $script:assetsDirectory = $originalDispatchAssetsDirectory
            $script:evidenceDirectory = $originalDispatchEvidenceDirectory
            $script:statePath = $originalDispatchStatePath
            $script:gitHubCli = $originalDispatchGitHubCli
            $script:finalizerRegistrationGraceAttempts =
                $originalRegistrationGraceAttempts
            $script:finalizerRegistrationPollDelayMilliseconds =
                $originalRegistrationPollDelay
            foreach ($environmentName in @(
                    "FAKE_DISPATCH_LOG",
                    "FAKE_DISPATCH_SOURCE_SHA",
                    "FAKE_DISPATCH_WORKFLOW_MARKER",
                    "FAKE_DISPATCH_WATCH_MARKER",
                    "FAKE_DISPATCH_RERUN_MARKER",
                    "FAKE_DISPATCH_LIST_COUNT",
                    "FAKE_DISPATCH_RELEASE_JSON",
                    "FAKE_DISPATCH_SCENARIO",
                    "FAKE_DISPATCH_REQUEST_ID",
                    "FAKE_DISPATCH_RUN_ID",
                    "FAKE_DISPATCH_STATE_PATH",
                    "FAKE_DISPATCH_EXPECTED_ATTEMPT"
                )) {
                Remove-Item "Env:$environmentName" -ErrorAction SilentlyContinue
            }
        }
    }
    finally {
        $env:PATH = $oldPath
        $env:ProgramFiles = $oldProgramFiles
        Remove-Item Env:TOPIAFORGE_RELEASE_TEST_IMPORT `
            -ErrorAction SilentlyContinue
        Remove-Item Env:FAKE_RELEASE_LOG -ErrorAction SilentlyContinue
        Remove-Item Env:FAKE_RELEASE_ASSET_SOURCE `
            -ErrorAction SilentlyContinue
    }

    Write-Host "release-admin orchestration tests passed."
}
finally {
    if (Test-Path -LiteralPath $testRoot) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}
