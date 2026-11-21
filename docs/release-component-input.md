# RedCompute Release-mode component input

RedCompute produces deterministic component bytes and an unsigned generic RedLeaf candidate for central release assembly. The candidate and signature-input bytes come from RedLeaf's authoritative `ReleaseTool` at the RedLeaf commit the build resolved; RedCompute never copies that contract or signs it. Central RedLeaf alone signs and publishes Stable/Nightly metadata.

## Output boundary

The pinned workflow `.github/workflows/release-redcompute-component.yml` builds one version-stamped, self-contained `win-x64` runtime ZIP and one strict `redcompute-component-input` version 1 descriptor. The descriptor schema is `schemas/redcompute-component-input.v1.schema.json`; objects reject unknown properties and the release tool also performs content and cross-field validation. It then invokes `candidate ingest-redcompute` twice against the two already-proved production paths and requires identical unsigned candidate and signature-input bytes. Candidate ingestion never rebuilds RedCompute.

The component input is channel-neutral. It contains no Stable/Nightly value, GitHub run ID, wall-clock build/publication time, artifact URL, candidate ID, signer input, or signature. Compatibility remains explicit: `requiresKernelApi` and `compatibleProductVersion` use RedLeaf's Phase 1 range grammar, while `providesComputeApi` is one exact SemVer. ZIP entries use the fixed DOS epoch. The generic candidate binds that tested ZIP to the fixed RedCompute producer prerelease with the safe immutable filename `redcompute-win-x64-<artifact-sha256>.zip`; Nightly and later Stable promotion reuse that candidate and those ZIP bytes.

`artifact.installPath` in the producer descriptor is the component-relative path `redcompute`. Candidate ingestion places it under the component-owned, versioned root `releases/redcompute-<version>-win-x64/redcompute`. The workflow derives that root from the component version; it accepts no suite release ID, so the exact candidate bytes remain independently reusable across channel publication and promotion.

The installed ZIP includes the self-contained RedCompute engine and .NET runtime for `win-x64`, capability JSON files, all ten provider/plugin assemblies, and the compact build-evidence JSON. There is no RedCompute web shell: the dashboard is a RedLeaf extension. An installed component needs none of Git, .NET, Node, or pnpm.

SBOM generation is not a producer input, component contract field, workflow gate, or RedLeaf promotion prerequisite. A developer may generate an SBOM manually for diagnostics, but it is deliberately outside the release candidate contract and this workflow carries no mandatory CycloneDX tool or action.

## Compact reproduction evidence

The embedded evidence contains only:

- the RedCompute repository URL, exact commit, and repository source path;
- the in-repository `RedCompute.PluginSdk` source path tied to that same repository and commit;
- the RedBamboo AppHost repository URL, the exact commit the build used (`--redbamboo-commit`, checked against the `--redbamboo` checkout's HEAD), and source path;
- the path and SHA-256 of every real `packages.lock.json` used by the locked restores and build;
- the pinned .NET SDK, self-contained .NET runtime, MSBuild, and NuGet versions; and
- the version, configuration, RID, deterministic/self-contained flags, and no-web-shell fact needed to cross-check the artifact contract.

The descriptor records the real ZIP file name, byte size, SHA-256, install path, RID, runtime shape, and compatibility/API fields. It derives the artifact hash and size from the produced bytes rather than accepting caller-supplied values.

There are no repository-tree or source-tree identities, source-to-lock mappings, aggregate lock identities, release-tool provenance chains, provider/file hashes, or every-runtime-file inventory. The provider graph remains an internal build input only: the producer checks source-declared provider identities and the exact published plugin DLL set before creating evidence, but does not serialize or upload that graph as provenance.

## Reproduction and safety

The workflow pins every GitHub Action by full commit SHA, checks out and verifies RedCompute at the called workflow's own `github.workflow_sha`, records its repository as exactly `https://github.com/RedBamboo-Interactive/redcompute`, checks out redbamboo-packages (AppHost) and RedLeaf (the ReleaseTool) at `main` once, at dispatch, and records both resolved commits in the step summary; RedCompute commits neither. It uses .NET SDK 9.0.303 and runtime 9.0.7 with roll-forward disabled, runs locked NuGet restores, runs the complete test solution with one MSBuild node, and publishes with deterministic CI properties. Compiler path mapping gives the RedCompute and AppHost roots stable virtual names, so source checkout locations do not leak into source-built DLL/PDB bytes. Both release tools are locked-restored and explicitly built once into runner-temporary locations. Dispatch values enter PowerShell only through environment variables and are validated as data before use.

The workflow produces the publish tree twice at the release boundary: deterministic archive creation is repeated and byte-compared, and the component descriptor, generic candidate, and signature input are each repeated and byte-compared. CI uploads unsigned inputs only. It has no key, sign command, signing job, registry, or server. Production signing remains fail-closed in RedLeaf.

Useful local verification commands (they do not launch or restart RedCompute):

```powershell
./release/Test-LockedRestoreModes.ps1 -ArtifactsPath artifacts/locked-restore-gate -RedBambooPackagesRoot=<exact-redbamboo-packages-checkout>
dotnet restore RedCompute.sln --locked-mode --artifacts-path artifacts/neutral -p:RedBambooPackagesRoot=<exact-redbamboo-packages-checkout>
dotnet test RedCompute.sln --configuration Release --no-restore --artifacts-path artifacts/neutral --maxcpucount:1 -p:RedBambooPackagesRoot=<exact-redbamboo-packages-checkout>
dotnet restore RedCompute.sln --locked-mode --runtime win-x64 --artifacts-path artifacts/win-x64 -p:RedBambooPackagesRoot=<exact-redbamboo-packages-checkout>
dotnet publish src/RedCompute.App/RedCompute.App.csproj --configuration Release --runtime win-x64 --self-contained true --no-restore --artifacts-path artifacts/win-x64 --output artifacts/publish -p:RedBambooPackagesRoot=<exact-redbamboo-packages-checkout>
```

The sibling AppHost project uses the consumer-owned lock at `release/locks/redbamboo-apphost/packages.lock.json`. The App project-reference restore metadata routes AppHost to that lock and declares the neutral plus `win-x64` restore graph; the lock is never copied into or generated inside the sibling checkout. The locked-restore gate compares every lock hash before and after its restore modes and verifies that the AppHost checkout did not change.

The Linux preview (`LeafTargetOs=linux`) has its own graph: each project keeps a `packages.linux.lock.json` beside its `packages.lock.json` (AppHost: `release/locks/redbamboo-apphost/packages.linux.lock.json`) with the neutral plus `linux-x64` graph, and the gate also restores `-p:LeafTargetOs=linux` and `--runtime linux-x64`. This component input and its evidence still describe only the win-x64 release, so they list only the `packages.lock.json` files.

## RedLeaf acquisition boundary

`release/redleaf-release-tool-input.v1.json` is the smallest producer-owned filename contract: the RedLeaf repository and the central artifact filename template. It commits no RedLeaf commit. The builder supplies the ReleaseTool: Release Manager builds it at the RedLeaf commit it resolved for the Nightly, and the fallback workflow at RedLeaf `main` at dispatch. That `ReleaseTool` accepts this compact descriptor without SBOM, rich provenance, or suite-release inputs and derives `compute/redcompute/<version>` itself. `release/Resolve-RedLeafReleaseToolInput.ps1` rejects a malformed input, or one that commits a `commit`, before SDK setup, dependency restore, or build.

After the build job, the isolated bridge job has only `actions: read` and `contents: write`. It retains the per-run Actions artifact, verifies exactly one ZIP against the unsigned candidate's size and SHA-256, and appends these two raw assets to the `redcompute-unsigned-candidates` prerelease:

- `<candidate-id>.candidate.json`
- `redcompute-win-x64-<artifact-sha256>.zip`

An existing asset name is reused only when a fresh download hashes to the same bytes; a collision fails and nothing is replaced. A reviewed one-component publication can therefore acquire the two direct URLs below, while the candidate already names its producer artifact URL:

```text
https://github.com/RedBamboo-Interactive/redcompute/releases/download/redcompute-unsigned-candidates/<candidate-id>.candidate.json
https://github.com/RedBamboo-Interactive/redcompute/releases/download/redcompute-unsigned-candidates/redcompute-win-x64-<artifact-sha256>.zip
```

These producer assets remain unsigned and convey no trust. Central RedLeaf alone checks the reviewed source URLs and hashes and signs metadata that continues to reference the exact RedCompute bytes. It does not copy the ZIP into a RedLeaf release. No SBOM, CycloneDX input, provenance graph, package registry, or live service is part of this boundary.

## leaf-node artifacts (Phase 3 R7)

The node agent ships with the Nightly beside RedCompute, from the same clone and source commit, as five additional files. They change nothing RedCompute ships: its archive, evidence and descriptors are built exactly as above, and `src/RedCompute.Node` is outside RedCompute.App's project graph.

| File | What it is |
|---|---|
| `leaf-node-win-x64.zip` | the win-x64 agent, self-contained and untrimmed (`Properties/PublishProfiles/win-x64.pubxml`), as the same deterministic zip as RedCompute's |
| `leaf-node-linux-x64.tar.gz` | the linux-x64 agent (`linux-x64.pubxml`), a deterministic ustar tarball: sorted entries, one timestamp, uid/gid 0, 0755 for ELF files and shared libraries |
| `leaf-node-manifest.v1.json` | version, source commit, and the size and SHA-256 of each archive (`RedCompute.ReleaseTool node validate` checks it) |
| `leaf-node-install.ps1` | the Windows installer: `irm <release>/leaf-node-install.ps1 \| iex; Install-LeafNode` |
| `leaf-node-install.sh` | the Linux installer: `curl -fsSL <release>/leaf-node-install.sh \| sh` |

`release/leaf-node/Build-LeafNodeArtifacts.ps1` is the recipe: per runtime a locked restore (`-r` on the command line, so the right lock file is used), a publish with the profile and `-p:Version=<the Nightly's version>`, then `RedCompute.ReleaseTool node archive`; then `node manifest` and `node validate`. It writes `[{name, path, sha256}]`, the shape of `result.json` `extraAssets`. Release Manager's RedCompute recipe runs it after RedCompute's own files and publishes the five files in RedCompute's Windows release; a source without `release/leaf-node` builds what it did before, and a failure never fails the RedCompute build.

The installers verify before they install, the way the kernel's `install.sh` does: the signed feed's RedCompute channel pointer, the record it names by SHA-256, `leaf-node-manifest.v1.json` in that record's release (it must name the record's version and source commit), then the archive's size and SHA-256. They are per user (they refuse root and an elevated PowerShell), run `leaf-node install` (an installed agent is stopped and upgraded side by side) and never pair: `leaf-node pair --server <origin> --code <code>` stays its own step. `LEAF_NODE_FEED_URL`, `LEAF_NODE_CHANNEL` and `LEAF_NODE_HOME` serve dry runs; with `LEAF_NODE_HOME` the agent keeps its registrations in that folder and never writes the Run value, the Uninstall key or the systemd unit. The archives are checked by hash through the feed's chain, not signed themselves: a signature of their own needs leaf-node as a feed component (a RedLeaf release definition).

`GET /nodes/agent` resolves the same chain on request (10-minute cache) and gives the "Add a node" dialog the download links and one install command per OS, or `available: false` and a reason.
