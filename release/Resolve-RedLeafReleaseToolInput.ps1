[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $InputPath,

    [string] $GitHubOutput
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# The RedLeaf ReleaseTool commit is a build input the builder supplies (Release Manager resolves RedLeaf
# origin/main once per Nightly; the fallback workflow resolves main at dispatch). RedCompute commits none.
$inputDocument = Get-Content -LiteralPath $InputPath -Raw | ConvertFrom-Json
if ($inputDocument.PSObject.Properties['commit']) {
    throw 'The RedLeaf ReleaseTool input must not commit a RedLeaf commit; the builder supplies it.'
}
$expectedProperties = @('centralArtifactFileNameTemplate', 'repositoryUrl', 'schemaVersion')
$actualProperties = @($inputDocument.PSObject.Properties.Name | Sort-Object)
if ($actualProperties.Count -ne $expectedProperties.Count -or
    (Compare-Object -ReferenceObject $expectedProperties -DifferenceObject $actualProperties)) {
    throw 'The version-1 RedLeaf ReleaseTool input must contain only its three required properties.'
}
if ($inputDocument.schemaVersion -ne 1) {
    throw 'The RedLeaf ReleaseTool input schemaVersion must be 1.'
}
if ($inputDocument.repositoryUrl -ne 'https://github.com/RedBamboo-Interactive/redleaf') {
    throw 'The ReleaseTool source repository must be exactly RedBamboo-Interactive/redleaf.'
}
if ($inputDocument.centralArtifactFileNameTemplate -ne 'redcompute-win-x64-{artifactSha256}.zip') {
    throw 'The central RedCompute artifact filename contract is unsupported.'
}

if ([string]::IsNullOrWhiteSpace($GitHubOutput)) {
    Write-Output ([string]$inputDocument.centralArtifactFileNameTemplate)
    return
}

"repository_url=$($inputDocument.repositoryUrl)" | Out-File -LiteralPath $GitHubOutput -Encoding utf8 -Append
"artifact_name_template=$($inputDocument.centralArtifactFileNameTemplate)" | Out-File -LiteralPath $GitHubOutput -Encoding utf8 -Append
