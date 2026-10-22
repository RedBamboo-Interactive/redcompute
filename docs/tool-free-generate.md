# Tool-free `/ai-session/generate`, isolation reporting, and response schemas

`POST /ai-session/generate` has two separate runtime capabilities:

- `Generate`: the runtime can perform a stateless completion. This does not imply that its agent
  harness lacks tools.
- `ToolFreeGenerate`: the runtime can run a completion with every tool removed by construction, and
  verifies that for the individual call before returning text.

Clients that require this boundary send `"requireToolFree": true`. RedCompute returns HTTP 422 with
`error: "tool_free_unavailable"` before running anything when the resolved runtime does not assert
`ToolFreeGenerate`. After the call, the provider must report `isolation: "verified-tool-free"` for
that exact call; otherwise RedCompute withholds the text, marks the job failed and returns HTTP 502
`error: "tool_free_unverified"`. Requests without the field (or with `false`) behave as before.

## API contract

Request fields added to `POST /ai-session/generate` (all optional):

| Field | Meaning |
|---|---|
| `requireToolFree` | boolean. Require verified tool-free isolation for this call. |
| `responseSchema` | JSON Schema object from the bounded subset below. |

Response fields added (also stored in the job `resultJson`):

| Field | Meaning |
|---|---|
| `isolation` | What was actually applied: `verified-tool-free`, `sandboxed-best-effort`, `unrestricted`, or `unverified` (provider reported nothing). |
| `isolationMechanism` | Short mechanism name, e.g. `claude-empty-init-tools`, `codex-model-boundary-relay`, `codex-sandbox:read-only`. |
| `json` | Only with `responseSchema`: the JSON value extracted from the model text, or `null`. |
| `schemaValid` | Only with `responseSchema`: `true` when `json` was found and validated with no errors. |
| `errors` | Only with `responseSchema`: validation errors with JSON paths, e.g. `$.verdict: must be one of "allow", "block"` (at most 50). |
| `schemaMode` | Only with `responseSchema`: `native` when the provider also enforced the schema with its own structured output, else `prompt`. |

`POST /ai-session/execute` responses (sync body and async job result) now also carry `isolation` and
`isolationMechanism`. Nothing else in either response changed.

A schema mismatch is not an HTTP error: the call returns 200 with `schemaValid: false`, and the
caller decides. An unsupported or malformed schema is rejected before the model runs with HTTP 422
`error: "invalid_response_schema"` and an `errors` array.

### Response schema subset

`ResponseSchema` (`src/RedCompute.Core/Sessions/ResponseSchema.cs`) accepts `type` (a name or an
array of `string`, `number`, `integer`, `boolean`, `object`, `array`, `null`), `properties`,
`required`, `additionalProperties` (boolean or schema), `items`, `enum`, `const`, `minLength`,
`maxLength`, `pattern` (.NET regex, 100 ms match timeout), `minimum`, `maximum`,
`exclusiveMinimum`, `exclusiveMaximum`, `minItems`, `maxItems`, and the annotations `title`,
`description` and root `$schema`. Every other keyword (`$ref`, `anyOf`, `format`, ...) is rejected,
so a caller never believes a constraint was checked when it was not. Limits: 32 KiB serialized,
depth 16, 512 schema nodes, 256 enum values, 512-character patterns.

The schema instruction is always appended to the system prompt. A provider may additionally enforce
it natively. Either way, RedCompute extracts the JSON deterministically (whole text, then each
fenced code block, then the first balanced object or array that parses) and validates it in C#. The
model and provider are never trusted for validity.

## Runtime status

### Claude Code: asserts `ToolFreeGenerate` (every generate call)

Generate launches with:

```text
--tools "" --strict-mcp-config --mcp-config '{"mcpServers":{}}'
```

It runs in a unique empty temporary working directory, deletes that directory after completion, and
fails without returning model text unless the Claude init event contains an empty `tools` array.
Successful calls report `verified-tool-free` / `claude-empty-init-tools`.

Response schemas are prompted, never passed as `--json-schema`: Claude Code 2.1.308 implements
`--json-schema` with a synthetic `StructuredOutput` tool, and a direct run with `--tools ""` reported
`init tools = ['StructuredOutput']` and answered through a `tool_use` block. That would break the
empty-tool invariant, so `schemaMode` is `prompt` for Claude.

Execute reports `verified-tool-free` only when the caller passed an explicit empty `tools` array and
that run's init event reported no tools; a non-empty `tools` grant is `sandboxed-best-effort`, and no
grant (the full agent with permissions skipped) is `unrestricted`.

### Codex: asserts `ToolFreeGenerate` (when the request sets `requireToolFree`)

Codex CLI 0.163.0 still has no switch that empties its tool surface and reports it. `exec --help`
offers sandbox modes, `--ignore-user-config`, `--ignore-rules`, `--ephemeral`, `--output-schema` and
`--enable/--disable <feature>`, and `codex features list` shows dozens of independent tool families
(`shell_tool`, `unified_exec`, `apps`, `plugins`, `browser_use`, `computer_use`, `multi_agent`,
`image_generation`, `view_image`, ...). Disabling them one by one is a denylist that the next release
can silently extend, which the 2026-10-17 probe already showed (`codex_apps` MCP tool survived).

The enforced mechanism is therefore at the model boundary, not in Codex's configuration.
`CodexToolFreeRelay` is a loopback HTTP relay inside RedCompute. A tool-free call runs:

```text
codex exec - --json --skip-git-repo-check --sandbox read-only
  --ephemeral --ignore-user-config --ignore-rules
  --disable enable_request_compression
  -c model_provider="redcompute_tool_free"
  -c model_providers.redcompute_tool_free.name="RedCompute tool-free relay"
  -c model_providers.redcompute_tool_free.base_url="http://127.0.0.1:<port>/<random nonce>/v1"
  -c model_providers.redcompute_tool_free.wire_api="responses"
  -c model_providers.redcompute_tool_free.requires_openai_auth=true
  [--output-schema <file>] [-c model_reasoning_effort=...] [--model ...]
```

Codex keeps its own login, token refresh and prompt assembly; with `requires_openai_auth` it sends
its ChatGPT (or API-key) credentials to the relay, which forwards them only to the fixed upstream for
that credential type (`https://chatgpt.com/backend-api/codex/` when a `chatgpt-account-id` header is
present, else `https://api.openai.com/v1/`). For every `POST /responses` the relay:

1. refuses compressed or unparseable bodies (never forwarded);
2. keeps only `message` input items and an allowlist of top-level fields, so every tool definition
   (Codex 0.163 sends its tools as an `additional_tools` input item under
   `x-openai-internal-codex-responses-lite`, not in `tools`) is dropped, and sets `tools: []`,
   `tool_choice: "none"`, `parallel_tool_calls: false`;
3. buffers the entire upstream response and inspects it before Codex sees any of it. Any output item
   other than `message`/`reasoning`, or any tool event (`*_call*`, `*tool*`, `response.mcp*`), is
   withheld and replaced by HTTP 400, which Codex treats as terminal rather than retrying.

Only `GET models` is passed through unchanged (no authority); every other route is refused. The call
reports `verified-tool-free` / `codex-model-boundary-relay` only when at least one model response
crossed the relay, nothing was refused, and Codex's own JSONL contains no item other than
`agent_message`, `reasoning` or `error`. Otherwise it fails closed with no model text. Without
`requireToolFree`, Codex generate is unchanged (`codex exec --sandbox read-only` from an empty
temporary directory) and reports `sandboxed-best-effort` / `codex-sandbox:read-only`.

Response schemas that satisfy OpenAI strict structured outputs (object root, every node typed,
every object closed with all properties required, no `minLength`/`maxLength`) are also passed as
`--output-schema`; Codex sends them as `text.format = {type: "json_schema", strict: true}`, which the
relay forwards. Other schemas are prompted only.

Codex execute keeps the full Codex tool surface and reports `sandboxed-best-effort` with the sandbox
it ran under, or `unrestricted` for `danger-full-access`.

Evidence (2026-10-22, codex-cli 0.163.0, ChatGPT sign-in, no credential or config changes):

- capture listener: `openai_base_url` and a custom `model_providers` entry both route
  `/models` and `/responses` with `authorization` and `chatgpt-account-id`; the default request body
  is zstd-compressed (hence `--disable enable_request_compression`); `--output-schema` is sent as a
  strict `json_schema` text format.
- `CodexToolFreeLiveTests` (opt-in, `REDCOMPUTE_LIVE_CODEX=1`) against the real CLI and backend: an
  absolute-path canary read request returned `NOTOOLS` with `verified-tool-free` and 11 tool
  definitions removed; a strict-schema request returned `{"verdict":"block"}`.

Unverified: API-key Codex login (the relay picks `api.openai.com` from the absent account header; no
API-key login was available to test), and Codex configured with a custom `openai_base_url` or model
provider, which the tool-free path deliberately bypasses (`--ignore-user-config`) and does not proxy.

### OpenCode: no `Generate`, no `ToolFreeGenerate`

OpenCode 1.17.15 has no stateless generate in RedCompute (`requireToolFree` still returns 422 via the
`Generate` check). `opencode run` exposes agents, models, `--pure` and `--auto`, but no empty-tool
switch; tools can only be denied through agent/permission configuration, which is a request, not an
enforced and reported surface, and its JSON events carry no tool inventory. A model-boundary relay
like Codex's would need a sanitizer per provider wire format (OpenAI Responses and Chat Completions,
Anthropic Messages, Google, local servers) behind OpenCode's provider layer. Route `requireToolFree`
callers to Claude Code or Codex until that exists.

## Live probe

The opt-in probe is not part of the test suite because it consumes provider tokens. Run it only
against a loopback RedCompute instance:

```powershell
$env:REDLEAF_EXECUTION_TOKEN = '<short-lived execution bearer>'
./scripts/probe-tool-free-generate.ps1 -BaseUri http://127.0.0.1:18800
```

The script refuses non-loopback origins, never prints the bearer, creates a unique canary under
`REDLEAF_SCRATCH_DIR` (or the system temporary directory), runs absolute-read, directory-listing,
shell-read, and URL-fetch probes for every configured `Generate` provider (sending
`requireToolFree` where `ToolFreeGenerate` is claimed), and prints a pass/fail table. A pass requires
the capability claim, `isolation: verified-tool-free`, an empty init tool list where the runtime
reports one, no tool event, and no canary disclosure.

## History

- 2026-10-17: the first deployed Claude flags (`--bare`, `--mcp-config {}`) failed closed against
  Claude Code 2.1.300 ("mcpServers: expected record"; `--bare` skips OAuth login). The current flags
  omit `--bare` and pass `{"mcpServers":{}}`.
- 2026-10-17: Codex 0.159.2 with `features.shell_tool = false` still invoked a `codex_apps` MCP tool
  for a URL probe, so Codex did not assert `ToolFreeGenerate` until the relay above (2026-10-22).
