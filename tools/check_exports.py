"""Independent consumers for exported assets; development-only Pillow/trimesh.
Compares PNG RGBA against explicit RGB565 bit expansion and independently
located source records. Verifies WAV with wave and OBJ with trimesh.
"""
import json
import struct
import sys
import wave
from pathlib import Path

from PIL import Image
import trimesh

def rgb565_to_rgb888(data):
    """Expand explicitly little-endian pixels independently of the C# decoder."""
    output = bytearray()
    for (pixel,) in struct.iter_unpack("<H", data):
        r, g, b = (pixel >> 11) & 31, (pixel >> 5) & 63, pixel & 31
        output.extend(((r << 3) | (r >> 2), (g << 2) | (g >> 4), (b << 3) | (b >> 2)))
    return bytes(output)

root = Path(sys.argv[1])
samples = json.loads((root / "samples.json").read_text(encoding="utf-8"))
encodings = set()
for sample in samples:
    b = Path(sample["source"]).read_bytes()
    _, _, pages, count = struct.unpack_from("<4I", b)
    offset, page = struct.unpack_from("<Ii", b, 24 + sample["index"] * 40 + 32)
    flags, width, height, palette_count = b[offset], *struct.unpack_from("<HH", b, offset + 4), struct.unpack_from("<H", b, offset + 12)[0]
    n = width * height
    pixel_bytes = n if palette_count else n * 2
    pixels = b[offset + 16:offset + 16 + pixel_bytes]
    alpha = b[offset + 16 + pixel_bytes:offset + 16 + pixel_bytes + n] if flags & 8 else bytes([255]) * n
    if palette_count:
        start = offset + 16 + pixel_bytes + (n if flags & 8 else 0) if flags & 128 else 24 + count * 40 + page * 512
        palette = b[start:start + (palette_count * 2 if flags & 128 else 512)]
        pixels = b"".join(palette[p*2:p*2+2] for p in pixels)
    rgb = rgb565_to_rgb888(pixels)
    rgba = bytearray(n*4)
    rgba[0::4], rgba[1::4], rgba[2::4], rgba[3::4] = rgb[0::3], rgb[1::3], rgb[2::3], alpha
    image = Image.open(sample["png"]).convert("RGBA")
    assert image.size == (width,height) and image.tobytes() == rgba, sample
    encodings.add(("embedded" if flags & 128 else "shared") if palette_count else "direct")
    if flags & 8: encodings.add("alpha")
waves = list(root.rglob("*.wav"))
for path in waves:
    with wave.open(str(path), "rb") as wav:
        assert wav.getnframes() > 0
        assert len(wav.readframes(wav.getnframes())) == wav.getnframes() * wav.getnchannels() * wav.getsampwidth()
models = list(root.rglob("*.obj"))
for path in models:
    model = trimesh.load(str(path), force="scene", process=False)
    assert model.geometry, path
    assert sum(len(g.faces) for g in model.geometry.values()) > 0, path
for path in root.rglob("*.json"):
    json.loads(path.read_text(encoding="utf-8"))
print(json.dumps({"png_exact_matches":len(samples),"encodings":sorted(encodings),"wave_files":len(waves),"obj_files":len(models),"status":"passed"},indent=2))
