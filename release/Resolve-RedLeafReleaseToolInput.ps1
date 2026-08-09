[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $InputPath,

    [string] $GitHubOutput
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$inputDocument = Get-Content -LiteralPath $InputPath -Raw | ConvertFrom-Json
$expectedProperties = @('centralArtifactFileNameTemplate', 'commit', 'repositoryUrl', 'schemaVersion')
$actualProperties = @($inputDocument.PSObject.Properties.Name | Sort-Object)
if ($actualProperties.Count -ne $expectedProperties.Count -or
    (Compare-Object -ReferenceObject $expectedProperties -DifferenceObject $actualProperties)) {
    throw 'The version-1 RedLeaf ReleaseTool input must contain only its four required properties.'
}
if ($inputDocument.schemaVersion -ne 1) {
    throw 'The RedLeaf ReleaseTool input schemaVersion must be 1.'
}
if ($inputDocument.repositoryUrl -ne 'https://github.com/RedBamboo-Interactive/redleaf') {
    throw 'The ReleaseTool and central artifact repository must be exactly RedBamboo-Interactive/redleaf.'
}
if ([string]$inputDocument.commit -notmatch '^[a-f0-9]{40}$') {
    throw 'The audited RedLeaf ReleaseTool commit is unresolved; replace the fail-closed placeholder with one exact lowercase commit SHA.'
}
if ($inputDocument.centralArtifactFileNameTemplate -ne 'redcompute-win-x64-{artifactSha256}.zip') {
    throw 'The central RedCompute artifact filename contract is unsupported.'
}

if ([string]::IsNullOrWhiteSpace($GitHubOutput)) {
    Write-Output ([string]$inputDocument.commit)
    return
}

"commit=$($inputDocument.commit)" | Out-File -LiteralPath $GitHubOutput -Encoding utf8 -Append
"repository_url=$($inputDocument.repositoryUrl)" | Out-File -LiteralPath $GitHubOutput -Encoding utf8 -Append
"artifact_name_template=$($inputDocument.centralArtifactFileNameTemplate)" | Out-File -LiteralPath $GitHubOutput -Encoding utf8 -Append
