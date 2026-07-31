# Compute is a headless child of the Leaf kernel: no frontend (the dashboard is the
# compute-dashboard Leaf plugin) and normally no manual launch — the kernel spawns it.
# Use this to rebuild the exe the kernel picks up; pass -NoLaunch to leave starting to
# the kernel (recommended), or let it launch for standalone debugging (the kernel adopts
# an already-running instance instead of spawning a second one).
#
# ── Why this script talks to the kernel before building ────────────────────────────────
# The kernel supervises Compute with RestartPolicy.OnCrash. A force-kill *is* a crash, so
# killing the process ourselves gets it resurrected ~2s later — reliably in the middle of
# the build — and the copy of the freshly built DLLs into bin\...\plugins\ then fails with
# "MSB3027: file is locked by RedCompute". Observed 2026-07-31:
#
#   21:27:14  Managed process 'kernel:compute' exited, restarting in 2s (attempt 1/5)
#   21:27:16  Managed process 'kernel:compute' started (PID 72360)
#   21:27:17  plugin finishes compiling -> copy hits a locked file
#
# The port-wait loop in the shared script does not save us: the port frees, the loop
# breaks, and only *then* does the supervisor respawn. So we ask the kernel to stop
# supervising first (StopAsync removes it from tracking before killing, so no restart is
# scheduled), and hand it back afterwards.

param(
    # Skip the kernel handshake and behave like the old script — for when the kernel is
    # not the thing running Compute.
    [switch]$NoKernel
)

$ErrorActionPreference = "Stop"
$LeafUrl = if ($env:REDLEAF_URL) { $env:REDLEAF_URL } else { "http://127.0.0.1:18804" }

function Invoke-Kernel($Path, $Body) {
    try {
        Invoke-RestMethod -Method Post -Uri "$LeafUrl$Path" -Body $Body `
            -ContentType 'application/json' -TimeoutSec 30
    } catch {
        Write-Host "  kernel call $Path failed: $($_.Exception.Message)" -ForegroundColor DarkYellow
        $null
    }
}

$handedOff = $false
if (-not $NoKernel) {
    Write-Host "=== Asking the kernel to release RedCompute ===" -ForegroundColor Cyan
    # force:true because the kernel refuses while jobs are running, and a rebuild is an
    # explicit "I want it down now".
    $stop = Invoke-Kernel '/api/setup/compute/stop' '{"force":true}'
    if ($stop -and $stop.ok) {
        $handedOff = $true
        Write-Host "  released; the kernel will not restart it until we ask" -ForegroundColor DarkGray
    } elseif ($stop) {
        Write-Host "  kernel declined: $($stop.error)" -ForegroundColor DarkYellow
    }

    # Wait for the port to actually go quiet before the shared script starts building.
    $deadline = (Get-Date).AddSeconds(20)
    while ((Get-Date) -lt $deadline) {
        if (-not (Get-NetTCPConnection -LocalPort 18800 -State Listen -ErrorAction SilentlyContinue)) { break }
        Start-Sleep -Milliseconds 250
    }
}

# When the kernel owns the lifecycle, never let the shared script launch the exe itself:
# a second instance would be adopted and we would be debugging a process nobody rebuilt.
$forwarded = $args
if ($handedOff -and $forwarded -notcontains '-NoLaunch') { $forwarded += '-NoLaunch' }

try {
    & "$PSScriptRoot\..\redbamboo-packages\dotnet\rebuild.ps1" `
        -AppName RedCompute `
        -Port 18800 `
        -SkipFrontend `
        -FrontendDir "$PSScriptRoot" `
        -BuildTarget "$PSScriptRoot\RedCompute.sln" `
        -ExePath "$PSScriptRoot\src\RedCompute.App\bin\Release\net9.0-windows\RedCompute.exe" `
        -ExtraKill 'wsl -d Ubuntu-24.04 -- pkill -f "uvicorn|server\.py" 2>$null' `
        @forwarded
} finally {
    # Always hand it back, including when the build failed — leaving the suite without
    # Compute is worse than leaving it on the previous binaries.
    if ($handedOff) {
        Write-Host "=== Handing RedCompute back to the kernel ===" -ForegroundColor Cyan
        Invoke-Kernel '/api/setup/compute/start' '{}' | Out-Null
    }
}
