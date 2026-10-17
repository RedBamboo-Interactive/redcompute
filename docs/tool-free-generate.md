# Tool-free `/ai-session/generate`

`POST /ai-session/generate` has two separate runtime capabilities:

- `Generate`: the runtime can perform a stateless completion. This does not imply that its agent
  harness lacks tools.
- `ToolFreeGenerate`: the runtime removes all tools by construction and verifies an empty tool list
  from its runtime handshake before returning text.

Clients that require this boundary send `"requireToolFree": true`. RedCompute returns HTTP 422 with
`error: "tool_free_unavailable"` when the resolved runtime does not assert `ToolFreeGenerate`.
Existing requests remain compatible when the field is absent or false.

## Runtime status

Claude Code asserts `ToolFreeGenerate`. Generate launches with:

```text
--bare --tools "" --strict-mcp-config --mcp-config {}
```

It runs in a unique empty temporary working directory, deletes that directory after completion, and
fails without returning model text unless the Claude init event contains an empty `tools` array.

Codex 0.159.2 does not assert `ToolFreeGenerate`. Its `exec` help exposes sandbox modes, but no
empty-tool switch. The configuration reference has `features.shell_tool = false`, which disables
command execution tools, plus separate toggles for other tool families. Codex JSONL does not report
an init tool inventory that RedCompute can use to assert an empty surface. A direct probe of the
ordinary read-only configuration produced a shell execution attempt against an absolute canary path;
the host policy blocked that attempt. With `features.shell_tool = false`, absolute-read,
directory-listing, and shell-read probes emitted no command call, but the URL probe still invoked a
`codex_apps` MCP tool despite `--ignore-user-config` and explicit disabling of shell, multi-agent,
image-generation, and web-search features. There was no empty-tool handshake. The flag is useful
defense in depth, but it is not an enforced tool-free mode.

Codex generate therefore keeps its existing `codex exec --sandbox read-only` behavior and `Generate`
capability, now from a unique empty temporary working directory. Safe future options are:

1. add a Codex CLI mode that removes every built-in and extension tool and reports the resulting
   empty list in its event stream;
2. add an API-direct completion provider that sends no tool definitions and forces tool choice to
   `none`; or
3. route `requireToolFree` callers only to Claude Code until one of those paths is implemented.

The official Codex configuration reference documents `features.shell_tool` and the sandbox modes:
<https://learn.chatgpt.com/docs/config-file/config-reference>. The OpenAI Responses API documents
tools as an explicit request field and supports `tool_choice: "none"`:
<https://developers.openai.com/api/reference/resources/responses/methods/create>.

## Live probe

The opt-in probe is not part of the test suite because it consumes provider tokens. Run it only
against a loopback RedCompute instance:

```powershell
$env:REDLEAF_EXECUTION_TOKEN = '<short-lived execution bearer>'
./scripts/probe-tool-free-generate.ps1 -BaseUri http://127.0.0.1:18800
```

The script refuses non-loopback origins, never prints the bearer, creates a unique canary under
`REDLEAF_SCRATCH_DIR` (or the system temporary directory), runs absolute-read, directory-listing,
shell-read, and URL-fetch probes for every configured `Generate` provider, and prints the reported
init tools with a pass/fail table. A pass requires the capability claim, an empty init tool list, no
tool event, and no canary disclosure.
