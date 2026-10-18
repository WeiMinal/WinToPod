#!/usr/bin/env python3
"""Size the supplied transparent marks; retain their color and alpha, no plate."""
from pathlib import Path
import hashlib
import json
import struct
from PIL import Image

ROOT = Path(__file__).resolve().parent.parent
BRAND = ROOT / 'src/WinToPod.Desktop/Assets/Brand'
SIZES = [16, 20, 24, 32, 40, 48, 64, 128, 256]


def build():
    sources = {}
    for color in ['black', 'white']:
        source = BRAND / f'WinToPod-{color}-source.png'
        with Image.open(source) as original:
            image = original.convert('RGBA')
        # The two supplied files have different transparent margins. Measure the
        # substantial alpha outline independently, without thresholding samples.
        bounds = image.getchannel('A').point(lambda n: 255 if n >= 128 else 0).getbbox()
        if bounds is None:
            raise ValueError('Logo is empty')
        image = image.crop(bounds)
        frames = []
        for size in SIZES:
            inner = max(1, round(size * .92))
            ratio = inner / max(image.size)
            resized = image.resize((max(1, round(image.width * ratio)), max(1, round(image.height * ratio))), Image.Resampling.LANCZOS)
            canvas = Image.new('RGBA', (size, size), (0, 0, 0, 0))
            canvas.alpha_composite(resized, ((size - resized.width) // 2, (size - resized.height) // 2))
            path = BRAND / f'WinToPod-{color}-{size}.png'
            canvas.save(path)
            frames.append(path.read_bytes())
        directory = bytearray(); offset = 6 + len(SIZES) * 16
        for size, data in zip(SIZES, frames):
            directory += struct.pack('<BBBBHHII', size % 256, size % 256, 0, 0, 1, 32, len(data), offset)
            offset += len(data)
        icon = BRAND / ('WinToPod.ico' if color == 'black' else 'WinToPod-dark.ico')
        icon.write_bytes(struct.pack('<HHH', 0, 1, len(SIZES)) + directory + b''.join(frames))
        sources[color] = {'sha256': hashlib.sha256(source.read_bytes()).hexdigest(), 'bounds': list(bounds)}
    browser = ROOT / 'browser/WinToPod.VideoSync/icons'; browser.mkdir(exist_ok=True)
    for color in ['black', 'white']:
        for size in [16, 32, 48, 128]:
            (browser / f'WinToPod-{color}-{size}.png').write_bytes((BRAND / f'WinToPod-{color}-{size}.png').read_bytes())
    report = {'transparent': True, 'plate': False, 'sizes': SIZES, 'sources': sources, 'logo_redesigned': False}
    print(json.dumps(report, indent=2))


if __name__ == '__main__':
    build()
