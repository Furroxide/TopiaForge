param([string]$Source, [string]$Output, [string]$Msbuild)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$RepositoryRoot = $Output
$scriptFile = Join-Path $Source 'tools/release/build-windows.ps1'
$guard = Join-Path $Source 'tools/release/windows-native-path-guard.targets'
$parseErrors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile($scriptFile, [ref]$null, [ref]$parseErrors)
if ($parseErrors.Count) { throw 'Release script parser errors' }
$definition = $ast.Find({ param($node)
    $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Invoke-Checked'
}, $true)
if ($null -eq $definition) { throw 'Build invocation missing' }
. ([scriptblock]::Create($definition.Extent.Text))
$previous = [Environment]::GetEnvironmentVariable('ForceImportBeforeCppTargets', 'Process')
if (-not [string]::IsNullOrEmpty($previous)) { throw 'A clean fixture environment is required' }
$pwsh = (Get-Process -Id $PID).Path
$child = Join-Path $Output 'child.ps1'
$marker = Join-Path $Output 'child.json'
@'
param([string]$Guard, [string]$Marker, [int]$Code)
$ok = [Environment]::GetEnvironmentVariable('ForceImportBeforeCppTargets') -eq $Guard
@{guardInherited=$ok} | ConvertTo-Json | Set-Content -LiteralPath $Marker
exit $Code
'@ | Set-Content -LiteralPath $child
$results = [System.Collections.Generic.List[object]]::new()
function Check([bool]$Value, [string]$Name) {
    if (-not $Value) { throw "Failed: $Name" }
    $results.Add(@{name=$Name;passed=$true})
}
function HookRestored {
    [string]::IsNullOrEmpty([Environment]::GetEnvironmentVariable('ForceImportBeforeCppTargets', 'Process'))
}
try {
    Invoke-Checked -FilePath $pwsh -WorkingDirectory $Output -NativePathGuard $guard `
        -Arguments @('-NoProfile','-File',$child,$guard,$marker,'0')
    Check ((Get-Content -LiteralPath $marker -Raw | ConvertFrom-Json).guardInherited -and (HookRestored)) 'success inherits and restores hook'
    $refused = $false
    try {
        Invoke-Checked -FilePath $pwsh -WorkingDirectory $Output -NativePathGuard $guard `
            -Arguments @('-NoProfile','-File',$child,$guard,$marker,'7')
    } catch { $refused = $true }
    Check ($refused -and (HookRestored)) 'child failure restores hook'
    $refused = $false
    try { Invoke-Checked -FilePath $pwsh -NativePathGuard (Join-Path $Output 'absent.targets') }
    catch { $refused = $true }
    Check ($refused -and (HookRestored)) 'missing guard refuses before child'
    [Environment]::SetEnvironmentVariable('ForceImportBeforeCppTargets','existing-hook','Process')
    $refused = $false
    try { Invoke-Checked -FilePath $pwsh -NativePathGuard $guard }
    catch { $refused = $true }
    Check ($refused -and $env:ForceImportBeforeCppTargets -eq 'existing-hook') 'existing hook is preserved and refused'
} finally {
    [Environment]::SetEnvironmentVariable('ForceImportBeforeCppTargets',$previous,'Process')
}

$project = Join-Path $Output 'sample.vcxproj'
Invoke-Checked -FilePath $Msbuild -WorkingDirectory $Output -NativePathGuard $guard `
    -Arguments @($project,'/nologo','/m:1','/nr:false','/v:minimal','/p:Configuration=Release','/p:Platform=x64')
Check ((Test-Path -LiteralPath (Join-Path $Output 'obj/sample.obj')) -and (HookRestored)) 'actual C++ graph compiles short output'

$excludedProject = Join-Path $Output 'excluded.vcxproj'
Invoke-Checked -FilePath $Msbuild -WorkingDirectory $Output -NativePathGuard $guard `
    -Arguments @($excludedProject,'/nologo','/m:1','/nr:false','/v:minimal',
        '/p:Configuration=Release','/p:Platform=x64','/t:TopiaForgeCheckNativeObjects')
Check (HookRestored) 'excluded source does not require an object output'

$longProject = Join-Path $Output 'long.vcxproj'
$refused = $false
try {
    # Only evaluate the real C++ output/guard targets: never run the compiler with a long path.
    Invoke-Checked -FilePath $Msbuild -WorkingDirectory $Output -NativePathGuard $guard `
        -Arguments @($longProject,'/nologo','/m:1','/nr:false','/v:minimal',
            '/p:Configuration=Release','/p:Platform=x64',
            '/t:TopiaForgeCheckNativePaths;TopiaForgeCheckNativeObjects')
} catch { $refused = $true }
Check ($refused -and (HookRestored)) 'real computed long output refuses without compiler'
@{cases=$results;fullReleaseBuild=$false;longPathCompilerStarted=$false} | ConvertTo-Json -Depth 5 |
    Set-Content -LiteralPath (Join-Path $Output 'integration-result.json')
Write-Output 'Native guard integration checks completed.'
