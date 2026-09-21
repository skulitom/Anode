#requires -Version 5.1
<# Register Anode for Codex and/or Claude Code using their installed CLIs.
   Backs up existing config files beside the originals. Registers Anode and its discoverable skill.
   Does not start a seat, change approvals, or open a sign-in dialog.
#>
param([ValidateSet('Auto','Both','Codex','Claude')][string]$Client = 'Auto',
    [string]$Anode,
    [switch]$NoSkill)
$ErrorActionPreference = 'Stop'
if (-not $Anode) {
    $Anode = Join-Path $PSScriptRoot 'anode.exe'
    if (-not (Test-Path -LiteralPath $Anode)) { $Anode = Join-Path $PSScriptRoot '..\dist\anode.exe' }
}
$Anode = (Resolve-Path -LiteralPath $Anode).Path
# Preflight every requested CLI before changing any settings.
$clients = @()
foreach ($candidate in @('Codex','Claude')) {
    $installed = Get-Command $candidate.ToLowerInvariant() -ErrorAction SilentlyContinue
    if ($Client -eq 'Auto') {
        if ($installed) { $clients += $candidate }
    } elseif ($Client -eq 'Both' -or $Client -eq $candidate) {
        if (-not $installed) { throw "$candidate CLI is not on PATH. Install it first or choose another client. No settings changed." }
        $clients += $candidate
    }
}
if ($clients.Count -eq 0) {
    Write-Host 'No Codex or Claude Code CLI found on PATH. Install a client, then rerun anode configure.'
    Write-Host 'Other MCP clients: https://github.com/skulitom/Anode/blob/main/docs/CONNECTING-AGENTS.md'
    return
}
$skillSource = Join-Path (Split-Path -Parent $Anode) 'skills\anode-desktop'
$skillFiles = @('SKILL.md', 'agents\openai.yaml')
if (-not $NoSkill) {
    foreach ($relative in $skillFiles) {
        if (-not (Test-Path -LiteralPath (Join-Path $skillSource $relative) -PathType Leaf)) {
            throw 'The Anode skill is missing. Extract the full release, or use -NoSkill to register MCP only. No settings changed.'
        }
    }
}
& $Anode version | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'Anode executable check failed.' }
# Read the Claude config before any client changes; PowerShell rejects JSON keys that differ only in case.
$claudeConfigPath = Join-Path $(if ($env:CLAUDE_CONFIG_DIR) { $env:CLAUDE_CONFIG_DIR } else { $env:USERPROFILE }) '.claude.json'
$claudeExisting = $null
if ('Claude' -in $clients -and (Test-Path -LiteralPath $claudeConfigPath)) {
    try { $claudeExisting = [IO.File]::ReadAllText($claudeConfigPath) | ConvertFrom-Json }
    catch {
        # Claude Code can record one project under two drive-letter spellings, which PowerShell cannot parse.
        throw ("Cannot read ${claudeConfigPath}: $($_.Exception.Message.TrimEnd('.', ' ')). No settings changed. " +
            "Run 'anode configure codex' to register Codex alone; for Claude Code, register Anode by hand or use its plugin: " +
            'https://github.com/skulitom/Anode/blob/main/docs/CONNECTING-AGENTS.md#1-claude-code')
    }
}
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
function Install-AgentSkill([string]$Directory) {
    if ($NoSkill) { return }
    $markerPath = Join-Path $Directory '.anode-skill.json'
    if ((Test-Path -LiteralPath $Directory) -and @(Get-ChildItem -LiteralPath $Directory -Force).Count -gt 0) {
        # Never overwrite a hand-authored skill or edits to a previously installed copy.
        $managed = $false
        try {
            $marker = Get-Content -LiteralPath $markerPath -Raw | ConvertFrom-Json
            $managed = $marker.product -eq 'anode-desktop'
            foreach ($relative in $skillFiles) {
                $path = Join-Path $Directory $relative
                if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or
                    (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $marker.files.$relative) { $managed = $false }
            }
        } catch { $managed = $false }
        if (-not $managed) {
            Write-Warning "Existing/customized skill left unchanged: $Directory. Updated source: $skillSource"
            return
        }
    }
    $hashes = @{}
    foreach ($relative in $skillFiles) {
        $source = Join-Path $skillSource $relative
        $destination = Join-Path $Directory $relative
        $after = [IO.File]::ReadAllText($source)
        $null = New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force
        if (Test-Path -LiteralPath $destination) {
            $before = [IO.File]::ReadAllText($destination)
            if ($before -cne $after) {
                Backup-Config $destination
                Save-Config $destination $before $after
            }
        } else { [IO.File]::WriteAllText($destination, $after, [Text.UTF8Encoding]::new($false)) }
        $hashes[$relative] = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash
    }
    [IO.File]::WriteAllText($markerPath, (@{ product = 'anode-desktop'; files = $hashes } | ConvertTo-Json -Depth 5), [Text.UTF8Encoding]::new($false))
    Write-Host "Agent skill installed: $Directory (automatic selection enabled; existing client approval settings still apply)."
}
if ('Codex' -in $clients) {
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
    Install-AgentSkill (Join-Path $env:USERPROFILE '.agents\skills\anode-desktop')
}
if ('Claude' -in $clients) {
    $null = Get-Command claude -ErrorAction Stop
    $configPath = $claudeConfigPath
    Backup-Config $configPath
    if ($claudeExisting.mcpServers.anode) {
        & claude mcp remove --scope user anode | Out-Host
        if ($LASTEXITCODE -ne 0) { throw 'Could not update the existing user-scoped Anode server.' }
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
    Write-Host 'Claude Code: Anode registered, tool timeout 420 seconds.'
    $skillRoot = if ($env:CLAUDE_CONFIG_DIR) { $env:CLAUDE_CONFIG_DIR } else { Join-Path $env:USERPROFILE '.claude' }
    Install-AgentSkill (Join-Path $skillRoot 'skills\anode-desktop')
}
Write-Host 'Ready. Start a new agent session (open sessions keep their old tool list), then ask it to call anode_guide.'
if ('Claude' -in $clients) { Write-Host 'Check in Claude Code: /mcp lists anode as connected.' }
if ('Codex' -in $clients) { Write-Host 'Check in Codex: codex mcp list shows anode.' }
# Quote the registered path when a bare 'anode' would not run it, as after install.ps1 -NoPath.
$onPath = Get-Command anode -ErrorAction SilentlyContinue
$anodeCommand = if ($onPath -and $onPath.Source -eq $Anode) { 'anode' } else { "& '" + $Anode.Replace("'", "''") + "'" }
Write-Host "If seat tools report missing prerequisites, run: $anodeCommand doctor | Out-Host"
