# Building a bespoke AI Session provider

A bespoke RedCompute provider may use any model API, local process, harness, container, or protocol internally. Consumers see only the `ISessionProvider` contract. Nova, Code, Roleplay, and Automations must not learn provider-specific behavior.

## Runtime contract

Create a .NET 9 class library named `RedCompute.Plugin.<Name>` and reference `RedCompute.PluginSdk` plus `RedCompute.Core`. The provider class must implement both `IPluginProvider` and `ISessionProvider` and expose a stable public static `ProviderTypeName`. RedCompute discovers `RedCompute.Plugin.*.dll` recursively from its `plugins` directory at startup and constructs the class from the longest constructor it can satisfy. The normal constructor shape is:

```csharp
public AcmeProvider(
    ProviderConfig config,
    string capabilitySlug,
    IJobTracker jobTracker,
    Action<string, Guid?> log)
```

`ProviderConfig` carries the authoritative Provider entity identity and configuration:

- `EntityId`, `EntitySlug`, and `DisplayName`
- `Backend` and `RuntimeBinding`
- `Endpoint`, `Model`, and vaulted `ApiKey`
- provider-specific non-secret settings through ordinary properties and `Extra`

Never read credentials from ordinary entity `settings`, source, logs, command arguments, transcripts, or generated files. A provider may pass a credential to its child process through a private process environment, or keep it in memory for an API client.

Declare only capabilities that work end to end. Unsupported interface operations must remain side-effect-free and return the contract's not-supported/not-found result or throw `NotSupportedException` at the admission boundary. In particular, never claim `Resume`, `Interrupt`, `ImageAttachments`, `FileAttachments`, `Generate`, or `StatelessExecution` because the underlying harness merely has something similarly named.

## Persistent-session invariants

For `PersistentSessions`, the provider owns one durable adapter state machine but not a second transcript:

1. Create the RedCompute job and provider session as one logical admission. A session never exists without its immutable job ID and provenance.
2. Persist the caller-supplied `messageUid` verbatim on the user record. Use it for the complete assistant turn as required by the shared session contract; it is not an individual stream-event ID.
3. Preserve provider-native message, part, and tool-call IDs. Stream fragments may be live-only; publish a completed turn atomically with stable native identities.
4. Emit ordered `SessionStreamEvent` records and return the same canonical history from `GetSession`, including after process restart.
5. `TrySendInputAsync` must report busy without interrupting the active turn. Duplicate admission with the same caller identity must not execute twice.
6. `InterruptSession`, `StopSessionAsync`, `ForceKillAsync`, and `ResumeSessionAsync` must preserve the distinction between user interruption, ordinary stop, error, and maintenance restart.
7. Confidential sessions, signed beneficiary provenance, repository identity, scratch-directory containment, attachments, and developer instructions must be enforced at the same boundary as existing providers.
8. Use RedCompute's completed transcript/checkpoint publisher and store conventions. Do not mirror partial output directly into RedLeaf or invent another conversation store.

Read the current `ISessionProvider`, existing Codex/OpenCode implementations, and their tests before coding. The interface is the source of truth when this guide and source differ.

## Database registration

A dedicated plugin instance is opt-in. Upsert a Provider entity by exact slug:

```http
PUT /api/entities/by-slug/acme-cloud
Authorization: Bearer <signed Leaf execution token>
Content-Type: application/json

{
  "type_slug": "provider",
  "name": "Acme Cloud",
  "data": {
    "backend": "acme",
    "provider_type": "AcmeHarness",
    "runtime_binding": "dedicated",
    "capabilities": ["ai-inference"],
    "endpoint_url": "https://api.acme.example/v1",
    "default_model": "acme-reasoner",
    "status": "active",
    "api_format": "custom",
    "settings": {
      "timeoutSeconds": 900,
      "region": "eu"
    }
  }
}
```

`provider_type` must exactly match `ProviderTypeName`. `runtime_binding: dedicated` causes RedCompute to create a runtime instance keyed by the Provider slug. Omit it or use `shared` only for a profile that deliberately reuses an existing backend such as OpenCode.

Put the credential in the vault after reading the created entity ID:

```http
PUT /api/entities/{provider-id}/secret-fields/api_key
Authorization: Bearer <signed Leaf execution token>
Content-Type: application/json

{ "value": "<secret>" }
```

Never place `api_key` inside the general Provider payload.

Register every selectable model as an `inference-model` entity whose `data.provider` references the Provider entity and whose `data.model_id` is the exact value sent to the harness. Add `quality-mode` entities when the model should participate in Fast, Standard, or Deep tier resolution. A Provider with `ai-inference` capability and a `default_model` is still admitted to the provider picker before quality modes exist.

## Build and deployment

Build and test in the canonical RedCompute checkout. Put the plugin project under `plugins/`, add it to `RedCompute.sln`, and add focused tests. The normal release staging process copies plugin outputs into the server's `plugins` directory. Provider discovery happens at process startup; do not hot-copy a replacement assembly into the live process.

Creating the Provider/model entities is safe before deployment: `/ai-session/providers/configured` reports `runtimeAvailable: false` until the declared plugin type is loaded. After a coordinated RedCompute handoff or restart it reports the runtime provider ID, capability flags, model list, and any bounded discovery error.

## Acceptance

At minimum prove:

- the plugin assembly is discovered by its declared `provider_type`;
- the Provider slug resolves to the dedicated instance, while existing shared profiles still resolve to their backend;
- no secret appears in entity data, logs, persisted configuration, process arguments, or transcript;
- provider/model discovery returns the expected default and available models;
- every claimed operation works through the public AI Session API;
- persistent sessions survive history reload and, when claimed, provider restart/resume;
- busy admission, duplicate retry, interruption, cancellation, failure, and stop have distinct correct outcomes;
- completed turns preserve order, `messageUid`, native identities, tool results, and atomic completion;
- a consuming app can select the Provider entity and model without provider-specific code.

Do not declare completion from a successful model response alone. The lifecycle, persistence, security, and discovery proofs are the provider.