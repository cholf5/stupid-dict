#!/usr/bin/env python3
"""Regenerate the app icon assets from a candidate source image.

Usage:
    python3 scripts/make-icon.py <source-image> [--content 830] [--radius 194]

The source is center-cropped to a square, flattened onto white if it has
alpha, then rendered in two geometries with the rounded corners baked into
the artwork (runtime icons go through no system mask; see
docs/plans/2026-10-08-app-icon-design.md):

    png/icns  Apple icon grid — 1024 canvas, 830 content block, transparent
              margins; macOS Dock/bundle icon semantics want the margin
    ico       full-bleed 1024 — Windows exe/taskbar icons render the tile
              at its native size with no system mask, so baked-in margins
              shrink the artwork a step below every full-bleed neighbour
              (2026-10-09 taskbar feedback); same corner proportion,
              scaled to the canvas

written as:

    src/StupidDict.App/Assets/app-icon.png    1024 master (Apple margins)
    src/StupidDict.App/Assets/app-icon.ico    full-bleed, 16/24/32/48/64/128/256 frames
    src/StupidDict.App/Assets/app-icon.icns   full iconset (needs macOS iconutil)

A preview sheet lands at /tmp/app-icon-preview.png: the candidate's
taskbar-visible artwork (the full-bleed ico geometry) next to the currently
committed master, rendered at 16/24/32/48 on dark and light taskbar
backgrounds — the small sizes where icon candidates usually fail.

Requires Pillow (python3 -m pip install pillow). iconutil only exists on
macOS; without it the icns is skipped with a warning and png/ico are still
written. Everything here is regenerable, so the Assets/ files stay committed.
"""

import argparse
import subprocess
import sys
import tempfile
from pathlib import Path

try:
    from PIL import Image, ImageDraw
except ImportError:
    sys.exit("需要 Pillow：python3 -m pip install pillow")

REPO = Path(__file__).resolve().parent.parent
ASSETS = REPO / "src" / "StupidDict.App" / "Assets"
CANVAS = 1024
SS = 4  # mask supersampling factor, for antialiased corners

# iconutil's iconset naming: each entry plus its @2x retina variant
ICONSET = [(f"icon_{s}x{s}.png", s) for s in (16, 32, 128, 256, 512)] + [
    (f"icon_{s}x{s}@2x.png", s * 2) for s in (16, 32, 128, 256, 512)
]
PREVIEW_SIZES = (16, 24, 32, 48)
PREVIEW_SCALE = 6  # nearest-neighbor blowup so single pixels stay judgeable
PREVIEW_BACKGROUNDS = (("dark", (24, 24, 28)), ("light", (232, 232, 236)))


def styled_icon(source: Path, content: int, radius: int) -> Image.Image:
    margin = (CANVAS - content) // 2
    im = Image.open(source)
    if im.mode in ("RGBA", "LA", "PA"):
        flattened = Image.new("RGB", im.size, (255, 255, 255))
        flattened.paste(im.convert("RGBA"), (0, 0), im.convert("RGBA"))
        im = flattened
    else:
        im = im.convert("RGB")
    side = min(im.size)  # center-crop to square
    left, top = (im.width - side) // 2, (im.height - side) // 2
    im = im.crop((left, top, left + side, top + side))
    block = im.resize((content, content), Image.LANCZOS)

    mask = Image.new("L", (content * SS, content * SS), 0)
    draw = ImageDraw.Draw(mask)
    draw.rounded_rectangle([0, 0, content * SS - 1, content * SS - 1], radius=radius * SS, fill=255)
    mask = mask.resize((content, content), Image.LANCZOS)

    styled = Image.new("RGBA", (CANVAS, CANVAS), (0, 0, 0, 0))
    styled.paste(block, (margin, margin), mask)
    return styled


def write_icns(master: Path, out: Path) -> bool:
    if subprocess.call(["which", "iconutil"], stdout=subprocess.DEVNULL) != 0:
        print("警告: 没有 iconutil（仅 macOS 有），跳过 icns；打包时无法携带 Dock 图标")
        return False
    with tempfile.TemporaryDirectory() as tmp:
        iconset = Path(tmp) / "app-icon.iconset"
        iconset.mkdir()
        for name, px in ICONSET:
            Image.open(master).resize((px, px), Image.LANCZOS).save(iconset / name)
        if subprocess.call(["iconutil", "-c", "icns", str(iconset), "-o", str(out)]) != 0:
            print("警告: iconutil 生成 icns 失败，跳过（png/ico 已写出）")
            return False
    return True


def write_preview(candidate: Image.Image, out: Path, previous=None) -> None:
    columns = [("candidate", candidate)]
    if previous is not None:
        columns.append(("previous", previous))
    cell = PREVIEW_SIZES[-1] * PREVIEW_SCALE + 24
    pad = 30
    row_h = PREVIEW_SIZES[-1] * PREVIEW_SCALE + 18
    sheet = Image.new(
        "RGB",
        (pad * 2 + cell * len(columns), pad * 2 + len(PREVIEW_BACKGROUNDS) * (pad + row_h * len(PREVIEW_SIZES))),
        (128, 128, 128),
    )
    draw = ImageDraw.Draw(sheet)
    y = pad
    for name, bg in PREVIEW_BACKGROUNDS:
        draw.rectangle([0, y - 8, sheet.width, y + row_h * len(PREVIEW_SIZES) + 10], fill=bg)
        for size in PREVIEW_SIZES:
            for i, (_, im) in enumerate(columns):
                big = im.resize((size, size), Image.LANCZOS).resize(
                    (size * PREVIEW_SCALE, size * PREVIEW_SCALE), Image.NEAREST
                )
                sheet.paste(big, (pad + i * cell + (cell - big.width) // 2, y), big)
            y += row_h
        y += pad
    sheet.save(out)


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("source", type=Path, help="候选源图（任意尺寸，自动中心裁方）")
    parser.add_argument("--content", type=int, default=830, help="内容块边长（默认 830，Apple 网格）")
    parser.add_argument("--radius", type=int, default=194, help="圆角半径（默认 194 ≈ 内容的 23.4%%）")
    args = parser.parse_args()
    if not args.source.exists():
        sys.exit(f"源图不存在: {args.source}")

    styled = styled_icon(args.source, args.content, args.radius)
    png = ASSETS / "app-icon.png"
    previous = Image.open(png).convert("RGBA") if png.exists() else None
    styled.save(png)
    # Windows ico 满幅出图：任务栏/exe 图标按 tile 原生尺寸原样渲染、无系统
    # 蒙版，Apple 网格边距烤进去整幅小一圈（2026-10-09 任务栏反馈）；圆角
    # 同比例放大到画布，不另造几何。
    bleed_radius = round(CANVAS * args.radius / args.content)
    full_bleed = styled_icon(args.source, CANVAS, bleed_radius)
    full_bleed.save(ASSETS / "app-icon.ico", format="ICO")
    icns_ok = write_icns(png, ASSETS / "app-icon.icns")

    alpha = styled.getchannel("A")
    assert alpha.getpixel((0, 0)) == 0 and alpha.getpixel((CANVAS - 1, CANVAS - 1)) == 0, "角部应透明"
    bbox = alpha.getbbox()
    margin = (CANVAS - args.content) // 2
    assert bbox == (margin, margin, margin + args.content, margin + args.content), f"内容区异常: {bbox}"

    bleed_alpha = full_bleed.getchannel("A")
    assert bleed_alpha.getpixel((0, 0)) == 0, "满幅 ico 角部应透明（圆角烤进素材）"
    bleed_bbox = bleed_alpha.getbbox()
    assert bleed_bbox == (0, 0, CANVAS, CANVAS), f"满幅 ico 未撑满画布: {bleed_bbox}"

    preview = Path(tempfile.gettempdir()) / "app-icon-preview.png"
    write_preview(full_bleed, preview, previous)
    print(f"已写出 {png}（Apple 网格边距）")
    print(f"已写出 {ASSETS / 'app-icon.ico'}（满幅）")
    if icns_ok:
        print(f"已写出 {ASSETS / 'app-icon.icns'}")
    print(f"预览（满幅 ico vs 旧母版，深/浅底 16/24/32/48px）: {preview}")
    print("提示: Assets/ 是入库文件，diff 满意后自行提交")


if __name__ == "__main__":
    main()
