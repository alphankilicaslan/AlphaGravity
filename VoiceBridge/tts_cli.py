import sys
import asyncio
import edge_tts

if len(sys.argv) < 3:
    sys.exit(1)

text_path = sys.argv[1]
out_mp3_path = sys.argv[2]
voice = sys.argv[3] if len(sys.argv) > 3 else "tr-TR-AhmetNeural"

with open(text_path, "r", encoding="utf-8") as f:
    text = f.read().strip()

async def main():
    c = edge_tts.Communicate(text, voice)
    await c.save(out_mp3_path)

if __name__ == "__main__":
    asyncio.run(main())
