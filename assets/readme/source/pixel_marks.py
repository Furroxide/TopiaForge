#!/usr/bin/env python3
"""Extract TopiaForge pixel-art marks as exact, compact SVG path data.

The shipped brand marks are clean pixel art: every source pixel is fully opaque
or fully transparent and only six colours appear, so this conversion is
lossless. Horizontal runs of identical cells are merged, then grouped by colour
into one <path> per colour, which keeps the emitted geometry small.

Needs Pillow, and the brand PNGs pulled by Git LFS:

    pip install Pillow
    git lfs pull

Run directly to print a standalone <g> fragment:

    python pixel_marks.py ../../../packages/launcher_ui/assets/brand/topiaforge-icon.png
"""

from __future__ import annotations

import sys
from pathlib import Path

from PIL import Image

REPO_ROOT = Path(__file__).resolve().parents[3]
BRAND = REPO_ROOT / "packages" / "launcher_ui" / "assets" / "brand"
ICON_PNG = BRAND / "topiaforge-icon.png"
WORDMARK_PNG = BRAND / "topiaforge-wordmark.png"

# packages/launcher_ui/lib/src/pixel_robot.dart:23-40. Kept in sync by hand;
# the sprite is deliberately code-defined so the mark stays first-party.
ROBOT_ROWS = (
    ".......oo.......",
    ".......kk.......",
    "...kkkkkkkkkk...",
    "...kwwwwwwwwk...",
    "...kwccwwccwk...",
    "...kwccwwccwk...",
    "...kwwwwwwwwk...",
    "...kwkkkkkkwk...",
    "...kkkkkkkkkk...",
    ".....kwwwwk.....",
    "..kkkkkkkkkkkk..",
    "..kwwkwoowkwwk..",
    "..kwwkwoowkwwk..",
    "..kwwkwwwwkwwk..",
    "..kkkkwwwwkkkk..",
    "......kk..kk....",
)


def _logical_cell(image: Image.Image) -> int:
    """Return the largest N for which the image is a perfect N x N block upscale."""
    width, height = image.size
    best = 1
    for cell in range(1, min(width, height) + 1):
        if width % cell or height % cell:
            continue
        if all(
            image.getpixel((left + dx, top + dy)) == image.getpixel((left, top))
            for top in range(0, height, cell)
            for left in range(0, width, cell)
            for dy in range(cell)
            for dx in range(cell)
        ):
            best = cell
    return best


def _paths_from_runs(runs: dict[str, list[tuple[int, int, int]]]) -> list[str]:
    out = []
    for fill in sorted(runs, key=lambda key: -len(runs[key])):
        data = "".join(f"M{x} {y}h{w}v1h-{w}z" for x, y, w in runs[fill])
        out.append(f'<path fill="{fill}" d="{data}"/>')
    return out


def mark_paths(png: Path) -> tuple[list[str], int, int]:
    """Return (path elements, logical width, logical height) for a brand PNG."""
    image = Image.open(png).convert("RGBA")
    cell = _logical_cell(image)
    grid = image.resize((image.width // cell, image.height // cell), Image.NEAREST)

    runs: dict[str, list[tuple[int, int, int]]] = {}
    for y in range(grid.height):
        x = 0
        while x < grid.width:
            r, g, b, alpha = grid.getpixel((x, y))
            if alpha == 0:
                x += 1
                continue
            run = 1
            while x + run < grid.width and grid.getpixel((x + run, y)) == (r, g, b, alpha):
                run += 1
            runs.setdefault(f"#{r:02X}{g:02X}{b:02X}", []).append((x, y, run))
            x += run

    return _paths_from_runs(runs), grid.width, grid.height


def robot_paths(palette: dict[str, str]) -> list[str]:
    """Return path elements for the code-defined pixel robot sprite."""
    runs: dict[str, list[tuple[int, int, int]]] = {}
    for y, row in enumerate(ROBOT_ROWS):
        x = 0
        while x < len(row):
            key = row[x]
            if key == ".":
                x += 1
                continue
            run = 1
            while x + run < len(row) and row[x + run] == key:
                run += 1
            runs.setdefault(palette[key], []).append((x, y, run))
            x += run
    return _paths_from_runs(runs)


def main() -> int:
    if len(sys.argv) != 2:
        print(__doc__, file=sys.stderr)
        return 2
    png = Path(sys.argv[1])
    paths, width, height = mark_paths(png)
    print(f"<!-- {png.name}: {width}x{height} logical grid -->")
    print(f'<g id="{png.stem}">')
    print("\n".join(paths))
    print("</g>")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
