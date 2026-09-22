#requires -Version 5.1
<#
.SYNOPSIS
    Read-only checks of release artifacts and distribution manifests against src\Anode\Anode.csproj.
.DESCRIPTION
    Checks the packaged executable's version, metadata and MCP tool list; SHA256SUMS; archive entry
    names and payload; anode-windows-x64.mcpb when built; and each distribution manifest that exists:
    packaging/mcpb/manifest.json, server.json, .claude-plugin, .codex-plugin, .agents/plugins,
    glama.json, bucket/anode.json and packaging/winget. Runs only 'anode version' and 'anode mcp'
    (initialize and tools/list), and never starts a seat. -Release also requires release notes and a
    CHANGELOG section for the version, and repository links in the MCPB manifest to resolve.
.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File scripts\test-distribution.ps1 -ArchiveDirectory artifacts\pkg-release
#>
param(
    [string]$ArchiveDirectory = (Join-Path $PSScriptRoot '..\artifacts\release'),
    [string]$Anode,
    [switch]$Release
)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$ArchiveDirectory = (Resolve-Path -LiteralPath $ArchiveDirectory).Path
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$failures = [Collections.Generic.List[string]]::new()
function Fail([string]$Message) { $failures.Add($Message); Write-Host "[fail] $Message" -ForegroundColor Red }
function Check([bool]$Condition, [string]$Message, [string]$Detail) {
    if ($Condition) { Write-Host "[ok] $Message" } else { Fail $(if ($Detail) { "$Message ($Detail)" } else { $Message }) }
}
function Warn([string]$Message) { Write-Host "[warn] $Message" -ForegroundColor Yellow }
function Read-Text([string]$Path) { [IO.File]::ReadAllText($Path, [Text.Encoding]::UTF8) }
function Get-Sha256([IO.Stream]$Stream) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try { ([BitConverter]::ToString($sha.ComputeHash($Stream)) -replace '-', '').ToLowerInvariant() } finally { $sha.Dispose() }
}
function Get-FileSha256([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
function Get-EntrySha256($Entry) { $stream = $Entry.Open(); try { Get-Sha256 $stream } finally { $stream.Dispose() } }
function Get-EntryText($Entry) {
    $reader = [IO.StreamReader]::new($Entry.Open(), [Text.Encoding]::UTF8)
    try { $reader.ReadToEnd() } finally { $reader.Dispose() }
}
function Test-SameSet([string[]]$Actual, [string[]]$Expected, [string]$Message) {
    $missing = @($Expected | Where-Object { $_ -cnotin $Actual })
    $extra = @($Actual | Where-Object { $_ -cnotin $Expected })
    $detail = @()
    if ($missing.Count) { $detail += 'missing ' + ($missing -join ', ') }
    if ($extra.Count) { $detail += 'unexpected ' + ($extra -join ', ') }
    Check ($detail.Count -eq 0) $Message ($detail -join '; ')
}
function Compare-Version([string]$Left, [string]$Right) { ([version]$Left).CompareTo([version]$Right) }

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
            '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"anode-distribution-check","version":"1"}}}',
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

function Test-RepositoryLink([string]$Url, [string]$Source) {
    if ($Url -notmatch '^https://github\.com/skulitom/Anode/blob/main/([^#?]+)') { return }
    $path = Join-Path $root ([Uri]::UnescapeDataString($Matches[1]).Replace('/', '\'))
    # Anchors are checked by scripts\check-docs.ps1.
    if (Test-Path -LiteralPath $path -PathType Leaf) { Write-Host "[ok] $Source links to an existing repository file: $Url" }
    elseif ($Release) { Fail "$Source links to a missing repository file: $Url" }
    else { Warn "$Source links to a missing repository file: $Url" }
}

# Fields from the MCPB manifest spec v0.3; the schema rejects other top-level keys.
$mcpbKeys = @('$schema','dxt_version','manifest_version','name','display_name','version','description','long_description',
    'author','repository','homepage','documentation','support','icon','icons','screenshots','localization','server','tools',
    'tools_generated','prompts','prompts_generated','keywords','license','privacy_policies','compatibility','user_config','_meta')
function Test-McpbManifest($Manifest, [string]$Source) {
    $unknown = @($Manifest.PSObject.Properties.Name | Where-Object { $_ -cnotin $mcpbKeys })
    Check ($unknown.Count -eq 0) "$Source uses only MCPB v0.3 fields" ('unknown ' + ($unknown -join ', '))
    Check ($Manifest.manifest_version -ceq '0.3' -and $Manifest.name -ceq 'anode') "$Source is MCPB 0.3 named anode"
    Check ($Manifest.version -ceq $version) "$Source version is $version" "found $($Manifest.version)"
    Check ($Manifest.description -ceq $description) "$Source description matches the csproj Description"
    Check ([bool]$Manifest.author.name) "$Source has an author name"
    $server = $Manifest.server
    Check ($server.type -ceq 'binary' -and $server.entry_point -ceq 'server/anode.exe' -and
        $server.mcp_config.command -ceq '${__dirname}/server/anode.exe' -and (@($server.mcp_config.args) -join ' ') -ceq 'mcp') `
        "$Source runs server/anode.exe mcp"
    Check ((@($Manifest.compatibility.platforms) -join ',') -ceq 'win32') "$Source is limited to win32"
    Test-SameSet @($Manifest.tools | ForEach-Object { [string]$_.name }) $tools "$Source lists the tools/list names"
    $policies = @($Manifest.privacy_policies)
    Check ($policies.Count -gt 0 -and @($policies | Where-Object { $_ -notmatch '^https://' }).Count -eq 0) "$Source has HTTPS privacy policies"
    $settings = @()
    if ($Manifest.user_config) {
        foreach ($setting in $Manifest.user_config.PSObject.Properties) {
            $settings += $setting.Name
            Check ($setting.Value.type -and $setting.Value.title -and $setting.Value.description) "$Source user_config.$($setting.Name) has type, title and description"
        }
    }
    if ($server.mcp_config.env) {
        foreach ($variable in $server.mcp_config.env.PSObject.Properties) {
            foreach ($reference in [regex]::Matches([string]$variable.Value, '\$\{user_config\.([^}]+)\}')) {
                Check ($reference.Groups[1].Value -cin $settings) "$Source env $($variable.Name) references a declared user_config"
            }
        }
    }
    foreach ($link in @($Manifest.homepage, $Manifest.documentation, $Manifest.support) + $policies) { if ($link) { Test-RepositoryLink $link $Source } }
}

function Read-Manifest([string]$Relative) {
    $path = Join-Path $root $Relative
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { Write-Host "[skip] $Relative is absent"; return $null }
    try { $value = Read-Text $path | ConvertFrom-Json; Write-Host "[ok] $Relative parses"; $value }
    catch { Fail "$Relative does not parse: $($_.Exception.Message)"; $null }
}

[xml]$project = Get-Content -LiteralPath (Join-Path $root 'src\Anode\Anode.csproj') -Raw
$properties = $project.Project.PropertyGroup
$version = @($properties.Version | Where-Object { $_ })[0]
$description = @($properties.Description | Where-Object { $_ })[0]
$title = @($properties.AssemblyTitle | Where-Object { $_ })[0]
$copyright = @($properties.Copyright | Where-Object { $_ })[0]
Write-Host "Anode $version distribution checks: $ArchiveDirectory" -ForegroundColor Cyan

$package = Join-Path $ArchiveDirectory 'anode-windows-x64.zip'
$bundlePath = Join-Path $ArchiveDirectory 'anode-windows-x64.mcpb'
$workspace = Join-Path ([IO.Path]::GetTempPath()) ('anode-distribution-' + [Guid]::NewGuid().ToString('N'))
try {
    if (-not (Test-Path -LiteralPath $package -PathType Leaf)) { throw "Missing $package. Build with scripts\build.ps1 -Package." }
    if ($Anode) { $exe = (Resolve-Path -LiteralPath $Anode).Path }
    else {
        # Test the executable users receive, not a local build output.
        $null = New-Item -ItemType Directory -Path $workspace
        $exe = Join-Path $workspace 'anode.exe'
        $zip = [IO.Compression.ZipFile]::OpenRead($package)
        try { [IO.Compression.ZipFileExtensions]::ExtractToFile($zip.GetEntry('anode.exe'), $exe) } finally { $zip.Dispose() }
    }

    $versionOutput = (& $exe version | Out-String).Trim()
    Check ($LASTEXITCODE -eq 0 -and $versionOutput -ceq "anode $version") "anode version reports $version" "printed '$versionOutput'"
    $info = (Get-Item -LiteralPath $exe).VersionInfo
    Check ($info.ProductVersion -like "$version*") "file version resource is $version" "found $($info.ProductVersion)"
    if ($title) { Check ($info.FileDescription -ceq $title) 'file description is the csproj AssemblyTitle' "found '$($info.FileDescription)'" }
    if ($copyright) { Check ($info.LegalCopyright -ceq $copyright) 'file copyright is the csproj Copyright' "found '$($info.LegalCopyright)'" }

    $tools = @(Get-McpToolNames $exe)
    Check ($tools.Count -gt 0 -and @($tools | Select-Object -Unique).Count -eq $tools.Count) "tools/list returns $($tools.Count) unique tools"
    foreach ($document in @('README.md', 'docs\PROTOCOL.md')) {
        $claims = @([regex]::Matches((Read-Text (Join-Path $root $document)), '\b(\d+) (?:MCP )?tools\b') | ForEach-Object { [int]$_.Groups[1].Value })
        if (-not $claims.Count) { Warn "$document states no tool count" }
        else { Check (@($claims | Where-Object { $_ -ne $tools.Count }).Count -eq 0) "$document tool count matches tools/list" "states $($claims -join ', ')" }
    }

    $sumsPath = Join-Path $ArchiveDirectory 'SHA256SUMS'
    $sums = [IO.File]::ReadAllBytes($sumsPath)
    Check ($sums -notcontains 13 -and $sums.Length -gt 0 -and $sums[-1] -eq 10) 'SHA256SUMS uses LF line endings'
    $listed = @()
    foreach ($line in ([Text.Encoding]::ASCII.GetString($sums).Split("`n") | ForEach-Object { $_.TrimEnd("`r") } | Where-Object { $_ })) {
        if ($line -notmatch '^([0-9a-f]{64})  ([A-Za-z0-9._-]+)$') { Fail "Malformed SHA256SUMS line: $line"; continue }
        $hash = $Matches[1]; $name = $Matches[2]; $listed += $name
        $path = Join-Path $ArchiveDirectory $name
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { Fail "SHA256SUMS lists a missing file: $name"; continue }
        Check ((Get-FileSha256 $path) -eq $hash) "SHA256SUMS matches $name"
    }
    Test-SameSet $listed @('anode-windows-x64.zip', 'install.ps1') 'SHA256SUMS lists the release assets'
    Check ((Get-FileSha256 (Join-Path $ArchiveDirectory 'install.ps1')) -eq (Get-FileSha256 (Join-Path $root 'scripts\install.ps1'))) 'released install.ps1 matches scripts\install.ps1'

    # The payload list is read from build.ps1, so the two cannot drift apart.
    $buildScript = [Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'build.ps1'), [ref]$null, [ref]$null)
    $assignment = $buildScript.Find({ param($node) $node -is [Management.Automation.Language.AssignmentStatementAst] -and $node.Left.Extent.Text -eq '$packageItems' }, $true)
    if (-not $assignment) { throw 'Cannot find $packageItems in build.ps1.' }
    $expected = [ordered]@{}
    foreach ($item in @($assignment.Right.FindAll({ param($node) $node -is [Management.Automation.Language.StringConstantExpressionAst] }, $true) | ForEach-Object { $_.Value })) {
        $source = if ($item -in @('install.ps1', 'connect-agents.ps1')) { Join-Path $root "scripts\$item" } else { Join-Path $root $item }
        if ($item -eq 'anode.exe') { $expected[$item] = $null }
        elseif (Test-Path -LiteralPath $source -PathType Container) {
            Get-ChildItem -LiteralPath $source -Recurse -File | ForEach-Object { $expected[$_.FullName.Substring($root.Length + 1).Replace('\', '/')] = $_.FullName }
        } else { $expected[$item] = $source }
    }
    $zip = [IO.Compression.ZipFile]::OpenRead($package)
    try {
        $entries = @($zip.Entries | Where-Object { $_.FullName -notmatch '/$' })
        $names = @($entries | ForEach-Object { $_.FullName })
        Check (@($names | Where-Object { $_.Contains('\') }).Count -eq 0) 'anode-windows-x64.zip entry names use /'
        Test-SameSet $names @($expected.Keys) 'anode-windows-x64.zip contains exactly the build.ps1 payload'
        $stale = @($entries | Where-Object { $expected[$_.FullName] -and (Get-EntrySha256 $_) -ne (Get-FileSha256 $expected[$_.FullName]) } | ForEach-Object { $_.FullName })
        Check ($stale.Count -eq 0) 'packaged documents and scripts match the repository' ('differ: ' + ($stale -join ', '))
        $packagedExe = $zip.GetEntry('anode.exe')
        $packagedExeHash = if ($packagedExe) { Get-EntrySha256 $packagedExe } else { $null }
    } finally { $zip.Dispose() }

    if (Test-Path -LiteralPath $bundlePath -PathType Leaf) {
        $zip = [IO.Compression.ZipFile]::OpenRead($bundlePath)
        try {
            $names = @($zip.Entries | ForEach-Object { $_.FullName })
            Check (@($names | Where-Object { $_.Contains('\') }).Count -eq 0) 'anode-windows-x64.mcpb entry names use /'
            $bundleExpected = @('LICENSE', 'manifest.json', 'server/anode.exe') + @($names | Where-Object { $_ -ceq 'icon.png' })
            Test-SameSet $names $bundleExpected 'anode-windows-x64.mcpb contains manifest.json, server/anode.exe, LICENSE and an optional icon.png'
            $manifestEntry = $zip.GetEntry('manifest.json')
            if ($manifestEntry) {
                $bundleManifest = Get-EntryText $manifestEntry | ConvertFrom-Json
                Test-McpbManifest $bundleManifest 'anode-windows-x64.mcpb manifest.json'
                Check (($bundleManifest.icon -ceq 'icon.png') -eq ('icon.png' -cin $names)) 'anode-windows-x64.mcpb declares icon.png exactly when it contains one'
            }
            $bundleExe = $zip.GetEntry('server/anode.exe')
            Check ($bundleExe -and (Get-EntrySha256 $bundleExe) -eq $packagedExeHash) 'anode-windows-x64.mcpb carries the packaged anode.exe'
            $license = $zip.GetEntry('LICENSE')
            Check ($license -and (Get-EntrySha256 $license) -eq (Get-FileSha256 (Join-Path $root 'LICENSE'))) 'anode-windows-x64.mcpb LICENSE matches the repository'
        } finally { $zip.Dispose() }
    } else { Write-Host '[skip] anode-windows-x64.mcpb was not built (build.ps1 -Package -Mcpb)' }

    $mcpb = Read-Manifest 'packaging\mcpb\manifest.json'
    if ($mcpb) { Test-McpbManifest $mcpb 'packaging/mcpb/manifest.json' }

    $server = Read-Manifest 'server.json'
    if ($server) {
        Check ($server.name -ceq 'io.github.skulitom/anode') 'server.json name is io.github.skulitom/anode'
        Check ($server.version -ceq $version) "server.json version is $version" "found $($server.version)"
        Check ($server.description -ceq $description -and $server.description.Length -le 100) 'server.json description matches the csproj Description (at most 100 characters)'
        foreach ($entry in @($server.packages | Where-Object { $_ })) {
            if ($entry.version) { Check ($entry.version -ceq $version) "server.json package version is $version" "found $($entry.version)" }
        }
    }
    $plugin = Read-Manifest '.claude-plugin\plugin.json'
    if ($plugin) {
        Check ($plugin.name -ceq 'anode') '.claude-plugin/plugin.json name is anode'
        Check ($plugin.version -ceq $version) ".claude-plugin/plugin.json version is $version" "found $($plugin.version)"
        if ($plugin.mcpServers) { Check ('mcp' -cin @($plugin.mcpServers.anode.args)) '.claude-plugin/plugin.json runs anode mcp' }
    }
    $marketplace = Read-Manifest '.claude-plugin\marketplace.json'
    if ($marketplace) {
        Check ($marketplace.name -ceq 'anode' -and @($marketplace.plugins | Where-Object { $_.name -ceq 'anode' }).Count -eq 1) '.claude-plugin/marketplace.json offers the anode plugin from the anode marketplace'
        foreach ($entry in @($marketplace.plugins | Where-Object { $_.version })) { Check ($entry.version -ceq $version) ".claude-plugin/marketplace.json $($entry.name) version is $version" }
    }
    $codexPlugin = Read-Manifest '.codex-plugin\plugin.json'
    if ($codexPlugin -and $codexPlugin.version) { Check ($codexPlugin.version -ceq $version) ".codex-plugin/plugin.json version is $version" "found $($codexPlugin.version)" }
    $null = Read-Manifest '.agents\plugins\marketplace.json'
    $null = Read-Manifest 'glama.json'

    # Scoop and winget describe a published release, so they may trail an unreleased csproj version.
    $bucket = Read-Manifest 'bucket\anode.json'
    if ($bucket) {
        Check ($bucket.version -match '^\d+\.\d+\.\d+$' -and (Compare-Version $bucket.version $version) -le 0) "bucket/anode.json version $($bucket.version) is released and not after $version"
        # Scoop accepts the download at the top level or per architecture; Anode ships x64 only.
        $download = if ($bucket.architecture) { $bucket.architecture.'64bit' } else { $bucket }
        Check ($download.url -ceq "https://github.com/skulitom/Anode/releases/download/v$($bucket.version)/anode-windows-x64.zip") 'bucket/anode.json downloads that version''s release ZIP' "found $($download.url)"
        Check ($download.hash -match '^(sha256:)?[0-9a-fA-F]{64}$') 'bucket/anode.json has a SHA-256 hash'
        # A PATH entry rather than a shim: Scoop's shim detaches the console a GUI-subsystem exe borrows.
        Check ('anode.exe' -cin @($bucket.bin) -or '.' -cin @($bucket.env_add_path)) 'bucket/anode.json puts anode.exe on PATH'
    }
    $winget = @(Get-ChildItem -LiteralPath (Join-Path $root 'packaging\winget') -Filter '*.yaml' -File -ErrorAction SilentlyContinue)
    if ($winget.Count) {
        $fields = @{}
        foreach ($file in $winget) {
            foreach ($line in (Read-Text $file.FullName) -split "`r?`n") {
                if ($line -match '^\s*-?\s*(PackageIdentifier|PackageVersion|ManifestType|InstallerUrl|InstallerSha256|ShortDescription):\s*(.+?)\s*$') {
                    $fields[$Matches[1]] = @($fields[$Matches[1]]) + $Matches[2].Trim('"', "'") | Where-Object { $_ }
                }
            }
        }
        Check (@($fields['PackageIdentifier'] | Select-Object -Unique).Count -eq 1 -and @($fields['PackageVersion'] | Select-Object -Unique).Count -eq 1) 'packaging/winget files share one package identifier and version'
        $wingetVersion = @($fields['PackageVersion'])[0]
        Check ($wingetVersion -match '^\d+\.\d+\.\d+$' -and (Compare-Version $wingetVersion $version) -le 0) "packaging/winget version $wingetVersion is released and not after $version"
        Check (@($fields['InstallerUrl'] | Where-Object { $_ -cne "https://github.com/skulitom/Anode/releases/download/v$wingetVersion/anode-windows-x64.zip" }).Count -eq 0 -and @($fields['InstallerUrl']).Count -gt 0) 'packaging/winget downloads that version''s release ZIP'
        Check (@($fields['InstallerSha256'] | Where-Object { $_ -cnotmatch '^[0-9A-F]{64}$' }).Count -eq 0 -and @($fields['InstallerSha256']).Count -gt 0) 'packaging/winget has upper-case SHA-256 hashes'
        if ($fields['ShortDescription']) { Check (@($fields['ShortDescription'])[0] -ceq $description) 'packaging/winget ShortDescription matches the csproj Description' }
    } else { Write-Host '[skip] packaging/winget is absent' }

    if ($Release) {
        $changelog = Read-Text (Join-Path $root 'CHANGELOG.md')
        $unreleased = [regex]::Match($changelog, '(?ms)^## Unreleased\s*\r?\n(?<body>.*?)(?=^## |\z)')
        Check (-not $unreleased.Success -or [string]::IsNullOrWhiteSpace($unreleased.Groups['body'].Value)) `
            'CHANGELOG.md has no unassigned Unreleased changes' 'Assign changes to a new version before publishing.'
        Check ((Read-Text (Join-Path $root '.github\RELEASE_NOTES.md')).Contains("What's new in $version")) "RELEASE_NOTES.md has What's new in $version"
        Check ((Read-Text (Join-Path $root 'CHANGELOG.md')) -match "(?m)^## $([regex]::Escape($version)) ") "CHANGELOG.md has a $version section"
    }
} finally {
    if (Test-Path -LiteralPath $workspace) { Remove-Item -LiteralPath $workspace -Recurse -Force }
}

if ($failures.Count) { throw "$($failures.Count) distribution check(s) failed." }
Write-Host 'All distribution checks passed.' -ForegroundColor Green
