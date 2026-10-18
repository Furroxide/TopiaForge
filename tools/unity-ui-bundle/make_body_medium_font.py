#!/usr/bin/env python3
"""Writes the kit's small-text face: a static Medium (wght 500) instance of the bundled Quicksand variable font.

The UI bundle bakes `TopiaForge Body Medium SDF` from the file this writes, Assets/Fonts/TopiaForgeBody-Medium.ttf.
Run it only when the source font changes, then re-bake that SDF and rebuild the bundle, as README.md describes.

    pip install fonttools==4.65.0
    python make_body_medium_font.py

The output is byte-for-byte reproducible with fontTools 4.65.0; another version can write different bytes. The
script refuses a source font other than the upstream release recorded in THIRD_PARTY_NOTICES.md.

The SIL Open Font License reserves the name "Quicksand", and an instance is a Modified Version, so the instance's
font names say "TopiaForge Body" instead. The copyright notice, the licence fields and the designer credits stay.
"""

import hashlib
import io
import sys
from pathlib import Path

from fontTools import version as fonttools_version
from fontTools.ttLib import TTFont
from fontTools.varLib import instancer

HERE = Path(__file__).resolve().parent
SOURCE = HERE / "Assets" / "Fonts" / "Quicksand-VariableFont_wght.ttf"
TARGET = HERE / "Assets" / "Fonts" / "TopiaForgeBody-Medium.ttf"
SOURCE_SHA256 = "39c9b64223561f56aaff6062a6f04063c4fc86809ad6768722c06614d977e1cc"
FONTTOOLS_VERSION = "4.65.0"
WEIGHT = 500
FAMILY = "TopiaForge Body"
STYLE = "Medium"
POSTSCRIPT = "TopiaForgeBody-Medium"


def sha256(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def rename(font: TTFont) -> None:
    """Gives the instance a family name outside the OFL Reserved Font Name, keeping every other record."""
    names = font["name"]
    version = names.getDebugName(5) or ""
    version_number = version.replace("Version ", "", 1)
    full = FAMILY + " " + STYLE
    replacements = {
        1: full,
        2: "Regular",
        3: version_number + ";NONE;" + POSTSCRIPT,
        4: full,
        6: POSTSCRIPT,
        16: FAMILY,
        17: STYLE,
    }
    for record in names.names:
        if record.nameID in replacements:
            record.string = replacements[record.nameID]
    # The variations prefix and the axis names belong to the variable font only.
    names.names = [record for record in names.names if record.nameID != 25]
    # Only the copyright notice, which must stay, may still name the original font.
    leftover = [record for record in names.names if record.nameID != 0 and "Quicksand" in record.toUnicode()]
    if leftover:
        raise SystemExit("A font name still uses the reserved name: " + repr([r.nameID for r in leftover]))


def build() -> bytes:
    data = SOURCE.read_bytes()
    if sha256(data) != SOURCE_SHA256:
        raise SystemExit(SOURCE.name + " is not the upstream release this instance is made from.")
    if fonttools_version != FONTTOOLS_VERSION:
        print("warning: fontTools " + fonttools_version + " is not " + FONTTOOLS_VERSION + "; the bytes may differ.",
              file=sys.stderr)
    # recalcTimestamp=False keeps the source's head timestamps, so a second run writes the same bytes.
    font = TTFont(SOURCE, recalcTimestamp=False)
    static = instancer.instantiateVariableFont(font, {"wght": WEIGHT})
    if "STAT" in static:
        del static["STAT"]
    rename(static)
    static["name"].removeUnusedNames(static)
    if static["OS/2"].usWeightClass != WEIGHT:
        raise SystemExit("The instance's weight class is " + str(static["OS/2"].usWeightClass) + ", not 500.")
    buffer = io.BytesIO()
    static.save(buffer)
    return buffer.getvalue()


def main() -> int:
    data = build()
    TARGET.write_bytes(data)
    print("wrote " + TARGET.name + " (" + str(len(data)) + " bytes, SHA-256 " + sha256(data) + ")")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
