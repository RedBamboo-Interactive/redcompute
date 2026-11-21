# Leaf node for Windows: the installer.
#
#   irm https://github.com/RedBamboo-Interactive/redleaf-releases/releases/download/<release>/leaf-node-install.ps1 | iex; Install-LeafNode
#
# Then pair the node with the code Leaf shows in Compute > Nodes > Add a node (a separate step):
#
#   & (Get-LeafNode) pair --server https://<leaf address> --code ABCD-EFGH
#
# Install-LeafNode reads the signed release feed's current RedCompute record (pointer, then record, whose
# SHA-256 the pointer names), takes leaf-node-manifest.v1.json from the same release, checks that it
# names that record's version and source commit, downloads leaf-node-win-x64.zip and checks that it has
# exactly the size and SHA-256 the manifest names. Only then does it run `leaf-node install`, which puts
# the agent in %LOCALAPPDATA%\Programs\LeafNode\<version>, starts it at logon (the HKCU Run value) and
# lists it in Apps & features. Running it again upgrades: the agent stops, the new version starts, the
# previous one is kept for rollback. Uninstall-LeafNode removes all of it.
#
# Nothing here needs administrator rights, and it refuses to run elevated. It never writes a key, a
# token or a password: pairing (`leaf-node pair`) is the only step that makes the node's key.
#
# Environment (for testing a release before it is published; leave unset otherwise):
#   LEAF_NODE_FEED_URL  another release feed (https://, or file:// for a local dry run)
#   LEAF_NODE_CHANNEL   nightly (default) or stable
#   LEAF_NODE_HOME      an isolated agent in this folder: its files there, no Run value, no Apps entry

function Get-LeafNode {
    <# The installed leaf-node.exe (from current.json), or $null when none is installed. #>
    $programs = if ($env:LEAF_NODE_HOME) { Join-Path $env:LEAF_NODE_HOME 'programs' }
                else { Join-Path $env:LOCALAPPDATA 'Programs\LeafNode' }
    $current = Join-Path $programs 'current.json'
    if (-not (Test-Path -LiteralPath $current -PathType Leaf)) { return $null }
    $path = (Get-Content -LiteralPath $current -Raw | ConvertFrom-Json).path
    $exe = Join-Path $path 'leaf-node.exe'
    if (Test-Path -LiteralPath $exe -PathType Leaf) { return $exe }
    return $null
}

function Install-LeafNode {
    [CmdletBinding()]
    param()
    $saved = $ProgressPreference
    $ProgressPreference = 'SilentlyContinue'
    $work = Join-Path ([IO.Path]::GetTempPath()) ('leaf-node-install-' + [Guid]::NewGuid().ToString('N'))
    try {
        Assert-LeafNodeUser
        New-Item -ItemType Directory -Path $work | Out-Null
        $release = Resolve-LeafNodeRelease $work
        $entry = @($release.Manifest.artifacts | Where-Object { $_.rid -ceq 'win-x64' })
        if ($entry.Count -ne 1 -or $entry[0].name -cne 'leaf-node-win-x64.zip') { throw 'the leaf-node manifest has no win-x64 archive.' }
        Write-LeafNode "downloading leaf-node $($release.Manifest.version)..."
        $zip = Join-Path $work 'leaf-node-win-x64.zip'
        Save-LeafNodeUrl ($release.Folder + '/leaf-node-win-x64.zip') $zip
        if ((Get-Item -LiteralPath $zip).Length -ne [long]$entry[0].sizeBytes -or (Get-LeafNodeHash $zip) -cne [string]$entry[0].sha256) {
            throw 'the download does not match the release manifest; nothing was installed.'
        }
        $folder = Join-Path $work 'leaf-node'
        Expand-Archive -LiteralPath $zip -DestinationPath $folder
        $exe = Join-Path $folder 'leaf-node.exe'
        if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw 'the archive has no leaf-node.exe.' }
        Write-LeafNode 'installing (an installed leaf-node is stopped and upgraded)...'
        Invoke-LeafNodeExe $exe @('install')
        $installed = Get-LeafNode
        Write-LeafNode "done. Pair this machine with the code from Leaf (Compute > Nodes > Add a node):"
        Write-LeafNode "  & `"$installed`" pair --server https://<leaf address> --code <code>"
    }
    finally {
        $ProgressPreference = $saved
        if (Test-Path -LiteralPath $work) { Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue }
    }
}

function Uninstall-LeafNode {
    <# Unpairs (best effort), deletes the node's key, autostart, the Apps entry, data and program files. #>
    [CmdletBinding()]
    param()
    Assert-LeafNodeUser
    $exe = Get-LeafNode
    if (-not $exe) { Write-LeafNode 'leaf-node is not installed.'; return }
    # Run from a copy, so the installed folder holds no running program and goes at once.
    $work = Join-Path ([IO.Path]::GetTempPath()) ('leaf-node-uninstall-' + [Guid]::NewGuid().ToString('N'))
    try {
        Copy-Item -LiteralPath (Split-Path -Parent $exe) -Destination $work -Recurse
        $copy = Join-Path $work 'leaf-node.exe'
        Invoke-LeafNodeExe $copy @('uninstall')
    }
    finally {
        if (Test-Path -LiteralPath $work) { Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue }
    }
}

function Write-LeafNode([string] $Message) { Write-Host "leaf-node installer: $Message" }

function Assert-LeafNodeUser {
    if (-not [Environment]::Is64BitOperatingSystem) { throw 'leaf-node needs 64-bit Windows.' }
    $identity = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
    if ($identity.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'run this in a normal PowerShell, not as administrator: leaf-node installs for your user only.'
    }
}

function Get-LeafNodeFeed {
    if ($env:LEAF_NODE_FEED_URL) { return $env:LEAF_NODE_FEED_URL.TrimEnd('/') }
    return 'https://raw.githubusercontent.com/RedBamboo-Interactive/redleaf-releases/release-feed'
}

function Save-LeafNodeUrl([string] $Url, [string] $Path) {
    $uri = [Uri]$Url
    if ($uri.Scheme -ceq 'file' -and $env:LEAF_NODE_FEED_URL) { Copy-Item -LiteralPath $uri.LocalPath -Destination $Path; return }
    if ($uri.Scheme -cne 'https') { throw "refusing to download from $Url (https only)." }
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
    try { Invoke-WebRequest -Uri $Url -OutFile $Path -UseBasicParsing }
    catch { throw "could not download $Url ($($_.Exception.Message))" }
}

function Get-LeafNodeHash([string] $Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }

function Resolve-LeafNodeRelease([string] $Work) {
    <# The signed feed's current RedCompute record, its release folder and the leaf-node manifest there. #>
    $channel = if ($env:LEAF_NODE_CHANNEL) { $env:LEAF_NODE_CHANNEL } else { 'nightly' }
    if ($channel -cne 'nightly' -and $channel -cne 'stable') { throw 'LEAF_NODE_CHANNEL must be nightly or stable.' }
    $feed = Get-LeafNodeFeed
    Write-LeafNode "reading the signed $channel release feed..."
    $pointerFile = Join-Path $Work 'pointer.json'
    Save-LeafNodeUrl "$feed/components/redcompute/channels/$channel.json" $pointerFile
    try { $pointer = (Get-Content -LiteralPath $pointerFile -Raw | ConvertFrom-Json).payload.record } catch { $pointer = $null }
    if (-not $pointer -or -not $pointer.url -or -not $pointer.sha256) { throw "the feed's RedCompute pointer is unreadable." }
    $recordFile = Join-Path $Work 'record.json'
    Save-LeafNodeUrl ([string]$pointer.url) $recordFile
    if ((Get-LeafNodeHash $recordFile) -cne [string]$pointer.sha256) { throw 'the RedCompute record does not match the hash its pointer names.' }
    $record = (Get-Content -LiteralPath $recordFile -Raw | ConvertFrom-Json).payload
    if ($record.componentId -cne 'redcompute' -or $record.targetPlatform.operatingSystem -cne 'windows' -or -not $record.artifact.url) {
        throw "the feed's RedCompute record is not a Windows RedCompute release."
    }
    $artifactUrl = [string]$record.artifact.url
    $folder = $artifactUrl.Substring(0, $artifactUrl.LastIndexOf('/'))
    $manifestFile = Join-Path $Work 'leaf-node-manifest.v1.json'
    try { Save-LeafNodeUrl "$folder/leaf-node-manifest.v1.json" $manifestFile }
    catch { throw "RedCompute $($record.version) was released without leaf-node; try again after the next Nightly. ($($_.Exception.Message))" }
    $manifest = Get-Content -LiteralPath $manifestFile -Raw | ConvertFrom-Json
    if ($manifest.schemaVersion -ne 1 -or $manifest.componentId -cne 'leaf-node' -or
        $manifest.version -cne $record.version -or $manifest.sourceCommit -cne $record.sourceCommit) {
        throw "the leaf-node manifest does not belong to RedCompute $($record.version)."
    }
    [pscustomobject]@{ Folder = $folder; Manifest = $manifest; Record = $record }
}

function Invoke-LeafNodeExe([string] $Exe, [string[]] $Arguments) {
    # leaf-node.exe is a Windows (no console) program: PowerShell would not wait for it, so it runs as a
    # process whose output is read here. The agent it starts holds none of these pipes.
    $start = New-Object Diagnostics.ProcessStartInfo
    $start.FileName = $Exe
    $start.Arguments = ($Arguments | ForEach-Object { '"' + $_ + '"' }) -join ' '
    $start.UseShellExecute = $false
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.CreateNoWindow = $true
    $process = [Diagnostics.Process]::Start($start)
    $stderr = $process.StandardError.ReadToEndAsync()
    $stdout = $process.StandardOutput.ReadToEnd()
    $process.WaitForExit()
    foreach ($line in (($stdout + $stderr.Result) -split "`r?`n")) { if ($line) { Write-Host "  $line" } }
    if ($process.ExitCode -ne 0) { throw "leaf-node $($Arguments -join ' ') failed ($($process.ExitCode))." }
}
