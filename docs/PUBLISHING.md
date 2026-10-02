# Publishing Anode

The official MCP Registry is published by [publish-registry.yml](../.github/workflows/publish-registry.yml)
using GitHub OIDC: no secret and no registry sign-in. Smithery, awesome-mcp-servers and the other
directories remain manual owner steps. The owner also tests and uploads each MCPB bundle before CI
can list it. Run the PowerShell commands from the repository root unless a step says otherwise.
Manual GitHub steps require `gh` signed in as skulitom (`gh auth status`); stop if any command fails.

Use this one-line description wherever a directory asks for it:

> Background Windows desktop for AI agents: native GUI automation, screenshots and app testing.

## 1. MCP Registry (automatic)

Merging this workflow to `main` lists **0.11.0 as metadata only**, provided the registry has no Anode
listing and the matching GitHub release exists. The repository's `server.json` uses schema
`2025-12-11` and intentionally has no `packages` property. A daily run can recover that first
listing if the merge run failed and the latest release still matches `server.json` on `main`.

For later releases, CI publishes the installable MCPB entry once the owner attaches the tested
`anode-windows-x64.mcpb` to the release ([section 2](#2-next-release-installable-mcpb)). The daily run
picks up the latest release, or dispatch immediately after uploading (replace `vX.Y.Z`):

```powershell
gh workflow run publish-registry.yml -R skulitom/Anode -f tag=vX.Y.Z
if ($LASTEXITCODE -ne 0) { throw 'Could not start registry publication.' }
gh run watch -R skulitom/Anode
if ($LASTEXITCODE -ne 0) { throw 'Could not watch registry publication.' }
```

Select the **Publish to MCP Registry** run. Runs are idempotent: an already published version is
skipped. CI verifies the bundle's attestation came from this repository's Release workflow and
that its manifest is the release's version, validates the entry, publishes through OIDC and reads
back the version and bundle hash.

For a release that will not get a bundle, explicitly publish its metadata instead:

```powershell
gh workflow run publish-registry.yml -R skulitom/Anode -f tag=vX.Y.Z -f metadata_only=true
if ($LASTEXITCODE -ne 0) { throw 'Could not start metadata-only publication.' }
```

**A published version cannot be changed.** Metadata-only publication permanently prevents that
version from getting its bundle; an installable entry then requires a new Anode version.

Check the run summary for the name, version, entry type and registry URL, and confirm that
[the registry search endpoint](https://registry.modelcontextprotocol.io/v0.1/servers?search=io.github.skulitom/anode)
contains `io.github.skulitom/anode` at the intended version. PulseMCP and GitHub's MCP registry draw
on the official registry (GitHub curates new servers by hand first); allow time for their listings
to update.

## 2. Next release: installable MCPB

Follow [the release process](../CONTRIBUTING.md#release-process), including assigning `Unreleased`
changes to the next version. Wait for the Release workflow for `vX.Y.Z` to succeed. That workflow
publishes only the ZIP, `install.ps1` and `SHA256SUMS` to GitHub Releases. The attested MCPB bundle,
its separate checksum and `server.registry.json` are in the **anode-windows-x64-mcpb** workflow artifact.

Find the successful run, confirm its tag is the intended release, then download its artifact.
Replace the tag and run ID below with the actual values; keep these variables for subsequent steps:

```powershell
gh run list -R skulitom/Anode --workflow release.yml -L 1
if ($LASTEXITCODE -ne 0) { throw 'Could not list release runs.' }
$tag = 'vX.Y.Z'
$runId = '<run-id>'
$artifactDirectory = "artifacts\mcpb-$tag"
gh run view $runId -R skulitom/Anode
if ($LASTEXITCODE -ne 0) { throw 'Could not inspect the release run.' }
gh run download $runId -R skulitom/Anode -n anode-windows-x64-mcpb -D $artifactDirectory
if ($LASTEXITCODE -ne 0) { throw 'MCPB artifact download failed.' }
gh attestation verify "$artifactDirectory\anode-windows-x64.mcpb" --repo skulitom/Anode --signer-workflow skulitom/Anode/.github/workflows/release.yml
if ($LASTEXITCODE -ne 0) { throw 'MCPB attestation verification failed.' }
$bundle = Join-Path $artifactDirectory 'anode-windows-x64.mcpb'
$entryPath = Join-Path $artifactDirectory 'server.registry.json'
$entry = Get-Content -LiteralPath $entryPath -Raw | ConvertFrom-Json
$hash = (Get-FileHash -LiteralPath $bundle -Algorithm SHA256).Hash.ToLowerInvariant()
$checksum = (Get-Content -LiteralPath "$bundle.sha256" -Raw).Trim()
if ($checksum -cne "$hash  anode-windows-x64.mcpb" -or $entry.packages[0].fileSha256 -cne $hash) { throw 'MCPB checksum mismatch.' }
if ("v$($entry.version)" -cne $tag -or $entry.packages[0].identifier -cne "https://github.com/skulitom/Anode/releases/download/$tag/anode-windows-x64.mcpb") { throw 'Artifact does not match the intended release.' }
```

Check: the run is successful for the intended tag, attestation verification succeeds for
`skulitom/Anode/.github/workflows/release.yml`, and both checksum comparisons pass. Now run the
[Claude Desktop test](../packaging/mcpb/README.md#claude-desktop-test-release-gate) on **that exact
downloaded file**, and record the results. Stop here if any test fails.

**Only if the test passes**, upload the tested bundle and start the registry workflow:

```powershell
gh release upload $tag "$artifactDirectory\anode-windows-x64.mcpb" -R skulitom/Anode
if ($LASTEXITCODE -ne 0) { throw 'MCPB release upload failed.' }
gh workflow run publish-registry.yml -R skulitom/Anode -f "tag=$tag"
if ($LASTEXITCODE -ne 0) { throw 'Could not start registry publication.' }
gh run watch -R skulitom/Anode
if ($LASTEXITCODE -ne 0) { throw 'Could not watch registry publication.' }
gh release view $tag -R skulitom/Anode --json tagName,assets
if ($LASTEXITCODE -ne 0) { throw 'Could not check release assets.' }
```

Check: select the **Publish to MCP Registry** run and confirm it succeeds. Its summary identifies
the version and entry type. The release must list `anode-windows-x64.mcpb`, and the
[registry search response](https://registry.modelcontextprotocol.io/v0.1/servers?search=io.github.skulitom/anode)
includes the new version with its MCPB package and matching hash. The uploaded file must be
**byte-identical to the tested file**, because `server.registry.json` carries its SHA-256. Do not
rebuild, repack or replace it between testing and upload. Leave the repository's `server.json`
metadata-only, and leave the release's `SHA256SUMS` covering only the ZIP and installer.

## 3. Smithery

Whether Smithery accepts a Windows-only MCPB bundle is **unknown**. Its documented command form is
`smithery mcp publish ./server.mcpb -n your-org/your-server`. The npm package name and login step are
not stated in that guidance: **check `npx -y @smithery/cli --help` first**, confirm the package and
follow its current login instructions before attempting publication as skulitom.

```powershell
npx -y @smithery/cli --help
if ($LASTEXITCODE -ne 0) { throw 'Check the Smithery CLI package and instructions before continuing.' }
```

After checking the package and completing the login step shown by its help, use the tested bundle
from step 2 (replace `vX.Y.Z`):

```powershell
npx -y @smithery/cli mcp publish .\artifacts\mcpb-vX.Y.Z\anode-windows-x64.mcpb -n skulitom/anode
if ($LASTEXITCODE -ne 0) { throw 'Smithery publication failed; check Windows-only bundle support.' }
```

Alternatively, upload the bundle at [Smithery's submission page](https://smithery.ai/new).
Check: the submission succeeds and its resulting listing identifies Anode as Windows-only with
the correct version and setup requirements; do not assume submission means acceptance.

## 4. awesome-mcp-servers PR

The `punkpeye/awesome-mcp-servers` CI requires a Glama score badge. First visit
[Glama's server directory](https://glama.ai/mcp/servers), select **Add Server**, sign in as skulitom,
and add/claim `skulitom/Anode`. The repository's `glama.json` lets the owner claim it. Check that
the [Anode Glama listing](https://glama.ai/mcp/servers/skulitom/Anode) and its score badge work before
opening the PR.

From a parent folder outside the Anode checkout:

```powershell
gh repo fork punkpeye/awesome-mcp-servers --clone
if ($LASTEXITCODE -ne 0) { throw 'Fork or clone failed.' }
Set-Location awesome-mcp-servers
git switch -c add-anode
if ($LASTEXITCODE -ne 0) { throw 'Could not create the contribution branch.' }
Get-ChildItem -Recurse -File -Filter 'CONTRIBUTING*' | ForEach-Object { Get-Content -LiteralPath $_.FullName }
```

Read the list's CONTRIBUTING instructions first, then add this exact line in alphabetical order
in the most fitting section of its README:

```markdown
- [skulitom/Anode](https://github.com/skulitom/Anode) [![skulitom/Anode MCP server](https://glama.ai/mcp/servers/skulitom/Anode/badges/score.svg)](https://glama.ai/mcp/servers/skulitom/Anode) #️⃣ 🏠 🪟 - A background Windows desktop for agents: a hidden child session with its own screen, pointer and keyboard focus, plus UI Automation, screenshots, command jobs and leases, so agents test GUI apps without taking over your mouse.
```

Check `git diff` for the single listing, its position and the working badge, and run any checks
required by CONTRIBUTING. Then commit, push to your fork, and open the PR:

```powershell
git diff --check
if ($LASTEXITCODE -ne 0) { throw 'Fix whitespace before committing.' }
git diff -- README.md
git add README.md
if ($LASTEXITCODE -ne 0) { throw 'Could not stage the listing.' }
git commit -m 'Add skulitom/Anode (background Windows desktop for agents)'
if ($LASTEXITCODE -ne 0) { throw 'Could not commit the listing.' }
git push -u origin add-anode
if ($LASTEXITCODE -ne 0) { throw 'Could not push to the owner fork.' }
$body = @'
Adds Anode, a Windows MCP server that gives agents a background desktop for GUI automation and app testing while the user keeps working. It includes screenshots, UI Automation, command jobs and desktop leases, and the listing includes its Glama score badge. I am the author and owner of skulitom/Anode.
'@
$bodyFile = Join-Path ([IO.Path]::GetTempPath()) ('anode-directory-pr-' + [Guid]::NewGuid().ToString('N') + '.md')
try {
    [IO.File]::WriteAllText($bodyFile, $body, [Text.UTF8Encoding]::new($false))
    gh pr create -R punkpeye/awesome-mcp-servers --head skulitom:add-anode --title 'Add skulitom/Anode (background Windows desktop for agents)' --body-file $bodyFile
    if ($LASTEXITCODE -ne 0) { throw 'Could not open the directory PR.' }
} finally { Remove-Item -LiteralPath $bodyFile -Force }
```

Check: open the returned PR URL, confirm the author disclosure and badge render correctly, and
wait for the directory's CI and review.

## 5. Other directories

- [mcp.so submission](https://mcp.so/submit)
- [mcpservers.org submission (free option)](https://mcpservers.org/submit)
- [Claude plugin directory submission form](https://clau.de/plugin-directory-submission)
- [Glama add/claim server](https://glama.ai/mcp/servers)

## What is published where today

CI publishes the official registry; the owner handles the other submissions:

| Channel | Current state |
| --- | --- |
| Scoop bucket | Served from this repository (`bucket/anode.json`). |
| Claude Code plugin marketplace | Served from this repository (`.claude-plugin/marketplace.json`). |
| MCP Registry | Published by `publish-registry.yml`: first listing as metadata, then per-release MCPB entries after the tested bundle is attached. |
| winget | Not yet published; manifests are maintained in this repository. |
| MCPB | Not yet a release asset; built and attested by Release as a workflow artifact for owner testing. |

## Manual fallback (device sign-in)

Use this only if CI publishing is unavailable. [mcp-publisher.ps1](../scripts/mcp-publisher.ps1)
downloads and verifies the pinned publisher and returns its path. Set `$entryPath` to `server.json`
for metadata only, or to the generated `server.registry.json` from section 2 after uploading the
tested bundle. The same immutable-version rule and Claude Desktop test gate apply.

```powershell
$ErrorActionPreference = 'Stop'
$publisher = ./scripts/mcp-publisher.ps1
$entryPath = 'server.json'
& $publisher validate $entryPath
if ($LASTEXITCODE -ne 0) { throw 'Registry entry validation failed.' }
& $publisher login github
if ($LASTEXITCODE -ne 0) { throw 'Registry sign-in failed.' }
& $publisher publish $entryPath
if ($LASTEXITCODE -ne 0) { throw 'Registry publication failed.' }
```

Complete the device-code sign-in as **skulitom**. Validation contacts the registry and must succeed
before sign-in and publication. Check the version and any bundle hash at the
[registry search endpoint](https://registry.modelcontextprotocol.io/v0.1/servers?search=io.github.skulitom/anode).
