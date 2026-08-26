# RedCompute is supervised by RedLeaf. A rebuild invoked from an Agent session is itself
# a descendant of RedCompute, so it must never stop Compute and then try to keep building:
# the supervisor correctly kills the whole process tree. This script builds an isolated
# Release stage first, then hands the short promote/restart transaction to a process owned
# by the interactive Windows desktop shell.

param(
    # Skip the RedLeaf supervisor handshake. Use only when RedLeaf is not supervising
    # the running Compute process.
    [switch]$NoKernel,

    # Promote the staged output but leave RedCompute stopped.
    [switch]$NoLaunch,

    # Build and verify an isolated stage without stopping or promoting anything. The
    # full RedLeaf rebuild uses this to prepare both services before one suite handoff.
    [switch]$StageOnly,

    # One-time bridge for installing the maintenance handoff into a runtime that predates it.
    # Future live rebuilds fail closed when the authenticated handoff is unavailable.
    [switch]$BootstrapMaintenance,

    # Optional exact destination for -StageOnly. It must remain under the generated
    # Release staging root.
    [string]$StageDirectory
)

$ErrorActionPreference = 'Stop'
$runStartedAt = Get-Date
$runId = $runStartedAt.ToString('yyyyMMdd-HHmmss-fff') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
$leafUrl = if ($env:REDLEAF_URL) { $env:REDLEAF_URL } else { 'http://127.0.0.1:18804' }
$releaseRoot = [IO.Path]::GetFullPath(
    (Join-Path $PSScriptRoot 'src\RedCompute.App\bin\Release'))
$liveDirectory = Join-Path $releaseRoot 'net9.0-windows'
$stageRoot = Join-Path $releaseRoot '.rebuild-staging'
$backupRoot = Join-Path $releaseRoot '.rebuild-backups'
$receiptRoot = Join-Path $releaseRoot '.rebuild-receipts'

if ($StageDirectory) {
    $stageDirectory = [IO.Path]::GetFullPath($StageDirectory)
} else {
    $stageDirectory = Join-Path (Join-Path $stageRoot $runId) 'net9.0-windows'
}
$stageRootPrefix = [IO.Path]::GetFullPath($stageRoot).TrimEnd('\') + '\'
if (-not $stageDirectory.StartsWith($stageRootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "StageDirectory must be under $stageRoot"
}

$stageRunDirectory = [IO.Path]::GetDirectoryName($stageDirectory)
$backupDirectory = Join-Path (Join-Path $backupRoot $runId) 'net9.0-windows'
$receiptPath = Join-Path $receiptRoot "redcompute-rebuild-$runId.json"
$logPath = Join-Path $receiptRoot "redcompute-rebuild-$runId.log"
$requestPath = Join-Path $receiptRoot "redcompute-rebuild-$runId.request.json"
$manifestPath = Join-Path $receiptRoot "redcompute-rebuild-$runId.manifest.json"
$artifactsDirectory = Join-Path $stageRunDirectory 'artifacts'

$desktopHelper = Join-Path $PSScriptRoot '..\redbamboo-packages\dotnet\desktop-process.ps1'
if (-not (Test-Path -LiteralPath $desktopHelper)) {
    throw "Desktop deployment helper is missing: $desktopHelper"
}
. $desktopHelper

New-Item -ItemType Directory -Path $stageRoot, $backupRoot, $receiptRoot -Force | Out-Null
if (Test-Path -LiteralPath $stageRunDirectory) {
    Remove-Item -LiteralPath $stageRunDirectory -Recurse -Force
}

Write-Host '=== Staging RedCompute Release output ===' -ForegroundColor Cyan
$appProject = Join-Path $PSScriptRoot 'src\RedCompute.App\RedCompute.App.csproj'
& dotnet build $appProject -c Release --nologo --artifacts-path $artifactsDirectory
if ($LASTEXITCODE -ne 0) {
    throw 'RedCompute Release staging build failed.'
}

$appOutputRoot = Join-Path $artifactsDirectory 'bin\RedCompute.App'
$appExecutables = @(Get-ChildItem -LiteralPath $appOutputRoot -Filter 'RedCompute.exe' `
    -File -Recurse -ErrorAction SilentlyContinue)
if ($appExecutables.Count -ne 1) {
    throw "Expected one isolated RedCompute application output, found $($appExecutables.Count) under $appOutputRoot."
}
$appOutputDirectory = $appExecutables[0].Directory.FullName
Invoke-RedBambooMirrorTree -Source $appOutputDirectory -Destination $stageDirectory | Out-Null

$stagedPluginDirectory = Join-Path $stageDirectory 'plugins'

foreach ($requiredFile in @('RedCompute.exe', 'RedCompute.dll', 'RedBamboo.AppHost.dll')) {
    if (-not (Test-Path -LiteralPath (Join-Path $stageDirectory $requiredFile))) {
        throw "RedCompute Release stage is incomplete: missing $requiredFile"
    }
}
$expectedPluginCount = @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'plugins') `
    -Filter 'RedCompute.Plugin.*.csproj' -File -Recurse).Count
$pluginCount = @(Get-ChildItem -LiteralPath $stagedPluginDirectory `
    -Filter 'RedCompute.Plugin.*.dll' -File -ErrorAction SilentlyContinue).Count
if ($pluginCount -ne $expectedPluginCount) {
    throw "RedCompute Release stage contains $pluginCount provider plugins; expected $expectedPluginCount."
}
$stageManifest = @(Get-RedBambooTreeManifest -Directory $stageDirectory)
$stageManifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $manifestPath -Encoding UTF8
Write-Host "  staged $pluginCount provider plugin(s) and $($stageManifest.Count) files at $stageDirectory" `
    -ForegroundColor DarkGray

if ($StageOnly) {
    [pscustomobject]@{
        runId = $runId
        stageDirectory = $stageDirectory
        manifestPath = $manifestPath
        fileCount = $stageManifest.Count
        redComputeDll = (Get-FileHash -LiteralPath (Join-Path $stageDirectory 'RedCompute.dll') -Algorithm SHA256).Hash
        appHostDll = (Get-FileHash -LiteralPath (Join-Path $stageDirectory 'RedBamboo.AppHost.dll') -Algorithm SHA256).Hash
    } | ConvertTo-Json -Depth 4
    Write-Host '=== STAGED ONLY: no process was stopped or restarted ===' -ForegroundColor Yellow
    exit 0
}

$request = [ordered]@{
    runId = $runId
    startedAt = $runStartedAt.ToString('o')
    leafUrl = $leafUrl
    liveDirectory = $liveDirectory
    stageDirectory = $stageDirectory
    backupDirectory = $backupDirectory
    manifestPath = $manifestPath
    receiptPath = $receiptPath
    logPath = $logPath
    noKernel = [bool]$NoKernel
    noLaunch = [bool]$NoLaunch
    hadComputeProcess = [bool](Get-Process -Name RedCompute -ErrorAction SilentlyContinue)
}
$request | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $requestPath -Encoding UTF8

$runningCompute = Get-Process -Name RedCompute -ErrorAction SilentlyContinue |
    Sort-Object StartTime -Descending | Select-Object -First 1
if ($runningCompute -and -not $BootstrapMaintenance) {
    if ([string]::IsNullOrWhiteSpace($env:REDLEAF_EXECUTION_TOKEN)) {
        throw 'A live RedCompute deployment requires REDLEAF_EXECUTION_TOKEN so the runtime can drain sessions safely.'
    }

    Write-Host '=== Arming authenticated RedCompute drain and desktop handoff ===' -ForegroundColor Cyan
    $headers = @{ Authorization = "Bearer $($env:REDLEAF_EXECUTION_TOKEN)" }
    $body = @{ requestPath = $requestPath } | ConvertTo-Json -Compress
    try {
        $admission = Invoke-RestMethod -Method Post `
            -Uri 'http://127.0.0.1:18800/maintenance/deploy-staged' `
            -Headers $headers -Body $body -ContentType 'application/json' -TimeoutSec 30
    } finally {
        $headers.Clear()
        $body = $null
    }
    if (-not $admission -or -not $admission.accepted) {
        throw 'RedCompute did not accept the staged deployment handoff.'
    }
    [pscustomobject]@{
        runId = $runId
        state = $admission.state
        requestPath = $requestPath
        receiptPath = $receiptPath
        message = 'Deployment armed. Active turns will finish before RedCompute restarts.'
    } | ConvertTo-Json -Depth 4
    Write-Host '=== ARMED: this caller may finish before the planned restart ===' -ForegroundColor Yellow
    exit 0
}

$powerShellExe = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
$deployScript = Join-Path $PSScriptRoot 'deploy-staged.ps1'
Write-Host '=== Handing staged deployment to the Windows desktop ===' -ForegroundColor Cyan
$deployerProcessId = Start-RedBambooDesktopProcess -FilePath $powerShellExe -ArgumentList @(
    '-NoProfile',
    '-NonInteractive',
    '-ExecutionPolicy',
    'Bypass',
    '-File',
    $deployScript,
    '-RequestPath',
    $requestPath
) -WorkingDirectory $PSScriptRoot
Write-Host "  desktop-owned deployer PID $deployerProcessId" -ForegroundColor DarkGray
Write-Host "  receipt: $receiptPath" -ForegroundColor DarkGray

# A RedCompute-hosted caller will disappear when the deployer performs the stop. That
# is expected. A normal console caller remains here and receives the terminal receipt.
$deadline = (Get-Date).AddMinutes(5)
while ((Get-Date) -lt $deadline) {
    if (Test-Path -LiteralPath $receiptPath) {
        try {
            $receipt = Get-Content -LiteralPath $receiptPath -Raw | ConvertFrom-Json
            if ($receipt.state -eq 'succeeded') {
                Write-Host '=== RedCompute deployed and verified ===' -ForegroundColor Green
                Get-Content -LiteralPath $receiptPath -Raw
                exit 0
            }
            if ($receipt.state -eq 'failed') {
                throw "RedCompute deployment failed: $($receipt.error). Log: $($receipt.logPath)"
            }
        } catch {
            if ($_.Exception.Message -like 'RedCompute deployment failed:*') { throw }
        }
    }
    Start-Sleep -Milliseconds 250
}
throw "Timed out waiting for RedCompute deployment receipt: $receiptPath"
