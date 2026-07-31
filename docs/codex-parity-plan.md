# Codex provider — parity with Claude Code

Goal: `providerId: "codex"` becomes a first-class interactive session provider, so that
(a) `POST /api/apps/nova/delegate` can target it, and (b) Nova discussions can run on GPT-5.6.

Status of the world when this was written (2026-07-31):
- `codex-cli` was **never installed** on this machine. Installed 0.146.0 (= npm latest).
- Not logged in. `codex login` is a prerequisite for every test below.
- `CodexProvider.Capabilities` = `StatelessExecution | ProjectDiscovery`. Everything else throws.
- Protocol schema dumped to `docs/codex-app-server-schema/` via
  `codex app-server generate-json-schema --out <dir>`. **That bundle is the source of truth**,
  not this document and not any blog post.

## Transport

`codex app-server` — long-lived process, JSON-RPC 2.0 over stdio (newline-delimited).
Structurally identical to OpenCode's ACP, so `OpenCodeSessionService.cs` is the template:
`ManagedSession`, `PendingRequests`, `SendRequest`, `WriteJsonLine`, `ReadStdout` transfer
near-verbatim. Only the method names and the event mapping differ.

Handshake: `initialize` request → response → `initialized` notification. Then thread methods.

| Concern | OpenCode (ACP) | Codex (app-server) |
|---|---|---|
| new session | `session/new` | `thread/start` |
| reattach | `session/load` | `thread/resume` (by `threadId`) |
| send prompt | `session/prompt` | `turn/start` (`{threadId, input[]}`) |
| cancel | `session/cancel` | `turn/interrupt` |
| mid-turn input | — | `turn/steer` |
| config | `session/set_config_option` | per-turn `model` / `effort` on `turn/start` |
| models | hardcoded | `model/list` |

`thread/start` params we care about: `cwd`, `model`, `sandbox`, `approvalPolicy`, `personality`.
`turn/start` params: `threadId`, `input[]`, and per-turn overrides `model`, `effort`, `cwd`,
`sandboxPolicy`, `approvalPolicy`, `outputSchema`.

`UserInput` is a tagged union: `{type:"text", text}` | `{type:"image", url}` | `{type:"image", path}`.
Image variants are what unlock the `ImageAttachments` capability.

## Model catalog

Delete `CodexSessionEndpoints.ModelCatalog` (the hardcoded array, stale since June — tops out
at gpt-5.5, no 5.6 Sol/Terra/Luna). Replace with `model/list`, cached per process with a
refresh, since the real catalog is account-scoped and resolved server-side.

`model/list` returns per model: `id`, `displayName`, `description`, `isDefault`, `hidden`,
`defaultReasoningEffort`, `supportedReasoningEfforts`, `serviceTiers`, `inputModalities`.

Two consequences:
- Drop the `ValidModels` allow-list check in `HandleExecute` — validate against the live list.
- `supportedReasoningEfforts` should drive the effort dropdown per model instead of a fixed set.

## Event mapping — the part that decides whether this looks right

The frontend is **fully provider-agnostic** (verified: zero `claude-code` checks in the render
path). It switches on `ChatEvent.type` ∈ `text | thinking | tool_use | tool_result | error |
status | question | question_resolved`, and picks tool renderers by **`toolName` string**.

So parity is entirely a backend mapping problem. Map to the names the UI already renders well,
or you get generic JSON boxes where Claude gave diffs and command cards.

### ThreadItem → UnifiedStreamEvent

| Codex item | → type | toolName | toolInput | toolResult |
|---|---|---|---|---|
| `agentMessage` | `text` | — | — | — |
| `reasoning` | `thinking` | — | — | — |
| `commandExecution` | `tool_use` + `tool_result` | `Bash` | `{command, description, timeout}` | `aggregatedOutput` |
| `fileChange` | `tool_use` | `Edit` (kind=update) / `Write` (add) / `Delete` | `{file_path, diff}` per change | — |
| `webSearch` | `tool_use` + `tool_result` | `WebSearch` | `{query}` | `results` |
| `mcpToolCall` | `tool_use` + `tool_result` | `tool` field | `arguments` | `result` / `error` |
| `dynamicToolCall` | `tool_use` + `tool_result` | `tool` field | `arguments` | `contentItems` |
| `subAgentActivity` | `tool_use` | `Agent` | `{agentPath, agentThreadId, kind}` | — |
| `collabAgentToolCall` | `tool_use` | `Agent` | `{prompt, model, reasoningEffort}` | — |
| `imageView` | `tool_use` | `Read` | `{file_path: path}` | — |
| `plan` | `text` | — | — | — |
| `contextCompaction` | `status` | — | — | — |
| `webSearch`/`imageGeneration`/`sleep` | best-effort | — | — | — |

`fileChange.changes[]` is `{path, kind, diff}` where `diff` is a **unified diff string** and
`kind` is `{type: add|delete|update}` (update may carry `move_path`). The UI's Edit renderer
wants `{file_path, old_string, new_string}` and renders its own `-`/`+` view — so either parse
the unified diff into old/new, or confirm the UI has a raw-diff renderer and feed it directly.
**Open question, resolve by eye during testing.** This is the single most likely thing to look
wrong.

### Streaming deltas

These are what make thinking blocks and command output feel live. Emit with `IsPartial = true`;
the client coalesces consecutive partials of the same type into one part, and a terminal
`status`/`error` event finalizes the block.

- `item/agentMessage/delta` → `text` partial
- `item/reasoning/textDelta` + `item/reasoning/summaryTextDelta` → `thinking` partial
- `item/commandExecution/outputDelta` → `tool_result` partial
- `item/fileChange/patchUpdated`, `item/mcpToolCall/progress`, `item/plan/delta` → progress

Note: the existing `ParseExecStreamLine` (CodexSessionService.cs:201) already parses the
`item.completed` vocabulary for exec mode. Same item types, but notifications use **slashes**
(`item/completed`) not dots. Normalize and reuse rather than writing it twice.

### Pairing

The UI pairs a `tool_use` with the **first following `tool_result`** positionally — another
`tool_use` in between breaks the chain. So emit strictly `tool_use` → `tool_result` adjacent
per tool. Don't batch all uses then all results.

### MessageUid

Every event must carry a turn-scoped `MessageUid` (see OpenCode's `EmitAndStore`, line ~1066):
minted once per assistant turn, cleared on `status`/`error`. It's what keeps the streamed view
and the reloaded-from-DB view identical. Without it, reload reshuffles the message blocks.

## Schema changes

`CodexSessionRecord` — add: `ThreadId` (the app-server thread id, needed for `thread/resume`),
`ProcessId` (kill orphans on restart), `LastActivity`, `Effort`, `Source`, `ContextWindow`.

`CodexMessageRecord` — add: `MessageUid`, `AttachmentsJson`, `Role`.

`CodexDbContext.Initialize()` currently calls `EnsureCreated()` only — **no migration path**.
An existing `codex.db` will not pick up new columns and EF will throw on first query. Copy
OpenCode's additive `ALTER TABLE` migration block (OpenCodeDbContext.cs:28-58).

## Capabilities

```csharp
SessionCapabilities.StatelessExecution | SessionCapabilities.ProjectDiscovery
  | SessionCapabilities.PersistentSessions | SessionCapabilities.Resume
  | SessionCapabilities.Interrupt | SessionCapabilities.SendMessage
  | SessionCapabilities.ConfigUpdate | SessionCapabilities.ImageAttachments
```

Not claiming `PermissionMode` or `Generate` initially. Approvals arrive as **server→client
requests** (`ExecCommandApprovalParams`, `ApplyPatchApprovalParams`, `PermissionsRequestApprovalParams`)
which must be answered or the turn hangs — simplest correct v1 is a non-interactive approval
policy plus `workspace-write` sandbox, matching how exec mode runs today. Wiring those to the
UI's `question` / `question_resolved` events is the follow-up that earns `PermissionMode`.

## Delegation

`DelegateEndpoints.cs:91-92` already forwards a `provider` field, and `ProviderConfigService`
`AliasMap` only aliases `claude-code` and `opencode` — `"codex"` resolves to itself. So once
the capabilities above are real, delegation should work with `{"provider": "codex"}` and no
change to Nova. Verify rather than assume.

Nova discussions go through `StartSessionAsync` + `SendMessageAsync`, **not** `GenerateAsync`
— so "talk to Nova on 5.6 Sol" falls out of this same work with no extra path.

## Test plan (all blocked on `codex login`)

1. `codex login`, then `codex exec "say hi" --json` — confirms auth and the exec path.
2. Hand-drive `app-server` over stdio: initialize → thread/start → turn/start, capture the raw
   notification stream to a fixture file. Everything below is asserted against that fixture.
3. `GET /ai-session/providers` shows codex with the new capability set and live 5.6 models.
4. Start a session from CodeRed, send a prompt that forces: reasoning, a shell command, a file
   edit, and a web search. **Look at it.** Thinking block streams and collapses; command card
   shows command + output; edit shows a diff not a JSON dump.
5. Interrupt mid-turn. Resume after a RedCompute restart.
6. Delegate via Nova with `{"provider":"codex"}`, confirm the session-complete callback fires.
7. Start a Nova discussion on 5.6 Sol.
