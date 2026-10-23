# Tool-free `/ai-session/generate`, isolation reporting, response schemas, and images

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
| `images` | Up to 4 images for the final user message, each `{ "attachmentId": "..." }` (staged by the same caller through `POST /ai-session/input-attachments`) or `{ "mediaType": "image/png", "base64": "..." }`, the shapes session messages already use. See [Images](#images-vision). |

Response fields added (also stored in the job `resultJson`):

| Field | Meaning |
|---|---|
| `isolation` | What was actually applied: `verified-tool-free`, `sandboxed-best-effort`, `unrestricted`, or `unverified` (provider reported nothing). |
| `isolationMechanism` | Short mechanism name, e.g. `claude-empty-init-tools`, `codex-model-boundary-relay`, `codex-sandbox:read-only`. |
| `json` | Only with `responseSchema`: the JSON value extracted from the model text, or `null`. |
| `schemaValid` | Only with `responseSchema`: `true` when `json` was found and validated with no errors. |
| `errors` | Only with `responseSchema`: validation errors with JSON paths, e.g. `$.verdict: must be one of "allow", "block"` (at most 50). |
| `schemaMode` | Only with `responseSchema`: `native` when the provider also enforced the schema with its own structured output, else `prompt`. |
| `vision` | `true` when the resolved provider accepts generate images (`GenerateVision`). Also reported per provider by `GET /ai-session/providers/configured`. |
| `imageCount` | Number of validated images forwarded with this call. |

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
it natively. Either way, RedCompute extracts the JSON deterministically and validates it in C#. The
model and provider are never trusted for validity:

- Extraction accepts the whole message as the JSON (optionally inside one Markdown fence), or else
  exactly one balanced object/array in the text. Two candidates, such as an injected verdict echoed
  next to the real one, are ambiguous and extract nothing (`more than one JSON candidate`). The scan
  is one linear pass over at most 256 Ki characters.
- Model output is untrusted input: a value whose strings or property names cannot be decoded
  (unpaired UTF-16 surrogates such as `"\ud800"`) is reported as a schema failure, never thrown and
  never echoed in `json`.
- `pattern` uses .NET `RegexOptions.NonBacktracking` (linear-time). Patterns it cannot run
  (backreferences, lookarounds, atomic groups, oversized automata) are rejected at parse. The
  dialect is .NET, not ECMA-262: `\d`/`\w`/`\s` are Unicode-aware, `$` also matches before a final
  `\n`, `.` excludes only `\n`, .NET-only syntax (`[a-z-[aeiou]]`, `(?i)`, `\p{IsGreek}`) is
  accepted and ECMA-only syntax (`\u{1F600}`, `\p{Script=Greek}`) is not. Patterns are unanchored,
  as in JSON Schema.
- Validation has an aggregate budget (500,000 work units, one per value visited plus one per
  pattern input character, and 250 ms); exceeding it is a schema error.

Native strict mode is used only for schemas inside OpenAI's documented strict subset
(developers.openai.com/api/docs/guides/structured-outputs, checked 2026-10-22): object root;
every node typed with one type or one type plus `null`; every object closed, all properties
required; no `const`, `pattern`, `minLength` or `maxLength`; enum values matching the type; at most
10 nesting levels, 5000 properties and 1000 enum values (and 15,000 characters for an enum with more
than 250 values). The root `$schema` keyword is stripped. Anything else is prompt-only rather than
risking a 400, and if the API still rejects the schema (`invalid_json_schema` on
`text.format.schema`, refused before generation and not billed), Codex reruns once in prompt mode.

## Images (vision)

Image inputs exist so a reviewer can see what a user saw, including text injected into an image.
They never weaken the tool-free boundary: the tool surface of a vision call is exactly that of a
text call, and providers only receive bytes RedCompute has validated.

- A provider without `GenerateVision` answers HTTP 422 `vision_unavailable` before any image is
  decoded. Callers fail closed; images are never silently dropped.
- Refused images answer HTTP 422 `invalid_images` with an `errors` array (`images[1]: ...`). Any
  unexpected failure while reading or decoding an image is also `invalid_images`, never a 500.
- Limits: at most 4 images, 5 MiB each (checked before base64 decoding or reading a staged file),
  at most 4096 px per side (and 4096 x 4096 pixels). PNG, JPEG, still WebP, GIF.
- Validation (`GenerateImageValidator`): the format sniff must match the declared media type; the
  container is walked structurally (PNG chunk CRCs through `IEND` with nothing after it, JPEG
  markers to a scan with the data ending in `EOI`, WebP RIFF size, and every GIF block from the
  colour table to the trailer, at most 4096 blocks), because the Windows decoder (WIC) silently fills
  in truncated PNG and JPEG data. Dimensions come from the headers and are checked before any pixel
  buffer exists: the canvas, and for GIF every frame's image descriptor, which must be at most
  4096 px per side and lie inside the canvas. (WIC decodes a GIF frame at its own declared size: a
  1.3 KB GIF with a 2x2 canvas and a 20000x20000 frame made it allocate about 530 MB, and at 40000 it
  threw `ArithmeticException`.) WIC then fully decodes the first frame, one decode at a time, and
  its size must match the headers. A GIF whose first frame is smaller than the canvas is composited
  at its offset onto a transparent canvas.
- Nothing the caller sent is forwarded. Every image, whatever its format, is re-encoded as a fresh
  PNG from its decoded pixels, after applying any EXIF orientation. That drops EXIF/GPS, PNG text
  chunks, JPEG APPn/COM/XMP segments, polyglot payloads and anything after the image data. If the
  PNG exceeds 5 MiB it is downscaled (at most four times) until it fits, else refused. Animated
  WebP is refused: WIC cannot decode it.
- `GenerateImage.Sha256` hashes the forwarded PNG (the Codex relay's evidence records the same
  hash); `SourceSha256` hashes the caller's original. Jobs record media type, size, dimensions and
  both hashes, never the bytes. An attachment owned by another caller reads the same as a missing
  one.
- Request body: generate alone raises Kestrel's 30,000,000-byte default to 32 MiB, which holds four
  images at the base64 cap (about 28 MB) plus a prompt; anything larger answers 413
  `request_too_large` (declared or streamed). The 413 is sent before the upload finishes, so a
  client that streams the whole body first may see a reset connection instead; send
  `Expect: 100-continue` with large bodies to read the 413 reliably. Large images are best sent by
  `attachmentId`.
- Follow-up candidate, not built: WebP is decoded in-process by the libwebp-based codec Windows
  provides to WIC through the Store "WebP Image Extensions" package (`Microsoft.WebpImageExtension`, 1.2.31.0 on the development machine; PNG, JPEG and GIF use WIC's
  built-in codecs). A hostile WebP therefore exercises a third-party native decoder inside the
  RedCompute process. Decoding untrusted images in an isolated, resource-limited helper process
  (returning only validated pixels) would contain that; without the codec, WebP images are refused.

Claude Code: images travel inline as base64 `image` blocks in one stream-json user message on stdin
(`--input-format stream-json`); nothing is written to disk. The launch keeps `--tools ""`,
`--strict-mcp-config` and the empty MCP config, and the init event must still list zero tools.
Verified live with 2.1.308 (haiku): init `tools=[]`, `mcp_servers=[]`, answer
`IMAGE: ORCHID 7319; FILES: NOTOOLS` for the fixture image plus a request to read `C:\Windows\win.ini`.

Codex, tool-free: images are not given to Codex at all. Codex 0.163's `--image` flag re-encodes
large images (a 3000x2000 PNG reached the wire as 1920x1280) and adds text parts announcing the file
path, so the relay could not prove which bytes reached the model. Instead the relay keeps only
`input_text`/`output_text` parts in every message (removing any image, file or unknown part Codex
sends) and appends the validated images as `input_image` data URLs to the final user message of
each model request. The call is verified only if every request carried every image; the evidence
records each injected image's SHA-256. Verified live (ChatGPT sign-in): answer
`IMAGE: ORCHID 7319; FILES: NOTOOLS`, injected SHA-256 equal to the fixture's, 11 tool definitions
removed, no refusals.

Codex without `requireToolFree` passes images with one `--image=<file>` per image from a private
temporary directory outside the empty working directory, deleted after the call (this path keeps
Codex's tools, as before, and reports `sandboxed-best-effort`).

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

Execute always passes `--dangerously-skip-permissions`, so it reports `verified-tool-free` only when
the caller passed an explicit empty `tools` array and that run's init event reported no tools.
Everything else is `unrestricted`: no grant (`claude-skip-permissions:default-tools`), a non-empty
grant (`claude-skip-permissions:tool-grant`), or an empty grant that init did not confirm
(`claude-skip-permissions:empty-grant-unverified`). A tool allowlist with permissions skipped is not
a sandbox.

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
  -c model_providers.redcompute_tool_free.request_max_retries=0
  -c model_providers.redcompute_tool_free.stream_max_retries=0
  -c model_providers.redcompute_tool_free.stream_idle_timeout_ms=<(timeout + 60) * 1000>
  [--output-schema <file>] [-c model_reasoning_effort=...] [--model ...]
```

The relay buffers each upstream response before Codex sees a byte, so Codex's own retry and idle
behaviour was measured against a stalling loopback server (codex-cli 0.163.0; all three keys are
present in the binary):

- headers sent, then silence: with default retries Codex re-sent the request 6 times (1 + 5
  `stream_max_retries`), i.e. six billed calls upstream. With `stream_max_retries=0` it sent 1 and
  failed with `idle timeout waiting for SSE` at the configured idle timeout.
- no headers at all (what the relay does while it buffers): a 3 s idle timeout did not fire, and a
  400 s header-less stall produced no error and no retry (1 POST) until the process was killed. The
  idle timer only runs once response headers arrive, and there is no hidden ~300 s pre-header
  timeout.

So tool-free calls disable both Codex retry loops, set the idle timeout past the call timeout in
case a later release starts it earlier, and do not rerun Codex after a failure (the generic
workspace-lock retry is off on this path). The bound that applies is RedCompute's own call
timeout (10–600 s), which kills the process; one call is billed at most once. The only automatic
second run is the schema fallback above, after a pre-generation 400.

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

Only `GET models` is passed through unchanged (no authority); every other route is refused. Besides
the unguessable per-call nonce, the relay requires `Host` to be exactly its own `127.0.0.1:<port>`
and refuses any request carrying an `Origin` header (DNS rebinding, browser pages); both fail the
call. The upstream client never follows redirects (`AllowAutoRedirect = false`), and an upstream 3xx
is refused with 502 rather than passed to Codex. The relay's evidence records, by name only, the
forwarded headers, dropped request fields, upstream URLs and statuses, and refusals. The call
reports `verified-tool-free` / `codex-model-boundary-relay` only when at least one model response
crossed the relay, nothing was refused, and Codex's own JSONL contains no item other than
`agent_message`, `reasoning` or `error`. Otherwise it fails closed with no model text. Without
`requireToolFree`, Codex generate is unchanged (`codex exec --sandbox read-only` from an empty
temporary directory) and reports `sandboxed-best-effort` / `codex-sandbox:read-only`.

On both Codex generate paths, only a schema inside the strict subset described above is passed as
`--output-schema` (with root `$schema` removed); Codex sends them as `text.format = {type: "json_schema", strict: true}`, which the
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
