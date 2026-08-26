param(
    [string]$ScratchRoot = $env:REDLEAF_SCRATCH_DIR
)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($ScratchRoot)) {
    throw 'ScratchRoot or REDLEAF_SCRATCH_DIR is required.'
}

$scratchPath = [IO.Path]::GetFullPath($ScratchRoot).TrimEnd('\')
$testRoot = Join-Path $scratchPath ('redcompute-deployment-test-' + [Guid]::NewGuid().ToString('N'))
$testPath = [IO.Path]::GetFullPath($testRoot)
if (-not $testPath.StartsWith($scratchPath + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing unexpected deployment test path: $testPath"
}

$helper = Join-Path $PSScriptRoot '..\..\redbamboo-packages\dotnet\desktop-process.ps1'
if (-not (Test-Path -LiteralPath $helper)) {
    throw "Deployment helper is missing: $helper"
}
. $helper

function Assert-Test {
    param([Parameter(Mandatory)][bool]$Condition, [Parameter(Mandatory)][string]$Message)
    if (-not $Condition) { throw $Message }
}

function Set-TestFile {
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][string]$Value)
    New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($Path)) -Force | Out-Null
    [IO.File]::WriteAllText($Path, $Value)
}

New-Item -ItemType Directory -Path $testPath -Force | Out-Null
try {
    Write-Host '=== mirror and manifest ==='
    $source = Join-Path $testPath 'mirror-source'
    $destination = Join-Path $testPath 'mirror-destination'
    Set-TestFile (Join-Path $source 'app.dll') 'new-app'
    Set-TestFile (Join-Path $source 'plugins\provider.dll') 'new-provider'
    Set-TestFile (Join-Path $destination 'stale.dll') 'stale'
    $sourceManifest = @(Get-RedBambooTreeManifest -Directory $source)
    Invoke-RedBambooMirrorTree -Source $source -Destination $destination | Out-Null
    Assert-RedBambooTreeManifest -Directory $destination -ExpectedManifest $sourceManifest | Out-Null
    Assert-Test -Condition (-not (Test-Path -LiteralPath (Join-Path $destination 'stale.dll'))) `
        -Message 'Mirror did not remove a stale destination file.'

    Write-Host '=== root working-directory handle ==='
    $heldLive = Join-Path $testPath 'held-live'
    $heldStage = Join-Path $testPath 'held-stage'
    $renamedLive = Join-Path $testPath 'held-live-renamed'
    Set-TestFile (Join-Path $heldLive 'app.dll') 'old'
    Set-TestFile (Join-Path $heldStage 'app.dll') 'new'
    $holder = Start-Process -FilePath powershell.exe -ArgumentList @(
        '-NoProfile', '-NonInteractive', '-Command', 'Start-Sleep -Seconds 30'
    ) -WorkingDirectory $heldLive -WindowStyle Hidden -PassThru
    try {
        Start-Sleep -Milliseconds 500
        $renameFailed = $false
        try { [IO.Directory]::Move($heldLive, $renamedLive) } catch { $renameFailed = $true }
        Assert-Test -Condition $renameFailed `
            -Message 'The scratch fixture did not reproduce the root-directory rename failure.'

        $heldManifest = @(Get-RedBambooTreeManifest -Directory $heldStage)
        Invoke-RedBambooMirrorTree -Source $heldStage -Destination $heldLive | Out-Null
        Assert-RedBambooTreeManifest -Directory $heldLive -ExpectedManifest $heldManifest | Out-Null
    } finally {
        Stop-Process -Id $holder.Id -Force -ErrorAction SilentlyContinue
        $holder.WaitForExit(5000) | Out-Null
    }

    Write-Host '=== locked-file rollback ==='
    $rollbackLive = Join-Path $testPath 'rollback-live'
    $rollbackStage = Join-Path $testPath 'rollback-stage'
    $rollbackBackup = Join-Path $testPath 'rollback-backup'
    Set-TestFile (Join-Path $rollbackLive 'a.dll') 'old-a'
    Set-TestFile (Join-Path $rollbackLive 'z.dll') 'old-z'
    # Different lengths make robocopy copy both files deterministically even when
    # NTFS timestamps land in the same comparison window on a fast test run.
    Set-TestFile (Join-Path $rollbackStage 'a.dll') 'new-a-expanded'
    Set-TestFile (Join-Path $rollbackStage 'z.dll') 'new-z-expanded'
    $oldManifest = @(Get-RedBambooTreeManifest -Directory $rollbackLive)
    Invoke-RedBambooMirrorTree -Source $rollbackLive -Destination $rollbackBackup | Out-Null
    Assert-RedBambooTreeManifest -Directory $rollbackBackup -ExpectedManifest $oldManifest | Out-Null

    $lockedFile = Join-Path $rollbackLive 'z.dll'
    $lock = [IO.File]::Open($lockedFile, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::None)
    $promotionFailed = $false
    try {
        try {
            Invoke-RedBambooMirrorTree -Source $rollbackStage -Destination $rollbackLive `
                -RetryCount 1 -RetryWaitSeconds 1 | Out-Null
        } catch {
            $promotionFailed = $true
        }
    } finally {
        $lock.Dispose()
    }
    Assert-Test -Condition $promotionFailed -Message 'A locked destination file did not fail promotion.'
    Invoke-RedBambooMirrorTree -Source $rollbackBackup -Destination $rollbackLive | Out-Null
    Assert-RedBambooTreeManifest -Directory $rollbackLive -ExpectedManifest $oldManifest | Out-Null

    Write-Host '=== manifest mismatch rejection ==='
    Set-TestFile (Join-Path $rollbackLive 'a.dll') 'corrupt'
    $mismatchRejected = $false
    try {
        Assert-RedBambooTreeManifest -Directory $rollbackLive -ExpectedManifest $oldManifest | Out-Null
    } catch {
        $mismatchRejected = $true
    }
    Assert-Test -Condition $mismatchRejected -Message 'Manifest verification accepted a corrupt file.'

    Write-Host '=== exclusive deployment lock ==='
    $deploymentLockPath = Join-Path $testPath 'deployment.lock'
    $firstLock = Enter-RedBambooDeploymentLock -Path $deploymentLockPath
    try {
        $secondLockRejected = $false
        try { $secondLock = Enter-RedBambooDeploymentLock -Path $deploymentLockPath } catch { $secondLockRejected = $true }
        if ($secondLock) { $secondLock.Dispose() }
        Assert-Test -Condition $secondLockRejected -Message 'A second deployment acquired the same lock.'
    } finally {
        $firstLock.Dispose()
    }

    Write-Host 'RedCompute deployment transaction scratch tests passed.' -ForegroundColor Green
} finally {
    if (Test-Path -LiteralPath $testPath) {
        Remove-Item -LiteralPath $testPath -Recurse -Force
    }
}
