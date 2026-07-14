function Resolve-TopiaForgeSdkCommand {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [ValidateSet("dart", "flutter")]
        [string]$Tool,

        [string]$RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
    )

    $executableName = if ($IsWindows -or $env:OS -eq "Windows_NT") {
        "$Tool.bat"
    }
    else {
        $Tool
    }
    $projectCommand = Join-Path $RepositoryRoot ".fvm/flutter_sdk/bin/$executableName"
    if (Test-Path -LiteralPath $projectCommand -PathType Leaf) {
        return (Resolve-Path -LiteralPath $projectCommand).Path
    }

    $pathCommand = Get-Command $Tool -CommandType Application -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if ($null -ne $pathCommand) {
        return $pathCommand.Source
    }

    throw "$Tool was not found in '$RepositoryRoot/.fvm/flutter_sdk/bin' or on PATH. " +
        "Follow docs/ContributorSetup.md to select Flutter 3.41.4 with FVM and configure PATH."
}
