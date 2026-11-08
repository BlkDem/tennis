"""Turns the variable Inter.woff2 that ships with Blender into static Regular and Bold TTFs for the UI, subset to
Latin, Cyrillic and the symbols the HUD uses. The web build has no system fonts to fall back on, so the game must
carry its own Cyrillic font.

    pip install fonttools brotli
    python Tools/Fonts/make_ui_font.py "<Blender>/5.2/datafiles/fonts/Inter.woff2" Assets/TennisSim/Fonts
"""
import sys

from fontTools.subset import Options, Subsetter
from fontTools.ttLib import TTFont
from fontTools.varLib.instancer import instantiateVariableFont

SRC, OUT_DIR = sys.argv[1], sys.argv[2]
RANGES = [(0x20, 0x7E), (0xA0, 0x17F), (0x400, 0x4FF), (0x2010, 0x2027), (0x2030, 0x203A), (0x20AC, 0x20AC),
          (0x2116, 0x2116), (0x2122, 0x2122), (0x2190, 0x2193), (0x25CF, 0x25CF)]
NEEDED = [0xB7, 0x2014, 0x2190, 0x2192, 0x25CF, ord("ё"), ord("Ж")]
unicodes = [u for a, b in RANGES for u in range(a, b + 1)]

for name, weight in (("Regular", 400), ("Bold", 700)):
    font = TTFont(SRC)
    axes = {a.axisTag: a.defaultValue for a in font["fvar"].axes}
    axes["wght"] = weight
    if "opsz" in axes:
        axes["opsz"] = 32  # display optical size: the UI text is mostly large
    font = instantiateVariableFont(font, axes, updateFontNames=True)
    options = Options()
    options.layout_features = ["kern", "liga", "calt"]
    options.name_IDs = ["*"]
    subsetter = Subsetter(options)
    subsetter.populate(unicodes=unicodes)
    subsetter.subset(font)
    font.flavor = None
    cmap = font.getBestCmap()
    missing = [f"U+{u:04X}" for u in NEEDED if u not in cmap]
    names = font["name"]
    family = names.getDebugName(1)
    sub = names.getDebugName(2)
    path = f"{OUT_DIR}/Inter-{name}.ttf"
    font.save(path)
    print(path, family, sub, len(cmap), "glyphs", "missing:", missing or "none")
