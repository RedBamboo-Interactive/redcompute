<#
.SYNOPSIS
  Builds the leaf-node release artifacts (Phase 3 R7, D3) from a RedCompute source checkout.

.DESCRIPTION
  The Nightly's RedCompute recipe runs this once RedCompute's own files are final, from the same clone
  and source commit. It adds five files and changes nothing RedCompute ships:

    leaf-node-win-x64.zip        the win-x64 agent (self-contained folder, deterministic zip)
    leaf-node-linux-x64.tar.gz   the linux-x64 agent (deterministic ustar tarball, Unix modes)
    leaf-node-manifest.v1.json   version, source commit, and the size and SHA-256 of each archive
    leaf-node-install.ps1        the Windows installer (per user, no admin)
    leaf-node-install.sh         the Linux installer (per user, refuses root)

  They are attached to RedCompute's release beside its artifact. The installers find them through the
  signed feed's RedCompute record (pointer, then record by hash), check the manifest against that
  record's version and source commit, and check the archive's size and SHA-256 against the manifest.

  -AssetList receives [{ name, path, sha256 }] in that order: the shape of result.json extraAssets.

.EXAMPLE
  ./release/leaf-node/Build-LeafNodeArtifacts.ps1 -RepositoryRoot . -OutputRoot S:\scratch\leaf-node `
    -WorkRoot S:\scratch\work -Version 0.1.24 -SourceCommit (git rev-parse HEAD) `
    -ReleaseTool S:\scratch\tool\RedCompute.ReleaseTool.dll -AssetList S:\scratch\assets.json
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $RepositoryRoot,
    [Parameter(Mandatory)] [string] $OutputRoot,
    [Parameter(Mandatory)] [string] $WorkRoot,
    [Parameter(Mandatory)] [ValidatePattern('^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$')] [string] $Version,
    [Parameter(Mandatory)] [ValidatePattern('^[a-f0-9]{40}$')] [string] $SourceCommit,
    [Parameter(Mandatory)] [string] $ReleaseTool,
    [Parameter(Mandatory)] [string] $AssetList,
    # Extra MSBuild properties for restore and publish (the recipe passes -p:RedBambooPackagesRoot=...).
    [string[]] $MSBuildProperty = @()
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
Set-StrictMode -Version Latest

function Invoke-Checked([string] $File, [string[]] $Arguments) {
    & $File @Arguments
    if ($LASTEXITCODE -ne 0) { throw "Command failed ($LASTEXITCODE): $File $($Arguments -join ' ')" }
}

$repo = [IO.Path]::GetFullPath($RepositoryRoot)
$out = [IO.Path]::GetFullPath($OutputRoot)
$work = [IO.Path]::GetFullPath($WorkRoot)
$tool = [IO.Path]::GetFullPath($ReleaseTool)
if ((Test-Path -LiteralPath $out) -and @(Get-ChildItem -LiteralPath $out -Force).Count -ne 0) { throw "OutputRoot must be empty: $out" }
New-Item -ItemType Directory -Force -Path $out, $work | Out-Null
$project = Join-Path $repo 'src/RedCompute.Node/RedCompute.Node.csproj'
$installers = Join-Path $repo 'release/leaf-node'

$archives = [ordered]@{}
foreach ($rid in 'win-x64', 'linux-x64') {
    Write-Output "[leaf-node] publishing $rid"
    $publish = Join-Path $work "publish-$rid"
    if (Test-Path -LiteralPath $publish) { Remove-Item -LiteralPath $publish -Recurse -Force }
    # -r on the command line as well as the profile: Directory.Build.props picks the lock file from it.
    Invoke-Checked dotnet (@('restore', $project, '--locked-mode', '--runtime', $rid, '--nologo') + $MSBuildProperty)
    Invoke-Checked dotnet (@('publish', $project, '--runtime', $rid, "-p:PublishProfile=$rid", '--no-restore',
        '--output', $publish, '--nologo', "-p:Version=$Version", '-p:ContinuousIntegrationBuild=true',
        '-p:Deterministic=true') + $MSBuildProperty)
    $name = if ($rid -ceq 'win-x64') { 'leaf-node-win-x64.zip' } else { 'leaf-node-linux-x64.tar.gz' }
    $archive = Join-Path $out $name
    Invoke-Checked dotnet @($tool, 'node', 'archive', '--source', $publish, '--output', $archive, '--rid', $rid)
    $archives[$rid] = $archive
}

$manifest = Join-Path $out 'leaf-node-manifest.v1.json'
Invoke-Checked dotnet @($tool, 'node', 'manifest', '--version', $Version, '--source-commit', $SourceCommit,
    '--windows-archive', $archives['win-x64'], '--linux-archive', $archives['linux-x64'], '--output', $manifest)
Invoke-Checked dotnet @($tool, 'node', 'validate', '--input', $manifest, '--archives', $out)
foreach ($installer in 'leaf-node-install.ps1', 'leaf-node-install.sh') {
    Copy-Item -LiteralPath (Join-Path $installers $installer) -Destination (Join-Path $out $installer)
}
# The Linux installer runs under sh: a CR anywhere breaks it, whatever the builder's core.autocrlf.
if ([IO.File]::ReadAllBytes((Join-Path $out 'leaf-node-install.sh')) -contains 13) { throw 'leaf-node-install.sh has CR line endings' }

$assets = @(foreach ($name in 'leaf-node-win-x64.zip', 'leaf-node-linux-x64.tar.gz', 'leaf-node-manifest.v1.json',
        'leaf-node-install.ps1', 'leaf-node-install.sh') {
    $path = Join-Path $out $name
    [ordered]@{ name = $name; path = $path; sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() }
})
ConvertTo-Json -InputObject $assets -Depth 5 | Set-Content -LiteralPath $AssetList -Encoding utf8NoBOM
foreach ($asset in $assets) { Write-Output "[leaf-node] $($asset.name) $($asset.sha256)" }
