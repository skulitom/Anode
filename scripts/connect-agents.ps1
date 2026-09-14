#requires -Version 5.1
<# Register Anode for Codex and/or Claude Code using their installed CLIs.
   Backs up existing config files beside the originals. Changes only Anode's server settings.
   Does not start a seat, change approvals, or open a sign-in dialog.
#>
param([ValidateSet('Both','Codex','Claude')][string]$Client = 'Both',
    [string]$Anode = (Join-Path $PSScriptRoot '..\dist\anode.exe'))
$ErrorActionPreference = 'Stop'
$Anode = (Resolve-Path -LiteralPath $Anode).Path
& $Anode version | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'Anode executable check failed.' }
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss-fff'
function Backup-Config([string]$Path) {
    if (Test-Path -LiteralPath $Path) {
        $backup = "$Path.anode-backup-$stamp"
        Copy-Item -LiteralPath $Path -Destination $backup -ErrorAction Stop
        Write-Host "Config backup: $backup"
    }
}
function Save-Config([string]$Path, [string]$Before, [string]$After) {
    if ([IO.File]::ReadAllText($Path) -cne $Before) { throw "Config changed concurrently: $Path. Rerun the connector script." }
    $temporary = "$Path.anode-$([Guid]::NewGuid().ToString('N')).tmp"
    try {
        [IO.File]::WriteAllText($temporary, $After, [Text.UTF8Encoding]::new($false))
        [IO.File]::Replace($temporary, $Path, [NullString]::Value)
    } finally { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary } }
}
if ($Client -in @('Both','Codex')) {
    $null = Get-Command codex -ErrorAction Stop
    $configRoot = if ($env:CODEX_HOME) { $env:CODEX_HOME } else { Join-Path $env:USERPROFILE '.codex' }
    $configPath = Join-Path $configRoot 'config.toml'
    Backup-Config $configPath
    & codex mcp add anode -- $Anode mcp | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'Codex registration failed.' }
    $before = [IO.File]::ReadAllText($configPath)
    $pattern = '(?ms)^\[mcp_servers\.anode\]\r?\n(?<body>.*?)(?=^\[|\z)'
    $section = [regex]::Match($before, $pattern)
    if (-not $section.Success) { throw 'Could not locate the Anode table written by Codex.' }
    $body = $section.Groups['body'].Value
    if ($body -match '(?m)^tool_timeout_sec\s*=') { $body = [regex]::Replace($body, '(?m)^tool_timeout_sec\s*=.*$', 'tool_timeout_sec = 420') }
    else { $body = "tool_timeout_sec = 420`r`n" + $body }
    $replacement = "[mcp_servers.anode]`r`n" + $body
    Save-Config $configPath $before ($before.Remove($section.Index,$section.Length).Insert($section.Index,$replacement))
    $check = (& codex mcp get anode --json | Out-String) | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0 -or $check.tool_timeout_sec -ne 420) { throw 'Codex Anode configuration check failed.' }
    Write-Host 'Codex: Anode registered, tool timeout 420 seconds.'
}
if ($Client -in @('Both','Claude')) {
    $null = Get-Command claude -ErrorAction Stop
    $configRoot = if ($env:CLAUDE_CONFIG_DIR) { $env:CLAUDE_CONFIG_DIR } else { $env:USERPROFILE }
    $configPath = Join-Path $configRoot '.claude.json'
    Backup-Config $configPath
    if (Test-Path -LiteralPath $configPath) {
        $existing = [IO.File]::ReadAllText($configPath) | ConvertFrom-Json
        if ($existing.mcpServers.anode) {
            & claude mcp remove --scope user anode | Out-Host
            if ($LASTEXITCODE -ne 0) { throw 'Could not update the existing user-scoped Anode server.' }
        }
    }
    & claude mcp add --transport stdio --scope user anode -- $Anode mcp | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'Claude Code registration failed.' }
    $before = [IO.File]::ReadAllText($configPath)
    $settings = $before | ConvertFrom-Json
    if (-not $settings.mcpServers.anode) { throw 'Could not find the Anode entry written by Claude Code.' }
    # Per-server timeout in current Claude Code; older clients can start the seat first.
    $settings.mcpServers.anode | Add-Member -NotePropertyName timeout -NotePropertyValue 420000 -Force
    Save-Config $configPath $before ($settings | ConvertTo-Json -Depth 100)
    & claude mcp get anode | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'Claude Code Anode connection check failed.' }
}
Write-Host 'Ready. Open a new agent session to load Anode tools; existing sessions may retain their previous tool catalog.'
