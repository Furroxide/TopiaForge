"""Rebuild the two sample sprite sources from their checked-in geometric shapes."""

import struct
import zlib
from pathlib import Path


def rounded_box(x, y, width, height, radius):
    dx = max(0, radius - x, x - (width - radius))
    dy = max(0, radius - y, y - (height - radius))
    return dx * dx + dy * dy <= radius * radius


def card(x, y):
    if not rounded_box(x, y, 96, 48, 6):
        return 0, 0, 0, 0
    if (x - 22) ** 2 + (y - 24) ** 2 <= 11 ** 2:
        return 250, 187, 96, 255
    if 42 <= x < 80 and 17 <= y < 21:
        return 225, 246, 244, 255
    if 42 <= x < 68 and 27 <= y < 31:
        return 108, 214, 204, 255
    return 32, 64, 76, 255


def mark(x, y):
    distance = abs(x - 16) + abs(y - 16)
    return (255, 255, 255, 255) if 7 <= distance < 14 else (0, 0, 0, 0)


def png(width, height, colour):
    def chunk(kind, data):
        return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data))

    rows = bytearray()
    for y in range(height):
        rows.append(0)
        for x in range(width):
            rows.extend(colour(x + 0.5, y + 0.5))
    header = struct.pack(">IIBBBBB", width, height, 8, 6, 0, 0, 0)
    return b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", header) + chunk(b"IDAT", zlib.compress(rows, 9)) + chunk(b"IEND", b"")


def main():
    folder = Path(__file__).resolve().parent / "Assets" / "UiResourceSamples"
    folder.mkdir(exist_ok=True)
    (folder / "PreviewCard.png").write_bytes(png(96, 48, card))
    (folder / "PreviewMark.png").write_bytes(png(32, 32, mark))


if __name__ == "__main__":
    main()
