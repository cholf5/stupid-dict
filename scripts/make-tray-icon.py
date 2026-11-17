#!/usr/bin/env python3
"""Bake the macOS menu-bar (template) tray icon.

The status-bar glyph is drawn here instead of being cropped out of app-icon.png:
AppKit renders a template image as a single-colour silhouette of its alpha
channel, so a coloured artwork would come out as a solid rounded square. The
glyph repeats the app icon's face (two dots + a dash inside a rounded card) but
as pure black + alpha, which the system inverts for light/dark menu bars.

Usage: python3 scripts/make-tray-icon.py
Writes src/StupidDict.App/Assets/tray-icon.png and /tmp/tray-icon-preview.png
(candidate at 18/22/36px on both menu-bar backgrounds, for eyeballing).
"""

from pathlib import Path

from PIL import Image, ImageDraw

ROOT = Path(__file__).resolve().parent.parent
OUT = ROOT / "src/StupidDict.App/Assets/tray-icon.png"
PREVIEW = Path("/tmp/tray-icon-preview.png")

SIZE = 128
SS = 4  # supersample factor


def draw_glyph() -> Image.Image:
    canvas = SIZE * SS
    image = Image.new("RGBA", (canvas, canvas), (0, 0, 0, 0))
    draw = ImageDraw.Draw(image)

    def s(*values: float) -> list[float]:
        return [v * SS for v in values]

    stroke = 11 * SS
    # Rounded card: the book cover of the app icon, drawn as an outline so it
    # stays open at 18px (a solid block reads as a smudge).
    draw.rounded_rectangle(
        s(14, 12, 114, 116), radius=28 * SS, outline=(0, 0, 0, 255), width=int(stroke)
    )
    # Eyes: same unequal pair as the app icon.
    draw.ellipse(s(35, 45, 59, 69), fill=(0, 0, 0, 255))
    draw.ellipse(s(75, 50, 89, 64), fill=(0, 0, 0, 255))
    # Mouth: a slightly tilted dash, as in the app icon.
    draw.line(s(55, 89, 80, 83), fill=(0, 0, 0, 255), width=int(9 * SS))
    for x, y in ((55, 89), (80, 83)):
        r = 4.5 * SS
        draw.ellipse([x * SS - r, y * SS - r, x * SS + r, y * SS + r], fill=(0, 0, 0, 255))

    return image.resize((SIZE, SIZE), Image.LANCZOS)


def main() -> None:
    glyph = draw_glyph()
    glyph.save(OUT)
    print(f"wrote {OUT.relative_to(ROOT)} ({glyph.width}x{glyph.height})")

    # Preview: candidate at menu-bar sizes over light and dark bars.
    sizes = [18, 22, 36]
    pad = 10
    cell = max(sizes) + pad * 2
    sheet = Image.new("RGBA", (cell * len(sizes), cell * 2), (0, 0, 0, 0))
    for row, bg in enumerate([(246, 246, 246, 255), (32, 32, 32, 255)]):
        for col, size in enumerate(sizes):
            tile = Image.new("RGBA", (cell, cell), bg)
            scaled = glyph.resize((size, size), Image.LANCZOS)
            if row == 1:
                # Template images are inverted on dark menu bars.
                light = Image.new("RGBA", scaled.size, (255, 255, 255, 255))
                light.putalpha(scaled.getchannel("A"))
                scaled = light
            tile.alpha_composite(scaled, ((cell - size) // 2, (cell - size) // 2))
            sheet.alpha_composite(tile, (col * cell, row * cell))
    sheet = sheet.resize((sheet.width * 4, sheet.height * 4), Image.NEAREST)
    sheet.save(PREVIEW)
    print(f"wrote {PREVIEW}")


if __name__ == "__main__":
    main()
