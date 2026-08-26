param(
    [Parameter(Mandatory)][string]$RequestPath
)

$ErrorActionPreference = 'Stop'
$deploymentHelper = Join-Path $PSScriptRoot '..\redbamboo-packages\dotnet\desktop-process.ps1'
if (-not (Test-Path -LiteralPath $deploymentHelper)) {
    throw "Desktop deployment helper is missing: $deploymentHelper"
}
. $deploymentHelper

function Write-DeploymentReceipt {
    param(
        [Parameter(Mandatory)][string]$State,
        [Parameter(Mandatory)][string]$Phase,
        [Parameter(Mandatory)][bool]$Success,
        [string]$ErrorMessage,
        [Nullable[bool]]$RollbackSucceeded,
        [object]$Process,
        [object[]]$Artifacts = @()
    )

    $parent = Get-CimInstance Win32_Process -Filter "ProcessId = $PID" -ErrorAction SilentlyContinue
    $parentProcess = if ($parent) {
        Get-Process -Id $parent.ParentProcessId -ErrorAction SilentlyContinue
    } else { $null }
    $receipt = [ordered]@{
        runId = $request.runId
        state = $State
        phase = $Phase
        success = $Success
        rollbackSucceeded = $RollbackSucceeded
        startedAt = $request.startedAt
        updatedAt = (Get-Date).ToString('o')
        deployerProcessId = $PID
        deployerParentProcessId = if ($parent) { $parent.ParentProcessId } else { $null }
        deployerParentName = if ($parentProcess) { $parentProcess.ProcessName } else { $null }
        process = if ($Process) { [ordered]@{
            id = $Process.Id
            startTime = $Process.StartTime.ToString('o')
            path = $Process.Path
        } } else { $null }
        artifacts = @($Artifacts)
        error = $ErrorMessage
        logPath = $request.logPath
    }
    $temporaryReceipt = "$($request.receiptPath).writing-$PID"
    $receipt | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $temporaryReceipt -Encoding UTF8
    Move-Item -LiteralPath $temporaryReceipt -Destination $request.receiptPath -Force
}

function Invoke-Kernel {
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][string]$Body)

    try {
        Invoke-RestMethod -Method Post -Uri "$($request.leafUrl)$Path" -Body $Body `
            -ContentType 'application/json' -TimeoutSec 30
    } catch {
        throw "RedLeaf call $Path failed: $($_.Exception.Message)"
    }
}

function Wait-ForComputeExit {
    $deadline = (Get-Date).AddSeconds(30)
    while ((Get-Date) -lt $deadline) {
        $process = Get-Process -Name RedCompute -ErrorAction SilentlyContinue
        $listener = Get-NetTCPConnection -LocalPort 18800 -State Listen -ErrorAction SilentlyContinue
        if (-not $process -and -not $listener) { return }
        Start-Sleep -Milliseconds 250
    }
    throw 'RedCompute did not stop cleanly within 30 seconds.'
}

function Wait-ForComputeHealth {
    $deadline = (Get-Date).AddSeconds(120)
    $lastProblem = 'process not found'
    while ((Get-Date) -lt $deadline) {
        $process = Get-Process -Name RedCompute -ErrorAction SilentlyContinue |
            Sort-Object StartTime -Descending | Select-Object -First 1
        if ($process) {
            try {
                $response = Invoke-WebRequest -UseBasicParsing -Uri 'http://127.0.0.1:18800/ping' -TimeoutSec 3
                if ($response.StatusCode -eq 200) { return $process }
                $lastProblem = "HTTP $($response.StatusCode)"
            } catch {
                $lastProblem = $_.Exception.Message
            }
        }
        Start-Sleep -Milliseconds 500
    }
    throw "RedCompute did not become healthy: $lastProblem"
}

function Stop-Compute {
    if ($serviceReleased) {
        try { Invoke-Kernel -Path '/api/setup/compute/stop' -Body '{"force":true}' | Out-Null } catch { }
    } else {
        Get-Process -Name RedCompute -ErrorAction SilentlyContinue |
            Stop-Process -Force -ErrorAction SilentlyContinue
    }
    try { Wait-ForComputeExit } catch { }
}

function Start-Compute {
    if ($serviceReleased) {
        $start = Invoke-Kernel -Path '/api/setup/compute/start' -Body '{}'
        if (-not $start -or -not $start.ok) {
            $errorText = if ($start -and $start.error) { $start.error } else { 'no successful response' }
            throw "RedLeaf refused to start RedCompute: $errorText"
        }
    } else {
        Start-Process -FilePath (Join-Path $liveDirectory 'RedCompute.exe') `
            -ArgumentList '--port 18800', '--redleaf-url http://127.0.0.1:18804' -WindowStyle Hidden
    }
}

function Get-ArtifactReceipt {
    param([Parameter(Mandatory)][string]$Path)

    $item = Get-Item -LiteralPath $Path
    [ordered]@{
        path = $item.FullName
        lastWriteTime = $item.LastWriteTime.ToString('o')
        sha256 = (Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash
    }
}

$resolvedRequestPath = (Resolve-Path -LiteralPath $RequestPath).Path
$request = Get-Content -LiteralPath $resolvedRequestPath -Raw | ConvertFrom-Json
$expectedLiveDirectory = [IO.Path]::GetFullPath(
    (Join-Path $PSScriptRoot 'src\RedCompute.App\bin\Release\net9.0-windows'))
$releaseRoot = [IO.Path]::GetDirectoryName($expectedLiveDirectory)
$stageRoot = [IO.Path]::GetFullPath((Join-Path $releaseRoot '.rebuild-staging'))
$backupRoot = [IO.Path]::GetFullPath((Join-Path $releaseRoot '.rebuild-backups'))
$receiptRoot = [IO.Path]::GetFullPath((Join-Path $releaseRoot '.rebuild-receipts'))
$liveDirectory = [IO.Path]::GetFullPath([string]$request.liveDirectory)
$stageDirectory = [IO.Path]::GetFullPath([string]$request.stageDirectory)
$backupDirectory = [IO.Path]::GetFullPath([string]$request.backupDirectory)
$manifestPath = [IO.Path]::GetFullPath([string]$request.manifestPath)

if ($liveDirectory -ne $expectedLiveDirectory) {
    throw "Refusing unexpected RedCompute live directory: $liveDirectory"
}
if (-not $stageDirectory.StartsWith($stageRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing unexpected RedCompute staging directory: $stageDirectory"
}
if (-not $backupDirectory.StartsWith($backupRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing unexpected RedCompute backup directory: $backupDirectory"
}
if (-not $manifestPath.StartsWith($receiptRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing unexpected RedCompute manifest path: $manifestPath"
}
foreach ($requiredFile in @('RedCompute.exe', 'RedCompute.dll', 'RedBamboo.AppHost.dll')) {
    if (-not (Test-Path -LiteralPath (Join-Path $stageDirectory $requiredFile))) {
        throw "Staged RedCompute output is incomplete: missing $requiredFile"
    }
}
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
    throw "Staged RedCompute manifest is missing: $manifestPath"
}

$transcriptStarted = $false
$serviceReleased = $false
$computeStopped = $false
$backupVerified = $false
$liveMutationStarted = $false
$rollbackSucceeded = $null
$hadComputeProcess = [bool]$request.hadComputeProcess
$oldManifest = @()
$lockStream = $null
try {
    $lockStream = Enter-RedBambooDeploymentLock -Path (Join-Path $releaseRoot '.rebuild-deploy.lock')
    Start-Transcript -Path $request.logPath -Force | Out-Null
    $transcriptStarted = $true

    # Windows PowerShell 5.1 returns a JSON array as one Object[] pipeline item.
    # Enumerate it explicitly so the manifest remains a flat list of file records.
    $parsedManifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    $stageManifest = @()
    foreach ($manifestEntry in $parsedManifest) { $stageManifest += $manifestEntry }
    Assert-RedBambooTreeManifest -Directory $stageDirectory -ExpectedManifest $stageManifest | Out-Null
    Write-DeploymentReceipt -State 'accepted' -Phase 'preflight-verified' -Success $false

    if (-not [bool]$request.noKernel) {
        Write-Host '=== Asking RedLeaf to release RedCompute ===' -ForegroundColor Cyan
        $stop = Invoke-Kernel -Path '/api/setup/compute/stop' -Body '{"force":true}'
        if (-not $stop -or -not $stop.ok) {
            $errorText = if ($stop -and $stop.error) { $stop.error } else { 'no successful response' }
            throw "RedLeaf refused to release RedCompute: $errorText"
        }
        $serviceReleased = $true
    } else {
        Get-Process -Name RedCompute -ErrorAction SilentlyContinue |
            Stop-Process -Force -ErrorAction SilentlyContinue
    }
    Wait-ForComputeExit
    $computeStopped = $true
    Write-DeploymentReceipt -State 'accepted' -Phase 'compute-stopped' -Success $false

    if (-not (Test-Path -LiteralPath $liveDirectory -PathType Container)) {
        throw "Live RedCompute directory is missing: $liveDirectory"
    }
    if (Test-Path -LiteralPath $backupDirectory) {
        throw "Backup directory already exists: $backupDirectory"
    }

    Write-Host '=== Backing up current RedCompute output ===' -ForegroundColor Cyan
    $oldManifest = @(Get-RedBambooTreeManifest -Directory $liveDirectory)
    Invoke-RedBambooMirrorTree -Source $liveDirectory -Destination $backupDirectory | Out-Null
    Assert-RedBambooTreeManifest -Directory $backupDirectory -ExpectedManifest $oldManifest | Out-Null
    $backupVerified = $true
    Write-DeploymentReceipt -State 'accepted' -Phase 'backup-verified' -Success $false

    Write-Host '=== Mirroring staged RedCompute output ===' -ForegroundColor Cyan
    $liveMutationStarted = $true
    Invoke-RedBambooMirrorTree -Source $stageDirectory -Destination $liveDirectory | Out-Null
    Assert-RedBambooTreeManifest -Directory $liveDirectory -ExpectedManifest $stageManifest | Out-Null
    Write-DeploymentReceipt -State 'accepted' -Phase 'candidate-verified' -Success $false

    $process = $null
    if (-not [bool]$request.noLaunch) {
        Write-Host '=== Starting RedCompute ===' -ForegroundColor Cyan
        Start-Compute
        $process = Wait-ForComputeHealth
        if ([IO.Path]::GetFullPath($process.Path) -ne [IO.Path]::GetFullPath((Join-Path $liveDirectory 'RedCompute.exe'))) {
            throw "RedCompute started from the wrong path: $($process.Path)"
        }
    }

    $artifacts = @(
        Get-ArtifactReceipt (Join-Path $liveDirectory 'RedCompute.dll')
        Get-ArtifactReceipt (Join-Path $liveDirectory 'RedBamboo.AppHost.dll')
    )
    Write-DeploymentReceipt -State 'succeeded' -Phase 'candidate-healthy' -Success $true `
        -Process $process -Artifacts $artifacts
    if (Test-Path -LiteralPath $backupDirectory) {
        try {
            Remove-Item -LiteralPath $backupDirectory -Recurse -Force
        } catch {
            Write-Host "  WARNING: verified backup cleanup failed: $($_.Exception.Message)" `
                -ForegroundColor DarkYellow
        }
    }
    exit 0
} catch {
    $failure = $_.Exception.Message
    Write-Host "RedCompute deployment failed: $failure" -ForegroundColor Red

    if ($liveMutationStarted) {
        Stop-Compute
        if ($backupVerified -and (Test-Path -LiteralPath $backupDirectory -PathType Container)) {
            try {
                Write-Host '=== Restoring previous RedCompute output ===' -ForegroundColor DarkYellow
                Invoke-RedBambooMirrorTree -Source $backupDirectory -Destination $liveDirectory | Out-Null
                Assert-RedBambooTreeManifest -Directory $liveDirectory -ExpectedManifest $oldManifest | Out-Null
                $rollbackSucceeded = $true
            } catch {
                $rollbackSucceeded = $false
                $failure = "$failure; rollback failed: $($_.Exception.Message)"
            }
        } else {
            $rollbackSucceeded = $false
            $failure = "$failure; rollback was unavailable because no verified backup exists"
        }
    } else {
        $rollbackSucceeded = $true
    }

    if ($computeStopped -and $hadComputeProcess -and -not [bool]$request.noLaunch -and $rollbackSucceeded) {
        try {
            Start-Compute
            $restoredProcess = Wait-ForComputeHealth
            if ([IO.Path]::GetFullPath($restoredProcess.Path) -ne `
                [IO.Path]::GetFullPath((Join-Path $liveDirectory 'RedCompute.exe'))) {
                throw "Restored RedCompute started from the wrong path: $($restoredProcess.Path)"
            }
        } catch {
            $rollbackSucceeded = $false
            $failure = "$failure; previous RedCompute could not be restored: $($_.Exception.Message)"
        }
    }

    Write-DeploymentReceipt -State 'failed' -Phase 'failed' -Success $false `
        -ErrorMessage $failure -RollbackSucceeded $rollbackSucceeded
    exit 1
} finally {
    if ($transcriptStarted) {
        try { Stop-Transcript | Out-Null } catch { }
    }
    if ($lockStream) {
        $lockStream.Dispose()
    }
}
