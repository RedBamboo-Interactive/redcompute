# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build & Run

**Full rebuild (stop → frontend → backend + plugins → launch):**
```
.\rebuild.bat
```
Or with options:
```powershell
.\rebuild.ps1                  # full rebuild + launch
.\rebuild.ps1 -SkipFrontend    # backend only
.\rebuild.ps1 -SkipBackend     # frontend only
.\rebuild.ps1 -NoLaunch        # build without launching
```

The rebuild script uses the shared `redbamboo-packages/dotnet/rebuild.ps1`. It stops RedCompute.exe, kills WSL backend processes, builds the solution, and launches. Normally you should NOT launch manually: RedCompute is a managed child of the Leaf kernel (RedLeaf), which spawns it (or adopts an already-running instance) — prefer `.\rebuild.ps1 -NoLaunch` and let the kernel start it.

The kernel locates `RedCompute.exe` through the `compute` path in `leaf.workspace.json` (in the `redleaf` repo), which points at this checkout; `src\RedCompute.App\bin\Release\net9.0-windows\RedCompute.exe` is the probed build output. `computeExePath` in the kernel config overrides it.

There are no tests and no CI pipeline.

## Architecture

RedCompute is a local AI compute orchestrator: a headless .NET 9 service that manages AI backends and exposes a unified REST API on port 18800. It has no frontend of its own — the dashboard is the `compute-dashboard` Leaf plugin, served by the kernel and talking through the kernel's `/compute/*` proxy. It uses a plugin architecture where capabilities and providers are pluggable.

### Projects

- **RedCompute.Core** — Interfaces, models, configuration. No dependencies. Defines `IBackendProvider`, `IPluginProvider`, `JobRequest`/`JobResult`, `CapabilityDefinition`, config classes, discovery models.
- **RedCompute.PluginSdk** — References Core + ASP.NET Core. Provides `ICustomEndpointProvider` interface and `ProviderHelpers` utilities for plugin authors.
- **RedCompute.App** — WPF tray app embedding an ASP.NET Core HTTP server. Contains the generic endpoint engine, discovery, registry, and all orchestration logic.
- **plugins/** — Individual provider projects, each implementing `IPluginProvider`. Discovered at runtime via assembly scanning.

### Plugin System

Providers are self-contained .NET projects under `plugins/`. Each implements `IPluginProvider` (from Core) and optionally `ICustomEndpointProvider` (from PluginSdk) for custom API routes. Built-in plugins:

- **RedCompute.Plugin.ComfyUI** — Image/video generation via ComfyUI workflows, WebSocket progress tracking
- **RedCompute.Plugin.Suno** — Cloud music generation via Suno API
- **RedCompute.Plugin.LocalWsl** — Generic HTTP proxy provider (starts process, health-checks, proxies requests)
- **RedCompute.Plugin.TtsLocal** — TTS via Qwen3-TTS (wraps LocalWsl, adds voice discovery)
- **RedCompute.Plugin.SttLocal** — STT via faster-whisper (wraps LocalWsl, adds model/language endpoints)
- **RedCompute.Plugin.ClaudeCode** — AI sessions via Claude Code CLI

**Creating a new provider:** Create a project under `plugins/`, reference `RedCompute.PluginSdk`, implement `IPluginProvider`. The constructor takes `(ProviderConfig config, string capabilitySlug, Action<string> log)`. The `ProviderType` property must match the `Type` field in config.json. At startup, `ProviderDiscovery` scans `{exe}/plugins/*.dll` and registers all `IPluginProvider` implementations.

**Adding a new capability:** Just configure it in `config.json` with a provider. If the provider's `CapabilitySlug` is a slug RedCompute has never seen, it creates the capability automatically. Add a `capabilities/{slug}.json` manifest for display metadata (icon, color, description).

### Capability & Provider System

A **capability** is a type of AI workload identified by a string slug (tts, stt, image-gen, etc.). A **provider** is a specific backend that fulfills a capability. One capability can have multiple providers; one is the active default.

Capabilities are NOT hardcoded — any string slug works. Display metadata (icon, color, name) comes from JSON manifest files in `capabilities/`, overridable by user config.

**Startup flow** (`App.xaml.cs`):
1. Load config from `%LOCALAPPDATA%\RedCompute\config.json` via `ConfigManager`
2. `ProviderDiscovery.ScanAssemblies()` — finds all `IPluginProvider` types from plugins/
3. `InitializeCapabilities()` — iterates config capabilities, creates providers via `ProviderDiscovery`, loads capability metadata via `CapabilityManifestLoader`, registers into `CapabilityRegistry`
4. Start `RelayServer` (ASP.NET Core on port 18800)
5. `ProbeRunningBackends()` — starts all active providers concurrently

**Key services:**
- `ProviderDiscovery` — assembly-scanning provider factory, replaces hardcoded switch
- `CapabilityManifestLoader` — loads capability display metadata from JSON manifests
- `CapabilityRegistry` — maps slug → `CapabilityEntry` (definition + providers + state)
- `JobTrackingService` — SQLite-backed job lifecycle (create/run/complete/fail)

### API Endpoints

`GenericCapabilityEndpoints` registers routes for every registered capability:

- `POST /{slug}/generate` — universal work endpoint. Validates against provider's `InputParameters` schema, calls `ExecuteAsync`, handles sync/async/proxy modes.
- `GET /{slug}/jobs/{id}/output` — retrieve completed output
- `GET /{slug}/jobs/{id}/progress` — real-time progress
- `/{slug}/{**path}` — proxy catch-all for providers with `GetProxyTargetUrl()`

Providers register additional endpoints via `ICustomEndpointProvider.MapCustomEndpoints()` (e.g., `/image-gen/workflows`, `/tts/voices`).

`/discover` manifest and `/openapi.json` are generated from provider-declared `InputParameters` and `OutputSchema` — no manual maintenance needed when adding providers.

Cross-cutting: `/status`, `/jobs`, `/logs`, `/control/start|stop/{slug}`, `/settings`, `/ws` (WebSocket events).

### Frontend

None in this repo. The dashboard is the `compute-dashboard` Leaf plugin (its own repo, `T:\Projects\compute-dashboard`), served by the Leaf kernel; it reaches this service through the kernel's `/compute/*` proxy.

### Database

SQLite via Entity Framework Core. `RedComputeDbContext` in `RedCompute.App/Data/`. Stores jobs and logs. Schema managed by `EnsureCreated()` + `MigrateSchema()`.

### Configuration

`%LOCALAPPDATA%\RedCompute\config.json`. Structure: `RedComputeConfig` → `Capabilities` (dict of slug → `CapabilityConfig` → `Providers` dict of name → `ProviderConfig`). `ProviderConfig.Type` matches a provider's `ProviderType` property. `ProviderConfig.Extra` (JsonExtensionData) holds per-provider flexible settings. Display overrides (`DisplayName`, `Icon`, `Color`) can be set in `CapabilityConfig`. Editable via `/settings` API endpoints or direct file edit.

Capability display metadata: `capabilities/{slug}.json` files with `displayName`, `description`, `icon` (FontAwesome class), `color` (hex), `category`. Shipped manifests in repo root `capabilities/`, user overrides in `%LOCALAPPDATA%\RedCompute\capabilities/`.
