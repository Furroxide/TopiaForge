#!/usr/bin/env python3
"""Emit the README hero and architecture SVGs in matched light and dark variants.

Both variants come from one composition so they cannot drift. Colours are the
canonical launcher tokens (packages/launcher_ui/lib/src/launcher_theme.dart) and
the in-game HUD tokens (src/TopiaForge.Mods.UnityUi/Core/TopiaForgePalette.cs).

Needs Pillow, and the brand PNGs pulled by Git LFS:

    pip install Pillow
    git lfs pull
    python build_readme_svg.py

Runs from any working directory. Writes the four SVGs to assets/readme/.
"""

from __future__ import annotations

import sys
from pathlib import Path
from xml.sax.saxutils import escape

# pixel_marks sits beside this file. CPython normally puts that directory on
# sys.path itself, but not under -P / PYTHONSAFEPATH, and not when this module
# is imported rather than run. Add it explicitly so every invocation works.
sys.path.insert(0, str(Path(__file__).resolve().parent))

from pixel_marks import ICON_PNG, WORDMARK_PNG, mark_paths, robot_paths  # noqa: E402

OUT = Path(__file__).resolve().parent.parent

SANS = "-apple-system,BlinkMacSystemFont,Segoe UI,Roboto,Helvetica,Arial,sans-serif"
MONO = "ui-monospace,SFMono-Regular,Menlo,Consolas,monospace"

# The terminal card reads as a terminal in both themes, so it keeps its own dark
# palette and only sinks slightly further on the dark page.
TERMINAL_TEXT = "#E8E4DA"
TERMINAL_FLAG = "#92E8C0"
TERMINAL_LABEL = "#8A93A6"
TERMINAL_OUTPUT = "#7E8899"
LAUNCH = "#FF7A11"
ACCENT = "#20F6FE"

THEMES = {
    "light": {
        "bg": "#F5F1E8",        # paper
        "panel": "#FFFCF6",     # surface
        "ink": "#2D3748",       # text
        "muted": "#6C6670",     # mutedText
        "faint": "#928A7C",     # faintText
        "border": "#E4B373",    # border
        "chip": "#FFE0BE",      # surfaceTint
        "terminal": "#161B24",  # logPanel
        "zone_b": "#168E96",    # accentDark, readable on paper
        "robot_outline": "#2D3748",
        "edge": "#E8DCC6",
        "shadow": 0.20,
    },
    "dark": {
        "bg": "#10141B",        # HudBackdrop
        "panel": "#161B24",     # HudSunken
        "ink": "#F2EFE8",
        "muted": "#C7C1B4",     # HudMuted
        "faint": "#8A8578",
        "border": "#3A465C",    # HudTint
        "chip": "#222934",
        "terminal": "#0B0E14",
        "zone_b": ACCENT,
        "robot_outline": "#5A6B85",
        "edge": "#2A3342",
        "shadow": 0.45,
    },
}


def text(x, y, body, size, fill, family=SANS, weight="400", spacing=None):
    extra = f' letter-spacing="{spacing}"' if spacing else ""
    return (
        f'<text x="{x}" y="{y}" font-family="{family}" font-size="{size}" '
        f'font-weight="{weight}" fill="{fill}"{extra}>{escape(body)}</text>'
    )


def command(x, y, head, tail, size):
    """One terminal line. tspans flow naturally, so no advance-width guessing.

    xml:space="preserve" keeps the separating spaces, which the default
    whitespace handling would otherwise collapse away.
    """
    tail_span = f'<tspan fill="{TERMINAL_FLAG}">{escape(tail)}</tspan>' if tail else ""
    return (
        f'<text x="{x}" y="{y}" xml:space="preserve" font-family="{MONO}" '
        f'font-size="{size}">'
        f'<tspan fill="{LAUNCH}" font-weight="700">$ </tspan>'
        f'<tspan fill="{TERMINAL_TEXT}">{escape(head)}</tspan>'
        f'{tail_span}</text>'
    )


def box(x, y, w, h, fill, stroke=None, rx=14, width=2):
    stroke_attr = f' stroke="{stroke}" stroke-width="{width}"' if stroke else ""
    return (
        f'<rect x="{x}" y="{y}" width="{w}" height="{h}" rx="{rx}" '
        f'fill="{fill}"{stroke_attr}/>'
    )


def shadow(x, y, w, h, opacity, rx=14):
    """The launcher's zero-blur offset shadow: BoxShadow(offset: (-3, 4), blur: 0)."""
    return (
        f'<rect x="{x - 3}" y="{y + 4}" width="{w}" height="{h}" rx="{rx}" '
        f'fill="#000" opacity="{opacity}"/>'
    )


def arrow(x1, x2, y, colour):
    """Horizontal connector with a small solid head."""
    return (
        f'<path d="M{x1} {y}H{x2 - 10}" stroke="{colour}" stroke-width="3" fill="none"/>'
        f'<path d="M{x2} {y}l-12 -7v14z" fill="{colour}"/>'
    )


def node(x, y, w, h, title, sub, theme, accent=False):
    stroke = LAUNCH if accent else theme["border"]
    return "".join([
        shadow(x, y, w, h, theme["shadow"]),
        box(x, y, w, h, theme["panel"], stroke, rx=14, width=3 if accent else 2),
        text(x + 20, y + 32, title, 23, theme["ink"], weight="700"),
        text(x + 20, y + 56, sub, 17, theme["muted"], family=MONO),
    ])


def svg_open(width, height, title, desc):
    return (
        '<?xml version="1.0" encoding="UTF-8"?>\n'
        f'<svg xmlns="http://www.w3.org/2000/svg" width="{width}" height="{height}" '
        f'viewBox="0 0 {width} {height}" role="img" aria-labelledby="t d">\n'
        f'<title id="t">{escape(title)}</title>\n'
        f'<desc id="d">{escape(desc)}</desc>\n'
    )


def build_hero(theme_name):
    t = THEMES[theme_name]
    word, _, _ = mark_paths(WORDMARK_PNG)
    robot = robot_paths({
        "k": t["robot_outline"],
        "w": "#FFFCF6",
        "c": ACCENT,
        "o": LAUNCH,
    })

    parts = [svg_open(
        1200, 400,
        "TopiaForge",
        "TopiaForge: build mods for Robotopia without touching Unity, BepInEx, "
        "or game internals. A terminal panel shows the four-command quickstart: "
        "doctor, new mod, cd, dev.",
    )]
    parts.append(box(0, 0, 1200, 400, t["bg"], t["edge"], rx=26))

    # Left: identity block, built from the shipped pixel wordmark.
    parts.append(text(64, 92, "ROBOTOPIA MODDING TOOLKIT", 20, t["muted"],
                      family=MONO, spacing="3.2"))
    parts.append(
        '<g transform="translate(64 112) scale(4)" shape-rendering="crispEdges">'
        + "".join(word) + "</g>"
    )
    parts.append(text(64, 238, "Build mods for Robotopia without touching", 22, t["ink"]))
    parts.append(text(64, 268, "Unity, BepInEx, or game internals.", 22, t["ink"]))

    # Deliberately version-agnostic so the hero never goes stale between releases.
    parts.append(box(64, 302, 340, 40, t["chip"], t["border"], rx=12))
    parts.append(text(84, 328, "0.x  ·  EARLY  ·  WINDOWS X64", 19, t["ink"],
                      family=MONO, spacing="1.4"))

    # Right: terminal card carrying the first real action.
    cx, cy, cw, ch = 596, 52, 540, 296
    parts.append(shadow(cx, cy, cw, ch, t["shadow"], rx=18))
    parts.append(box(cx, cy, cw, ch, t["terminal"], LAUNCH, rx=18, width=3))
    parts.append(
        f'<g transform="translate({cx + 22} {cy + 14}) scale(1.75)" '
        'shape-rendering="crispEdges">' + "".join(robot) + "</g>"
    )
    parts.append(text(cx + 76, cy + 36, "topiaforge", 18, TERMINAL_LABEL, family=MONO))
    parts.append(
        f'<path d="M{cx} {cy + 56}H{cx + cw}" stroke="#000" '
        'stroke-width="2" opacity="0.35"/>'
    )

    commands = [
        ("topiaforge doctor", " --strict"),
        ("topiaforge new mod", " example.first-mod"),
        ("cd", " example.first-mod"),
        ("topiaforge dev", ""),
    ]
    y = cy + 100
    for head, tail in commands:
        parts.append(command(cx + 24, y, head, tail, 21))
        y += 36
    parts.append(text(cx + 24, y + 14, "restore → build → test → pack → install",
                      17, TERMINAL_OUTPUT, family=MONO))

    parts.append("</svg>\n")
    return "\n".join(parts)


def build_architecture(theme_name):
    t = THEMES[theme_name]
    icon, _, _ = mark_paths(ICON_PNG)

    parts = [svg_open(
        1200, 560,
        "TopiaForge architecture",
        "Inside the game process, BepInEx and UnityDoorstop load "
        "TopiaForge.ModManager, which owns the Unity-free core, the V1 safe "
        "contracts, and the in-game UI renderer. On the desktop, the launcher and "
        "the topiaforge CLI share launcher_data and launcher_domain and never load "
        "game code.",
    )]
    parts.append(box(0, 0, 1200, 560, t["bg"], t["edge"], rx=26))
    parts.append(text(64, 76, "ARCHITECTURE", 20, t["muted"], family=MONO, spacing="3.2"))

    # Zone A: inside the game process.
    parts.append(box(48, 104, 1104, 250, t["bg"], t["border"], rx=20))
    parts.append(text(76, 140, "IN THE GAME PROCESS", 19, LAUNCH,
                      family=MONO, weight="700", spacing="2.4"))

    parts.append(node(76, 162, 272, 72, "Robotopia", "Unity Mono · HDRP", t))
    parts.append(arrow(360, 392, 198, t["faint"]))
    parts.append(node(392, 162, 272, 72, "BepInEx", "+ UnityDoorstop", t))
    parts.append(arrow(676, 708, 198, t["faint"]))
    parts.append(node(708, 162, 368, 72, "TopiaForge.ModManager", "the mod loader",
                      t, accent=True))
    parts.append(
        '<g transform="translate(1016 176) scale(2.4)" shape-rendering="crispEdges">'
        + "".join(icon) + "</g>"
    )

    # Bracket from the loader down to the three assemblies it owns.
    parts.append(
        f'<path d="M892 234v14M220 248h856M220 248v14M576 248v14M932 248v14" '
        f'fill="none" stroke="{t["faint"]}" stroke-width="2"/>'
    )
    payload = [
        (76, "ModManager.Core", "Unity-free domain"),
        (432, "Mods.Abstractions", "V1 safe contracts"),
        (788, "Mods.UnityUi", "in-game UI renderer"),
    ]
    for x, title, sub in payload:
        parts.append(box(x, 262, 288, 64, t["panel"], t["border"], rx=12))
        parts.append(text(x + 18, 288, title, 20, t["ink"], weight="700"))
        parts.append(text(x + 18, 312, sub, 17, t["muted"], family=MONO))

    # Zone B: the desktop side, which never loads game code.
    parts.append(box(48, 378, 1104, 150, t["bg"], t["border"], rx=20))
    parts.append(text(76, 412, "ON THE DESKTOP  ·  NEVER LOADS GAME CODE", 19,
                      t["zone_b"], family=MONO, weight="700", spacing="2.4"))

    parts.append(node(76, 430, 288, 72, "Launcher", "Flutter · Bloc", t))
    parts.append(node(392, 430, 288, 72, "topiaforge CLI", "Dart", t))
    parts.append(arrow(692, 724, 466, t["faint"]))
    parts.append(box(724, 430, 352, 32, t["panel"], t["border"], rx=10))
    parts.append(text(740, 453, "launcher_data — I/O · repair", 18, t["ink"], family=MONO))
    parts.append(box(724, 470, 352, 32, t["panel"], t["border"], rx=10))
    parts.append(text(740, 493, "launcher_domain — pure Dart", 18, t["ink"], family=MONO))

    parts.append("</svg>\n")
    return "\n".join(parts)


def main():
    for name in THEMES:
        (OUT / f"hero-{name}.svg").write_bytes(build_hero(name).encode("utf-8"))
        (OUT / f"architecture-{name}.svg").write_bytes(
            build_architecture(name).encode("utf-8")
        )
        print(f"wrote hero-{name}.svg and architecture-{name}.svg")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
