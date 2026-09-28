#requires -Version 5.1

<#
.SYNOPSIS
    Downloads and verifies the pinned MCP Registry publisher for Windows x64.
.DESCRIPTION
    Verifies the archive's SHA-256 before extracting only mcp-publisher.exe.
    Writes the executable's full path as its only pipeline output. Requires network access.
.PARAMETER Directory
    Download and extraction directory. Defaults to artifacts\tools in the repository.
.EXAMPLE
    $publisher = ./scripts/mcp-publisher.ps1
    & $publisher validate server.json
#>
[CmdletBinding()]
param(
    [string]$Directory = (Join-Path $PSScriptRoot '..\artifacts\tools')
)
$ErrorActionPreference = 'Stop'
$Directory = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Directory)
$null = New-Item -ItemType Directory -Path $Directory -Force
$archive = Join-Path $Directory 'mcp-publisher_windows_amd64.tar.gz'
Invoke-WebRequest -UseBasicParsing https://github.com/modelcontextprotocol/registry/releases/download/v1.8.1/mcp-publisher_windows_amd64.tar.gz -OutFile $archive
$expected = '399ad0d6e00a50812b563a71d8bfbff5160c085e6b13aac6ec083d98d5ff7c45'
$actual = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actual -cne $expected) { throw "MCP Registry publisher SHA-256 mismatch: expected $expected, got $actual." }
# Git's GNU tar treats a Windows drive letter in the archive path as a remote host.
& "$env:SystemRoot\System32\tar.exe" -xzf $archive -C $Directory mcp-publisher.exe | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'MCP Registry publisher extraction failed.' }
(Resolve-Path -LiteralPath (Join-Path $Directory 'mcp-publisher.exe')).Path
