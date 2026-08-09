[CmdletBinding()]
param(
    [string] $ArtifactsPath = 'artifacts/locked-restore-gate',
    [string] $RedBambooPackagesRoot
)

$ErrorActionPreference = 'Stop'
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not [System.IO.Path]::IsPathRooted($ArtifactsPath)) {
    $ArtifactsPath = Join-Path $repoRoot $ArtifactsPath
}
$ArtifactsPath = [System.IO.Path]::GetFullPath($ArtifactsPath)

if ([string]::IsNullOrWhiteSpace($RedBambooPackagesRoot)) {
    $RedBambooPackagesRoot = Join-Path $repoRoot '..\redbamboo-packages'
} elseif (-not [System.IO.Path]::IsPathRooted($RedBambooPackagesRoot)) {
    $RedBambooPackagesRoot = Join-Path $repoRoot $RedBambooPackagesRoot
}
$RedBambooPackagesRoot = [System.IO.Path]::GetFullPath($RedBambooPackagesRoot)

$appHostProject = Join-Path $RedBambooPackagesRoot 'dotnet\RedBamboo.AppHost\RedBamboo.AppHost.csproj'
if (-not (Test-Path -LiteralPath $appHostProject -PathType Leaf)) {
    throw "Pinned AppHost source project was not found at '$appHostProject'."
}

function Get-LockSnapshot {
    Get-ChildItem -LiteralPath $repoRoot -Recurse -Filter packages.lock.json -File |
        Where-Object { $_.FullName -notmatch '[\\/](?:\.git|bin|obj|artifacts|\.release-deps)[\\/]' } |
        Sort-Object FullName |
        ForEach-Object {
            $relativePath = $_.FullName.Substring($repoRoot.Length).TrimStart('\', '/')
            [pscustomobject]@{
                Path = $relativePath.Replace('\', '/')
                Hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
            }
        }
}

function Assert-LocksUnchanged([object[]] $Before) {
    $after = @(Get-LockSnapshot)
    $beforeText = $Before | ConvertTo-Json -Compress
    $afterText = $after | ConvertTo-Json -Compress
    if ($beforeText -cne $afterText) {
        throw 'A committed NuGet lock file was added, removed, or changed during the dual-mode restore gate.'
    }
}

function Invoke-LockedRestore([string] $Mode, [string[]] $ExtraArguments) {
    $modeArtifacts = Join-Path $ArtifactsPath $Mode
    New-Item -ItemType Directory -Path $modeArtifacts -Force | Out-Null
    $arguments = @(
        'restore', (Join-Path $repoRoot 'RedCompute.sln'),
        '--locked-mode',
        '--artifacts-path', $modeArtifacts,
        '--maxcpucount:1',
        '--nologo',
        "-p:RedBambooPackagesRoot=$RedBambooPackagesRoot"
    ) + $ExtraArguments

    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "The $Mode locked restore failed with exit code $LASTEXITCODE."
    }
}

$locksBefore = @(Get-LockSnapshot)
if ($locksBefore.Count -eq 0) {
    throw 'No committed NuGet lock files were found.'
}
$siblingStatusBefore = @(git -C $RedBambooPackagesRoot status --porcelain=v1 --untracked-files=all)
if ($LASTEXITCODE -ne 0) {
    throw "Unable to inspect the AppHost source checkout at '$RedBambooPackagesRoot'."
}

$restoreFailure = $null
try {
    Invoke-LockedRestore -Mode 'neutral' -ExtraArguments @()
    Invoke-LockedRestore -Mode 'win-x64' -ExtraArguments @('--runtime', 'win-x64')
} catch {
    $restoreFailure = $_
}

Assert-LocksUnchanged -Before $locksBefore
$siblingStatusAfter = @(git -C $RedBambooPackagesRoot status --porcelain=v1 --untracked-files=all)
if ($LASTEXITCODE -ne 0) {
    throw "Unable to re-inspect the AppHost source checkout at '$RedBambooPackagesRoot'."
}
if (($siblingStatusBefore -join "`n") -cne ($siblingStatusAfter -join "`n")) {
    throw 'The dual-mode restore changed the AppHost source checkout.'
}
if ($null -ne $restoreFailure) {
    throw $restoreFailure
}

Write-Host "Locked restore gate passed for RID-neutral and win-x64 graphs; $($locksBefore.Count) lock files were unchanged."
