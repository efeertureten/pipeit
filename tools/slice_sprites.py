#!/usr/bin/env python3
"""
Su Ustası — sprite slicer.

Cuts the official reference sheets into individual, transparent PNG sprites for
both the Unity project and the web playable.

    python tools/slice_sprites.py            # slice everything
    python tools/slice_sprites.py --preview  # also write a checkerboard contact sheet

Only dependency: Pillow (pip install pillow).

How it works (per sprite):
  1. Crop a generous source region that contains exactly one illustration.
  2. Estimate the paper-cream background colour from the crop border (median).
  3. Foreground = pixels whose max per-channel distance from the background > THRESH.
  4. Label 4-connected foreground components and keep the requested ones
     ("largest" drops the little direction arrows / confetti that float around
     every illustration on the sheets).
  5. Fill enclosed holes unless they are genuinely background-coloured
     (so the inside of chain links stays transparent, but pale highlights do not).
  6. Build a soft alpha edge from the colour distance and "decontaminate" the
     anti-aliased fringe so no cream halo remains on dark backgrounds.
  7. Trim, pad, optionally downscale, write PNG + a JSON manifest.
"""
from __future__ import annotations

import argparse
import json
import os
import sys
from statistics import median

try:
    from PIL import Image, ImageChops, ImageFilter
except ImportError:  # pragma: no cover
    sys.exit("Pillow is required:  pip install pillow")

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
REF_DIR = os.path.join(ROOT, "reference-assets")

OUT_WEB = os.path.join(ROOT, "web-playable", "assets", "sprites")
OUT_UNITY = os.path.join(ROOT, "unity-project", "Assets", "_Game", "Resources", "Sprites")

THRESH = 38          # foreground if max channel distance from background > THRESH
EDGE_LO, EDGE_HI = 10, 70   # soft-alpha ramp for the anti-aliased fringe
PAD = 6

# --------------------------------------------------------------------------------------
# Sprite catalogue. Rects are in ORIGINAL source pixels: (left, top, right, bottom).
# --------------------------------------------------------------------------------------
# pipes_spritesheet.jpg is 2752x1536 laid out as a clean 4x2 grid (688x768 cells).
_CW, _CH = 688, 768


def _cell(col: int, row: int) -> tuple[int, int, int, int]:
    return (col * _CW, row * _CH, (col + 1) * _CW, (row + 1) * _CH)


SPRITES = [
    # ---- 8 GDD pipe types (pipes_spritesheet.jpg) -----------------------------------
    dict(name="pipe_straight",     src="pipes_spritesheet.jpg", rect=_cell(0, 0), keep="largest", group="Pipes", max=512),
    dict(name="pipe_elbow",        src="pipes_spritesheet.jpg", rect=_cell(1, 0), keep="largest", group="Pipes", max=512),
    dict(name="pipe_dry",          src="pipes_spritesheet.jpg", rect=_cell(2, 0), keep="largest", group="Pipes", max=512),
    dict(name="pipe_rusty",        src="pipes_spritesheet.jpg", rect=_cell(3, 0), keep="largest", group="Pipes", max=512),
    dict(name="pipe_frozen",       src="pipes_spritesheet.jpg", rect=_cell(0, 1), keep="largest", group="Pipes", max=512),
    dict(name="pipe_locked",       src="pipes_spritesheet.jpg", rect=_cell(1, 1), keep="largest", group="Pipes", max=512),
    dict(name="pipe_leaking",      src="pipes_spritesheet.jpg", rect=_cell(2, 1), keep="largest+water", group="Pipes", max=512),
    dict(name="pipe_rotary_valve", src="pipes_spritesheet.jpg", rect=_cell(3, 1), keep="largest", group="Pipes", max=512),
    # ---- Usta mascot (mascot_usta.jpg, 2048x2048) -------------------------------------
    # Happy pose sits top-left, worried pose + burst pipe bottom-right; the regions do
    # not overlap in x, which keeps confetti of one pose out of the other.
    dict(name="mascot_happy",   src="mascot_usta.jpg", rect=(0, 120, 1030, 1420),   keep="largest", group="Mascot", max=768),
    dict(name="mascot_worried", src="mascot_usta.jpg", rect=(1000, 700, 2048, 2048), keep="all", min_area=900, group="Mascot", max=768),
    # ---- HUD icons (gameplay_hud.jpg, 1536x2752) — used by the web HUD ----------------
    dict(name="booster_xray",   src="gameplay_hud.jpg", rect=(210, 2290, 560, 2650),  keep="largest", group="UI", max=256),
    dict(name="booster_hammer", src="gameplay_hud.jpg", rect=(595, 2290, 945, 2650),  keep="largest", group="UI", max=256),
    dict(name="booster_patch",  src="gameplay_hud.jpg", rect=(975, 2290, 1330, 2650), keep="largest", group="UI", max=256),
    dict(name="icon_drop",      src="gameplay_hud.jpg", rect=(100, 95, 240, 275),     keep="largest", group="UI", max=128),
    dict(name="btn_settings",   src="gameplay_hud.jpg", rect=(1240, 50, 1500, 320),   keep="largest", group="UI", max=192),
]


# --------------------------------------------------------------------------------------
def estimate_bg(img: Image.Image) -> tuple[int, int, int]:
    """Median colour of the 1px crop border (robust to a few sprite pixels touching it)."""
    w, h = img.size
    px = img.load()
    samples = []
    step = max(1, (w + h) // 400)
    for x in range(0, w, step):
        samples.append(px[x, 0]); samples.append(px[x, h - 1])
    for y in range(0, h, step):
        samples.append(px[0, y]); samples.append(px[w - 1, y])
    return tuple(int(median(c[i] for c in samples)) for i in range(3))


def distance_map(img: Image.Image, bg) -> Image.Image:
    """L image: max |channel - bg| per pixel (Chebyshev colour distance)."""
    diff = ImageChops.difference(img, Image.new("RGB", img.size, bg))
    r, g, b = diff.split()
    return ImageChops.lighter(ImageChops.lighter(r, g), b)


def label(work: bytearray, w: int, h: int):
    """4-connected components over `work` (1 = member). Consumes `work`. Returns list of pixel-index lists."""
    n = w * h
    comps = []
    start = 0
    find = work.find
    while True:
        i = find(1, start)
        if i < 0:
            break
        work[i] = 0
        stack = [i]
        pix = []
        push, pop, add = stack.append, stack.pop, pix.append
        while stack:
            p = pop()
            add(p)
            x = p % w
            if x > 0 and work[p - 1]:
                work[p - 1] = 0; push(p - 1)
            if x < w - 1 and work[p + 1]:
                work[p + 1] = 0; push(p + 1)
            if p >= w and work[p - w]:
                work[p - w] = 0; push(p - w)
            if p < n - w and work[p + w]:
                work[p + w] = 0; push(p + w)
        comps.append(pix)
        start = i + 1
    return comps


def bbox_of(pix, w):
    xs = [p % w for p in pix]
    ys = [p // w for p in pix]
    return min(xs), min(ys), max(xs), max(ys)


def is_water(pix, rgb_bytes) -> bool:
    """Mean colour is water-blue. Drops carry a dark outline, so their mean sits around
    (90,150,165): blue clearly dominates red and is at least as strong as green."""
    r = g = b = 0
    for p in pix:
        o = 3 * p
        r += rgb_bytes[o]; g += rgb_bytes[o + 1]; b += rgb_bytes[o + 2]
    n = len(pix)
    r, g, b = r / n, g / n, b / n
    return b > 110 and b - r > 45 and b >= g


def slice_sprite(spec: dict, cache: dict) -> tuple[Image.Image, dict]:
    src_path = os.path.join(REF_DIR, spec["src"])
    if src_path not in cache:
        cache[src_path] = Image.open(src_path).convert("RGB")
    sheet = cache[src_path]
    l, t, r, b = spec["rect"]
    r, b = min(r, sheet.width), min(b, sheet.height)
    crop = sheet.crop((l, t, r, b))
    w, h = crop.size
    n = w * h
    bg = estimate_bg(crop)
    d = distance_map(crop, bg)
    dbytes = d.tobytes()
    rgb = crop.tobytes()

    fg = bytearray(d.point([0] * (THRESH + 1) + [1] * (255 - THRESH)).tobytes())
    comps = label(bytearray(fg), w, h)
    if not comps:
        raise RuntimeError(f"{spec['name']}: no foreground found")
    comps.sort(key=len, reverse=True)

    keep_mode = spec.get("keep", "largest")
    keep = [comps[0]]
    if keep_mode == "largest+water":
        bx0, by0, bx1, by1 = bbox_of(comps[0], w)
        for c in comps[1:]:
            if len(c) < 40:
                continue
            cx0, cy0, cx1, cy1 = bbox_of(c, w)
            inside = cx0 >= bx0 and cy0 >= by0 and cx1 <= bx1 and cy1 <= by1
            if inside and is_water(c, rgb):
                keep.append(c)
    elif keep_mode == "all":
        min_area = spec.get("min_area", 200)
        keep += [c for c in comps[1:] if len(c) >= min_area]

    mask = bytearray(n)
    for c in keep:
        for p in c:
            mask[p] = 1
    comp_area = sum(len(c) for c in keep)

    # ---- hole filling --------------------------------------------------------------
    # Outside = non-mask pixels reachable from the crop border.
    nonmask = bytearray(1 - v for v in mask)
    outside = bytearray(n)
    stack = [i for i in range(w) if nonmask[i]] + [n - w + i for i in range(w) if nonmask[n - w + i]]
    stack += [y * w for y in range(h) if nonmask[y * w]] + [y * w + w - 1 for y in range(h) if nonmask[y * w + w - 1]]
    for p in stack:
        nonmask[p] = 0; outside[p] = 1
    while stack:
        p = stack.pop()
        x = p % w
        for q, ok in ((p - 1, x > 0), (p + 1, x < w - 1), (p - w, p >= w), (p + w, p < n - w)):
            if ok and nonmask[q]:
                nonmask[q] = 0; outside[q] = 1; stack.append(q)
    holes_work = bytearray(1 if (mask[i] == 0 and outside[i] == 0) else 0 for i in range(n))
    filled = 0
    for hole in label(holes_work, w, h):
        mean_d = sum(dbytes[p] for p in hole) / len(hole)
        # Pinholes / pale highlights get filled, genuine background (chain loops) stays clear.
        if len(hole) < max(400, comp_area * 0.004) or mean_d > 16:
            for p in hole:
                mask[p] = 1
            filled += 1

    # ---- soft alpha + decontamination ---------------------------------------------
    mask_img = Image.frombytes("L", (w, h), bytes(255 if v else 0 for v in mask))
    inner = mask_img.filter(ImageFilter.MinFilter(5))       # solid core (eroded 2px)
    outer = mask_img.filter(ImageFilter.MaxFilter(3))       # fringe reach (dilated 1px)
    ramp = [0 if v <= EDGE_LO else 255 if v >= EDGE_HI else int((v - EDGE_LO) * 255 / (EDGE_HI - EDGE_LO)) for v in range(256)]
    soft = ImageChops.multiply(outer, d.point(ramp))
    alpha = ImageChops.lighter(inner, soft)
    abytes = alpha.tobytes()

    out = bytearray(4 * n)
    br, bgc, bb = bg
    for i in range(n):
        a = abytes[i]
        o3, o4 = 3 * i, 4 * i
        if a == 0:
            continue
        pr, pg, pb = rgb[o3], rgb[o3 + 1], rgb[o3 + 2]
        if a < 255:
            k = 255.0 / a       # un-blend: p = a*c + (1-a)*bg  =>  c = bg + (p-bg)/a
            pr = min(255, max(0, int(br + (pr - br) * k)))
            pg = min(255, max(0, int(bgc + (pg - bgc) * k)))
            pb = min(255, max(0, int(bb + (pb - bb) * k)))
        out[o4] = pr; out[o4 + 1] = pg; out[o4 + 2] = pb; out[o4 + 3] = a

    rgba = Image.frombytes("RGBA", (w, h), bytes(out))
    box = rgba.getchannel("A").getbbox()
    x0, y0, x1, y1 = box
    x0, y0 = max(0, x0 - PAD), max(0, y0 - PAD)
    x1, y1 = min(w, x1 + PAD), min(h, y1 + PAD)
    rgba = rgba.crop((x0, y0, x1, y1))

    max_dim = spec.get("max")
    if max_dim and max(rgba.size) > max_dim:
        s = max_dim / max(rgba.size)
        size = (max(1, round(rgba.width * s)), max(1, round(rgba.height * s)))
        rgba = rgba.convert("RGBa").resize(size, Image.LANCZOS).convert("RGBA")

    meta = {
        "name": spec["name"],
        "group": spec["group"],
        "source": spec["src"],
        "sourceRect": [l + x0, t + y0, l + x1, t + y1],
        "size": list(rgba.size),
        "pivot": [0.5, 0.5],
        "background": "#%02X%02X%02X" % bg,
        "components": len(keep),
        "holesFilled": filled,
    }
    return rgba, meta


def contact_sheet(images: list[tuple[str, Image.Image]], path: str) -> None:
    cell = 300
    cols = 5
    rows = (len(images) + cols - 1) // cols
    sheet = Image.new("RGB", (cols * cell, rows * cell), (40, 44, 52))
    # checkerboard so transparency + halos are obvious
    tile = 16
    for y in range(0, sheet.height, tile):
        for x in range(0, sheet.width, tile):
            if (x // tile + y // tile) % 2:
                sheet.paste((70, 74, 84), (x, y, x + tile, y + tile))
    for i, (_, im) in enumerate(images):
        th = im.copy()
        th.thumbnail((cell - 20, cell - 20), Image.LANCZOS)
        cx, cy = (i % cols) * cell, (i // cols) * cell
        sheet.paste(th, (cx + (cell - th.width) // 2, cy + (cell - th.height) // 2), th)
    sheet.save(path)


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--preview", metavar="PNG", nargs="?", const=os.path.join(ROOT, "tools", "sprite_preview.png"),
                    help="write a checkerboard contact sheet (default tools/sprite_preview.png)")
    ap.add_argument("--only", nargs="*", help="slice only these sprite names")
    args = ap.parse_args()

    cache: dict = {}
    manifest = []
    images = []
    for spec in SPRITES:
        if args.only and spec["name"] not in args.only:
            continue
        im, meta = slice_sprite(spec, cache)
        web_path = os.path.join(OUT_WEB, spec["name"] + ".png")
        unity_dir = os.path.join(OUT_UNITY, spec["group"])
        os.makedirs(OUT_WEB, exist_ok=True)
        os.makedirs(unity_dir, exist_ok=True)
        im.save(web_path, optimize=True)
        im.save(os.path.join(unity_dir, spec["name"] + ".png"), optimize=True)
        manifest.append(meta)
        images.append((spec["name"], im))
        print(f"  {spec['name']:<20} {im.width:>4}x{im.height:<4}  bg={meta['background']}  "
              f"parts={meta['components']} holes={meta['holesFilled']}")

    if not args.only:
        for path in (os.path.join(OUT_WEB, "sprites.json"), os.path.join(OUT_UNITY, "sprites.json")):
            with open(path, "w", encoding="utf-8") as f:
                json.dump({"generatedBy": "tools/slice_sprites.py", "sprites": manifest}, f, indent=2, ensure_ascii=False)
    if args.preview:
        contact_sheet(images, args.preview)
        print(f"  preview -> {args.preview}")
    print(f"Sliced {len(manifest)} sprites -> {os.path.relpath(OUT_WEB, ROOT)} and {os.path.relpath(OUT_UNITY, ROOT)}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
