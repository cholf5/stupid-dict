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
    src/StupidDict.App/Assets/app-icon.ico    full-bleed 16..256 frames (256 = PNG,
                                              <256 = 32bpp BMP, see write_ico)
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
import io
import struct
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
ICO_SIZES = (16, 24, 32, 48, 64, 128, 256)


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


def ico_bmp_frame(im: Image.Image) -> bytes:
    """Encode one frame as a 32bpp DIB (BITMAPINFOHEADER + bottom-up BGRA
    rows + 1bpp all-zero AND mask). biHeight is doubled because the DIB must
    describe the XOR bitmap plus the AND mask below it. The mask stays zero:
    with 32bpp the alpha channel governs transparency, the mask is just the
    stride legacy parsers expect (online converters that botch these frames
    are the classic broken-alpha icon source)."""
    w, h = im.size
    bgra = im.convert("RGBA").tobytes("raw", "BGRA")
    xor = b"".join(bgra[y * w * 4:(y + 1) * w * 4] for y in range(h - 1, -1, -1))
    and_stride = ((w + 31) // 32) * 4
    and_mask = b"\x00" * (and_stride * h)
    header = struct.pack(
        "<IiiHHIIiiII", 40, w, h * 2, 1, 32, 0, len(xor) + len(and_mask), 0, 0, 0, 0
    )
    return header + xor + and_mask


def write_ico(full_bleed: Image.Image, out: Path) -> None:
    # Vista icon convention: only the 256 frame is PNG-compressed, frames
    # below it must be 32bpp BMP/DIB. Pillow >=9.3 writes every frame as PNG
    # by default, and small PNG frames break icon extraction in parts of the
    # Windows shell (2026-11-16 desktop-icon report: shell picked a wrong
    # fallback frame and the artwork rendered smaller than neighbours).
    frames = {}
    for size in ICO_SIZES:
        if size == 256:
            png = io.BytesIO()
            full_bleed.resize((256, 256), Image.LANCZOS).save(png, format="PNG")
            frames[size] = png.getvalue()
        else:
            frames[size] = ico_bmp_frame(full_bleed.resize((size, size), Image.LANCZOS))
    body = b"".join(frames[size] for size in ICO_SIZES)
    offset = 6 + 16 * len(ICO_SIZES)
    with out.open("wb") as fp:
        fp.write(struct.pack("<HHH", 0, 1, len(ICO_SIZES)))
        for size in ICO_SIZES:
            data = frames[size]
            # bWidth/bHeight bytes are 0 for 256 (single byte can't hold it)
            fp.write(struct.pack(
                "<BBBBHHII",
                0 if size == 256 else size, 0 if size == 256 else size,
                0, 0, 1, 32, len(data), offset,
            ))
            offset += len(data)
        fp.write(body)


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
    # Range checks BEFORE anything is written: content <= 0 crashes the resize
    # (and the ico bleed math divides by it), content > canvas breaks the
    # bbox assertions with a negative margin, negative radius crashes Pillow.
    if not 0 < args.content <= CANVAS:
        sys.exit(f"--content 须在 1..{CANVAS} 之间，收到 {args.content}")
    if args.radius < 0:
        sys.exit(f"--radius 不能为负，收到 {args.radius}")
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
    write_ico(full_bleed, ASSETS / "app-icon.ico")
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

    # parse-back sanity on the container we just assembled
    ico = (ASSETS / "app-icon.ico").read_bytes()
    assert struct.unpack("<H", ico[4:6])[0] == len(ICO_SIZES), "ico 帧数异常"
    for i, size in enumerate(ICO_SIZES):
        e = 6 + i * 16
        data_off = struct.unpack("<I", ico[e + 12:e + 16])[0]
        sig = ico[data_off:data_off + 4]
        if size == 256:
            assert sig == b"\x89PNG", "256 帧应为 PNG"
        else:
            assert ico[e] == size and sig == struct.pack("<I", 40), f"{size} 帧应为 32bpp BMP"

    preview = Path(tempfile.gettempdir()) / "app-icon-preview.png"
    write_preview(full_bleed, preview, previous)
    print(f"已写出 {png}（Apple 网格边距）")
    print(f"已写出 {ASSETS / 'app-icon.ico'}（满幅；256=PNG，<256=32bpp BMP）")
    if icns_ok:
        print(f"已写出 {ASSETS / 'app-icon.icns'}")
    print(f"预览（满幅 ico vs 旧母版，深/浅底 16/24/32/48px）: {preview}")
    print("提示: Assets/ 是入库文件，diff 满意后自行提交")


if __name__ == "__main__":
    main()
