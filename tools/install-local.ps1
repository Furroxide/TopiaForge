# DEPRECATED: this wrapper forwards to the cross-platform CLI and will be removed in a future release.
# Use instead:  dart run robotopia dev-install [--game-dir <path>]   (from apps/robotopia_cli)
param(
    [string]$GameDir = "",
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
Write-Warning "tools/install-local.ps1 is deprecated; use 'robotopia dev-install' instead."

$repo = Resolve-Path (Join-Path $PSScriptRoot "..")
$cliApp = Join-Path $repo "apps/robotopia_cli"

Push-Location $cliApp
try {
    & dart pub get
    if ($LASTEXITCODE -ne 0) { throw "dart pub get failed (exit $LASTEXITCODE)." }

    $args = @("run", (Join-Path "bin" "robotopia.dart"), "dev-install", "--configuration", $Configuration)
    if ($GameDir -ne "") {
        $args += @("--game-dir", $GameDir)
    }
    & dart @args
    if ($LASTEXITCODE -ne 0) { throw "robotopia dev-install failed (exit $LASTEXITCODE)." }
}
finally {
    Pop-Location
}
