import os
import hashlib
import subprocess
import sys

VENV_DIR = os.path.expanduser("~/diarization-env")
SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
REQUIREMENTS = os.path.join(SCRIPT_DIR, "requirements.txt")
REQUIREMENTS_MARKER = os.path.join(VENV_DIR, ".redcompute-requirements.sha256")


def bootstrap():
    venv_python = os.path.join(VENV_DIR, "bin", "python")
    in_venv = sys.prefix != sys.base_prefix
    with open(REQUIREMENTS, "rb") as source:
        requirements_hash = hashlib.sha256(source.read()).hexdigest()
    if not in_venv and not os.path.exists(venv_python):
        subprocess.check_call([sys.executable, "-m", "venv", VENV_DIR])
    installed_hash = None
    try:
        with open(REQUIREMENTS_MARKER, encoding="ascii") as marker:
            installed_hash = marker.read().strip()
    except FileNotFoundError:
        pass
    if installed_hash != requirements_hash:
        python = sys.executable if in_venv else venv_python
        subprocess.check_call([python, "-m", "pip", "install", "--disable-pip-version-check", "-r", REQUIREMENTS])
        with open(REQUIREMENTS_MARKER, "w", encoding="ascii") as marker:
            marker.write(requirements_hash)
    if not in_venv:
        os.execv(venv_python, [venv_python, __file__] + sys.argv[1:])


bootstrap()

import argparse
import asyncio
import base64
import logging
import time
from dataclasses import dataclass, field
from typing import Any

import numpy as np
import torch
import uvicorn
from fastapi import FastAPI, HTTPException, Request
from transformers import AutoModelForAudioFrameClassification, AutoProcessor

logger = logging.getLogger("diarization-server")

CONTRACT_VERSION = "diarization.v1"
DEFAULT_MODEL = "nvidia/Nemotron-3-Diarization"
DEFAULT_REVISION = "f667ed73aee57d40cc39428eb768b4fd87a0a29e"
SAMPLE_RATE = 16000
FRAME_STRIDE_SECONDS = 0.01
MAX_AUDIO_BYTES = 64 * 1024 * 1024
SESSION_TTL_SECONDS = 6 * 60 * 60

processor = None
model = None
model_id = None
model_revision = None
device = None
model_lock = asyncio.Lock()


@dataclass
class StreamingSession:
    max_speakers: int
    pending_audio: np.ndarray = field(default_factory=lambda: np.empty(0, dtype=np.float32))
    speaker_cache: Any = None
    first_chunk: bool = True
    emitted_frames: int = 0
    received_samples: int = 0
    completed: bool = False
    responses: dict[str, dict] = field(default_factory=dict)
    touched_at: float = field(default_factory=time.monotonic)
    lock: asyncio.Lock = field(default_factory=asyncio.Lock)


sessions: dict[str, StreamingSession] = {}


def load_model(name: str, revision: str):
    global processor, model, model_id, model_revision, device
    if not torch.cuda.is_available():
        raise RuntimeError("Nemotron diarization requires a CUDA-capable NVIDIA GPU")
    logger.info("Loading %s at revision %s", name, revision)
    processor = AutoProcessor.from_pretrained(name, revision=revision)
    model = AutoModelForAudioFrameClassification.from_pretrained(
        name,
        revision=revision,
        dtype=torch.bfloat16,
    ).to("cuda").eval()
    processor.set_streaming_mode("low_latency")
    model_id = name
    model_revision = revision
    device = str(model.device)


app = FastAPI(title="Nemotron Speaker Diarization Server")


@app.get("/health")
async def health():
    return {
        "status": "ok" if model is not None else "loading",
        "model": model_id,
        "revision": model_revision,
        "device": device,
        "contractVersion": CONTRACT_VERSION,
    }


@app.get("/model/info")
async def model_info():
    if model is None or processor is None:
        raise HTTPException(503, "Model not loaded")
    return {
        "model": model_id,
        "revision": model_revision,
        "device": device,
        "contractVersion": CONTRACT_VERSION,
        "maxSpeakers": 8,
        "speakerIdentity": "anonymous",
        "frameStrideSeconds": FRAME_STRIDE_SECONDS,
        "streaming": True,
        "streamingMode": "low_latency",
        "streamingLatencyMs": processor.streaming_latency_ms,
    }


def bool_value(value: Any, default: bool = False) -> bool:
    if value is None:
        return default
    if isinstance(value, bool):
        return value
    return str(value).strip().lower() in {"1", "true", "yes", "on"}


async def parse_request(request: Request) -> tuple[bytes, dict]:
    content_type = request.headers.get("content-type", "")
    if "multipart" in content_type:
        form = await request.form()
        audio_file = form.get("audio")
        if audio_file is None:
            raise HTTPException(422, "No 'audio' file in multipart form")
        audio_bytes = await audio_file.read()
        params = {
            "mode": form.get("mode", "offline"),
            "session_id": form.get("session_id"),
            "chunk_id": form.get("chunk_id"),
            "is_first_chunk": bool_value(form.get("is_first_chunk")),
            "is_last_chunk": bool_value(form.get("is_last_chunk")),
            "max_speakers": form.get("max_speakers", 8),
        }
    elif "json" in content_type:
        body = await request.json()
        audio_b64 = body.get("audio_base64")
        if not audio_b64:
            raise HTTPException(422, "JSON body requires 'audio_base64' field")
        try:
            audio_bytes = base64.b64decode(audio_b64, validate=True)
        except Exception as exc:
            raise HTTPException(422, "Invalid base64 in 'audio_base64'") from exc
        params = {
            "mode": body.get("mode", "offline"),
            "session_id": body.get("session_id"),
            "chunk_id": body.get("chunk_id"),
            "is_first_chunk": bool_value(body.get("is_first_chunk")),
            "is_last_chunk": bool_value(body.get("is_last_chunk")),
            "max_speakers": body.get("max_speakers", 8),
        }
    else:
        raise HTTPException(415, "Use multipart/form-data or application/json")

    if not audio_bytes:
        raise HTTPException(422, "Audio is empty")
    if len(audio_bytes) > MAX_AUDIO_BYTES:
        raise HTTPException(413, f"Audio exceeds the {MAX_AUDIO_BYTES}-byte limit")
    try:
        params["max_speakers"] = int(params["max_speakers"])
    except (TypeError, ValueError) as exc:
        raise HTTPException(422, "max_speakers must be an integer") from exc
    if not 1 <= params["max_speakers"] <= 8:
        raise HTTPException(422, "max_speakers must be between 1 and 8")
    if params["mode"] not in {"offline", "streaming"}:
        raise HTTPException(422, "mode must be offline or streaming")
    return audio_bytes, params


def decode_audio(audio_bytes: bytes) -> np.ndarray:
    command = [
        "ffmpeg", "-hide_banner", "-loglevel", "error", "-i", "pipe:0",
        "-f", "f32le", "-ac", "1", "-ar", str(SAMPLE_RATE), "pipe:1",
    ]
    completed = subprocess.run(
        command,
        input=audio_bytes,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        check=False,
    )
    if completed.returncode != 0:
        detail = completed.stderr.decode("utf-8", errors="replace")[-1000:]
        raise HTTPException(422, f"Audio decode failed: {detail}")
    audio = np.frombuffer(completed.stdout, dtype=np.float32).copy()
    if audio.size == 0:
        raise HTTPException(422, "Decoded audio is empty")
    return audio


def speaker_id(index: int) -> str:
    return f"speaker_{index:02d}"


def contract_from_logits(
    logits: torch.Tensor,
    offset_seconds: float,
    max_speakers: int,
    attention_mask: torch.Tensor | None = None,
) -> dict:
    if processor is None:
        raise HTTPException(503, "Model not loaded")
    logits_cpu = logits.detach().float().cpu()
    mask_cpu = attention_mask.detach().cpu() if attention_mask is not None else None
    extracted = processor.extract_speaker_dict(logits_cpu, mask_cpu)[0]
    probabilities = logits_cpu[0].clamp(0.0, 1.0)
    turns = []
    for item in extracted:
        index = int(item["Speaker"])
        if index >= max_speakers:
            continue
        local_start = float(item["Start"])
        local_end = float(item["End"])
        start_frame = max(0, int(local_start / FRAME_STRIDE_SECONDS))
        end_frame = min(probabilities.shape[0], max(start_frame + 1, int(np.ceil(local_end / FRAME_STRIDE_SECONDS))))
        confidence = float(probabilities[start_frame:end_frame, index].mean().item())
        turns.append({
            "start": round(offset_seconds + local_start, 3),
            "end": round(offset_seconds + local_end, 3),
            "speakerId": speaker_id(index),
            "confidence": round(confidence, 4),
        })
    turns.sort(key=lambda turn: (turn["start"], turn["end"], turn["speakerId"]))
    return {
        "turns": turns,
        "overlaps": overlap_regions(turns),
        "speakers": sorted({turn["speakerId"] for turn in turns}),
    }


def overlap_regions(turns: list[dict]) -> list[dict]:
    boundaries = sorted({point for turn in turns for point in (turn["start"], turn["end"])})
    overlaps = []
    for start, end in zip(boundaries, boundaries[1:]):
        if end <= start:
            continue
        speakers = sorted({
            turn["speakerId"] for turn in turns
            if turn["start"] < end and turn["end"] > start
        })
        if len(speakers) < 2:
            continue
        if overlaps and overlaps[-1]["speakers"] == speakers and overlaps[-1]["end"] == start:
            overlaps[-1]["end"] = end
        else:
            overlaps.append({"start": start, "end": end, "speakers": speakers})
    return overlaps


async def infer(inputs, speaker_cache=None):
    if model is None:
        raise HTTPException(503, "Model not loaded")
    async with model_lock:
        moved = inputs.to(model.device, dtype=model.dtype)
        with torch.inference_mode():
            return model(**moved, speaker_cache=speaker_cache)


async def diarize_offline(audio: np.ndarray, max_speakers: int) -> dict:
    if processor is None or model is None:
        raise HTTPException(503, "Model not loaded")
    inputs = processor(audio, sampling_rate=SAMPLE_RATE)
    async with model_lock:
        moved = inputs.to(model.device, dtype=model.dtype)
        with torch.inference_mode():
            output = model(**moved)
    contract = contract_from_logits(output.logits, 0.0, max_speakers, inputs.attention_mask)
    return {
        "contractVersion": CONTRACT_VERSION,
        "mode": "offline",
        "model": {"id": model_id, "revision": model_revision, "maxSpeakers": 8},
        "speakerIdentity": "anonymous",
        "durationSeconds": round(audio.size / SAMPLE_RATE, 3),
        "frameStrideSeconds": FRAME_STRIDE_SECONDS,
        **contract,
    }


def cleanup_sessions():
    cutoff = time.monotonic() - SESSION_TTL_SECONDS
    stale = [key for key, value in sessions.items() if value.touched_at < cutoff]
    for key in stale:
        sessions.pop(key, None)


async def diarize_streaming(audio: np.ndarray, params: dict) -> dict:
    if processor is None or model is None:
        raise HTTPException(503, "Model not loaded")
    session_id = str(params.get("session_id") or "").strip()
    chunk_id = str(params.get("chunk_id") or "").strip()
    if not session_id or not chunk_id:
        raise HTTPException(422, "streaming mode requires session_id and chunk_id")

    cleanup_sessions()
    is_first = params["is_first_chunk"]
    if is_first:
        existing = sessions.get(session_id)
        if existing is not None:
            async with existing.lock:
                existing.touched_at = time.monotonic()
                if chunk_id in existing.responses:
                    return existing.responses[chunk_id]
        sessions[session_id] = StreamingSession(max_speakers=params["max_speakers"])
    state = sessions.get(session_id)
    if state is None:
        raise HTTPException(409, "Unknown streaming session; send is_first_chunk=true")

    async with state.lock:
        state.touched_at = time.monotonic()
        if chunk_id in state.responses:
            return state.responses[chunk_id]
        if state.completed:
            raise HTTPException(409, "Streaming session is already complete")
        if state.max_speakers != params["max_speakers"]:
            raise HTTPException(409, "max_speakers cannot change during a streaming session")

        input_start_seconds = state.received_samples / SAMPLE_RATE
        state.received_samples += audio.size
        state.pending_audio = np.concatenate((state.pending_audio, audio))
        result_logits = []
        result_offset_frames = state.emitted_frames

        while True:
            required = (processor.num_samples_first_audio_chunk
                        if state.first_chunk else processor.num_samples_per_audio_chunk)
            if state.pending_audio.size < required:
                break
            chunk = state.pending_audio[:required]
            inputs = processor(
                chunk,
                sampling_rate=SAMPLE_RATE,
                is_streaming=True,
                is_first_audio_chunk=state.first_chunk,
            )
            output = await infer(inputs, state.speaker_cache)
            state.speaker_cache = output.speaker_cache
            logits = output.logits.detach().cpu()
            result_logits.append(logits)
            emitted = logits.shape[1]
            state.emitted_frames += emitted
            consumed_samples = min(state.pending_audio.size, emitted * int(SAMPLE_RATE * FRAME_STRIDE_SECONDS))
            state.pending_audio = state.pending_audio[consumed_samples:]
            state.first_chunk = False

        if params["is_last_chunk"] and state.pending_audio.size:
            inputs = processor(
                state.pending_audio,
                sampling_rate=SAMPLE_RATE,
                is_streaming=True,
                is_first_audio_chunk=state.first_chunk,
                is_last_audio_chunk=True,
            )
            output = await infer(inputs, state.speaker_cache)
            state.speaker_cache = output.speaker_cache
            logits = output.logits.detach().cpu()
            result_logits.append(logits)
            state.emitted_frames += logits.shape[1]
            state.pending_audio = np.empty(0, dtype=np.float32)
            state.first_chunk = False

        if result_logits:
            combined = torch.cat(result_logits, dim=1)
            contract = contract_from_logits(
                combined,
                result_offset_frames * FRAME_STRIDE_SECONDS,
                state.max_speakers,
            )
        else:
            contract = {"turns": [], "overlaps": [], "speakers": []}

        state.completed = params["is_last_chunk"]
        response = {
            "contractVersion": CONTRACT_VERSION,
            "mode": "streaming",
            "model": {"id": model_id, "revision": model_revision, "maxSpeakers": 8},
            "speakerIdentity": "anonymous",
            "sessionId": session_id,
            "chunkId": chunk_id,
            "inputStartSeconds": round(input_start_seconds, 3),
            "inputDurationSeconds": round(audio.size / SAMPLE_RATE, 3),
            "emittedThroughSeconds": round(state.emitted_frames * FRAME_STRIDE_SECONDS, 3),
            "bufferedSeconds": round(state.pending_audio.size / SAMPLE_RATE, 3),
            "frameStrideSeconds": FRAME_STRIDE_SECONDS,
            "final": state.completed,
            **contract,
        }
        state.responses[chunk_id] = response
        return response


@app.post("/diarize")
async def diarize(request: Request):
    audio_bytes, params = await parse_request(request)
    audio = decode_audio(audio_bytes)
    started = time.monotonic()
    if params["mode"] == "streaming":
        result = await diarize_streaming(audio, params)
    else:
        result = await diarize_offline(audio, params["max_speakers"])
    if "processingSeconds" not in result:
        result["processingSeconds"] = round(time.monotonic() - started, 3)
    return result


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description="Nemotron speaker diarization server")
    parser.add_argument("--model", default=os.environ.get("DIARIZATION_MODEL", DEFAULT_MODEL))
    parser.add_argument("--revision", default=os.environ.get("DIARIZATION_MODEL_REVISION", DEFAULT_REVISION))
    parser.add_argument("--port", type=int, default=int(os.environ.get("DIARIZATION_PORT", "8768")))
    parser.add_argument("--host", default="0.0.0.0")
    args = parser.parse_args()

    logging.basicConfig(level=logging.INFO, format="%(asctime)s %(name)s %(levelname)s %(message)s")
    load_model(args.model, args.revision)
    uvicorn.run(app, host=args.host, port=args.port)
