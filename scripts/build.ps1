#requires -Version 5.1
<#
.SYNOPSIS
    Builds a single self-contained anode.exe into dist\.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\build.ps1
    powershell -ExecutionPolicy Bypass -File scripts\build.ps1 -OutputDirectory C:\Tools\anode -Test
#>
param(
    [string]$OutputDirectory = 'dist',
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',
    [switch]$Test,
    [switch]$QuickTest
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $projectRoot

if (-not [System.IO.Path]::IsPathRooted($OutputDirectory)) {
    $OutputDirectory = Join-Path $projectRoot $OutputDirectory
}

Write-Host "Building Anode ($Configuration) -> $OutputDirectory" -ForegroundColor Cyan

dotnet publish src\Anode\Anode.csproj `
    -c $Configuration `
    -r win-x64 `
    --self-contained true `
    -o $OutputDirectory `
    --nologo
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }

Copy-Item -LiteralPath README.md  -Destination (Join-Path $OutputDirectory 'README.md')  -Force
Copy-Item -LiteralPath LICENSE    -Destination (Join-Path $OutputDirectory 'LICENSE.txt') -Force
Copy-Item -LiteralPath (Join-Path $projectRoot 'docs') -Destination $OutputDirectory -Recurse -Force

$exe = Join-Path $OutputDirectory 'anode.exe'
if (-not (Test-Path $exe)) { throw "Expected $exe to exist." }

if ($Test -or $QuickTest) {
    Write-Host "`nSelf-test" -ForegroundColor Cyan
    $testArguments = @('selftest')
    if ($QuickTest) { $testArguments += '--quick' }
    & $exe @testArguments | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'Self-test failed.' }
}

$size = [math]::Round((Get-Item $exe).Length / 1MB, 1)
Write-Host "`nReady: $exe ($size MB)" -ForegroundColor Green
Write-Host "Next:  $exe doctor" -ForegroundColor Gray
