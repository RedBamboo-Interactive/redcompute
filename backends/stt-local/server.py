import os
import hashlib
import subprocess
import sys

VENV_DIR = os.path.expanduser("~/stt-env")
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
import base64
import io
import json
import logging
import time
from contextlib import asynccontextmanager

import httpx
import uvicorn
from fastapi import FastAPI, HTTPException, Request
from fastapi.responses import StreamingResponse

logger = logging.getLogger("stt-server")

model = None
model_name = None
device = None
compute_type = None
DIARIZATION_URL = os.environ.get("DIARIZATION_URL", "http://127.0.0.1:8768").rstrip("/")

AVAILABLE_MODELS = [
    {"name": "tiny", "parameters": "39M", "vram_gb": 1.0, "relative_speed": 32},
    {"name": "tiny.en", "parameters": "39M", "vram_gb": 1.0, "relative_speed": 32},
    {"name": "base", "parameters": "74M", "vram_gb": 1.0, "relative_speed": 16},
    {"name": "base.en", "parameters": "74M", "vram_gb": 1.0, "relative_speed": 16},
    {"name": "small", "parameters": "244M", "vram_gb": 2.0, "relative_speed": 6},
    {"name": "small.en", "parameters": "244M", "vram_gb": 2.0, "relative_speed": 6},
    {"name": "medium", "parameters": "769M", "vram_gb": 5.0, "relative_speed": 2},
    {"name": "medium.en", "parameters": "769M", "vram_gb": 5.0, "relative_speed": 2},
    {"name": "large-v2", "parameters": "1550M", "vram_gb": 5.0, "relative_speed": 1},
    {"name": "large-v3", "parameters": "1550M", "vram_gb": 5.0, "relative_speed": 1},
    {"name": "large-v3-turbo", "parameters": "809M", "vram_gb": 3.0, "relative_speed": 8},
    {"name": "distil-large-v3", "parameters": "756M", "vram_gb": 3.0, "relative_speed": 6},
]

SUPPORTED_LANGUAGES = {
    "af": "Afrikaans", "am": "Amharic", "ar": "Arabic", "as": "Assamese",
    "az": "Azerbaijani", "ba": "Bashkir", "be": "Belarusian", "bg": "Bulgarian",
    "bn": "Bengali", "bo": "Tibetan", "br": "Breton", "bs": "Bosnian",
    "ca": "Catalan", "cs": "Czech", "cy": "Welsh", "da": "Danish",
    "de": "German", "el": "Greek", "en": "English", "es": "Spanish",
    "et": "Estonian", "eu": "Basque", "fa": "Persian", "fi": "Finnish",
    "fo": "Faroese", "fr": "French", "gl": "Galician", "gu": "Gujarati",
    "ha": "Hausa", "haw": "Hawaiian", "he": "Hebrew", "hi": "Hindi",
    "hr": "Croatian", "ht": "Haitian Creole", "hu": "Hungarian", "hy": "Armenian",
    "id": "Indonesian", "is": "Icelandic", "it": "Italian", "ja": "Japanese",
    "jw": "Javanese", "ka": "Georgian", "kk": "Kazakh", "km": "Khmer",
    "kn": "Kannada", "ko": "Korean", "la": "Latin", "lb": "Luxembourgish",
    "ln": "Lingala", "lo": "Lao", "lt": "Lithuanian", "lv": "Latvian",
    "mg": "Malagasy", "mi": "Maori", "mk": "Macedonian", "ml": "Malayalam",
    "mn": "Mongolian", "mr": "Marathi", "ms": "Malay", "mt": "Maltese",
    "my": "Myanmar", "ne": "Nepali", "nl": "Dutch", "nn": "Nynorsk",
    "no": "Norwegian", "oc": "Occitan", "pa": "Punjabi", "pl": "Polish",
    "ps": "Pashto", "pt": "Portuguese", "ro": "Romanian", "ru": "Russian",
    "sa": "Sanskrit", "sd": "Sindhi", "si": "Sinhala", "sk": "Slovak",
    "sl": "Slovenian", "sn": "Shona", "so": "Somali", "sq": "Albanian",
    "sr": "Serbian", "su": "Sundanese", "sv": "Swedish", "sw": "Swahili",
    "ta": "Tamil", "te": "Telugu", "tg": "Tajik", "th": "Thai",
    "tk": "Turkmen", "tl": "Tagalog", "tr": "Turkish", "tt": "Tatar",
    "uk": "Ukrainian", "ur": "Urdu", "uz": "Uzbek", "vi": "Vietnamese",
    "yi": "Yiddish", "yo": "Yoruba", "yue": "Cantonese", "zh": "Chinese",
    "zu": "Zulu",
}


def load_model(name: str, dev: str = "cuda", comp: str = "float16"):
    global model, model_name, device, compute_type
    from faster_whisper import WhisperModel
    logger.info("Loading model '%s' on %s (%s)", name, dev, comp)
    model = WhisperModel(name, device=dev, compute_type=comp)
    model_name = name
    device = dev
    compute_type = comp


@asynccontextmanager
async def lifespan(app: FastAPI):
    yield


app = FastAPI(title="Faster Whisper STT Server", lifespan=lifespan)


@app.get("/health")
async def health():
    return {
        "status": "ok" if model is not None else "loading",
        "model": model_name,
        "device": device,
        "compute_type": compute_type,
        "optional_diarization": DIARIZATION_URL,
    }


@app.get("/model/info")
async def model_info():
    if model is None:
        raise HTTPException(503, "Model not loaded")
    return {
        "model": model_name,
        "device": device,
        "compute_type": compute_type,
        "supported_languages": list(SUPPORTED_LANGUAGES.keys()),
        "optional_diarization": True,
    }


@app.post("/model/reload")
async def model_reload(request: Request):
    body = await request.json()
    try:
        load_model(body.get("model", model_name), body.get("device", device), body.get("compute_type", compute_type))
        return {"status": "ok", "model": model_name}
    except Exception as exc:
        raise HTTPException(500, str(exc)) from exc


@app.get("/models")
async def list_models():
    return {"models": AVAILABLE_MODELS, "current": model_name}


@app.get("/languages")
async def list_languages():
    return {
        "languages": [
            {"code": code, "name": name}
            for code, name in sorted(SUPPORTED_LANGUAGES.items(), key=lambda item: item[1])
        ]
    }


def bool_value(value, default=False):
    if value is None:
        return default
    if isinstance(value, bool):
        return value
    return str(value).strip().lower() in {"1", "true", "yes", "on"}


async def parse_request(request: Request) -> tuple[bytes, dict]:
    content_type = request.headers.get("content-type", "")
    if "multipart" in content_type:
        source = await request.form()
        audio_file = source.get("audio")
        if audio_file is None:
            raise HTTPException(422, "No 'audio' file in multipart form")
        audio_bytes = await audio_file.read()
    elif "json" in content_type:
        source = await request.json()
        audio_b64 = source.get("audio_base64")
        if not audio_b64:
            raise HTTPException(422, "JSON body requires 'audio_base64' field")
        try:
            audio_bytes = base64.b64decode(audio_b64, validate=True)
        except Exception as exc:
            raise HTTPException(422, "Invalid base64 in 'audio_base64'") from exc
    else:
        raise HTTPException(415, "Use multipart/form-data or application/json")

    if not audio_bytes:
        raise HTTPException(422, "Audio is empty")
    diarize = bool_value(source.get("diarize"))
    params = {
        "language": source.get("language", "auto"),
        "task": source.get("task", "transcribe"),
        "word_timestamps": bool_value(source.get("word_timestamps")) or diarize,
        "initial_prompt": source.get("initial_prompt"),
        "vad_filter": bool_value(source.get("vad_filter"), True),
        "diarize": diarize,
        "diarization_mode": source.get("diarization_mode", "offline"),
        "diarization_session_id": source.get("diarization_session_id"),
        "diarization_chunk_id": source.get("diarization_chunk_id"),
        "diarization_is_first_chunk": bool_value(source.get("diarization_is_first_chunk")),
        "diarization_is_last_chunk": bool_value(source.get("diarization_is_last_chunk")),
        "diarization_max_speakers": int(source.get("diarization_max_speakers", 8)),
    }
    return audio_bytes, params


def run_transcription(audio_bytes: bytes, params: dict):
    if model is None:
        raise HTTPException(503, "Model not loaded")
    language = None if params["language"] == "auto" else params["language"]
    return model.transcribe(
        io.BytesIO(audio_bytes),
        language=language,
        task=params["task"],
        word_timestamps=params["word_timestamps"],
        initial_prompt=params.get("initial_prompt"),
        vad_filter=params["vad_filter"],
    )


def format_segment(segment, word_timestamps: bool) -> dict:
    result = {
        "start": round(segment.start, 3),
        "end": round(segment.end, 3),
        "text": segment.text.strip(),
        "avg_logprob": round(segment.avg_logprob, 4),
        "no_speech_prob": round(segment.no_speech_prob, 4),
    }
    if word_timestamps and segment.words:
        result["words"] = [
            {
                "start": round(word.start, 3),
                "end": round(word.end, 3),
                "word": word.word,
                "probability": round(word.probability, 4),
            }
            for word in segment.words
        ]
    return result


async def run_diarization(audio_bytes: bytes, params: dict) -> dict:
    payload = {
        "audio_base64": base64.b64encode(audio_bytes).decode("ascii"),
        "mode": params["diarization_mode"],
        "session_id": params["diarization_session_id"],
        "chunk_id": params["diarization_chunk_id"],
        "is_first_chunk": params["diarization_is_first_chunk"],
        "is_last_chunk": params["diarization_is_last_chunk"],
        "max_speakers": params["diarization_max_speakers"],
    }
    try:
        async with httpx.AsyncClient(timeout=300) as client:
            response = await client.post(f"{DIARIZATION_URL}/diarize", json=payload)
    except httpx.HTTPError as exc:
        raise HTTPException(502, f"Diarization backend unavailable: {exc}") from exc
    if response.status_code >= 400:
        raise HTTPException(response.status_code, f"Diarization failed: {response.text[:1000]}")
    return response.json()


def enrich_words(segments: list[dict], diarization: dict, mode: str):
    turns = diarization.get("turns", [])
    input_offset = diarization.get("inputStartSeconds", 0.0) if mode == "streaming" else 0.0
    for segment in segments:
        segment_speakers = set()
        for word in segment.get("words", []):
            start = input_offset + word["start"]
            end = input_offset + word["end"]
            candidates = []
            for turn in turns:
                overlap = max(0.0, min(end, turn["end"]) - max(start, turn["start"]))
                if overlap > 0:
                    candidates.append((overlap, turn.get("confidence", 0.0), turn["speakerId"]))
            if candidates:
                candidates.sort(reverse=True)
                word["speakerId"] = candidates[0][2]
                word["speakerConfidence"] = candidates[0][1]
                word["overlappingSpeakerIds"] = sorted({candidate[2] for candidate in candidates})
                segment_speakers.update(candidate[2] for candidate in candidates)
        if segment_speakers:
            segment["speakerIds"] = sorted(segment_speakers)


@app.post("/transcribe")
async def transcribe(request: Request):
    audio_bytes, params = await parse_request(request)
    started = time.monotonic()
    segments, info = run_transcription(audio_bytes, params)
    result_segments = []
    full_text = []
    for segment in segments:
        result_segments.append(format_segment(segment, params["word_timestamps"]))
        full_text.append(segment.text.strip())

    diarization = None
    if params["diarize"]:
        diarization = await run_diarization(audio_bytes, params)
        enrich_words(result_segments, diarization, params["diarization_mode"])

    result = {
        "text": " ".join(full_text),
        "language": info.language,
        "language_probability": round(info.language_probability, 4),
        "duration_seconds": round(info.duration, 3),
        "processing_seconds": round(time.monotonic() - started, 3),
        "segments": result_segments,
    }
    if diarization is not None:
        result["diarization"] = diarization
    return result


@app.post("/transcribe/stream")
async def transcribe_stream(request: Request):
    audio_bytes, params = await parse_request(request)
    if params["diarize"]:
        raise HTTPException(422, "Use /transcribe for diarized output; provider streaming is session-based")
    started = time.monotonic()
    segments, info = run_transcription(audio_bytes, params)

    async def generate():
        full_text = []
        for segment in segments:
            item = format_segment(segment, params["word_timestamps"])
            item["type"] = "segment"
            full_text.append(segment.text.strip())
            yield json.dumps(item) + "\n"
        yield json.dumps({
            "type": "result",
            "text": " ".join(full_text),
            "language": info.language,
            "language_probability": round(info.language_probability, 4),
            "duration_seconds": round(info.duration, 3),
            "processing_seconds": round(time.monotonic() - started, 3),
        }) + "\n"

    return StreamingResponse(generate(), media_type="application/x-ndjson")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description="Faster Whisper STT server")
    parser.add_argument("--model", default=os.environ.get("STT_MODEL", "large-v3"))
    parser.add_argument("--device", default=os.environ.get("STT_DEVICE", "cuda"))
    parser.add_argument("--compute-type", default=os.environ.get("STT_COMPUTE_TYPE", "float16"))
    parser.add_argument("--port", type=int, default=int(os.environ.get("STT_PORT", "8766")))
    parser.add_argument("--host", default="0.0.0.0")
    args = parser.parse_args()

    logging.basicConfig(level=logging.INFO, format="%(asctime)s %(name)s %(levelname)s %(message)s")
    load_model(args.model, args.device, args.compute_type)
    uvicorn.run(app, host=args.host, port=args.port)
