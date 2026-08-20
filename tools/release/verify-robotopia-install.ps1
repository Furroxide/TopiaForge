[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$GameDirectory,

    [Parameter(Mandatory = $true)]
    [string]$MetadataPath
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

function Assert-BoundedRegularFile {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][long]$MaximumBytes,
        [Parameter(Mandatory = $true)][string]$Label,
        [switch]$AllowEmpty
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "$Label is missing: $Path"
    }
    $item = Get-Item -LiteralPath $Path -Force
    if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0 -or
        (-not $AllowEmpty -and $item.Length -le 0) -or
        $item.Length -gt $MaximumBytes) {
        throw "$Label must be a bounded, non-reparse regular file."
    }
    return $item
}

function Assert-ExactProperties {
    param(
        [Parameter(Mandatory = $true)][psobject]$Value,
        [Parameter(Mandatory = $true)][string[]]$Expected,
        [Parameter(Mandatory = $true)][string]$Label
    )

    $actual = @($Value.PSObject.Properties.Name | Sort-Object)
    $wanted = @($Expected | Sort-Object)
    if (Compare-Object $actual $wanted -SyncWindow 0) {
        throw "$Label contains forbidden or missing properties."
    }
}

function Get-Sha256 {
    param([Parameter(Mandatory = $true)][string]$Path)
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).
        Hash.ToLowerInvariant()
}

$metadataItem = Assert-BoundedRegularFile -Path $MetadataPath `
    -MaximumBytes 65536 -Label "Robotopia build metadata"
$metadataRaw = [System.IO.File]::ReadAllText($metadataItem.FullName)
try {
    $metadataDocument = [System.Text.Json.JsonDocument]::Parse(
        $metadataRaw,
        [System.Text.Json.JsonDocumentOptions]@{
            AllowTrailingCommas = $false
            CommentHandling = [System.Text.Json.JsonCommentHandling]::Disallow
            MaxDepth = 16
        }
    )
}
catch {
    throw "Robotopia build metadata is not strict JSON."
}
try {
    if ($metadataDocument.RootElement.ValueKind -ne
        [System.Text.Json.JsonValueKind]::Object) {
        throw "Robotopia build metadata must be an object."
    }
    $metadataPropertyNames = @(
        $metadataDocument.RootElement.EnumerateObject() |
            ForEach-Object { $_.Name }
    )
    if ($metadataPropertyNames.Count -ne
        @($metadataPropertyNames | Sort-Object -Unique).Count) {
        throw "Robotopia build metadata contains duplicate properties."
    }
}
finally {
    $metadataDocument.Dispose()
}
$metadata = $metadataRaw | ConvertFrom-Json
Assert-ExactProperties -Value $metadata -Expected @(
    "archives",
    "baseUrl",
    "buildId",
    "manifestUrl",
    "sourcePlatform",
    "windowsFilesManifest"
) -Label "Robotopia build metadata"
Assert-ExactProperties -Value $metadata.windowsFilesManifest -Expected @(
    "fileCount",
    "gameExecutableSha256",
    "path",
    "sha256"
) -Label "Robotopia Windows files-manifest policy"

$manifestPolicy = $metadata.windowsFilesManifest
if ([string]$manifestPolicy.path -cne "filelist.json" -or
    [string]$manifestPolicy.sha256 -cnotmatch "^[0-9a-f]{64}$" -or
    [string]$manifestPolicy.gameExecutableSha256 -cnotmatch
        "^[0-9a-f]{64}$" -or
    $manifestPolicy.fileCount -isnot [Int64] -or
    [Int64]$manifestPolicy.fileCount -le 0) {
    throw "Robotopia Windows files-manifest policy is invalid."
}

$resolvedGameDirectory = [System.IO.Path]::GetFullPath($GameDirectory).
    TrimEnd("\", "/")
if (-not (Test-Path -LiteralPath $resolvedGameDirectory -PathType Container)) {
    throw "Robotopia game directory is missing."
}
$gameDirectoryItem = Get-Item -LiteralPath $resolvedGameDirectory -Force
if (($gameDirectoryItem.Attributes -band
        [System.IO.FileAttributes]::ReparsePoint) -ne 0 -or
    -not $gameDirectoryItem.Name.Equals(
        "Robotopia",
        [System.StringComparison]::Ordinal
    )) {
    throw "Robotopia must be a non-reparse directory named exactly Robotopia."
}

$manifestPath = Join-Path $gameDirectoryItem.Parent.FullName `
    ([string]$manifestPolicy.path)
$manifestItem = Assert-BoundedRegularFile -Path $manifestPath `
    -MaximumBytes 1048576 -Label "Official Robotopia files manifest"
$manifestSha256 = Get-Sha256 $manifestItem.FullName
if ($manifestSha256 -cne [string]$manifestPolicy.sha256) {
    throw "Installed Robotopia files manifest does not match the pinned official archive."
}

$manifestRaw = [System.IO.File]::ReadAllText($manifestItem.FullName)
try {
    $manifestDocument = [System.Text.Json.JsonDocument]::Parse(
        $manifestRaw,
        [System.Text.Json.JsonDocumentOptions]@{
            AllowTrailingCommas = $false
            CommentHandling = [System.Text.Json.JsonCommentHandling]::Disallow
            MaxDepth = 16
        }
    )
}
catch {
    throw "Official Robotopia files manifest is not strict JSON."
}
try {
    $root = $manifestDocument.RootElement
    if ($root.ValueKind -ne [System.Text.Json.JsonValueKind]::Object) {
        throw "Official Robotopia files manifest must be an object."
    }
    $rootProperties = @($root.EnumerateObject())
    $rootNames = @($rootProperties | ForEach-Object { $_.Name })
    if ($rootNames.Count -ne @($rootNames | Sort-Object -Unique).Count) {
        throw "Official Robotopia files manifest contains duplicate root properties."
    }
    if ($rootNames.Count -ne 3 -or
        $rootNames -cnotcontains "version" -or
        $rootNames -cnotcontains "root" -or
        $rootNames -cnotcontains "files" -or
        $root.GetProperty("version").ValueKind -ne
            [System.Text.Json.JsonValueKind]::Number -or
        $root.GetProperty("version").GetInt32() -ne 1 -or
        $root.GetProperty("root").ValueKind -ne
            [System.Text.Json.JsonValueKind]::String -or
        $root.GetProperty("root").GetString() -cne "Robotopia" -or
        $root.GetProperty("files").ValueKind -ne
            [System.Text.Json.JsonValueKind]::Array) {
        throw "Official Robotopia files manifest contract is invalid."
    }
    $files = @($root.GetProperty("files").EnumerateArray())
    if ($files.Count -ne [Int64]$manifestPolicy.fileCount) {
        throw "Official Robotopia files manifest count does not match the pin."
    }

    $seenOrdinal = [System.Collections.Generic.HashSet[string]]::new(
        [System.StringComparer]::Ordinal
    )
    $seenInsensitive = [System.Collections.Generic.HashSet[string]]::new(
        [System.StringComparer]::OrdinalIgnoreCase
    )
    $previousPath = $null
    $gamePrefix = $resolvedGameDirectory +
        [System.IO.Path]::DirectorySeparatorChar
    $gameExecutableSha256 = $null

    foreach ($entry in $files) {
        if ($entry.ValueKind -ne [System.Text.Json.JsonValueKind]::Object) {
            throw "Official Robotopia files manifest entries must be objects."
        }
        $entryProperties = @($entry.EnumerateObject())
        $entryNames = @($entryProperties | ForEach-Object { $_.Name })
        if ($entryNames.Count -ne @($entryNames | Sort-Object -Unique).Count -or
            $entryNames.Count -ne 3 -or
            $entryNames -cnotcontains "path" -or
            $entryNames -cnotcontains "sha256" -or
            $entryNames -cnotcontains "size") {
            throw "Official Robotopia files manifest entry shape is invalid."
        }
        $relative = $entry.GetProperty("path").GetString()
        $expectedSha256 = $entry.GetProperty("sha256").GetString()
        $sizeElement = $entry.GetProperty("size")
        if ([string]::IsNullOrWhiteSpace($relative) -or
            $relative.Contains("\") -or $relative.StartsWith("/") -or
            $relative.Contains("`0") -or $relative.Contains("`r") -or
            $relative.Contains("`n") -or
            @($relative.Split("/")) -contains "." -or
            @($relative.Split("/")) -contains ".." -or
            $expectedSha256 -cnotmatch "^[0-9a-f]{64}$" -or
            $sizeElement.ValueKind -ne [System.Text.Json.JsonValueKind]::Number) {
            throw "Official Robotopia files manifest entry values are invalid."
        }
        $expectedSize = $sizeElement.GetInt64()
        if ($expectedSize -lt 0 -or
            -not $seenOrdinal.Add($relative) -or
            -not $seenInsensitive.Add($relative) -or
            ($null -ne $previousPath -and
                [System.StringComparer]::Ordinal.Compare(
                    $previousPath,
                    $relative
                ) -ge 0)) {
            throw "Official Robotopia files manifest paths are unsafe or unsorted."
        }
        $previousPath = $relative

        $candidate = [System.IO.Path]::GetFullPath(
            (Join-Path $resolvedGameDirectory (
                $relative.Replace("/", [System.IO.Path]::DirectorySeparatorChar)
            ))
        )
        if (-not $candidate.StartsWith(
                $gamePrefix,
                [System.StringComparison]::OrdinalIgnoreCase
            )) {
            throw "Official Robotopia file escapes the game directory: $relative"
        }
        $candidateItem = Assert-BoundedRegularFile -Path $candidate `
            -MaximumBytes ([long]::MaxValue) `
            -Label "Official Robotopia file '$relative'" -AllowEmpty
        if ($candidateItem.Length -ne $expectedSize) {
            throw "Official Robotopia file size mismatch: $relative"
        }
        $actualSha256 = Get-Sha256 $candidateItem.FullName
        if ($actualSha256 -cne $expectedSha256) {
            throw "Official Robotopia file digest mismatch: $relative"
        }
        if ($relative -ceq "Robotopia.exe") {
            $gameExecutableSha256 = $actualSha256
        }
    }
    if ($null -eq $gameExecutableSha256) {
        throw "Official Robotopia files manifest does not identify Robotopia.exe."
    }
    if ($gameExecutableSha256 -cne
        [string]$manifestPolicy.gameExecutableSha256) {
        throw "Robotopia.exe does not match the independently pinned digest."
    }
}
finally {
    $manifestDocument.Dispose()
}

[ordered]@{
    schema = "robotopia-official-install-v1"
    buildId = [Int64]$metadata.buildId
    archiveSha256 = [string]$metadata.archives.windows.sha256
    filesManifestSha256 = $manifestSha256
    filesVerified = [Int64]$manifestPolicy.fileCount
    gameExecutableSha256 = $gameExecutableSha256
} | ConvertTo-Json -Compress
