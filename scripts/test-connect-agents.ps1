#requires -Version 5.1
<# Invoked only by test-install.ps1 with disposable client homes and fake CLI functions. #>
param([Parameter(Mandatory=$true)][string]$Anode)
$ErrorActionPreference = 'Stop'
if (-not $env:ANODE_TEST_CLIENT_ROOT) { throw 'Run test-install.ps1 instead.' }
$testRoot = [IO.Path]::GetFullPath($env:ANODE_TEST_CLIENT_ROOT).TrimEnd('\') + '\'
foreach ($directory in @($env:CODEX_HOME, $env:CLAUDE_CONFIG_DIR, $env:USERPROFILE)) {
    if (-not $directory -or -not [IO.Path]::GetFullPath($directory).StartsWith($testRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Client home is outside the disposable test root.'
    }
    $null = New-Item -ItemType Directory -Path $directory -Force
}
$codexConfig = Join-Path $env:CODEX_HOME 'config.toml'
$claudeConfig = Join-Path $env:CLAUDE_CONFIG_DIR '.claude.json'
$connector = Join-Path (Split-Path -Parent $Anode) 'connect-agents.ps1'
$global:AnodeTestAvailable = @('codex','claude')
$global:AnodeTestCalls = @()
function Assert([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function Get-Command {
    param([string]$Name, [string]$ErrorAction)
    if ($Name -in $global:AnodeTestAvailable) { return [pscustomobject]@{ Name = $Name } }
    if ($ErrorAction -eq 'Stop') { throw "Test client missing: $Name" }
}
function codex {
    $global:AnodeTestCalls += 'codex'
    $global:LASTEXITCODE = 0
    if ($args[1] -eq 'add') {
        $existing = [IO.File]::ReadAllText($codexConfig)
        $existing = [regex]::Replace($existing, '(?ms)^\[mcp_servers\.anode\]\r?\n.*?(?=^\[|\z)', '')
        $entry = "`r`n[mcp_servers.anode]`r`ncommand = '" + $args[-2] + "'`r`nargs = [""mcp""]`r`n"
        [IO.File]::WriteAllText($codexConfig, $existing + $entry)
    } elseif ($args[1] -eq 'get') {
        $text = [IO.File]::ReadAllText($codexConfig)
        Assert ($text -match 'tool_timeout_sec = 420') 'Codex timeout was not set.'
        '{"tool_timeout_sec":420}'
    } else { throw 'Unexpected mock Codex command.' }
}
function claude {
    $global:AnodeTestCalls += 'claude'
    $global:LASTEXITCODE = 0
    $settings = [IO.File]::ReadAllText($claudeConfig) | ConvertFrom-Json
    if ($args[1] -eq 'remove') {
        $settings.mcpServers.PSObject.Properties.Remove('anode')
    } elseif ($args[1] -eq 'add') {
        $settings.mcpServers | Add-Member -NotePropertyName anode -NotePropertyValue ([pscustomobject]@{
            command = $args[-2]; args = @('mcp'); type = 'stdio'
        }) -Force
    } elseif ($args[1] -eq 'get') {
        Assert ($settings.mcpServers.anode.timeout -eq 420000) 'Claude timeout was not set.'
        return
    } else { throw 'Unexpected mock Claude command.' }
    [IO.File]::WriteAllText($claudeConfig, ($settings | ConvertTo-Json -Depth 100))
}
$codexBefore = "# Preserve comments and unrelated server`r`napproval_policy = 'on-request'`r`n[mcp_servers.other]`r`ncommand = 'other.exe'`r`n"
$claudeBefore = '{"theme":"dark","mcpServers":{"other":{"command":"other.exe"}}}'
[IO.File]::WriteAllText($codexConfig, $codexBefore)
[IO.File]::WriteAllText($claudeConfig, $claudeBefore)
& $connector -Client Auto -Anode $Anode
$codexSkill = Join-Path $env:USERPROFILE '.agents\skills\anode-desktop\SKILL.md'
$claudeSkill = Join-Path $env:CLAUDE_CONFIG_DIR 'skills\anode-desktop\SKILL.md'
$packagedSkill = [IO.File]::ReadAllText((Join-Path (Split-Path -Parent $Anode) 'skills\anode-desktop\SKILL.md'))
Assert ([IO.File]::ReadAllText($codexSkill) -ceq $packagedSkill) 'Codex skill was not installed.'
Assert ([IO.File]::ReadAllText($claudeSkill) -ceq $packagedSkill) 'Claude skill was not installed.'
Assert ((Get-Content -LiteralPath $codexConfig -Raw).StartsWith($codexBefore)) 'Unrelated TOML changed.'
$result = Get-Content -LiteralPath $claudeConfig -Raw | ConvertFrom-Json
Assert ($result.theme -eq 'dark' -and $result.mcpServers.other.command -eq 'other.exe') 'Unrelated JSON changed.'
Assert ($result.mcpServers.anode.command -eq $Anode) 'Claude executable path was not preserved.'
Assert ((Get-Content -LiteralPath $codexConfig -Raw).Contains($Anode)) 'Codex executable path was not preserved.'
Assert (@(Get-ChildItem -LiteralPath $env:CODEX_HOME -Filter '*.anode-backup-*').Count -eq 1) 'Codex backup missing.'
Assert (@(Get-ChildItem -LiteralPath $env:CLAUDE_CONFIG_DIR -Filter '*.anode-backup-*').Count -eq 1) 'Claude backup missing.'
& $connector -Client Both -Anode $Anode
Assert ([regex]::Matches((Get-Content -LiteralPath $codexConfig -Raw), '\[mcp_servers\.anode\]').Count -eq 1) 'Repeat setup duplicated the server.'
Write-Host '[ok] both clients, backups, repeat configuration and unrelated settings'

# Verify an owned old distribution updates with backups, then preserve user edits.
$skillMarker = Join-Path (Split-Path -Parent $codexSkill) '.anode-skill.json'
$oldSkill = 'previous Anode skill distribution'
[IO.File]::WriteAllText($codexSkill, $oldSkill)
$marker = Get-Content -LiteralPath $skillMarker -Raw | ConvertFrom-Json
$marker.files.'SKILL.md' = (Get-FileHash -LiteralPath $codexSkill -Algorithm SHA256).Hash
$marker | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $skillMarker
& $connector -Client Codex -Anode $Anode
Assert ([IO.File]::ReadAllText($codexSkill) -ceq $packagedSkill) 'Managed skill update failed.'
$skillBackups = @(Get-ChildItem -LiteralPath (Split-Path -Parent $codexSkill) -Filter 'SKILL.md.anode-backup-*')
Assert ($skillBackups.Count -eq 1 -and [IO.File]::ReadAllText($skillBackups[0].FullName) -ceq $oldSkill) 'Previous skill was not backed up.'
[IO.File]::WriteAllText($codexSkill, 'my customized workflow')
& $connector -Client Codex -Anode $Anode
Assert ([IO.File]::ReadAllText($codexSkill) -ceq 'my customized workflow') 'Customized skill was overwritten.'
$testProfile = $env:USERPROFILE
try {
    $env:USERPROFILE = Join-Path $testRoot 'optout-profile'
    & $connector -Client Codex -Anode $Anode -NoSkill
    Assert (-not (Test-Path -LiteralPath (Join-Path $env:USERPROFILE '.agents'))) 'Skill opt-out wrote a personal skill.'
} finally { $env:USERPROFILE = $testProfile }
Write-Host '[ok] discoverable skills, managed updates, customizations and opt-out'

$global:AnodeTestAvailable = @('claude')
$global:AnodeTestCalls = @()
& $connector -Client Auto -Anode $Anode
Assert ($global:AnodeTestCalls.Count -gt 0 -and 'codex' -notin $global:AnodeTestCalls) 'Auto required a missing client.'
$global:AnodeTestCalls = @()
$failure = $null
try { & $connector -Client Both -Anode $Anode } catch { $failure = $_.Exception.Message }
Assert ($failure -match 'No settings changed' -and $global:AnodeTestCalls.Count -eq 0) 'Explicit both did not preflight missing clients.'
$global:AnodeTestAvailable = @()
& $connector -Client Auto -Anode $Anode
Assert ($global:AnodeTestCalls.Count -eq 0) 'No-client auto configuration invoked a CLI.'
Write-Host '[ok] single/no-client detection and explicit-client preflight'
