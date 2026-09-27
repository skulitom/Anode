#requires -Version 5.1
<#
.SYNOPSIS
    Writes an installable MCP Registry entry for a release without publishing it.
.DESCRIPTION
    Copies the metadata-only server.json and adds one MCPB package with the release URL and
    the bundle's SHA-256. -Tag must match the project version with a leading v.
    The bundle must exist locally; this script does not contact GitHub or the registry.
.PARAMETER Tag
    Release tag, for example v0.10.0.
.PARAMETER Bundle
    Path to the built anode-windows-x64.mcpb file.
.PARAMETER OutFile
    Destination JSON file. Defaults to artifacts\release\server.registry.json in the repository.
.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File scripts\registry-entry.ps1 -Tag v0.10.0 -Bundle artifacts\pkg-release\anode-windows-x64.mcpb -OutFile artifacts\pkg-release\server.registry.json
#>
param(
    [Parameter(Mandatory = $true)][string]$Tag,
    [Parameter(Mandatory = $true)][string]$Bundle,
    [string]$OutFile = (Join-Path $PSScriptRoot '..\artifacts\release\server.registry.json')
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
[xml]$project = Get-Content -LiteralPath (Join-Path $root 'src\Anode\Anode.csproj') -Raw
$version = @($project.Project.PropertyGroup.Version | Where-Object { $_ })[0]
if ($Tag -cnotmatch '^v\d+\.\d+\.\d+$' -or $Tag -cne "v$version") {
    throw "Tag '$Tag' must match the project version: v$version."
}
if (-not (Test-Path -LiteralPath $Bundle -PathType Leaf)) {
    throw "Missing MCPB bundle: $Bundle. Build it with scripts\build.ps1 -Package -Mcpb."
}
$bundlePath = (Resolve-Path -LiteralPath $Bundle).Path
$metadataPath = Join-Path $root 'server.json'
$server = [IO.File]::ReadAllText($metadataPath, [Text.Encoding]::UTF8) | ConvertFrom-Json
if ('packages' -in $server.PSObject.Properties.Name) { throw 'server.json must remain metadata-only (no packages property).' }
if ($server.version -cne $version) { throw "server.json version '$($server.version)' must match the project version: $version." }
$destination = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutFile)
if ($destination -ieq $metadataPath -or $destination -ieq $bundlePath) {
    throw '-OutFile must not overwrite server.json or the MCPB bundle.'
}
$hash = (Get-FileHash -LiteralPath $bundlePath -Algorithm SHA256).Hash.ToLowerInvariant()
$server | Add-Member -NotePropertyName packages -NotePropertyValue @([ordered]@{
    registryType = 'mcpb'
    identifier = "https://github.com/skulitom/Anode/releases/download/$Tag/anode-windows-x64.mcpb"
    version = $version
    fileSha256 = $hash
    transport = [ordered]@{ type = 'stdio' }
})

# Windows PowerShell 5.1 aligns nested values with property names. Keep its JSON escaping,
# but normalize the leading whitespace to two spaces per object/array level on all versions.
$depth = 0
$lines = foreach ($line in (($server | ConvertTo-Json -Depth 100) -split '\r?\n')) {
    $trimmed = $line.Trim()
    $trimmed = $trimmed -replace '^("(?:[^"\\]|\\.)*":) +', '$1 '
    if ($trimmed -match '^[}\]]') { $depth-- }
    ('  ' * $depth) + $trimmed
    if ($trimmed -match '[{\[]$') { $depth++ }
}
$null = [IO.Directory]::CreateDirectory((Split-Path -Parent $destination))
[IO.File]::WriteAllText($destination, (($lines -join "`n") + "`n"), [Text.UTF8Encoding]::new($false))
Write-Host "Registry entry: $destination" -ForegroundColor Green
