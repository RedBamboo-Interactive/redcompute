& "$PSScriptRoot\..\redbamboo-packages\dotnet\rebuild.ps1" `
    -AppName RedCompute `
    -Port 18800 `
    -PackageManager npm `
    -FrontendDir "$PSScriptRoot\web" `
    -BuildTarget "$PSScriptRoot\RedCompute.sln" `
    -ExePath "$PSScriptRoot\src\RedCompute.App\bin\Release\net9.0-windows\RedCompute.exe" `
    -ExtraKill 'wsl -d Ubuntu-24.04 -- pkill -f "uvicorn|server\.py" 2>$null' `
    @args
