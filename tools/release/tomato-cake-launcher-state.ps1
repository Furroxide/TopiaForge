# Reads what the official Tomato Cake launcher records about its Windows
# Robotopia install. The launcher keeps its state in
# %LOCALAPPDATA%\Tomato Cake\launcher. By default the game lives in that
# directory's Robotopia child, with installed-build.json beside it. When a
# player moves the game in the official launcher, launcher-config.json names
# the new parent directory in game_dir. The game then lives at
# <game_dir>\Robotopia with its current filelist.json beside it, while
# installed-build.json stays in the state directory. A filelist.json left in
# the state directory describes an earlier build, so nothing here reads it.
#
# The launcher, the in-game loader and the GameCompat extractor apply the
# same rules. Other hosts report no state directory, which leaves the macOS
# and Proton layouts unchanged.

function Get-TomatoCakeLauncherStateDirectory {
    if (-not $IsWindows -or
        [string]::IsNullOrWhiteSpace($env:LOCALAPPDATA) -or
        -not [System.IO.Path]::IsPathFullyQualified($env:LOCALAPPDATA)) {
        return $null
    }
    return [System.IO.Path]::Combine($env:LOCALAPPDATA, "Tomato Cake", "launcher")
}

# Describes why Value is not an absolute local directory path, or returns
# $null when its shape is acceptable. Windows paths must start with a drive
# letter such as D:\, so UNC shares and \\?\ or \\.\ device paths are refused
# and reading the record never reaches a network host. Other paths must start
# with /. Either way empty, "." and ".." segments are refused.
function Get-TomatoCakeGameDirectoryProblem {
    param(
        [Parameter(Mandatory = $true)][AllowEmptyString()][string]$Value,
        [Parameter(Mandatory = $true)][bool]$WindowsPaths
    )
    if ($Value.Length -eq 0) {
        return "game_dir is empty."
    }
    if ($Value.Length -gt 32767) {
        return "game_dir exceeds 32767 characters."
    }
    if (-not $Value.Trim().Equals($Value, [System.StringComparison]::Ordinal)) {
        return "game_dir has surrounding whitespace."
    }
    foreach ($character in $Value.ToCharArray()) {
        if ([int]$character -lt 0x20 -or [int]$character -eq 0x7F) {
            return "game_dir contains control characters."
        }
    }
    if ($WindowsPaths) {
        if ($Value -cnotmatch '^[A-Za-z]:[\\/]') {
            return "game_dir must be an absolute path on a local drive letter."
        }
        $rest = $Value.Substring(3)
        if ($rest.IndexOfAny([char[]]@(':', '*', '?', '"', '<', '>', '|')) -ge 0) {
            return "game_dir contains characters Windows paths cannot hold."
        }
        $segments = [System.Collections.Generic.List[string]]::new(
            [string[]]$rest.Split([char[]]@('\', '/'))
        )
    }
    else {
        if (-not $Value.StartsWith("/", [System.StringComparison]::Ordinal)) {
            return "game_dir must be an absolute path."
        }
        $segments = [System.Collections.Generic.List[string]]::new(
            [string[]]$Value.Substring(1).Split('/')
        )
    }
    if ($segments.Count -gt 0 -and $segments[$segments.Count - 1].Length -eq 0) {
        $segments.RemoveAt($segments.Count - 1)
    }
    foreach ($segment in $segments) {
        if ($segment.Length -eq 0 -or $segment -ceq "." -or $segment -ceq "..") {
            return 'game_dir must not contain empty, "." or ".." segments.'
        }
        if ($WindowsPaths -and
            ($segment.EndsWith(".", [System.StringComparison]::Ordinal) -or
                $segment.EndsWith(" ", [System.StringComparison]::Ordinal))) {
            return "game_dir segments must not end with a dot or a space."
        }
    }
    return $null
}

# Returns the full path of an existing directory after checking that no
# component from the volume root down to it is a link or other reparse point.
function Get-TomatoCakeRealDirectory {
    param([Parameter(Mandatory = $true)][string]$Path)
    $directory = [System.IO.DirectoryInfo]::new([System.IO.Path]::GetFullPath($Path))
    $chain = [System.Collections.Generic.List[System.IO.DirectoryInfo]]::new()
    for ($current = $directory; $null -ne $current; $current = $current.Parent) {
        $chain.Insert(0, $current)
    }
    foreach ($component in $chain) {
        if (-not $component.Exists) {
            throw "$($component.FullName) is not an existing directory."
        }
        if (($component.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "$($component.FullName) is a link or other reparse point."
        }
    }
    $fullName = $directory.FullName
    $root = [System.IO.Path]::GetPathRoot($fullName)
    while ($fullName.Length -gt $root.Length -and
        ($fullName.EndsWith([System.IO.Path]::DirectorySeparatorChar) -or
            $fullName.EndsWith([System.IO.Path]::AltDirectorySeparatorChar))) {
        $fullName = $fullName.Substring(0, $fullName.Length - 1)
    }
    return $fullName
}

function Assert-TomatoCakeUniqueJsonProperties {
    param([Parameter(Mandatory = $true)][System.Text.Json.JsonElement]$Element)
    if ($Element.ValueKind -eq [System.Text.Json.JsonValueKind]::Object) {
        $names = [System.Collections.Generic.HashSet[string]]::new(
            [System.StringComparer]::Ordinal
        )
        foreach ($property in $Element.EnumerateObject()) {
            if (-not $names.Add($property.Name)) {
                throw "it contains duplicate properties."
            }
            Assert-TomatoCakeUniqueJsonProperties -Element $property.Value
        }
    }
    elseif ($Element.ValueKind -eq [System.Text.Json.JsonValueKind]::Array) {
        foreach ($item in $Element.EnumerateArray()) {
            Assert-TomatoCakeUniqueJsonProperties -Element $item
        }
    }
}

# Reads launcher-config.json strictly: a regular file of at most 64 KiB, the
# bound every reader applies to installed-build.json, holding one strict UTF-8
# JSON object with no byte order mark and no duplicate properties at any depth.
# Unknown properties are tolerated, as every installed-build.json reader
# already tolerates the launcher's extra "dev" field. game_dir must name an
# existing local directory by absolute path, and neither it nor any ancestor
# may be a reparse point. Never throws; Problem explains a refused record.
function Read-TomatoCakeLauncherConfig {
    param([AllowNull()][AllowEmptyString()][string]$StateDirectory)
    $result = [ordered]@{ Present = $false; GameDirectory = $null; Problem = $null }
    if ([string]::IsNullOrEmpty($StateDirectory)) {
        return [pscustomobject]$result
    }
    $path = [System.IO.Path]::Combine($StateDirectory, "launcher-config.json")
    if (-not [System.IO.File]::Exists($path) -and
        -not [System.IO.Directory]::Exists($path)) {
        return [pscustomobject]$result
    }
    $result.Present = $true
    try {
        $attributes = [System.IO.File]::GetAttributes($path)
        if (($attributes -band ([System.IO.FileAttributes]::Directory -bor
                        [System.IO.FileAttributes]::ReparsePoint)) -ne 0) {
            throw "it must be a regular file, not a directory or reparse point."
        }
        $length = [System.IO.FileInfo]::new($path).Length
        if ($length -le 0 -or $length -gt 65536) {
            throw "it must be between 1 and 65536 bytes."
        }
        $bytes = [System.IO.File]::ReadAllBytes($path)
        if ($bytes.Length -ne $length) {
            throw "it changed while it was read."
        }
        if ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and
            $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) {
            throw "it must not start with a byte order mark."
        }
        $text = [System.Text.UTF8Encoding]::new($false, $true).GetString($bytes)
        $document = [System.Text.Json.JsonDocument]::Parse(
            $text,
            [System.Text.Json.JsonDocumentOptions]@{
                AllowTrailingCommas = $false
                CommentHandling = [System.Text.Json.JsonCommentHandling]::Disallow
                MaxDepth = 64
            }
        )
        try {
            $root = $document.RootElement
            if ($root.ValueKind -ne [System.Text.Json.JsonValueKind]::Object) {
                throw "it must hold one JSON object."
            }
            Assert-TomatoCakeUniqueJsonProperties -Element $root
            $value = $null
            foreach ($property in $root.EnumerateObject()) {
                if ($property.Name -ceq "game_dir" -and
                    $property.Value.ValueKind -eq [System.Text.Json.JsonValueKind]::String) {
                    $value = $property.Value.GetString()
                }
            }
            if ($null -eq $value) {
                throw "it has no string game_dir."
            }
        }
        finally {
            $document.Dispose()
        }
        $problem = Get-TomatoCakeGameDirectoryProblem -Value $value -WindowsPaths $IsWindows
        if ($null -ne $problem) {
            throw $problem
        }
        $result.GameDirectory = Get-TomatoCakeRealDirectory -Path $value
    }
    catch {
        $result.GameDirectory = $null
        $result.Problem = "launcher-config.json was rejected: $($_.Exception.Message)"
    }
    return [pscustomobject]$result
}

# <game_dir>\Robotopia when launcher-config.json is valid and that directory
# exists without being a reparse point; otherwise $null.
function Get-TomatoCakeRelocatedGameRoot {
    param([AllowNull()][AllowEmptyString()][string]$StateDirectory)
    $config = Read-TomatoCakeLauncherConfig -StateDirectory $StateDirectory
    if ($null -eq $config.GameDirectory) {
        return $null
    }
    $root = [System.IO.DirectoryInfo]::new(
        [System.IO.Path]::Combine($config.GameDirectory, "Robotopia")
    )
    if (-not $root.Exists -or
        ($root.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        return $null
    }
    return $root.FullName
}

# The state directory's installed-build.json, but only for the install
# launcher-config.json points at: GameRoot must be a directory named Robotopia
# whose parent is game_dir itself, with no reparse point anywhere on its path.
# Anything else returns $null, so the marker can never be attributed to a
# different install.
function Get-TomatoCakeLauncherInstalledBuildPath {
    param(
        [Parameter(Mandatory = $true)][string]$GameRoot,
        [AllowNull()][AllowEmptyString()][string]$StateDirectory
    )
    $config = Read-TomatoCakeLauncherConfig -StateDirectory $StateDirectory
    if ($null -eq $config.GameDirectory) {
        return $null
    }
    try {
        $root = Get-TomatoCakeRealDirectory -Path $GameRoot
    }
    catch {
        return $null
    }
    $comparison = if ($IsWindows) {
        [System.StringComparison]::OrdinalIgnoreCase
    }
    else {
        [System.StringComparison]::Ordinal
    }
    $parent = [System.IO.Path]::GetDirectoryName($root)
    if (-not ([System.IO.Path]::GetFileName($root)).Equals(
            "Robotopia",
            [System.StringComparison]::OrdinalIgnoreCase
        ) -or
        [string]::IsNullOrEmpty($parent) -or
        -not $parent.Equals($config.GameDirectory, $comparison)) {
        return $null
    }
    return [System.IO.Path]::Combine($StateDirectory, "installed-build.json")
}

# The directory the official launcher runs the game from: <game_dir>\Robotopia
# after a move, otherwise its default location. Without a state directory the
# historical default expression is kept unchanged.
function Get-TomatoCakeOfficialGameDirectory {
    param(
        [AllowNull()][AllowEmptyString()][string]$StateDirectory =
        (Get-TomatoCakeLauncherStateDirectory)
    )
    if ([string]::IsNullOrEmpty($StateDirectory)) {
        return "$env:LOCALAPPDATA\Tomato Cake\launcher\Robotopia"
    }
    $relocated = Get-TomatoCakeRelocatedGameRoot -StateDirectory $StateDirectory
    if ($null -ne $relocated) {
        return $relocated
    }
    return [System.IO.Path]::Combine($StateDirectory, "Robotopia")
}

# Reads the Robotopia build id from installed-build.json. The game root and,
# for a Robotopia folder, its parent come first, as before. The launcher state
# directory's marker comes last, and only when launcher-config.json moved the
# game to exactly GameRoot. The first marker that exists decides, so a broken
# higher-priority marker fails closed instead of falling through.
function Get-RobotopiaInstalledBuildId {
    param(
        [Parameter(Mandatory = $true)][string]$GameRoot,
        [AllowNull()][AllowEmptyString()][string]$LauncherStateDirectory =
        (Get-TomatoCakeLauncherStateDirectory)
    )
    $resolvedGameRoot = [System.IO.Path]::GetFullPath($GameRoot).TrimEnd("\", "/")
    $candidates = [System.Collections.Generic.List[string]]::new()
    $candidates.Add((Join-Path $resolvedGameRoot "installed-build.json"))
    $launcherNote = ""
    if ((Split-Path -Leaf $resolvedGameRoot).Equals(
            "Robotopia",
            [System.StringComparison]::OrdinalIgnoreCase
        )) {
        $candidates.Add((Join-Path (Split-Path -Parent $resolvedGameRoot) `
                    "installed-build.json"))
        $launcherMarker = Get-TomatoCakeLauncherInstalledBuildPath `
            -GameRoot $resolvedGameRoot -StateDirectory $LauncherStateDirectory
        if ($null -ne $launcherMarker) {
            $candidates.Add($launcherMarker)
        }
        else {
            $config = Read-TomatoCakeLauncherConfig -StateDirectory $LauncherStateDirectory
            if ($null -ne $config.Problem) {
                $launcherNote = " Tomato Cake $($config.Problem)"
            }
            elseif ($null -ne $config.GameDirectory) {
                $launcherNote = " Tomato Cake's launcher-config.json moved the game to " +
                    [System.IO.Path]::Combine($config.GameDirectory, "Robotopia") + "."
            }
        }
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
    throw "Robotopia installed-build.json is missing.$launcherNote"
}
