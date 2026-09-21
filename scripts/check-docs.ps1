#requires -Version 5.1
<#
.SYNOPSIS
    Read-only documentation checks: links, GitHub anchors, tool counts and tool names.
.DESCRIPTION
    Resolves every relative link and #anchor, and every link into github.com/skulitom/Anode (main)
    or raw.githubusercontent.com/skulitom/Anode/main, in README.md, CHANGELOG.md, CONTRIBUTING.md,
    AGENTS.md, CLAUDE.md, llms.txt, docs/*.md, examples/**/*.md, packaging/**/*.md, the agent skill
    and the distribution manifests that exist (packaging/mcpb, packaging/winget, server.json, plugin
    and marketplace JSON, glama.json, bucket/anode.json). Tool counts, the PROTOCOL.md tool list and
    seat_/anode_/steam_/gamepad_ tokens are compared with the tools/list reply of 'anode mcp'.
    Runs no other anode command.
.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File scripts\check-docs.ps1 -Anode src\Anode\bin\Debug\net8.0-windows\win-x64\anode.exe
#>
param([string]$Anode)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
# Error codes that share a tool prefix.
$nonTools = @('seat_busy', 'seat_stopped', 'seat_not_ready')
$problems = [Collections.Generic.List[string]]::new()
function Report([string]$Document, [int]$Line, [string]$Message) { $problems.Add("${Document}:${Line}: $Message") }

if (-not $Anode) {
    $Anode = @('Debug', 'Release') | ForEach-Object { Join-Path $root "src\Anode\bin\$_\net8.0-windows\win-x64\anode.exe" } |
        Where-Object { Test-Path -LiteralPath $_ } | Sort-Object { (Get-Item -LiteralPath $_).LastWriteTimeUtc } -Descending | Select-Object -First 1
    if (-not $Anode) { throw 'Pass -Anode with a freshly built anode.exe, or run dotnet build Anode.sln first.' }
}
$Anode = (Resolve-Path -LiteralPath $Anode).Path

# Tool discovery works without machine setup or a seat; stdin EOF ends the server.
function Get-McpToolNames([string]$Executable) {
    $start = New-Object Diagnostics.ProcessStartInfo
    $start.FileName = $Executable
    $start.Arguments = 'mcp'
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.StandardOutputEncoding = [Text.UTF8Encoding]::new($false)
    $process = [Diagnostics.Process]::Start($start)
    try {
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        $process.StandardInput.Write((@(
            '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"anode-docs-check","version":"1"}}}',
            '{"jsonrpc":"2.0","method":"notifications/initialized"}',
            '{"jsonrpc":"2.0","id":2,"method":"tools/list"}') -join "`n") + "`n")
        $process.StandardInput.Close()
        if (-not $process.WaitForExit(60000)) { $process.Kill(); throw "$Executable mcp did not exit after its input closed." }
        $process.WaitForExit()
        $reply = @($stdout.GetAwaiter().GetResult() -split "`r?`n" | Where-Object { $_.Trim() } | ForEach-Object {
            try { $_ | ConvertFrom-Json } catch { $null }
        } | Where-Object { $_ -and $_.id -eq 2 })
        if ($reply.Count -ne 1 -or -not $reply[0].result.tools) {
            throw "tools/list failed (exit $($process.ExitCode)). $($stderr.GetAwaiter().GetResult())"
        }
        @($reply[0].result.tools | ForEach-Object { [string]$_.name })
    } finally { $process.Dispose() }
}

function Get-RelativeName([string]$Path) { $Path.Substring($root.Length + 1).Replace('\', '/') }

# Lines of a Markdown file with code blocks, inline code, HTML comments and front matter blanked.
$sourceCache = @{}
function Get-Source([string]$Path) {
    if ($sourceCache.ContainsKey($Path)) { return $sourceCache[$Path] }
    $text = [IO.File]::ReadAllText($Path, [Text.Encoding]::UTF8)
    $text = [regex]::Replace($text, '(?s)<!--.*?-->', { param($m) [regex]::Replace($m.Value, '[^\n]', '') })
    $lines = @($text -split '\r?\n')
    $prose = [string[]]::new($lines.Count)
    $fence = $null
    $frontMatter = $lines.Count -gt 0 -and $lines[0] -eq '---'
    for ($i = 0; $i -lt $lines.Count; $i++) {
        $line = $lines[$i]
        if ($frontMatter) {
            $prose[$i] = ''
            if ($i -gt 0 -and $line -eq '---') { $frontMatter = $false }
            continue
        }
        if ($fence) {
            $prose[$i] = ''
            if ($line -match "^ {0,3}$([regex]::Escape($fence))+\s*$") { $fence = $null }
            continue
        }
        if ($line -match '^ {0,3}(```+|~~~+)') { $fence = $Matches[1]; $prose[$i] = ''; continue }
        $prose[$i] = [regex]::Replace($line, '(`+)(.+?)\1', { param($m) ' ' * $m.Length })
    }
    $source = [pscustomobject]@{ Lines = $lines; Prose = $prose }
    $sourceCache[$Path] = $source
    $source
}

# GitHub heading IDs: lower case, punctuation other than - and _ removed, spaces to -, repeats numbered.
function Get-Slug([string]$Heading) {
    $text = [regex]::Replace($Heading, '!\[[^\]]*\]\([^)]*\)', '')
    $text = [regex]::Replace($text, '\[([^\]]*)\](?:\([^)]*\)|\[[^\]]*\])', '$1')
    $text = [regex]::Replace($text, '<[^>]+>|&#?\w+;', '')
    $text = [regex]::Replace($text.Replace('`', ''), '(\*\*|__)(.+?)\1', '$2')
    $text = [regex]::Replace($text, '(?<![\w*])([*_])(?!\s)(.+?)(?<!\s)\1(?![\w*])', '$2')
    [regex]::Replace($text.Trim().ToLowerInvariant(), '[^\p{L}\p{M}\p{N}\p{Pc}\- ]', '').Replace(' ', '-')
}
$anchorCache = @{}
function Get-Anchors([string]$Path) {
    if ($anchorCache.ContainsKey($Path)) { return $anchorCache[$Path] }
    $source = Get-Source $Path
    $anchors = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    for ($i = 0; $i -lt $source.Lines.Count; $i++) {
        $prose = $source.Prose[$i]
        foreach ($match in [regex]::Matches($prose, '\b(?:id|name)\s*=\s*"([^"]+)"')) { $null = $anchors.Add($match.Groups[1].Value) }
        $heading = $null
        if ($prose -match '^ {0,3}#{1,6}(?:[ \t]|$)') {
            $heading = [regex]::Replace($source.Lines[$i], '^ {0,3}#{1,6}[ \t]*|[ \t]+#+[ \t]*$|[ \t]*$', '')
        } elseif ($i -gt 0 -and $prose -match '^ {0,3}(=+|-+)[ \t]*$' -and $source.Prose[$i - 1].Trim() -and
            $source.Prose[$i - 1] -notmatch '^ {0,3}([#>|<]|[-*+] |\d+[.)] |(=+|-+)[ \t]*$)') {
            $heading = $source.Lines[$i - 1].Trim()
        }
        if ($null -eq $heading) { continue }
        $slug = Get-Slug $heading
        $candidate = $slug
        for ($n = 1; -not $anchors.Add($candidate); $n++) { $candidate = "$slug-$n" }
    }
    $anchorCache[$Path] = $anchors
    $anchors
}

# Exact-case path check inside the repository, as GitHub resolves links.
function Test-RepositoryPath([string]$Path) {
    $full = [IO.Path]::GetFullPath($Path).TrimEnd('\')
    if ($full -ieq $root) { return $true }
    if (-not $full.StartsWith($root + '\', [StringComparison]::OrdinalIgnoreCase)) { return $false }
    $current = $root
    foreach ($segment in $full.Substring($root.Length + 1).Split('\')) {
        if (-not $segment -or $segment.IndexOfAny([char[]]'*?') -ge 0 -or -not [IO.Directory]::Exists($current)) { return $false }
        $entry = @([IO.Directory]::EnumerateFileSystemEntries($current, $segment)) | Where-Object { [IO.Path]::GetFileName($_) -ceq $segment }
        if (-not $entry) { return $false }
        $current = Join-Path $current $segment
    }
    $true
}

function Test-Link([string]$Document, [int]$Line, [string]$Target) {
    $target = $Target.Trim()
    $path = $null; $anchor = $null
    if ($target -match '^(?i:https?://(?:www\.)?github\.com/skulitom/anode)/?(?:#(.*))?$') {
        $path = Join-Path $root 'README.md'; $anchor = $Matches[1]
        if ($anchor -eq 'readme') { $anchor = $null }
    } elseif ($target -match '^(?i:https?://(?:www\.)?github\.com/skulitom/anode)/(?:blob|tree)/main/([^?#]*)(?:\?[^#]*)?(?:#(.*))?$') {
        $path = Join-Path $root ([Uri]::UnescapeDataString($Matches[1]).Replace('/', '\')); $anchor = $Matches[2]
    } elseif ($target -match '^(?i:https?://raw\.githubusercontent\.com/skulitom/anode)/(?:refs/heads/)?main/([^?#]*)') {
        $path = Join-Path $root ([Uri]::UnescapeDataString($Matches[1]).Replace('/', '\'))
    } elseif ($target -match '^(?:[a-zA-Z][a-zA-Z0-9+.-]*:|//)') {
        return
    } else {
        $parts = $target.Split([char[]]'#', 2)
        $relative = [Uri]::UnescapeDataString(($parts[0] -split '\?')[0])
        if ($parts.Count -gt 1) { $anchor = $parts[1] }
        $base = if ($relative.StartsWith('/')) { $root } else { Split-Path -Parent (Join-Path $root $Document.Replace('/', '\')) }
        $path = if ($relative) { Join-Path $base $relative.TrimStart('/').Replace('/', '\') } else { Join-Path $root $Document.Replace('/', '\') }
    }
    if (-not (Test-RepositoryPath $path)) { Report $Document $Line "broken link: $Target"; return }
    if (-not $anchor -or $anchor -match '^L\d+(-L\d+)?$' -or $path -notmatch '\.md$' -or -not (Test-Path -LiteralPath $path -PathType Leaf)) { return }
    $anchor = [Uri]::UnescapeDataString($anchor)
    $anchors = Get-Anchors $path
    if (-not $anchors.Contains($anchor) -and -not $anchors.Contains($anchor.ToLowerInvariant())) {
        Report $Document $Line "missing anchor #$anchor in $(Get-RelativeName $path): $Target"
    }
}

# Distribution manifests quote documentation URLs, tool names and counts as well.
$documents = @(@('README.md', 'CHANGELOG.md', 'CONTRIBUTING.md', 'AGENTS.md', 'CLAUDE.md', 'llms.txt', 'skills\anode-desktop\SKILL.md',
    'packaging\mcpb\manifest.json', 'server.json', '.claude-plugin\plugin.json', '.claude-plugin\marketplace.json',
    '.codex-plugin\plugin.json', '.agents\plugins\marketplace.json', 'glama.json', 'bucket\anode.json') |
    ForEach-Object { Join-Path $root $_ } | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf })
$documents += @(Get-ChildItem -LiteralPath (Join-Path $root 'docs') -Filter '*.md' -File | ForEach-Object { $_.FullName })
foreach ($glob in @('examples\*.md', 'packaging\*.md', 'packaging\winget\*.yaml')) {
    $path = Join-Path $root (Split-Path -Parent $glob)
    if (Test-Path -LiteralPath $path) {
        $documents += @(Get-ChildItem -LiteralPath $path -Filter (Split-Path -Leaf $glob) -File -Recurse | ForEach-Object { $_.FullName })
    }
}

$linkPatterns = @(
    '\]\(\s*<([^>]*)>',
    '\]\(\s*([^\s()<>]+(?:\([^\s()]*\)[^\s()<>]*)*)',
    '^ {0,3}\[(?!\^)[^\]]+\]:\s*<?([^\s>]+)',
    '\b(?:href|src)\s*=\s*"([^"]*)"',
    '(?i)\bhttps://(?:github\.com|raw\.githubusercontent\.com)/skulitom/anode\b[^\s<>"''()\[\]`*]*')
$tools = @(Get-McpToolNames $Anode)
$toolPattern = [regex]'\b(?:seat|anode|steam|gamepad)_[a-z]+(?:_[a-z]+)*\b'
foreach ($path in $documents) {
    $document = Get-RelativeName $path
    $source = Get-Source $path
    for ($i = 0; $i -lt $source.Lines.Count; $i++) {
        $targets = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach ($pattern in $linkPatterns) {
            foreach ($match in [regex]::Matches($source.Prose[$i], $pattern)) {
                $value = $match.Value
                if ($match.Groups.Count -gt 1) { $value = $match.Groups[1].Value }
                $null = $targets.Add($value.TrimEnd('.', ',', ';', ':', '!', '?'))
            }
        }
        foreach ($target in $targets) { if ($target) { Test-Link $document ($i + 1) $target } }
        # CHANGELOG keeps historical counts and names.
        if ($document -ne 'CHANGELOG.md') {
            foreach ($match in [regex]::Matches($source.Prose[$i], '\b(\d+) (?:MCP )?tools\b')) {
                if ([int]$match.Groups[1].Value -ne $tools.Count) { Report $document ($i + 1) "says '$($match.Value)'; tools/list has $($tools.Count)" }
            }
            foreach ($match in $toolPattern.Matches($source.Lines[$i])) {
                if ($match.Value -cnotin $tools -and $match.Value -cnotin $nonTools) { Report $document ($i + 1) "unknown tool name $($match.Value)" }
            }
        }
    }
}
$protocol = [IO.File]::ReadAllText((Join-Path $root 'docs\PROTOCOL.md'), [Text.Encoding]::UTF8)
foreach ($tool in $tools) {
    if ($protocol -cnotmatch "(?<![A-Za-z0-9_])$tool(?![A-Za-z0-9_])") { Report 'docs/PROTOCOL.md' 0 "does not mention tool $tool" }
}

Write-Host "Checked $($documents.Count) documents against $($tools.Count) tools from $Anode"
if ($problems.Count) {
    $problems | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
    throw "$($problems.Count) documentation problem(s)."
}
Write-Host 'Documentation links, anchors and tool names are consistent.' -ForegroundColor Green
