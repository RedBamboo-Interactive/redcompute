param(
    [Parameter(Mandatory)][string]$RequestPath
)

$ErrorActionPreference = 'Stop'
$desktopHelper = Join-Path $PSScriptRoot '..\redbamboo-packages\dotnet\desktop-process.ps1'
if (-not (Test-Path -LiteralPath $desktopHelper)) {
    throw "Desktop deployment helper is missing: $desktopHelper"
}
. $desktopHelper

$powerShellExe = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
$deployScript = Join-Path $PSScriptRoot 'deploy-staged.ps1'
$deployerProcessId = Start-RedBambooDesktopProcess -FilePath $powerShellExe -ArgumentList @(
    '-NoProfile',
    '-NonInteractive',
    '-ExecutionPolicy',
    'Bypass',
    '-File',
    $deployScript,
    '-RequestPath',
    $RequestPath
) -WorkingDirectory $PSScriptRoot

[pscustomobject]@{
    accepted = $true
    deployerProcessId = $deployerProcessId
    requestPath = $RequestPath
} | ConvertTo-Json -Depth 3
