@echo off
echo === Stopping RedCompute ===
taskkill /IM RedCompute.exe /F 2>nul
timeout /t 1 /nobreak >nul

echo === Building frontend ===
pushd web
call npm run build
if errorlevel 1 (
    echo FRONTEND BUILD FAILED
    popd
    exit /b 1
)
popd

echo === Building backend ===
dotnet build src\RedCompute.App\RedCompute.App.csproj -c Release
if errorlevel 1 (
    echo BACKEND BUILD FAILED
    exit /b 1
)

echo === Starting RedCompute ===
start "" "src\RedCompute.App\bin\Release\net9.0-windows\RedCompute.exe"
echo === Done ===
