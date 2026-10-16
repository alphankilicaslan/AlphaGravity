import sys
import io
import faster_whisper
import torch

try:
    sys.stdout.reconfigure(encoding='utf-8')
except Exception:
    pass

if len(sys.argv) < 2:
    sys.exit(0)

wav_path = sys.argv[1]
device = "cuda" if torch.cuda.is_available() else "cpu"
compute = "float16" if device == "cuda" else "int8"

try:
    model = faster_whisper.WhisperModel("large-v3-turbo", device=device, compute_type=compute)
    segments, info = model.transcribe(
        wav_path,
        language="tr",
        beam_size=1,
        best_of=1,
        temperature=0.0,
        initial_prompt="Merhaba, Türkçe konuşma ve sesli asistan komutları.",
        vad_filter=True
    )
    text = " ".join([s.text.strip() for s in segments if s.text]).strip()
    print(text)
except Exception as ex:
    sys.stderr.write(str(ex))
