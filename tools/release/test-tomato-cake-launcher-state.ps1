[CmdletBinding()]
param()

# Regression tests for tomato-cake-launcher-state.ps1: a game the official Tomato
# Cake launcher moved, as observed with build 2545. launcher-config.json names
# the new parent, the game moves to <game_dir>\Robotopia, and
# installed-build.json stays in the launcher's state directory beside a stale
# filelist.json. Every case passes the state directory explicitly, so the
# Windows lookup runs against real directories on every host.

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

. (Join-Path $PSScriptRoot "tomato-cake-launcher-state.ps1")

$failures = [System.Collections.Generic.List[string]]::new()
$passed = 0
function Test-Case {
    param([string]$Name, [scriptblock]$Action)
    try {
        & $Action
        $script:passed++
        Write-Host "PASS $Name"
    }
    catch {
        $script:failures.Add("${Name}: $($_.Exception.Message)")
        Write-Host "FAIL ${Name}: $($_.Exception.Message)"
    }
}

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) {
        throw $Message
    }
}

function Assert-ThrowsMatch {
    param([scriptblock]$Action, [string]$Pattern)
    try {
        & $Action | Out-Null
    }
    catch {
        if ($_.Exception.Message -match $Pattern) {
            return
        }
        throw "Expected '$Pattern', got '$($_.Exception.Message)'."
    }
    throw "Expected a failure matching '$Pattern'."
}

function Initialize-Fixture {
    param([string]$Name)
    $fixtureRoot = [System.IO.Path]::Combine($testRoot, $Name)
    $state = [System.IO.Path]::Combine($fixtureRoot, "LocalAppData", "Tomato Cake", "launcher")
    $parent = [System.IO.Path]::Combine($fixtureRoot, "Games", "Tomato Cake")
    [System.IO.Directory]::CreateDirectory($state) | Out-Null
    return [pscustomobject]@{
        Root = $fixtureRoot
        State = $state
        Config = [System.IO.Path]::Combine($state, "launcher-config.json")
        Marker = [System.IO.Path]::Combine($state, "installed-build.json")
        DefaultGame = [System.IO.Path]::Combine($state, "Robotopia")
        Parent = $parent
        Game = [System.IO.Path]::Combine($parent, "Robotopia")
    }
}

function Write-ConfigText {
    param($Fixture, [string]$Text)
    [System.IO.File]::WriteAllBytes(
        $Fixture.Config,
        [System.Text.UTF8Encoding]::new($false).GetBytes($Text)
    )
}

function Write-GameDirectory {
    param($Fixture, [string]$GameDirectory)
    Write-ConfigText $Fixture ((@{ game_dir = $GameDirectory } | ConvertTo-Json -Compress))
}

function Write-StateMarker {
    param($Fixture, [int]$BuildId)
    [System.IO.File]::WriteAllText($Fixture.Marker, "{`"id`":$BuildId,`"dev`":false}")
}

function Add-DirectoryLink {
    param([string]$Path, [string]$Target)
    $kind = if ($IsWindows) { "Junction" } else { "SymbolicLink" }
    New-Item -ItemType $kind -Path $Path -Target $Target | Out-Null
}

function ConvertTo-JsonString {
    param([string]$Value)
    return ($Value | ConvertTo-Json -Compress)
}

$testRoot = [System.IO.Directory]::CreateTempSubdirectory(
    "topiaforge-install-state-"
).FullName
if (-not $IsWindows) {
    # macOS spells the temp root through /var, a link the checks refuse.
    $testRoot = (& realpath -- $testRoot).Trim()
}
try {
    Test-Case "game_dir shape follows the host's local path rules" {
        foreach ($value in @(
                "D:\Users\player\Local\Tomato Cake\launcher",
                "d:/Games/Tomato Cake/",
                "D:\"
            )) {
            Assert-True ($null -eq (Get-TomatoCakeGameDirectoryProblem $value $true)) "Refused Windows shape: $value"
        }
        foreach ($value in @(
                "\\server\share\Games", "//server/share/Games", "\\?\D:\Games",
                "\\.\D:\Games", "D:Games", "\Games", "Games\Tomato Cake",
                "D:\Games\..\Other", "D:\Games\.\Tomato Cake", "D:\Games\\Tomato Cake",
                "D:\Games:stream", "D:\Games\Tomato*", "D:\Games.\Tomato Cake",
                "D:\Games \Tomato Cake", " D:\Games", "D:\Games$([char]1)", ""
            )) {
            Assert-True ($null -ne (Get-TomatoCakeGameDirectoryProblem $value $true)) "Accepted Windows shape: $value"
        }
        foreach ($value in @("/home/player/Games", "/", "/srv/games/")) {
            Assert-True ($null -eq (Get-TomatoCakeGameDirectoryProblem $value $false)) "Refused POSIX shape: $value"
        }
        foreach ($value in @(
                "Games", "D:\Games", "/home/../etc", "/home/./player",
                "//server/share", "/home//player", ""
            )) {
            Assert-True ($null -ne (Get-TomatoCakeGameDirectoryProblem $value $false)) "Accepted POSIX shape: $value"
        }
    }

    Test-Case "an absent record leaves the default layout alone" {
        $fixture = Initialize-Fixture "absent"
        $config = Read-TomatoCakeLauncherConfig -StateDirectory $fixture.State
        Assert-True (-not $config.Present -and $null -eq $config.Problem -and
            $null -eq $config.GameDirectory) "An absent record was not reported as absent."
        Assert-True (-not (Read-TomatoCakeLauncherConfig -StateDirectory "").Present) "A missing state directory produced a record."
        Assert-True ($null -eq (Get-TomatoCakeRelocatedGameRoot -StateDirectory $fixture.State)) "An absent record offered a relocated game."
        Assert-True ((Get-TomatoCakeOfficialGameDirectory -StateDirectory $fixture.State) -ceq
            $fixture.DefaultGame) "Without a record the official game directory is not the default."
    }

    Test-Case "a valid record names the relocated game" {
        $fixture = Initialize-Fixture "valid"
        [System.IO.Directory]::CreateDirectory($fixture.Game) | Out-Null
        Write-ConfigText $fixture ('{"game_dir":' +
            (ConvertTo-JsonString ($fixture.Parent + [System.IO.Path]::DirectorySeparatorChar)) +
            ',"future_setting":{"nested":true}}')
        $config = Read-TomatoCakeLauncherConfig -StateDirectory $fixture.State
        Assert-True ($config.Present -and $config.GameDirectory -ceq $fixture.Parent) "A valid record was not read: $($config.Problem)"
        Assert-True ((Get-TomatoCakeRelocatedGameRoot -StateDirectory $fixture.State) -ceq $fixture.Game) "A valid record did not offer the relocated game."
        Assert-True ((Get-TomatoCakeOfficialGameDirectory -StateDirectory $fixture.State) -ceq
            $fixture.Game) "The official game directory did not follow the record."
        [System.IO.Directory]::Delete($fixture.Game)
        Assert-True ($null -eq (Get-TomatoCakeRelocatedGameRoot -StateDirectory $fixture.State)) "A relocated parent without a Robotopia folder was offered."
    }

    # Each case reads $fixture from the loop below when the test runs.
    $malformed = [ordered]@{
        "truncated JSON" = { '{"game_dir":' }
        "a duplicate game_dir" = { $v = ConvertTo-JsonString $fixture.Parent; "{`"game_dir`":$v,`"game_dir`":$v}" }
        "a duplicate nested property" = { "{`"game_dir`":$(ConvertTo-JsonString $fixture.Parent),`"extra`":{`"a`":1,`"a`":2}}" }
        "a duplicate property inside an array" = { "{`"game_dir`":$(ConvertTo-JsonString $fixture.Parent),`"extra`":[{`"a`":1,`"a`":2}]}" }
        "trailing content" = { "{`"game_dir`":$(ConvertTo-JsonString $fixture.Parent)} x" }
        "a trailing comma" = { "{`"game_dir`":$(ConvertTo-JsonString $fixture.Parent),}" }
        "a comment" = { "{`"game_dir`":$(ConvertTo-JsonString $fixture.Parent) /* moved */}" }
        "an array root" = { "[$(ConvertTo-JsonString $fixture.Parent)]" }
        "a missing game_dir" = { '{"gameDir":"ignored"}' }
        "a numeric game_dir" = { '{"game_dir":7}' }
        "an empty game_dir" = { '{"game_dir":""}' }
        "a relative game_dir" = { '{"game_dir":"Games/Tomato Cake"}' }
        "a dot-dot segment" = { "{`"game_dir`":$(ConvertTo-JsonString ([System.IO.Path]::Combine($fixture.Parent, '..', 'x')))}" }
        "a padded game_dir" = { "{`"game_dir`":$(ConvertTo-JsonString (' ' + $fixture.Parent))}" }
        "a stale game_dir that no longer exists" = { "{`"game_dir`":$(ConvertTo-JsonString ([System.IO.Path]::Combine($fixture.Root, 'moved-away')))}" }
        "an oversized file" = { "{`"game_dir`":$(ConvertTo-JsonString $fixture.Parent)}" + (" " * 65536) }
    }
    $index = 0
    foreach ($entry in $malformed.GetEnumerator()) {
        $fixture = Initialize-Fixture "malformed-$index"
        $index++
        Test-Case "refuses $($entry.Key)" {
            [System.IO.Directory]::CreateDirectory($fixture.Game) | Out-Null
            Write-StateMarker $fixture 2545
            Write-ConfigText $fixture (& $entry.Value)
            $config = Read-TomatoCakeLauncherConfig -StateDirectory $fixture.State
            Assert-True ($config.Present -and $null -eq $config.GameDirectory -and
                $config.Problem -like "launcher-config.json was rejected: *") "The record was not refused with a reason."
            Assert-True ($null -eq (Get-TomatoCakeRelocatedGameRoot -StateDirectory $fixture.State)) "A refused record offered a relocated game."
            Assert-True ($null -eq (Get-TomatoCakeLauncherInstalledBuildPath -GameRoot $fixture.Game -StateDirectory $fixture.State)) "A refused record lent its marker."
            Assert-ThrowsMatch { Get-RobotopiaInstalledBuildId $fixture.Game -LauncherStateDirectory $fixture.State } "missing\. Tomato Cake launcher-config\.json was rejected"
            Assert-True ((Get-TomatoCakeOfficialGameDirectory -StateDirectory $fixture.State) -ceq
                $fixture.DefaultGame) "A refused record moved the official game directory."
        }
    }

    Test-Case "refuses record bytes that are not one strict UTF-8 regular file" {
        $fixture = Initialize-Fixture "bytes"
        [System.IO.Directory]::CreateDirectory($fixture.Game) | Out-Null
        $valid = [System.Text.UTF8Encoding]::new($false).GetBytes(
            "{`"game_dir`":$(ConvertTo-JsonString $fixture.Parent)}"
        )
        foreach ($case in @(
                @{ Bytes = [byte[]](@(0xEF, 0xBB, 0xBF) + $valid); Pattern = "byte order mark" },
                @{ Bytes = [byte[]]($valid[0..12] + @(0xC3, 0x28) + $valid[13..($valid.Length - 1)]); Pattern = "rejected" },
                @{ Bytes = [byte[]]@(); Pattern = "between 1 and 65536 bytes" }
            )) {
            [System.IO.File]::WriteAllBytes($fixture.Config, $case.Bytes)
            $problem = (Read-TomatoCakeLauncherConfig -StateDirectory $fixture.State).Problem
            Assert-True ($null -ne $problem -and $problem -match $case.Pattern) "Expected '$($case.Pattern)', got '$problem'."
        }
        [System.IO.File]::Delete($fixture.Config)
        [System.IO.Directory]::CreateDirectory($fixture.Config) | Out-Null
        $problem = (Read-TomatoCakeLauncherConfig -StateDirectory $fixture.State).Problem
        Assert-True ($null -ne $problem -and $problem -match "regular file") "A directory record was not refused: '$problem'."
    }

    Test-Case "refuses a game_dir that passes through a link" {
        $fixture = Initialize-Fixture "links"
        $real = [System.IO.Path]::Combine($fixture.Root, "real")
        [System.IO.Directory]::CreateDirectory([System.IO.Path]::Combine($real, "nested", "Robotopia")) | Out-Null
        [System.IO.Directory]::CreateDirectory([System.IO.Path]::Combine($real, "Robotopia")) | Out-Null
        $alias = [System.IO.Path]::Combine($fixture.Root, "alias")
        Add-DirectoryLink $alias $real
        foreach ($gameDirectory in @($alias, [System.IO.Path]::Combine($alias, "nested"))) {
            Write-GameDirectory $fixture $gameDirectory
            $config = Read-TomatoCakeLauncherConfig -StateDirectory $fixture.State
            Assert-True ($null -eq $config.GameDirectory -and $config.Problem -match "reparse point") "A linked game_dir was accepted: $gameDirectory"
        }
        $elsewhere = [System.IO.Path]::Combine($fixture.Root, "elsewhere")
        [System.IO.Directory]::CreateDirectory($elsewhere) | Out-Null
        [System.IO.Directory]::CreateDirectory($fixture.Parent) | Out-Null
        Add-DirectoryLink $fixture.Game $elsewhere
        Write-GameDirectory $fixture $fixture.Parent
        Assert-True ($null -ne (Read-TomatoCakeLauncherConfig -StateDirectory $fixture.State).GameDirectory) "A real game_dir was refused because its Robotopia folder is a link."
        Assert-True ($null -eq (Get-TomatoCakeRelocatedGameRoot -StateDirectory $fixture.State)) "A linked Robotopia folder was offered."
        Assert-True ($null -eq (Get-TomatoCakeLauncherInstalledBuildPath -GameRoot $fixture.Game -StateDirectory $fixture.State)) "A linked Robotopia folder received the launcher marker."
    }

    Test-Case "attributes the state marker only to <game_dir>/Robotopia" {
        $fixture = Initialize-Fixture "attribution"
        $sibling = [System.IO.Path]::Combine($fixture.Parent, "Robotopia Backup")
        $other = [System.IO.Path]::Combine($fixture.Root, "other", "Robotopia")
        foreach ($directory in @($fixture.Game, $fixture.DefaultGame, $sibling, $other)) {
            [System.IO.Directory]::CreateDirectory($directory) | Out-Null
        }
        Write-GameDirectory $fixture $fixture.Parent
        Assert-True ((Get-TomatoCakeLauncherInstalledBuildPath -GameRoot $fixture.Game -StateDirectory $fixture.State) -ceq
            $fixture.Marker) "The relocated game did not receive the launcher marker."
        Assert-True ($null -eq (Get-TomatoCakeLauncherInstalledBuildPath -GameRoot $fixture.Game -StateDirectory "")) "A missing state directory lent a marker."
        foreach ($unrelated in @($sibling, $other, $fixture.DefaultGame,
                [System.IO.Path]::Combine($fixture.Parent, "missing", "Robotopia"))) {
            Assert-True ($null -eq (Get-TomatoCakeLauncherInstalledBuildPath -GameRoot $unrelated -StateDirectory $fixture.State)) "The launcher marker reached $unrelated."
        }
        if ($IsWindows) {
            Write-GameDirectory $fixture $fixture.Parent.ToUpperInvariant()
            Assert-True ((Get-TomatoCakeLauncherInstalledBuildPath -GameRoot $fixture.Game -StateDirectory $fixture.State) -ceq
                $fixture.Marker) "Windows path comparison did not ignore letter case."
        }
    }

    Test-Case "Get-RobotopiaInstalledBuildId keeps the default layout" {
        $fixture = Initialize-Fixture "build-default"
        [System.IO.Directory]::CreateDirectory($fixture.DefaultGame) | Out-Null
        Write-StateMarker $fixture 2545
        Assert-True ((Get-RobotopiaInstalledBuildId $fixture.DefaultGame -LauncherStateDirectory $fixture.State) -eq 2545) "The default layout lost its marker."
    }

    Test-Case "Get-RobotopiaInstalledBuildId follows a relocated game" {
        $fixture = Initialize-Fixture "build-relocated"
        [System.IO.Directory]::CreateDirectory($fixture.Game) | Out-Null
        Write-StateMarker $fixture 2545
        [System.IO.File]::WriteAllText(
            [System.IO.Path]::Combine($fixture.State, "filelist.json"),
            "stale manifest from an earlier build"
        )
        Assert-ThrowsMatch { Get-RobotopiaInstalledBuildId $fixture.Game -LauncherStateDirectory $fixture.State } "^Robotopia installed-build\.json is missing\.$"
        Write-GameDirectory $fixture $fixture.Parent
        Assert-True ((Get-RobotopiaInstalledBuildId $fixture.Game -LauncherStateDirectory $fixture.State) -eq 2545) "The relocated game did not read the launcher marker."
        Assert-ThrowsMatch { Get-RobotopiaInstalledBuildId $fixture.Game -LauncherStateDirectory "" } "missing"
    }

    Test-Case "Get-RobotopiaInstalledBuildId never lends the marker to a mismatched game_dir" {
        $fixture = Initialize-Fixture "build-mismatched"
        $other = [System.IO.Path]::Combine($fixture.Root, "other", "Robotopia")
        [System.IO.Directory]::CreateDirectory($other) | Out-Null
        [System.IO.Directory]::CreateDirectory($fixture.Game) | Out-Null
        Write-StateMarker $fixture 2545
        Write-GameDirectory $fixture $fixture.Parent
        Assert-ThrowsMatch { Get-RobotopiaInstalledBuildId $other -LauncherStateDirectory $fixture.State } "missing\. Tomato Cake's launcher-config\.json moved the game to"
    }

    Test-Case "a marker beside the game outranks a stale launcher marker" {
        $fixture = Initialize-Fixture "build-stale"
        [System.IO.Directory]::CreateDirectory($fixture.Game) | Out-Null
        Write-StateMarker $fixture 2544
        Write-GameDirectory $fixture $fixture.Parent
        $beside = [System.IO.Path]::Combine($fixture.Parent, "installed-build.json")
        [System.IO.File]::WriteAllText($beside, '{"id":2545}')
        Assert-True ((Get-RobotopiaInstalledBuildId $fixture.Game -LauncherStateDirectory $fixture.State) -eq 2545) "The stale launcher marker outranked the marker beside the game."
        [System.IO.File]::WriteAllText($beside, '{"id":0}')
        Assert-ThrowsMatch { Get-RobotopiaInstalledBuildId $fixture.Game -LauncherStateDirectory $fixture.State } "invalid"
    }

    if ($IsWindows) {
        Test-Case "the default state directory follows LOCALAPPDATA on Windows" {
            $fixture = Initialize-Fixture "localappdata"
            [System.IO.Directory]::CreateDirectory($fixture.Game) | Out-Null
            Write-GameDirectory $fixture $fixture.Parent
            $previous = $env:LOCALAPPDATA
            try {
                $env:LOCALAPPDATA = [System.IO.Path]::Combine($fixture.Root, "LocalAppData")
                Assert-True ((Get-TomatoCakeLauncherStateDirectory) -ceq $fixture.State) "The state directory did not follow LOCALAPPDATA."
                Assert-True ((Get-TomatoCakeOfficialGameDirectory) -ceq $fixture.Game) "The default game directory did not follow the record."
                $env:LOCALAPPDATA = "relative"
                Assert-True ($null -eq (Get-TomatoCakeLauncherStateDirectory)) "A relative LOCALAPPDATA produced a state directory."
            }
            finally {
                $env:LOCALAPPDATA = $previous
            }
        }
    }
    else {
        Test-Case "other hosts have no launcher state directory" {
            Assert-True ($null -eq (Get-TomatoCakeLauncherStateDirectory)) "A non-Windows host produced a state directory."
            Assert-True ((Get-TomatoCakeOfficialGameDirectory) -ceq
                "$env:LOCALAPPDATA\Tomato Cake\launcher\Robotopia") "The historical default changed off Windows."
        }
    }

    Write-Host "Robotopia install state tests: $passed passed, $($failures.Count) failed."
    if ($failures.Count -ne 0) {
        throw ($failures -join "`n")
    }
}
finally {
    if (Test-Path -LiteralPath $testRoot) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}
