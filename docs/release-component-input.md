# RedCompute Release-mode component input

RedCompute produces deterministic component bytes and an unsigned generic RedLeaf candidate for central release assembly. The candidate and signature-input bytes come from one exact pinned copy of RedLeaf's authoritative `ReleaseTool`; RedCompute never copies that contract or signs it. Central RedLeaf alone signs and publishes Stable/Nightly metadata.

## Output boundary

The pinned workflow `.github/workflows/release-redcompute-component.yml` builds one version-stamped, self-contained `win-x64` runtime ZIP and one strict `redcompute-component-input` version 1 descriptor. The descriptor schema is `schemas/redcompute-component-input.v1.schema.json`; objects reject unknown properties and the release tool also performs content and cross-field validation. It then invokes `candidate ingest-redcompute` twice against the two already-proved production paths and requires identical unsigned candidate and signature-input bytes. Candidate ingestion never rebuilds RedCompute.

The component input is channel-neutral. It contains no Stable/Nightly value, GitHub run ID, wall-clock build/publication time, artifact URL, candidate ID, signer input, or signature. Compatibility remains explicit: `requiresKernelApi` and `compatibleProductVersion` use RedLeaf's Phase 1 range grammar, while `providesComputeApi` is one exact SemVer. ZIP entries use the fixed DOS epoch. The generic candidate binds that tested ZIP to the required future central tag with the safe immutable filename `redcompute-win-x64-<artifact-sha256>.zip`; Nightly and later Stable promotion reuse that same tag, candidate, and ZIP bytes.

`artifact.installPath` is the component-relative path `redcompute`. RedLeaf's suite assembler must place it under its release-owned `releases/<release-id>/redcompute` root. Binding a suite release ID here would prevent byte reuse across suite assembly and promotion.

The installed ZIP includes the self-contained RedCompute engine and .NET runtime for `win-x64`, capability JSON files, all nine provider/plugin assemblies, and the compact build-evidence JSON. There is no RedCompute web shell: the dashboard is a RedLeaf extension. An installed component needs none of Git, .NET, Node, or pnpm.

SBOM generation is not a producer input, component contract field, workflow gate, or RedLeaf promotion prerequisite. A developer may generate an SBOM manually for diagnostics, but it is deliberately outside the release candidate contract and this workflow carries no mandatory CycloneDX tool or action.

## Compact reproduction evidence

The embedded evidence contains only:

- the RedCompute repository URL, exact commit, and repository source path;
- the in-repository `RedCompute.PluginSdk` source path tied to that same repository and commit;
- the RedBamboo AppHost repository URL, exact pinned commit, and source path;
- the path and SHA-256 of every real `packages.lock.json` used by the locked restores and build;
- the pinned .NET SDK, self-contained .NET runtime, MSBuild, and NuGet versions; and
- the version, configuration, RID, deterministic/self-contained flags, and no-web-shell fact needed to cross-check the artifact contract.

The descriptor records the real ZIP file name, byte size, SHA-256, install path, RID, runtime shape, and compatibility/API fields. It derives the artifact hash and size from the produced bytes rather than accepting caller-supplied values.

There are no repository-tree or source-tree identities, source-to-lock mappings, aggregate lock identities, release-tool provenance chains, provider/file hashes, or every-runtime-file inventory. The provider graph remains an internal build input only: the producer checks source-declared provider identities and the exact published plugin DLL set before creating evidence, but does not serialize or upload that graph as provenance.

## Reproduction and safety

The workflow pins every GitHub Action by full commit SHA, checks out and verifies RedCompute at the called workflow's own `github.workflow_sha`, records its repository as exactly `https://github.com/RedBamboo-Interactive/redcompute`, checks out AppHost only at its committed dependency SHA, and checks out RedLeaf only at the committed ReleaseTool input SHA. It uses .NET SDK 9.0.303 and runtime 9.0.7 with roll-forward disabled, runs locked NuGet restores, runs the complete test solution with one MSBuild node, and publishes with deterministic CI properties. Compiler path mapping gives the RedCompute and AppHost roots stable virtual names, so source checkout locations do not leak into source-built DLL/PDB bytes. Both release tools are locked-restored and explicitly built once into runner-temporary locations. Dispatch values enter PowerShell only through environment variables and are validated as data before use.

The workflow produces the publish tree twice at the release boundary: deterministic archive creation is repeated and byte-compared, and the component descriptor, generic candidate, and signature input are each repeated and byte-compared. CI uploads unsigned inputs only. It has no key, sign command, signing job, registry, or server. Production signing remains fail-closed in RedLeaf.

Useful local verification commands (they do not launch or restart RedCompute):

```powershell
./release/Test-LockedRestoreModes.ps1 -ArtifactsPath artifacts/locked-restore-gate -RedBambooPackagesRoot=<exact-redbamboo-packages-checkout>
dotnet restore RedCompute.sln --locked-mode --artifacts-path artifacts/neutral -p:RedBambooPackagesRoot=<exact-redbamboo-packages-checkout>
dotnet test RedCompute.sln --configuration Release --no-restore --artifacts-path artifacts/neutral --maxcpucount:1 -p:RedBambooPackagesRoot=<exact-redbamboo-packages-checkout>
dotnet restore RedCompute.sln --locked-mode --runtime win-x64 --artifacts-path artifacts/win-x64 -p:RedBambooPackagesRoot=<exact-redbamboo-packages-checkout>
dotnet publish src/RedCompute.App/RedCompute.App.csproj --configuration Release --runtime win-x64 --self-contained true --no-restore --artifacts-path artifacts/win-x64 --output artifacts/publish -p:RedBambooPackagesRoot=<exact-redbamboo-packages-checkout>
```

The sibling AppHost project uses the consumer-owned lock at `release/locks/redbamboo-apphost/packages.lock.json`. The App project-reference restore metadata routes AppHost to that lock and declares the neutral plus `win-x64` restore graph; the lock is never copied into or generated inside the sibling checkout. The locked-restore gate compares every lock hash before and after both restore modes and verifies that the AppHost checkout did not change.

## RedLeaf acquisition boundary

`release/redleaf-release-tool-input.v1.json` is the smallest producer-owned pin and filename contract. Its committed `commit` is the audited central RedLeaf source `4bf0894014b392e60cf0b5c6ca85920428ba7516`, whose `ReleaseTool` accepts this compact descriptor without SBOM or rich provenance inputs. `release/Resolve-RedLeafReleaseToolInput.ps1` rejects any unresolved or malformed value before SDK setup, dependency restore, or build.

After the build job, the isolated bridge job has only `actions: read` and `contents: write`. It retains the per-run Actions artifact, verifies exactly one ZIP against the unsigned candidate's size and SHA-256, and appends these two raw public assets to the `redcompute-unsigned-candidates` prerelease:

- `<candidate-id>.candidate.json`
- `<candidate-id>.redcompute-win-x64.zip`

An existing asset name is reused only when a fresh download hashes to the same bytes; a collision fails and nothing is replaced. A reviewed central plan can therefore use the two direct URLs below as `descriptorUrl` and `artifactSourceUrl`, while the candidate itself already names the future central artifact URL:

```text
https://github.com/RedBamboo-Interactive/redcompute/releases/download/redcompute-unsigned-candidates/<candidate-id>.candidate.json
https://github.com/RedBamboo-Interactive/redcompute/releases/download/redcompute-unsigned-candidates/<candidate-id>.redcompute-win-x64.zip
```

These public producer assets remain unsigned and convey no trust. The producer is pinned to the audited central RedLeaf commit above; central RedLeaf alone checks the reviewed source URLs and hashes, copies the exact ZIP to its declared central tag, and signs the candidate. No SBOM, CycloneDX input, provenance graph, package registry, or live service is part of this boundary.
