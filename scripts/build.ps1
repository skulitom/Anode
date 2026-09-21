#requires -Version 5.1
<#
.SYNOPSIS
    Builds a single self-contained anode.exe into -OutputDirectory (default dist\).
.DESCRIPTION
    -QuickTest runs selftest --quick against the new executable; -Test runs the full selftest.
    Both also run scripts\check-docs.ps1.
    -Package writes anode-windows-x64.zip, install.ps1 and SHA256SUMS into -ArchiveDirectory.
    -Mcpb (with -Package) also writes anode-windows-x64.mcpb, a Claude Desktop extension for
    manual testing; it is not a release asset yet.
    A running Anode or MCP client locks dist\anode.exe; build elsewhere with -OutputDirectory.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\build.ps1 -QuickTest
    powershell -ExecutionPolicy Bypass -File scripts\build.ps1 -OutputDirectory artifacts\pkg-build -ArchiveDirectory artifacts\pkg-release -QuickTest -Package
#>
param(
    [string]$OutputDirectory = 'dist',
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',
    [switch]$Test,
    [switch]$QuickTest,
    [switch]$Package,
    [switch]$Mcpb,
    [string]$ArchiveDirectory = 'artifacts\release'
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
if ($Package -and $Configuration -ne 'Release') { throw '-Package requires -Configuration Release for the single-file executable.' }
if ($Mcpb -and -not $Package) { throw '-Mcpb requires -Package.' }

function Resolve-BuildPath([string]$Path) {
    if (-not [IO.Path]::IsPathRooted($Path)) { $Path = Join-Path $projectRoot $Path }
    [IO.Path]::GetFullPath($Path).TrimEnd('\')
}

# Archive entries use '/' separators, ordinal order and one timestamp, whichever PowerShell runs this.
function New-AnodeArchive([string]$Destination, [Collections.IDictionary]$Entries, [DateTimeOffset]$Timestamp) {
    Add-Type -AssemblyName System.IO.Compression
    $names = [Collections.Generic.List[string]]::new()
    foreach ($name in $Entries.Keys) {
        if ($name.Contains('\') -or $name.StartsWith('/')) { throw "Invalid archive entry name: $name" }
        $names.Add($name)
    }
    $names.Sort([StringComparer]::Ordinal)
    if (Test-Path -LiteralPath $Destination) { Remove-Item -LiteralPath $Destination -Force }
    $stream = [IO.File]::Open($Destination, 'CreateNew', 'ReadWrite', 'None')
    try {
        $zip = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create)
        try {
            foreach ($name in $names) {
                $entry = $zip.CreateEntry($name, [IO.Compression.CompressionLevel]::Optimal)
                $entry.LastWriteTime = $Timestamp
                $target = $entry.Open()
                try {
                    $source = [IO.File]::OpenRead($Entries[$name])
                    try { $source.CopyTo($target) } finally { $source.Dispose() }
                } finally { $target.Dispose() }
            }
        } finally { $zip.Dispose() }
    } finally { $stream.Dispose() }
}

function Add-ArchiveItems([Collections.IDictionary]$Entries, [string]$Root, [string[]]$Items) {
    foreach ($item in $Items) {
        $path = Join-Path $Root $item
        if (-not (Test-Path -LiteralPath $path)) { throw "Package item is missing: $path" }
        $files = if (Test-Path -LiteralPath $path -PathType Container) { Get-ChildItem -LiteralPath $path -Recurse -File } else { Get-Item -LiteralPath $path }
        foreach ($file in @($files)) {
            $Entries[$file.FullName.Substring($Root.Length + 1).Replace('\', '/')] = $file.FullName
        }
    }
}

# The last commit time keeps archive timestamps stable between builds of one commit.
function Get-SourceTimestamp {
    $ErrorActionPreference = 'Continue'
    if (Get-Command git -ErrorAction SilentlyContinue) {
        $value = "$(& git -C $projectRoot log -1 --format=%cI 2>$null)".Trim()
        if ($LASTEXITCODE -eq 0 -and $value -match '^\d{4}-\d\d-\d\dT') {
            return [DateTimeOffset]::Parse($value, [Globalization.CultureInfo]::InvariantCulture).ToUniversalTime()
        }
    }
    [DateTimeOffset]::UtcNow
}

Push-Location -LiteralPath $projectRoot
try {
    $OutputDirectory = Resolve-BuildPath $OutputDirectory
    if (($projectRoot.TrimEnd('\') + '\').StartsWith($OutputDirectory + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Choose a dedicated -OutputDirectory, not the repository root or one of its parents.'
    }
    $exe = Join-Path $OutputDirectory 'anode.exe'
    if (Test-Path -LiteralPath $exe) {
        try { [IO.File]::Open($exe, 'Open', 'ReadWrite', 'None').Dispose() }
        catch { throw "anode.exe in $OutputDirectory is in use by a running Anode or MCP client; pass -OutputDirectory <other folder>." }
    }
    # Files removed from docs\ or the skill must not survive from an earlier build. Clean only a
    # folder that already holds an Anode build, so a mistyped -OutputDirectory loses nothing.
    $stale = @('docs', 'skills') | ForEach-Object { Join-Path $OutputDirectory $_ } | Where-Object { Test-Path -LiteralPath $_ }
    if ($stale -and -not (Test-Path -LiteralPath $exe -PathType Leaf)) {
        throw "$OutputDirectory has docs or skills folders but no anode.exe; choose an empty folder or an earlier Anode build output."
    }
    foreach ($path in $stale) {
        # Windows PowerShell 5.1 recurses through junctions; remove a link without touching its target.
        # OneDrive folders are reparse points too, but ordinary folders with content: test LinkType.
        if ((Get-Item -LiteralPath $path -Force).LinkType -in 'Junction', 'SymbolicLink') { [IO.Directory]::Delete($path) }
        else { Remove-Item -LiteralPath $path -Recurse -Force }
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

    if (-not (Test-Path $exe)) { throw "Expected $exe to exist." }

    if ($Test -or $QuickTest) {
        Write-Host "`nSelf-test" -ForegroundColor Cyan
        $testArguments = @('selftest')
        if ($QuickTest) { $testArguments += '--quick' }
        & $exe @testArguments | Out-Host
        if ($LASTEXITCODE -ne 0) { throw 'Self-test failed.' }
        Write-Host "`nDocumentation check" -ForegroundColor Cyan
        & (Join-Path $PSScriptRoot 'check-docs.ps1') -Anode $exe
    }

    $size = [math]::Round((Get-Item $exe).Length / 1MB, 1)
    if ($Package) {
        $ArchiveDirectory = Resolve-BuildPath $ArchiveDirectory
        $null = New-Item -ItemType Directory -Path $ArchiveDirectory -Force
        $timestamp = Get-SourceTimestamp
        $archive = Join-Path $ArchiveDirectory 'anode-windows-x64.zip'
        # Explicit payload prevents stale binaries, logs and local files entering a release.
        # scripts\test-distribution.ps1 reads this list.
        $packageItems = @('anode.exe','connect-agents.ps1','install.ps1','README.md','LICENSE','CHANGELOG.md','CONTRIBUTING.md','AGENTS.md','llms.txt','docs','skills')
        $entries = @{}
        Add-ArchiveItems $entries $OutputDirectory $packageItems
        New-AnodeArchive $archive $entries $timestamp
        Copy-Item -LiteralPath scripts\install.ps1 -Destination $ArchiveDirectory -Force
        $sums = @('anode-windows-x64.zip','install.ps1') | ForEach-Object {
            $hash = (Get-FileHash -LiteralPath (Join-Path $ArchiveDirectory $_) -Algorithm SHA256).Hash.ToLowerInvariant()
            "$hash  $_"
        }
        # LF endings, so sha256sum -c works on every platform.
        [IO.File]::WriteAllText((Join-Path $ArchiveDirectory 'SHA256SUMS'), (($sums -join "`n") + "`n"), [Text.Encoding]::ASCII)
        Write-Host "Release archive: $archive" -ForegroundColor Green

        $bundle = Join-Path $ArchiveDirectory 'anode-windows-x64.mcpb'
        if (Test-Path -LiteralPath $bundle) { Remove-Item -LiteralPath $bundle -Force }
        if ($Mcpb) {
            # MCPB (Claude Desktop extension). Not in SHA256SUMS or the release until it passes a live Claude Desktop test.
            [xml]$project = Get-Content -LiteralPath src\Anode\Anode.csproj -Raw
            $version = @($project.Project.PropertyGroup.Version | Where-Object { $_ })[0]
            $manifest = [IO.File]::ReadAllText((Join-Path $projectRoot 'packaging\mcpb\manifest.json')) | ConvertFrom-Json
            $manifest.version = $version
            $bundleEntries = @{ 'server/anode.exe' = $exe; 'LICENSE' = (Join-Path $projectRoot 'LICENSE') }
            $icon = Join-Path $projectRoot 'assets\anode-512.png'
            if (Test-Path -LiteralPath $icon -PathType Leaf) {
                $manifest | Add-Member -NotePropertyName icon -NotePropertyValue 'icon.png' -Force
                $bundleEntries['icon.png'] = $icon
            } else { $manifest.PSObject.Properties.Remove('icon') }
            $manifestFile = Join-Path ([IO.Path]::GetTempPath()) ('anode-mcpb-' + [Guid]::NewGuid().ToString('N') + '.json')
            try {
                [IO.File]::WriteAllText($manifestFile, ($manifest | ConvertTo-Json -Depth 20), [Text.UTF8Encoding]::new($false))
                $bundleEntries['manifest.json'] = $manifestFile
                New-AnodeArchive $bundle $bundleEntries $timestamp
            } finally { Remove-Item -LiteralPath $manifestFile -Force -ErrorAction SilentlyContinue }
            Write-Host "Claude Desktop extension (manual testing only): $bundle" -ForegroundColor Green
        }
    }
    Write-Host "`nReady: $exe ($size MB)" -ForegroundColor Green
    Write-Host "Next:  & '$($exe.Replace("'", "''"))' doctor | Out-Host" -ForegroundColor Gray
} finally { Pop-Location }
