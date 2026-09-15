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
    [switch]$QuickTest,
    [switch]$Package,
    [string]$ArchiveDirectory = 'artifacts\release'
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
Copy-Item -LiteralPath LICENSE    -Destination (Join-Path $OutputDirectory 'LICENSE') -Force
Copy-Item -LiteralPath CHANGELOG.md -Destination $OutputDirectory -Force
Copy-Item -LiteralPath CONTRIBUTING.md -Destination $OutputDirectory -Force
Copy-Item -LiteralPath AGENTS.md -Destination $OutputDirectory -Force
Copy-Item -LiteralPath llms.txt -Destination $OutputDirectory -Force
Copy-Item -LiteralPath scripts\install.ps1 -Destination $OutputDirectory -Force
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
if ($Package) {
    if ($Configuration -ne 'Release') { throw '-Package requires -Configuration Release for the single-file executable.' }
    if (-not [IO.Path]::IsPathRooted($ArchiveDirectory)) { $ArchiveDirectory = Join-Path $projectRoot $ArchiveDirectory }
    $null = New-Item -ItemType Directory -Path $ArchiveDirectory -Force
    $archive = Join-Path $ArchiveDirectory 'anode-windows-x64.zip'
    # Explicit payload prevents stale binaries, logs and local files entering a release.
    $payload = @('anode.exe','connect-agents.ps1','install.ps1','README.md','LICENSE','CHANGELOG.md','CONTRIBUTING.md','AGENTS.md','llms.txt','docs','skills') |
        ForEach-Object { Join-Path $OutputDirectory $_ }
    Compress-Archive -LiteralPath $payload -DestinationPath $archive -Force
    Copy-Item -LiteralPath scripts\install.ps1 -Destination $ArchiveDirectory -Force
    @('anode-windows-x64.zip','install.ps1') | ForEach-Object {
        $hash = (Get-FileHash -LiteralPath (Join-Path $ArchiveDirectory $_) -Algorithm SHA256).Hash.ToLowerInvariant()
        "$hash  $_"
    } | Set-Content -LiteralPath (Join-Path $ArchiveDirectory 'SHA256SUMS') -Encoding ASCII
    Write-Host "Release archive: $archive" -ForegroundColor Green
}
Write-Host "`nReady: $exe ($size MB)" -ForegroundColor Green
Write-Host "Next:  $exe doctor" -ForegroundColor Gray
