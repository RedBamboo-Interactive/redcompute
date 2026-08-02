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
    [switch]$NoKernel,

    # Build the Release output but leave launching to the caller. RedLeaf's full rebuild
    # uses this while it replaces the kernel that will own Compute afterwards.
    [switch]$NoLaunch
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
$hadComputeProcess = [bool](Get-Process RedCompute -ErrorAction SilentlyContinue)
if (-not $NoKernel) {
    Write-Host "=== Asking the kernel to release RedCompute ===" -ForegroundColor Cyan
    # force:true because the kernel refuses while jobs are running, and a rebuild is an
    # explicit "I want it down now".
    $stop = Invoke-Kernel '/api/setup/compute/stop' '{"force":true}'
    if ($stop -and $stop.ok) {
        $handedOff = $true
        Write-Host "  released; the kernel will not restart it until we ask" -ForegroundColor DarkGray
    } elseif ($stop) {
        Write-Host "  kernel is not supervising this instance: $($stop.error)" -ForegroundColor DarkYellow
        Write-Host "  rebuild has explicit authority and will stop RedCompute directly" -ForegroundColor Cyan
    } else {
        Write-Host "  kernel did not answer; rebuild will take direct authority if Compute stays down" -ForegroundColor DarkYellow
    }

    # A managed release is asynchronous, so wait for it. An external instance will not
    # react to the kernel call; direct authority below stops it immediately instead of
    # pointlessly waiting twenty seconds for a port that cannot close on its own.
    if ($handedOff) {
        $deadline = (Get-Date).AddSeconds(20)
        while ((Get-Date) -lt $deadline) {
            if (-not (Get-NetTCPConnection -LocalPort 18800 -State Listen -ErrorAction SilentlyContinue)) { break }
            Start-Sleep -Milliseconds 250
        }
    }

    # Staying down is the thing that matters, not going down. If the kernel is unhealthy
    # its release can fail while still supervising, and it resurrects Compute ~2s later --
    # mid-build, holding the DLLs we are about to copy over. Observed 2026-07-31: the
    # kernel was throwing "Cannot access a disposed object" and the release silently
    # no-opped.
    #
    # Fail here rather than build. A stale deploy that looks successful costs far more
    # than a refused one: the suite comes back up healthy on old code, and the next hour
    # goes on wondering why the fix did not take.
    Get-Process RedCompute -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 4
    if (Get-Process RedCompute -ErrorAction SilentlyContinue) {
        throw @"
RedCompute came back while we were trying to build it -- the kernel is still supervising it.

Anything built now would fail to copy into bin\...\plugins\ and you would end up running
stale code that looks freshly deployed.

Fix: restart RedLeaf (its tray icon -> Restart), then run this again. If it keeps happening,
check the kernel log for 'Cannot access a disposed object' -- a half-disposed kernel cannot
release its managed children.
"@
    }
}

# When the kernel owns the lifecycle, never let the shared script launch the exe itself:
# a second instance would be adopted and we would be debugging a process nobody rebuilt.
#
# Use named hashtable splatting here. Array splatting passes strings positionally in
# Windows PowerShell 5.1, so the old @('-NoLaunch') forwarding silently bound the value
# to an earlier parameter and the shared rebuild script launched Compute anyway. During
# a full Leaf rebuild that started Compute several seconds before the kernel/API.
$sharedArgs = @{
    AppName      = 'RedCompute'
    Port         = 18800
    SkipFrontend = $true
    FrontendDir  = $PSScriptRoot
    BuildTarget  = "$PSScriptRoot\RedCompute.sln"
    ExePath      = "$PSScriptRoot\src\RedCompute.App\bin\Release\net9.0-windows\RedCompute.exe"
    ExtraKill    = 'wsl -d Ubuntu-24.04 -- pkill -f "uvicorn|server\.py" 2>$null'
    NoLaunch     = [bool]($handedOff -or $NoLaunch)
}
$buildSucceeded = $false

try {
    & "$PSScriptRoot\..\redbamboo-packages\dotnet\rebuild.ps1" @sharedArgs
    if ($LASTEXITCODE -ne 0) { throw "RedCompute Release build failed" }
    if ($sharedArgs.NoLaunch -and (Get-Process RedCompute -ErrorAction SilentlyContinue)) {
        throw "RedCompute was launched despite the -NoLaunch deployment contract"
    }
    $buildSucceeded = $true
} finally {
    # Always hand it back, including when the build failed — leaving the suite without
    # Compute is worse than leaving it on the previous binaries.
    if ($handedOff) {
        Write-Host "=== Handing RedCompute back to the kernel ===" -ForegroundColor Cyan
        Invoke-Kernel '/api/setup/compute/start' '{}' | Out-Null
    } elseif ($NoLaunch -and -not $buildSucceeded -and $hadComputeProcess -and
        -not (Get-Process RedCompute -ErrorAction SilentlyContinue)) {
        # The outer RedLeaf rebuild never gets to its kernel restart when this build
        # fails. Restore the previous service instead of turning a failed build into a
        # Compute outage. Its binaries may be old, but the failure receipt will say so.
        $exe = "$PSScriptRoot\src\RedCompute.App\bin\Release\net9.0-windows\RedCompute.exe"
        if (Test-Path $exe) {
            Write-Host "=== Build failed; restoring the previous RedCompute service ===" -ForegroundColor DarkYellow
            Start-Process -FilePath $exe -ArgumentList '--port 18800', '--redleaf-url http://127.0.0.1:18804' -WindowStyle Hidden
        }
    }
}
