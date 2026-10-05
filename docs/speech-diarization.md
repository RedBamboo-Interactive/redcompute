# Speech diarization

RedCompute exposes speaker diarization as an independent `diarization` capability and as an optional enrichment
of `stt`. The separation is deliberate: transcription answers what was said, diarization assigns anonymous
speaker channels to time ranges, and named-speaker identification is a later protected derivation.

## Runtime

The local provider runs NVIDIA `Nemotron-3-Diarization` at pinned model revision
`f667ed73aee57d40cc39428eb768b4fd87a0a29e`. It supports up to eight anonymous speakers and uses arrival-ordered
speaker caching for stateful chunks. RedCompute ships both local Python servers inside its release payload:

- `backends/stt-local` provides faster-whisper and optional diarization composition.
- `backends/diarization-local` provides standalone offline and session-based streaming diarization.

Both providers receive audio through RedCompute's authenticated, job-backed JSON API. Source audio remains with
the caller; outputs are derived JSON artifacts. The backend requires WSL Ubuntu 24.04, FFmpeg and an NVIDIA CUDA
runtime. Its dedicated `~/diarization-env` virtual environment is bootstrapped from the pinned requirements.

## Standalone contract

`POST /diarization/generate` accepts `audio_base64` and:

- `mode`: `offline` or `streaming`.
- `session_id` and `chunk_id`: required in streaming mode.
- `is_first_chunk` and `is_last_chunk`: explicit stream lifecycle boundaries.
- `max_speakers`: integer from 1 through 8.

The result contract is `diarization.v1`. It returns anonymous turns, overlap intervals, speaker channels, model
identity and revision, and timing metadata. A streaming `chunk_id` is idempotent within its session. Speaker
labels are stable only inside one streaming session or one offline recording.

## Optional STT enrichment

Existing STT requests are unchanged. Set `diarize: true` to force word timestamps, run the same audio through
the diarization provider and add `speakerId`, `speakerConfidence`, and `overlappingSpeakerIds` to words when a
speaker interval is available. Streaming STT additionally accepts the `diarization_*` equivalents of the
standalone session fields.

If diarization is explicitly requested and unavailable, the STT job fails instead of silently returning an
unattributed transcript. In streaming mode the diarizer may retain a small look-ahead tail; later chunks or the
final flush emit those speaker intervals. Consumers reconcile them by session time rather than assuming every
word in the current chunk is immediately final.

## Identity boundary

`speaker_00` means only the first anonymous voice channel in that session. It is not an account, player,
character, NPC or agent identity. Voice enrollment and account matching require a separate capability with
protected embeddings and confidence thresholds. Roleplay persona attribution remains distinct even after a
human speaker is identified.

Overlapping turns report simultaneous activity but do not separate a mixed microphone signal into independent
audio sources. Recovering every word from simultaneous speech requires source separation or individual channels.
