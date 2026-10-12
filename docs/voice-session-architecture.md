# Voice sessions: architecture and implementation plan

Status: accepted for implementation on 2026-10-11 in Nova discussion `65af5151`.

## Outcome

Leaf gains a provider-neutral `voice-session` capability. The first provider is OpenAI GPT-Live over WebRTC. A voice session is a low-latency media and conversational shell around the same Nova discussion and the same authoritative Nova AI session. It is not a second Agent, a second memory store, or a private transcript that disappears when the call ends.

The first product surface is Nova web on mobile and desktop. The browser carries microphone and speaker media directly to the provider over WebRTC. RedLeaf and RedCompute retain identity, authorization, provider credentials, transcript ownership, delegation, tools, and durable state.

## Product invariants

1. Starting voice never creates a second Nova discussion or replaces the linked AI session.
2. The OpenAI API key never reaches the browser, SDP, logs, transcript, query string, or Nova frontend bundle.
3. RedCompute owns provider-neutral voice lifecycle and ordered voice events. RedLeaf/Nova owns the authorized durable discussion projection. The existing AI session remains the authority for Nova memory, tools, actions, and full delegated answers.
4. Every canonical conversational turn has one stable `messageUid`. Provider event IDs and delegation IDs are correlation identities, not message identities.
5. Casual spoken turns are mirrored into the existing AI session without triggering a second model turn. Delegated user requests enter through Nova's ordinary message pipeline so current context, memory, tools, queueing, and provenance remain intact.
6. A delegated backend answer is the canonical assistant answer. GPT-Live's spoken rendition is delivery metadata and must not become a duplicate assistant message.
7. Backend completion and audible delivery are separate facts. An action may complete after the caller interrupts or closes audio. The UI and records must never claim the user heard an answer merely because the backend completed.
8. Transcript fragments are not turns. They are accumulated by session timeline and finalized only at a proven speaker transition, delegation boundary, or graceful close. Gaps in packet arrival are not used as a completion signal.
9. A service restart ends an ephemeral voice transport visibly and safely. It may not replay a delegated action. Canonical AI-session and discussion records survive normally.
10. Existing typed chat, queue, commentary/final, tool, pagination, reconnect, confidential-discussion, and maintenance behavior remains unchanged when voice is unused.

## System boundary

### Browser

The Nova client owns only the user gesture and media plane:

- asks for microphone permission;
- creates `RTCPeerConnection`, the `oai-events` data channel, and an audio output element;
- sends the SDP offer to Nova on the current discussion route;
- applies the returned SDP answer;
- renders ephemeral captions and connection state from the data channel;
- exposes mute and end controls;
- reports coarse playback state without claiming per-utterance audibility.

The browser does not receive the OpenAI key, register tools, call Leaf tools, choose an arbitrary callback, or write canonical transcript records.

### Nova plugin

Nova is the authorized product adapter:

- validates access to the discussion and its Agent;
- ensures the discussion has its ordinary AI session before voice begins;
- builds a bounded voice prompt identifying the Agent and explaining the client-delegation policy;
- creates the voice session through the signed Compute gateway with the discussion, Agent, beneficiary, and AI-session provenance;
- receives signed loopback voice events from RedCompute;
- persists finalized casual turns into the existing discussion and AI-session transcript with stable identities;
- sends delegated requests through `MessagePipeline`, preserving current Nova context, memory, tools, queueing, and signed provenance;
- observes the resulting AI-session turn, extracts the verified final answer, and returns it to the matching voice delegation;
- publishes only the ordinary canonical discussion invalidations already understood by shared Chat.

Nova does not implement provider-specific WebSocket events or own the media connection.

### RedCompute voice-session core

RedCompute owns the provider-neutral runtime:

- capability/provider resolution;
- voice-session admission, lifecycle, status, duration, and close;
- association with the immutable caller provenance, discussion, Agent, and AI session;
- normalized transcript fragments and utterances;
- delegation correlation and exactly-once admission identity;
- authenticated callbacks to Nova;
- provider updates for delegation progress/results;
- graceful close and final usage capture.

Voice transports are ephemeral. The core keeps active state in memory and emits durable conversational facts to the owning Nova discussion as they become stable. A restart closes active media rather than attempting to resurrect an external call.

### OpenAI GPT-Live provider

The provider plugin owns only OpenAI protocol details:

- creates `POST /v1/live/sessions` with `{ session, transport: { type: "webrtc", sdp } }`;
- uses `gpt-live-1`, a configured voice, input transcription, and client delegation;
- returns only the opaque OpenAI session ID and SDP answer to the core;
- attaches a server-side sideband WebSocket to `/v1/live/sessions/{id}/attach`;
- normalizes input/output transcript deltas, delegation-created, error, usage, and closed events;
- accepts provider-neutral thinking/commentary/instruction updates and maps them to GPT-Live commands;
- closes gracefully and waits for `session.closed` within a bounded drain.

It never receives Leaf tools or RedLeaf credentials.

## API contract

The browser calls Nova. Nova calls RedCompute through `IComputeGateway`.

### Nova surface

`POST /api/apps/nova/discussions/{discussionId}/voice-sessions`

Request:

```json
{
  "offerSdp": "...",
  "provider": "openai-live",
  "model": "gpt-live-1",
  "voice": "marin"
}
```

Response:

```json
{
  "id": "vs_...",
  "provider": "openai-live",
  "model": "gpt-live-1",
  "answerSdp": "...",
  "state": "connecting"
}
```

`GET /api/apps/nova/discussions/{discussionId}/voice-sessions/{voiceSessionId}` returns safe status, elapsed time, delegation state, and final usage when available.

`POST /api/apps/nova/discussions/{discussionId}/voice-sessions/{voiceSessionId}/close` requests graceful close and is idempotent.

### RedCompute surface

`POST /voice-session/sessions` creates a session. Required signed execution context includes the discussion and linked AI session. The request carries the SDP offer and bounded provider-neutral prompt. Callback routes are server-owned configuration, not caller-supplied arbitrary URLs.

`GET /voice-session/sessions/{id}` returns a confidentiality-safe lifecycle snapshot after authorization.

`POST /voice-session/sessions/{id}/delegations/{delegationId}/update` accepts `thinking`, `commentary`, or `failed` updates from the owning Nova execution. It is idempotent by `eventId`.

`POST /voice-session/sessions/{id}/close` is idempotent and performs a bounded graceful shutdown.

`POST /voice-session/previews` creates a receive-only, transcript-free audition from an SDP offer, provider and
voice. The sample text and maximum lifetime are server-owned. After `session.started`, RedCompute asks an optional
`IVoicePreviewProvider` to speak the fixed sample over its trusted control channel. Preview transcript and delegation
events are discarded, Nova is never called, and the ordinary close route remains available for prompt browser
teardown. A hard server timeout closes abandoned previews independently of the browser.

The Nova-to-RedCompute contract never exposes the provider API key or the sideband socket.

## Transcript and delegation state machine

Transcript deltas are ordered by `(startMs, endMs, arrivalOrdinal)`. The accumulator keeps one open utterance per current speaker.

- A speaker change finalizes the previous open utterance.
- `session.delegation.created` finalizes user text through the delegation offset and binds it to the provider delegation ID.
- Graceful close finalizes remaining non-empty utterances.
- Network arrival gaps alone do not finalize text.
- Repeated provider event IDs are ignored. Distinct events with identical text are retained.

For a casual exchange:

1. user utterance finalizes when assistant output begins;
2. RedCompute posts a stable `voice.utterance.finalized` callback;
3. Nova injects the user turn into the current AI-session transcript and persists the discussion bridge;
4. assistant utterance finalizes on the next user input or close and is persisted the same way.

For delegated work:

1. the delegation boundary finalizes the user utterance and assigns `voice:{voiceSessionId}:{delegationId}:user` as the stable operation/message identity;
2. RedCompute posts `voice.delegation.created` once;
3. Nova calls its ordinary `MessagePipeline` with `inputMethod=voice`, the stable UID, and the transcript text;
4. the existing AI session executes with Nova's full instructions, memory, context, and tools;
5. Nova observes the canonical final after that UID and posts a matching commentary update to RedCompute;
6. RedCompute appends commentary on the sideband using the original delegation ID;
7. the backend final remains the one canonical assistant message. Output transcript caused by that commentary is marked as spoken delivery and is not persisted as another assistant turn.

If voice closes during step 4, the backend work continues under normal Nova semantics. Its canonical result remains visible in chat. Delivery to the closed voice session becomes `not_delivered`, never `failed_action`.

## Security and privacy

- All browser routes use the existing RedLeaf authenticated origin and discussion access policy.
- All Nova mutations into RedCompute use signed execution identity with discussion and session context.
- RedCompute callbacks target a fixed loopback Nova route and use authenticated service transport. No arbitrary callback URL is accepted.
- Provider credentials come from the existing vaulted Provider entity snapshot. The provider refuses admission when the key is absent.
- SDP is treated as sensitive transient data: no structured logging of request/response bodies and `private, no-store` on every route.
- Confidential discussions retain owner and owning-Agent checks. Safe status events omit transcript text.
- Provider errors are normalized before reaching the browser. Raw headers, keys, and upstream bodies are never returned.
- Settings previews are receive-only and never request microphone access. Their fixed sample cannot be used as a
  general speech-generation endpoint, and they retain no transcript or discussion association.

## Prompt and context policy

GPT-Live receives a compact voice-shell prompt, not the entire Agent workspace or hidden instructions. It includes:

- Agent display name and conversational voice guidance;
- that it may handle lightweight social conversation directly;
- that factual work, memory lookup, actions, tools, commitments, and uncertainty must delegate to the client;
- that it must not claim an action succeeded until commentary arrives from Leaf;
- that it should keep speech concise, natural, interruptible, and honest about waiting.

The current AI session receives the ordinary Nova context built by `MessagePipeline`; no parallel memory synchronization protocol is introduced.

## Failure behavior

- Missing API key: admission returns `provider_not_configured`; the UI links the user to Provider settings.
- Microphone denial: no server session is created.
- SDP/OpenAI failure: close peer connection, retain no canonical turn, show retryable error.
- Sideband failure while media remains up: mark delegation unavailable, instruct GPT-Live not to claim actions, keep casual audio if safe, and show degraded state.
- Nova callback failure: retry the same event ID with bounded backoff; never mint a new message UID.
- Backend queue/busy: preserve the existing Nova queue semantics and send quiet progress to GPT-Live.
- Browser disconnect: close after a short bounded grace; finalized stable turns are retained.
- RedCompute restart: media ends visibly; canonical delegated work is not replayed.

## Implementation plan

### Phase 1: provider-neutral RedCompute core

1. Add `voice-session` capability manifest and default disabled/unconfigured OpenAI Live provider entry.
2. Add Plugin SDK contracts for create, close, update, normalized events, and safe snapshots.
3. Add a coordinator with admission authorization, stable IDs, transcript accumulation, callback retry/deduplication, and shutdown drain.
4. Map typed discovery endpoints and tests for authorization, idempotency, transcript boundaries, close, callback retries, and secret redaction.

### Phase 2: GPT-Live provider

1. Add `RedCompute.Plugin.OpenAILive` and build/package wiring.
2. Implement JSON WebRTC session creation and sideband WebSocket attachment with the vaulted key.
3. Normalize GPT-Live transcript/delegation/lifecycle events.
4. Implement thinking/commentary updates and graceful close.
5. Add deterministic HTTP/WebSocket protocol tests with fake handlers; no paid call in the default suite.

### Phase 3: Nova backend adapter

1. Add `VoiceSessionService` and discussion routes.
2. Ensure the ordinary AI session exists and construct signed voice-session provenance.
3. Persist casual finalized turns using stable message identities and existing discussion invalidation semantics.
4. Admit delegated user work through `MessagePipeline`.
5. Observe the matching AI-session completion, return verified commentary, and keep backend/spoken-delivery states separate.
6. Add backend tests for access, confidential discussions, duplicate callbacks, casual turns, delegated turns, queueing, interruption, and closed voice delivery.

### Phase 4: Nova web experience

1. Add a voice-session client hook and focused full-screen/mobile panel.
2. Negotiate WebRTC only after the microphone user gesture.
3. Render live captions, mute, elapsed time, backend-working state, degraded state, and end.
4. Reconcile canonical chat through existing history/live paths; captions remain ephemeral.
5. Add unit tests and a browser fixture for connect, close, permission failure, reconnect failure, and mobile layout.

### Phase 5: provider entity and setup

1. Add the `voice-session` capability and OpenAI Live Provider package/seeds in `leaf-capabilities` once its current unrelated work is reconciled.
2. Vault the OpenAI project API key through the existing Provider settings flow.
3. Expose model `gpt-live-1`, supported voices, and cost text. Do not imply ChatGPT subscription billing.

### Phase 6: verification and deployment

1. Run focused tests first, then full affected RedCompute and Nova suites, frontend typecheck/build, and release contract checks.
2. Verify clean canonical commits and `HEAD == origin/main` for every changed repository.
3. Preflight active sessions and imminent schedules. Use the checked-in RedLeaf rebuild controller; never force active work down.
4. Verify loaded versions and served frontend hashes.
5. Run a zero-cost mocked browser acceptance, then one short real GPT-Live call only after a vaulted API key is present. Confirm casual continuity, one delegated tool-capable turn, interruption, close, reload, and no duplicate transcript turns.

## Explicit non-goals for the first slice

- replacing Nova's primary model with GPT-Live;
- sending Leaf tool schemas or credentials to OpenAI;
- resurrecting a provider call across a RedCompute restart;
- claiming exact per-utterance audible completion without a real playback acknowledgement;
- modifying shared Chat reconciliation unless a proven contract gap requires it;
- changing the existing recorded-message STT/TTS push-to-talk path.
