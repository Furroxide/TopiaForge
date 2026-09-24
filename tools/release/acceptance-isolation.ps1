# Private release acceptance verification. This helper never launches the game.
function Get-ReleaseIsolationRecordHash {
    param(
        [Parameter(Mandatory = $true)][AllowEmptyString()][string]$Path,
        [string]$ExpectedSha256 = ''
    )
    if ([string]::IsNullOrWhiteSpace($Path)) {
        throw 'An explicit -AcceptanceIsolationRecord is required for isolated acceptance.'
    }
    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $item = Get-Item -LiteralPath $fullPath -Force -ErrorAction SilentlyContinue
    if ($null -eq $item -or $item -isnot [System.IO.FileInfo] -or
        $item.Length -le 0 -or $item.Length -gt 65536) {
        throw 'The acceptance isolation record must be a bounded regular file.'
    }
    $ancestor = $item
    while ($null -ne $ancestor) {
        if (($ancestor.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw 'The acceptance isolation record cannot traverse links or reparse points.'
        }
        $ancestor = if ($ancestor -is [System.IO.FileInfo]) { $ancestor.Directory } else { $ancestor.Parent }
    }
    $digest = (Get-FileHash -LiteralPath $fullPath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($ExpectedSha256 -and $digest -cne $ExpectedSha256) {
        throw 'The frozen acceptance isolation record bytes changed.'
    }
    return $digest
}

function Assert-ReleaseIsolationUnlinkedPath {
    param([string]$Path)
    if ($IsWindows) {
        $spelling = $Path.Replace('/', '\')
        if ($spelling.StartsWith('\\?\', [System.StringComparison]::Ordinal) -or
            $spelling.StartsWith('\\.\', [System.StringComparison]::Ordinal) -or
            $spelling.StartsWith('\??\', [System.StringComparison]::Ordinal)) {
            throw 'Release isolation path aliases using device namespaces are not permitted.'
        }
    }
    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $current = $fullPath
    while (-not [string]::IsNullOrEmpty($current)) {
        $attributes = $null
        try { $attributes = [System.IO.File]::GetAttributes($current) }
        catch [System.IO.FileNotFoundException] { $attributes = $null }
        catch [System.IO.DirectoryNotFoundException] { $attributes = $null }
        if ($null -ne $attributes) {
            if (($attributes -band ([System.IO.FileAttributes]::ReparsePoint -bor
                        [System.IO.FileAttributes]::Device)) -ne 0) {
                throw 'Release isolation paths cannot traverse links, reparse points or devices.'
            }
            if ($current -cne $fullPath -and
                ($attributes -band [System.IO.FileAttributes]::Directory) -eq 0) {
                throw 'A release isolation path ancestor is not an ordinary directory.'
            }
        }
        $current = [System.IO.Path]::GetDirectoryName($current)
    }
}

function Assert-ReleaseIsolationRecordOutsideOutputs {
    param([string]$RecordPath, [string[]]$OutputDirectories)
    Assert-ReleaseIsolationUnlinkedPath -Path $RecordPath
    $record = [System.IO.Path]::GetFullPath($RecordPath)
    foreach ($output in $OutputDirectories) {
        Assert-ReleaseIsolationUnlinkedPath -Path $output
        $directory = [System.IO.Path]::GetFullPath($output).TrimEnd('\', '/')
        if ($record.Equals($directory, [System.StringComparison]::OrdinalIgnoreCase) -or
            $record.StartsWith($directory + [System.IO.Path]::DirectorySeparatorChar,
                [System.StringComparison]::OrdinalIgnoreCase)) {
            throw 'Release output cleanup must not overwrite the acceptance isolation record.'
        }
    }
}

function Invoke-ReleaseIsolationVerifier {
    param([string]$FilePath, [string[]]$Arguments, [string]$WorkingDirectory)
    Push-Location $WorkingDirectory
    try {
        # Keep diagnostics on stderr; stdout is a private, bounded JSON summary.
        $result = & $FilePath @Arguments | Out-String
        if ($LASTEXITCODE -ne 0) { throw 'The private acceptance isolation verifier rejected the evidence.' }
        return $result
    }
    finally { Pop-Location }
}

function Get-VerifiedReleaseAcceptanceIsolation {
    param(
        [Parameter(Mandatory = $true)][string]$EvidencePath,
        [Parameter(Mandatory = $true)][string]$IsolationRecordPath,
        [Parameter(Mandatory = $true)][string]$CliPath,
        [string[]]$PrefixArguments = @(),
        [string]$WorkingDirectory = (Get-Location).Path
    )
    $recordHash = Get-ReleaseIsolationRecordHash -Path $IsolationRecordPath
    $evidence = Get-Item -LiteralPath $EvidencePath -Force -ErrorAction SilentlyContinue
    if ($null -eq $evidence -or $evidence -isnot [System.IO.FileInfo] -or
        ($evidence.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0 -or
        $evidence.Length -le 0 -or $evidence.Length -gt 16777216) {
        throw 'Private acceptance evidence must be a bounded regular file.'
    }
    $evidenceHash = (Get-FileHash -LiteralPath $EvidencePath -Algorithm SHA256).Hash
    $arguments = @($PrefixArguments) + @(
        'acceptance', 'verify-isolation', '--evidence', $EvidencePath,
        '--isolation-record', $IsolationRecordPath
    )
    $text = Invoke-ReleaseIsolationVerifier -FilePath $CliPath -Arguments $arguments `
        -WorkingDirectory $WorkingDirectory
    if ([string]::IsNullOrWhiteSpace($text) -or $text.Length -gt 65536) {
        throw 'The acceptance isolation verifier returned an invalid summary.'
    }
    $summary = $text | ConvertFrom-Json -DateKind String
    $keys = @('schemaVersion', 'status', 'gameDirectory', 'managerRoot', 'provisioningRecordSha256', 'acknowledgementSha256')
    if (@($summary.PSObject.Properties.Name).Count -ne $keys.Count -or
        @($summary.PSObject.Properties.Name | Where-Object { $_ -cnotin $keys }).Count -ne 0 -or
        $summary.schemaVersion -isnot [Int64] -or $summary.schemaVersion -ne 1 -or
        $summary.status -isnot [string] -or $summary.status -cne 'admitted' -or
        $summary.gameDirectory -isnot [string] -or
        -not [System.IO.Path]::IsPathFullyQualified($summary.gameDirectory) -or
        $summary.managerRoot -isnot [string] -or
        -not [System.IO.Path]::IsPathFullyQualified($summary.managerRoot) -or
        $summary.provisioningRecordSha256 -isnot [string] -or
        $summary.provisioningRecordSha256 -cne $recordHash -or
        $summary.acknowledgementSha256 -isnot [string] -or
        $summary.acknowledgementSha256 -cnotmatch '\A[0-9a-f]{64}\z') {
        throw 'The acceptance isolation verifier returned an invalid or mismatched summary.'
    }
    $null = Get-ReleaseIsolationRecordHash -Path $IsolationRecordPath -ExpectedSha256 $recordHash
    if ((Get-FileHash -LiteralPath $EvidencePath -Algorithm SHA256).Hash -cne $evidenceHash) {
        throw 'Private acceptance evidence changed during isolation verification.'
    }
    return $summary
}
