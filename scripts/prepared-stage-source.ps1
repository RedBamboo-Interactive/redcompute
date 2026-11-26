function Get-RedComputePreparedSource {
    param([Parameter(Mandatory)][string]$RepositoryRoot)

    $status = @(git --no-optional-locks -C $RepositoryRoot status --porcelain=v2 --branch --untracked-files=normal)
    if ($LASTEXITCODE -ne 0 -or $status.Count -eq 0 -or $status[0] -cnotmatch '^# branch\.oid ([0-9a-f]{40}|[0-9a-f]{64})$') {
        throw 'Cannot determine the canonical RedCompute checkout for the prepared stage.'
    }
    $commit = $Matches[1]
    if ($status | Where-Object { -not $_.StartsWith('# ', [StringComparison]::Ordinal) }) {
        throw 'A prepared RedCompute stage requires a clean canonical checkout.'
    }
    return $commit
}
