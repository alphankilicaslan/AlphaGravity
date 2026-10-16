import os
import io
import time
import asyncio
from typing import Optional
from pydantic import BaseModel
import uvicorn
from fastapi import FastAPI, Request, Response, HTTPException
from fastapi.middleware.cors import CORSMiddleware
import faster_whisper
import edge_tts
import torch

app = FastAPI(title="AlphaGravity Voice Bridge", version="1.0.0")

app.add_middleware(
    CORSMiddleware,
    allow_origins=["*"],
    allow_credentials=True,
    allow_methods=["*"],
    allow_headers=["*"],
)

whisper_model = None
whisper_device = "cpu"
whisper_compute = "int8"

try:
    if torch.cuda.is_available():
        whisper_device = "cuda"
        whisper_compute = "float16"
        print(f"[VoiceBridge] Using CUDA GPU: {torch.cuda.get_device_name(0)}")
    else:
        print("[VoiceBridge] CUDA not found, using CPU with int8 quantization")

    print("[VoiceBridge] Loading Faster-Whisper 'large-v3-turbo' model...")
    whisper_model = faster_whisper.WhisperModel(
        "large-v3-turbo",
        device=whisper_device,
        compute_type=whisper_compute
    )
    print(f"[VoiceBridge] Faster-Whisper 'large-v3-turbo' model loaded successfully on {whisper_device} ({whisper_compute})!")
except Exception as ex:
    print(f"[VoiceBridge] CUDA initialization failed ({ex}), falling back to CPU 'large-v3-turbo' or 'tiny'...")
    try:
        whisper_device = "cpu"
        whisper_compute = "int8"
        whisper_model = faster_whisper.WhisperModel("large-v3-turbo", device="cpu", compute_type="int8")
        print("[VoiceBridge] Faster-Whisper 'large-v3-turbo' loaded on CPU (int8)!")
    except Exception as e2:
        try:
            whisper_model = faster_whisper.WhisperModel("tiny", device="cpu", compute_type="int8")
            print("[VoiceBridge] Fallback 'tiny' model loaded on CPU!")
        except Exception as e3:
            print(f"[VoiceBridge] Critical error loading Faster-Whisper: {e3}")

class TTSRequest(BaseModel):
    text: str
    voice: Optional[str] = "tr-TR-AhmetNeural"
    rate: Optional[str] = "+0%"

@app.get("/health")
def health_check():
    return {
        "status": "ok",
        "service": "AlphaGravity Voice Bridge",
        "device": whisper_device,
        "compute": whisper_compute,
        "model_loaded": whisper_model is not None
    }

# Known noise/silence hallucinations from YouTube training data
HALLUCINATION_BLACKLIST = [
    "izlediğiniz için", "izlediginiz icin",
    "abone ol", "abone olmayı", "beğenmeyi unutmayın",
    "altyazı", "altyazi", "m.k.", "trt", "kanalımıza", "videoyu", "nokta", "virgül"
]

@app.post("/transcribe")
async def transcribe_audio(request: Request):
    if whisper_model is None:
        raise HTTPException(status_code=503, detail="Whisper model not initialized")

    body = await request.body()
    if not body or len(body) < 1600:
        return {"text": "", "latency_ms": 0}

    t0 = time.time()

    try:
        # In-memory WAV transcription (zero tempfile disk I/O)
        audio_stream = io.BytesIO(body)

        segments, info = whisper_model.transcribe(
            audio_stream,
            language="tr",
            beam_size=1,
            best_of=1,
            temperature=0.0,
            condition_on_previous_text=False,
            initial_prompt="Merhaba, Türkçe konuşma ve sesli asistan komutları.",
            vad_filter=True,
            vad_parameters=dict(
                threshold=0.70,
                min_silence_duration_ms=650,
                speech_pad_ms=200
            )
        )

        text_parts = []
        for seg in segments:
            # Filter out segments that are pure silence/background noise
            if seg.no_speech_prob > 0.85:
                continue
            seg_text = seg.text.strip()
            if seg_text:
                text_parts.append(seg_text)

        result_text = " ".join(text_parts).strip()

        # Strict anti-hallucination filter for silence noise
        clean_lower = result_text.lower().strip()
        if len(clean_lower) < 35 and any(h in clean_lower for h in HALLUCINATION_BLACKLIST):
            print(f"[VoiceBridge] Filtered noise hallucination: '{result_text}'")
            result_text = ""

        latency = int((time.time() - t0) * 1000)
        print(f"[VoiceBridge] Transcribed in {latency}ms: '{result_text}'")
        return {"text": result_text, "latency_ms": latency}
    except Exception as ex:
        print(f"[VoiceBridge] Transcribe error: {ex}")
        return {"text": "", "latency_ms": 0}

@app.post("/tts")
async def text_to_speech(req: TTSRequest):
    if not req.text or not req.text.strip():
        raise HTTPException(status_code=400, detail="Empty text provided")

    voice = req.voice or "tr-TR-AhmetNeural"
    rate = req.rate or "+0%"

    t0 = time.time()
    try:
        communicate = edge_tts.Communicate(text=req.text.strip(), voice=voice, rate=rate)
        mp3_buffer = io.BytesIO()
        async for chunk in communicate.stream():
            if chunk["type"] == "audio":
                mp3_buffer.write(chunk["data"])

        mp3_bytes = mp3_buffer.getvalue()
        latency = int((time.time() - t0) * 1000)
        print(f"[VoiceBridge] Edge-TTS in-memory generated {len(mp3_bytes)} bytes in {latency}ms (Voice: {voice})")

        return Response(content=mp3_bytes, media_type="audio/mpeg")
    except Exception as ex:
        print(f"[VoiceBridge] Edge-TTS error: {ex}")
        raise HTTPException(status_code=500, detail=str(ex))

if __name__ == "__main__":
    print("[VoiceBridge] Starting server on http://127.0.0.1:58222 ...")
    uvicorn.run(app, host="127.0.0.1", port=58222, log_level="warning")
