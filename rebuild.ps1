# Compute is a headless child of the Leaf kernel: no frontend (the dashboard is the
# compute-dashboard Leaf plugin) and normally no manual launch — the kernel spawns it.
# Use this to rebuild the exe the kernel picks up; pass -NoLaunch to leave starting to
# the kernel (recommended), or let it launch for standalone debugging (the kernel adopts
# an already-running instance instead of spawning a second one).
& "$PSScriptRoot\..\redbamboo-packages\dotnet\rebuild.ps1" `
    -AppName RedCompute `
    -Port 18800 `
    -SkipFrontend `
    -FrontendDir "$PSScriptRoot" `
    -BuildTarget "$PSScriptRoot\RedCompute.sln" `
    -ExePath "$PSScriptRoot\src\RedCompute.App\bin\Release\net9.0-windows\RedCompute.exe" `
    -ExtraKill 'wsl -d Ubuntu-24.04 -- pkill -f "uvicorn|server\.py" 2>$null' `
    @args
