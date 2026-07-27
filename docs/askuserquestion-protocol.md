# AskUserQuestion over the Claude Code stream-json control protocol

Findings are from the installed CLI, not inference. Two sources:

- **`@anthropic-ai/claude-agent-sdk@0.3.170`** — `sdk.d.ts` (hand-authored TypeScript
  declarations) and the bundled native binary
  `node_modules/@anthropic-ai/claude-agent-sdk/node_modules/@anthropic-ai/claude-agent-sdk-win32-x64/claude.exe`.
  **This is the binary RedCompute actually spawns** (`ClaudeSessionService.ResolveClaudePath`,
  `ClaudeSessionService.cs:1440`).
- **`@anthropic-ai/claude-code@2.1.220`** — `sdk-tools.d.ts` (generated from the tools'
  JSON Schemas) and `bin/claude.exe`.

Both `claude.exe` files are Bun single-file executables with the JS bundle embedded as
plain text. Citations below are byte offsets into
`C:\Users\laure\AppData\Roaming\npm\node_modules\@anthropic-ai\claude-code\bin\claude.exe`
(v2.1.220) unless noted; every shape was then **verified live** against the 0.3.170 binary
(see "Live verification").

---

## 1. Why the bug happens

`AskUserQuestion.checkPermissions` returns `behavior: "ask"` **unconditionally** — it is
not a safety gate, it is how the tool requests data:

```js
// claude.exe @244819227  (AskUserQuestion tool definition)
requiresUserInteraction(){return!0},
async checkPermissions(e){
  return {behavior:"ask", message:"Answer questions?",
          updatedInput:{questions:e.questions, ...e.metadata&&{metadata:e.metadata}}}
},
async call(e,t){ let{questions:r,answers:n={},annotations:o}=e; ... }
```

`"Answer questions?"` is that `message`. When the ask cannot be routed to a client it is
handed back as the denial reason — which is exactly the `<error>Answer questions?</error>`
the model sees.

### `bypassPermissions` does NOT suppress it

In the permission evaluator (`sN_`, `claude.exe @249673843`) the ordering is decisive:

```js
l = await e.checkPermissions(y, r);              // -> {behavior:"ask", message:"Answer questions?"}
if (l?.behavior === "deny") return l;
let c = ufn(o, e, t, "ask"); if (c) ...           // ask rules
if (e.requiresUserInteraction?.())                // <-- AskUserQuestion RETURNS HERE
    return l?.behavior === "ask" ? l : {behavior:"ask", ...};
...
let u = En(r), d = i9e(e, u),
    p = d === "bypassPermissions" || (d === "plan" && u.isBypassPermissionsModeAvailable);
if (p) return {behavior:"allow", updatedInput:Hrp(l,t), decisionReason:{type:"mode",mode:d}};
```

The `requiresUserInteraction()` early-return sits **above** the `bypassPermissions`
short-circuit. Tools that declare `requiresUserInteraction()` (AskUserQuestion,
ExitPlanMode) are therefore exempt from bypass and still produce an `ask`.

Downstream in `nN_` (`@249677992`) the auto-mode block that could rewrite the ask is gated
on `tqs(u)`, and `tqs(e){return e==="auto"||e==="plan"&&xz()}` (`@249662741`) — false under
`bypassPermissions`. So the `ask` survives intact.

**Conclusion: the permission-mode flags are not the cause and do not need to change.**

### The actual cause: no `--permission-prompt-tool stdio`

Whether an `ask` becomes an outbound `control_request` or a silent denial is decided by
one function (`claude.exe @258597315`):

```js
function ekm(e, t, r, n){
  if (e === "stdio") return t.createCanUseTool(n);          // <-- control-protocol bridge
  if (!e) return async (i,s,a,l,c,u) => u ?? await fL(i,s,a,l,c);  // <-- passthrough: ask stays an ask
  ...                                                        // named MCP permission-prompt tool
}
```

and its only caller (`@258502226`):

```js
let q = l.sdkUrl ? "stdio" : l.permissionPromptToolName;
let re = ekm(q, _, () => t().mcp.tools, Y);
```

With no `--permission-prompt-tool`, the `ask` is returned unchanged and the headless
session converts it to a denial carrying `message` — `"Answer questions?"`. The relevant
denial builder (`Z6s`, `@249669782`):

```js
function Z6s(e){ return {behavior:"deny", message:e,
  decisionReason:{type:"asyncAgent",
    reason:"Action requires interactive approval and permission prompts are not available in this context"}} }
```

There is also a dedicated constant for this class of failure (`@238800579`):
`KMi = {type:"asyncAgent", reason:"tool requires user interaction; no prompt available in headless mode"}`.

**`--permission-prompt-tool stdio` is the single missing piece.** It is a documented CLI
flag (`--permission-prompt-tool <tool>`, "MCP tool to use for permission prompts (only
works with --print)", `@165807458`); `stdio` is the magic value the Agent SDK itself passes
(the SDK's own arg builder contains the literal pair `--permission-prompt-tool` / `stdio`,
`@222648408`).

---

## 2. Outbound envelope: CLI → client

Emitted by `Query.createCanUseTool` (`@258391552`), framed by `sendRequest`
(`@258390137`): `{type:"control_request", request_id:n, request:e}`.

```jsonc
{
  "type": "control_request",
  "request_id": "d35da532-03c6-435e-a8b5-ef318f396d3d",   // top level, NOT inside `request`
  "request": {
    "subtype": "can_use_tool",
    "tool_name": "AskUserQuestion",
    "display_name": "AskUserQuestion",
    "input": { "questions": [ /* AskUserQuestionInput.questions */ ] },
    "tool_use_id": "toolu_01AdKovbxsrRmQt5aeWDMmrL",

    // present only when applicable / on newer builds:
    "description": "…",
    "permission_suggestions": [ /* PermissionUpdate[] */ ],
    "blocked_path": "…",
    "decision_reason": "…",
    "decision_reason_type": "rule|mode|subcommandResults|permissionPromptTool|hook|asyncAgent|sandboxOverride|workingDir|safetyCheck|classifier|other",
    "classifier_approvable": true,
    "matched_ask_rule": { "source": "…", "tool_name": "…", "rule_content": "…" },
    "title": "…",
    "agent_id": "…",
    "suppress_always_allow_rule": true,
    "requires_user_interaction": true
  }
}
```

Typed as `SDKControlRequest` / `SDKControlPermissionRequest` in
`claude-agent-sdk/sdk.d.ts:3412` and `:3309`.

> **Version caveat that matters for the implementation.** `requires_user_interaction` is a
> 2.1.220-era field (`@254700647`, `@258391552`). The **0.3.170 binary RedCompute spawns
> does not emit it** — the observed request contained only `subtype`, `tool_name`,
> `display_name`, `input`, `tool_use_id`. Routing must therefore key off
> `tool_name == "AskUserQuestion"`, not `requires_user_interaction`.

`input` is the tool input **after** `checkPermissions` rewrote it — i.e. exactly
`{questions: [...]}` (plus `metadata` when the model supplied it).

### Cancellation

If the turn aborts while a request is outstanding the CLI emits
`{"type":"control_cancel_request","request_id":"…"}` and rejects its own promise
(`@258390137`). A client holding a pending question must drop it on this frame.

---

## 3. Inbound response: client → CLI

Written as one JSON line to the CLI's stdin. Envelope is `ControlResponse`
(`sdk.d.ts:307`); the client-side reference implementation is `@250482508`:

```js
{type:"control_response", response:{subtype:"success", request_id:e.request_id, response:r}}
// on failure:
{type:"control_response", response:{subtype:"error",   request_id:e.request_id, error:le(r)}}
```

The inner `response` payload for `can_use_tool` is validated against
`n1n = union([UdE, BdE])` (`@258377046`, `@258378640`):

```js
UdE = object({ behavior: literal("allow"),
               updatedInput:       record(string(), unknown()).optional(),
               updatedPermissions: array(PermissionUpdate).optional(),
               toolUseID:          string().optional(),
               decisionClassification: enum(["user_temporary","user_permanent","user_reject"]).optional() })

BdE = object({ behavior: literal("deny"),
               message:   string(),
               interrupt: boolean().optional(),
               toolUseID: string().optional(),
               decisionClassification: … })
```

Mirrored as `PermissionResult` in `sdk.d.ts:2069`. The CLI's own validation error text
states the contract verbatim (`@90958525`):

> `Expected {behavior: 'allow', updatedInput?: object} or {behavior: 'deny', message: string}.`

`updatedInput` **must satisfy the tool's input schema** or the call is rejected
(`@124495680`: `"The permission handler returned updatedInput for X that failed schema
validation"`). An `allow` with a missing/empty `updatedInput` falls back to the original
input (`o1n`, `@258377046`; log string at `@118334314`).

---

## 4. How an AskUserQuestion answer is encoded

The answer rides back inside `updatedInput`. `AskUserQuestion.call` reads it straight off
its own input (`@244819227`):

```js
async call(e,t){ let{questions:r, answers:n={}, annotations:o}=e, {response:i, afkTimeoutMs:s}=e; … }
```

`answers` is keyed by the **question text**, per `AskUserQuestionOutput`
(`claude-code/sdk-tools.d.ts:3396`):

```ts
answers: { [questionText: string]: string }   // multi-select answers are comma-separated
response?: string                              // freeform text typed instead of choosing
annotations?: { [questionText: string]: { preview?: string; notes?: string } }
afkTimeoutMs?: number                          // set only when the dialog auto-resolved
```

So the response is:

```jsonc
{
  "type": "control_response",
  "response": {
    "subtype": "success",
    "request_id": "<request_id from the control_request>",
    "response": {
      "behavior": "allow",
      "updatedInput": {
        "questions": [ /* echoed verbatim from request.input.questions */ ],
        "answers": { "Which colour do you prefer?": "Blue" }
      }
    }
  }
}
```

Notes:

- Keys must match `question` **exactly**; a question with no matching key renders as
  `"<question>"=(no option selected)` in the tool result
  (`mapToolResultToToolResultBlockParam`, `@244819227`).
- Values are option **`label`** strings. Multi-select → one string, comma-separated.
- Free text the user typed instead of picking an option goes in `response`, not `answers`.
- Echoing `questions` is required: `updatedInput` is validated against the whole
  `AskUserQuestionInput` schema, in which `questions` is mandatory.

### Denial

```jsonc
{"type":"control_response","response":{"subtype":"success","request_id":"…",
 "response":{"behavior":"deny","message":"<shown to the model as the tool result>"}}}
```

Note the outer subtype stays `"success"` — it describes the transport, not the decision.
The `message` becomes the tool_result content with `is_error: true`.

---

## 5. Live verification

Against the exact binary RedCompute spawns (`claude-agent-sdk-win32-x64@0.3.170`), with
RedCompute's real session arguments plus `--permission-prompt-tool stdio`:

```
--output-format stream-json --verbose --input-format stream-json
--permission-mode bypassPermissions --thinking-display summarized
--permission-prompt-tool stdio
```

**Test 1 — answer round trip.** The CLI emitted the `control_request` exactly as
documented above (no `requires_user_interaction`, no `description`). Replying with
`{behavior:"allow", updatedInput:{questions:[…], answers:{"Which colour do you prefer?":"Blue"}}}`
produced:

```
tool_result: Your questions have been answered: "Which colour do you prefer?"="Blue".
             You can now continue with these answers in mind.
assistant:   Blue
result:      success
```

**Test 2 — denial.** Replying `{behavior:"deny", message:"No interactive client is
attached…"}` produced a `tool_result` with `is_error: true` whose content is that message
verbatim.

**Test 3 — no prompt storm (the no-regression check).** Same flags, a turn that ran
`Bash`, `Write` and `Edit`: **zero** `control_request` frames were emitted; all three tools
executed unprompted. This confirms the ordering analysis in §1 — under
`bypassPermissions` only `requiresUserInteraction()` tools reach the ask path, so adding
`--permission-prompt-tool stdio` does not reintroduce permission prompts.

---

## 6. Consequences for RedCompute

| Question | Answer |
| --- | --- |
| Does `bypassPermissions` suppress the `control_request`? | **No.** The `requiresUserInteraction()` early-return precedes the bypass short-circuit. |
| Do the permission-mode flags need to change? | **No.** `--permission-mode bypassPermissions` (sessions) and `--dangerously-skip-permissions` (agent runs) stay as they are. |
| What was actually missing? | `--permission-prompt-tool stdio` on persistent sessions, plus an inbound `control_request` handler and a stdin `control_response` writer. |
| Can one-shot agent runs answer? | **No — and the flag must not be added there.** `ExecuteAgentAsync` closes stdin right after writing the prompt (`WriteStdinAsync`, `ClaudeSessionService.cs:1965`), so there is no channel to reply on; adding the flag would hang the run instead of failing it. They keep the current self-denial behaviour. |
