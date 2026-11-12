#!/usr/bin/env python3
"""
Su Ustası — AI UI art processor (magenta chroma key → transparent UI sprites).

    python tools/process_ai_art.py              # process everything
    python tools/process_ai_art.py --only coin pill
    npm run sprites:ai

Sources: reference-assets/*.jpg (AI-generated UI art on a magenta background, JPEG).
Outputs: web-playable/assets/sprites/ui/  and  unity-project/Assets/_Game/Resources/Sprites/UI/
         (+ ui_sprites.json with sizes and 9-slice borders)
Backups: web-playable/assets/sprites/_orig/  (the HUD sprites these supersede; written once, never overwritten)
Preview: ui-shots/ai_art_preview_dark.png and ui-shots/ai_art_preview_light.png (halo check)

Requires Pillow + numpy  (python -m pip install pillow numpy).

Why not a single colour key: the sources are JPEGs, so the magenta carries 8x8 block noise,
tone drift and chroma bleed into the outlines. Per pixel we therefore build a *soft* key:
  bgness = hueBand(285..325°) × saturationRamp × max(near-bg in CIELAB ΔE, same-chroma shadow)
  fgness = 1 − bgness   → thresholded RANGE, not equality (partial alpha in the transition).
Warm pinks (mascot cheeks ~356°, hammer/coin sparkles 10–25°) fall outside the hue band and are
kept. Then: speck/hole cleanup (connected components), the shared soft-alpha edge from
slice_sprites.py, a 1px erode + feather, colour un-blending (slice_sprites.unblend) and a magenta
despill on the edge band only (R and B are limited relative to G, interior colours untouched).
"""
from __future__ import annotations

import argparse
import json
import shutil
import sys
from pathlib import Path

try:
    import numpy as np
    from PIL import Image, ImageChops, ImageFilter
except ImportError:  # pragma: no cover
    sys.exit("Pillow and numpy are required:  python -m pip install pillow numpy")

TOOLS = Path(__file__).resolve().parent
sys.path.insert(0, str(TOOLS))
from slice_sprites import contact_sheet, estimate_bg, label, soft_alpha, unblend  # noqa: E402  (shared helpers)

ROOT = TOOLS.parent
SRC_DIR = ROOT / "reference-assets"
OUT_WEB = ROOT / "web-playable" / "assets" / "sprites" / "ui"
OUT_UNITY = ROOT / "unity-project" / "Assets" / "_Game" / "Resources" / "Sprites" / "UI"
BACKUP_DIR = ROOT / "web-playable" / "assets" / "sprites" / "_orig"
SHOTS_DIR = ROOT / "ui-shots"

#: Existing HUD sprites that the AI art supersedes (backed up once before being replaced).
SUPERSEDED = ["booster_xray.png", "booster_hammer.png", "booster_patch.png", "btn_settings.png", "icon_drop.png"]

# ---------------------------------------------------------------------------- key tuning
HUE_LO, HUE_HI, HUE_SOFT = 285.0, 325.0, 12.0   # magenta band in degrees, soft shoulders
SAT_LO, SAT_HI = 0.22, 0.42                     # greys/whites/pastels are never keyed
DE_LO, DE_HI = 14.0, 42.0                       # CIELAB ΔE to the estimated background
CHROMA_LO, CHROMA_HI = 0.030, 0.085             # rg-chromaticity distance for darker same-hue shadows
SHADOW_V_LO, SHADOW_V_HI = 0.35, 0.55           # shadows are bright (V≈0.8); dark outline noise is not
FG_EDGE_LO, FG_EDGE_HI = 60, 215                # soft_alpha ramp on the fgness map (0..255)
MIN_SPECK = 120                                 # px at source resolution: smaller opaque islands are noise
MAX_HOLE = 600                                  # px: smaller enclosed transparent holes are filled
PAD = 6

# ---------------------------------------------------------------------------- catalogue
# rect = (x0, y0, x1, y1) as fractions of the source size. slice = CSS border-image style
# fractions (top, right, bottom, left) of the OUTPUT height (h) or width (w) for 9-slicing.
SPRITES = [
    dict(name="btn_green", src="buttons_sheet*.jpg", rect=(0, 0.000, 1, 0.345), max=768, slice=("h", 0.32, 0.5, 0.42, 0.5)),
    dict(name="btn_blue", src="buttons_sheet*.jpg", rect=(0, 0.345, 1, 0.656), max=768, slice=("h", 0.32, 0.5, 0.42, 0.5)),
    dict(name="btn_red", src="buttons_sheet*.jpg", rect=(0, 0.656, 1, 1.000), max=768, slice=("h", 0.32, 0.5, 0.42, 0.5)),
    dict(name="panel_modal", src="panel_modal.jpg", rect=(0, 0, 1, 1), max=768, slice=("wh", 0.14, 0.12, 0.16, 0.12)),
    dict(name="icon_drop", src="drops_sheet.jpg", rect=(0, 0, 0.502, 1), max=192),
    dict(name="icon_drop_empty", src="drops_sheet.jpg", rect=(0.502, 0, 1, 1), max=192),
    dict(name="coin", src="coin_pill.jpg", rect=(0, 0, 1, 0.74), max=256, shape="circle"),
    dict(name="pill", src="coin_pill.jpg", rect=(0, 0.60, 1, 1), max=512, shape="stadium", slice=("h", 0.45, 0.5, 0.45, 0.5)),
    dict(name="btn_settings", src="btn_settings_raw.jpg", rect=(0, 0, 1, 1), max=256),
    dict(name="booster_xray", src="booster_xray_raw.jpg", rect=(0, 0, 1, 1), max=384),
    dict(name="booster_hammer", src="booster_hammer_raw.jpg", rect=(0, 0, 1, 1), max=384),
    dict(name="booster_patch", src="booster_patch_raw.jpg", rect=(0, 0, 1, 1), max=384),
    dict(name="mascot_drop_wave", src="mascot_sheet.jpg", rect=(0, 0, 0.352, 1), max=512),
    dict(name="mascot_drop_sad", src="mascot_sheet.jpg", rect=(0.352, 0, 0.643, 1), max=512),
    dict(name="mascot_drop_happy", src="mascot_sheet.jpg", rect=(0.643, 0, 1, 1), max=512),
]


# ---------------------------------------------------------------------------- colour maths
def ramp(v, lo, hi):
    return np.clip((v - lo) / (hi - lo), 0.0, 1.0)


def rgb_to_hsv(rgb: np.ndarray):
    """rgb float in 0..1 → hue degrees, saturation, value."""
    r, g, b = rgb[..., 0], rgb[..., 1], rgb[..., 2]
    mx, mn = rgb.max(-1), rgb.min(-1)
    c = mx - mn
    safe = np.where(c > 1e-6, c, 1.0)
    h = np.where(mx == r, ((g - b) / safe) % 6, np.where(mx == g, (b - r) / safe + 2, (r - g) / safe + 4)) * 60.0
    h = np.where(c > 1e-6, h, 0.0)
    s = np.where(mx > 1e-6, c / np.where(mx > 1e-6, mx, 1.0), 0.0)
    return h, s, mx


def rgb_to_lab(rgb: np.ndarray) -> np.ndarray:
    """sRGB (0..1) → CIELAB (D65)."""
    lin = np.where(rgb <= 0.04045, rgb / 12.92, ((rgb + 0.055) / 1.055) ** 2.4)
    m = np.array([[0.4124564, 0.3575761, 0.1804375], [0.2126729, 0.7151522, 0.0721750], [0.0193339, 0.1191920, 0.9503041]])
    xyz = lin @ m.T / np.array([0.95047, 1.0, 1.08883])
    f = np.where(xyz > 216 / 24389, np.cbrt(xyz), (24389 / 27 * xyz + 16) / 116)
    return np.stack([116 * f[..., 1] - 16, 500 * (f[..., 0] - f[..., 1]), 200 * (f[..., 1] - f[..., 2])], -1)


def hue_band(h: np.ndarray) -> np.ndarray:
    """1 inside [HUE_LO, HUE_HI], linear shoulders of HUE_SOFT degrees, 0 elsewhere (no wrap needed)."""
    return np.minimum(ramp(h, HUE_LO - HUE_SOFT, HUE_LO), 1.0 - ramp(h, HUE_HI, HUE_HI + HUE_SOFT))


def fgness_map(rgb: np.ndarray, bg: np.ndarray) -> np.ndarray:
    """0 = certainly magenta background (incl. its darker drop shadows) … 1 = certainly sprite."""
    h, s, v = rgb_to_hsv(rgb)
    lab = rgb_to_lab(rgb)
    lab_bg = rgb_to_lab(bg.reshape(1, 1, 3))[0, 0]
    de = np.sqrt(((lab - lab_bg) ** 2).sum(-1))
    near = 1.0 - ramp(de, DE_LO, DE_HI)
    chrom = rgb / np.maximum(rgb.sum(-1, keepdims=True), 1e-6)
    chrom_bg = bg / max(bg.sum(), 1e-6)
    # Drop shadows on the sheet keep the magenta chroma at V≈0.8. Very dark magenta-tinted pixels are
    # JPEG ringing inside navy outlines — never key those (SHADOW_V_LO..HI gate).
    shadow = (1.0 - ramp(np.sqrt(((chrom - chrom_bg) ** 2).sum(-1)), CHROMA_LO, CHROMA_HI)) \
        * ramp(bg.max() - v, 0.0, 0.05) * ramp(v, SHADOW_V_LO, SHADOW_V_HI)
    bgness = hue_band(h) * ramp(s, SAT_LO, SAT_HI) * np.maximum(near, shadow)
    return 1.0 - bgness


def despill(rgb: np.ndarray, band: np.ndarray) -> np.ndarray:
    """Magenta despill on `band` pixels: when both R and B exceed G, pull both down by the excess."""
    r, g, b = rgb[..., 0], rgb[..., 1], rgb[..., 2]
    spill = np.maximum(np.minimum(r, b) - g, 0.0) * band
    out = rgb.copy()
    out[..., 0] = r - spill
    out[..., 2] = b - spill
    return out


def magenta_residue(rgba: np.ndarray) -> float:
    """Halo metric: share of visible edge pixels that still read as magenta."""
    a = rgba[..., 3]
    edge = (a > 20) & (a < 255)
    near_edge = np.array(Image.fromarray(((a < 250) * 255).astype(np.uint8)).filter(ImageFilter.MaxFilter(5))) > 0
    edge |= (a >= 250) & near_edge
    if not edge.any():
        return 0.0
    h, s, v = rgb_to_hsv(rgba[..., :3].astype(np.float32) / 255.0)
    bad = edge & (h >= HUE_LO - HUE_SOFT) & (h <= HUE_HI + HUE_SOFT) & (s > 0.35) & (v > 0.25)
    return float(bad.sum()) / float(edge.sum())


# ---------------------------------------------------------------------------- cleanup
def drop_small_components(mask: np.ndarray, min_area: int) -> tuple[np.ndarray, int]:
    """Remove opaque islands smaller than min_area (JPEG specks). Uses slice_sprites.label."""
    h, w = mask.shape
    comps = label(bytearray(mask.astype(np.uint8).tobytes()), w, h)
    flat = mask.reshape(-1).copy()
    removed = 0
    for c in comps:
        if len(c) < min_area:
            flat[np.fromiter(c, dtype=np.int64)] = False
            removed += 1
    return flat.reshape(h, w), removed


def enclosed_small_holes(mask: np.ndarray, max_area: int) -> tuple[np.ndarray, int]:
    """Transparent regions not touching the crop border and smaller than max_area (isolated magenta spots)."""
    h, w = mask.shape
    holes = np.zeros(h * w, dtype=bool)
    n = 0
    for c in label(bytearray((~mask).astype(np.uint8).tobytes()), w, h):
        if len(c) >= max_area:
            continue
        idx = np.fromiter(c, dtype=np.int64)
        ys, xs = idx // w, idx % w
        if ys.min() == 0 or xs.min() == 0 or ys.max() == h - 1 or xs.max() == w - 1:
            continue
        holes[idx] = True
        n += 1
    return holes.reshape(h, w), n


def shape_mask(kind: str, mask: np.ndarray) -> np.ndarray:
    """Anti-aliased circle / stadium fitted to the silhouette (coin and pill touch on the sheet)."""
    h, w = mask.shape
    yy, xx = np.mgrid[0:h, 0:w].astype(np.float32) + 0.5
    if kind == "circle":
        rows = mask[: int(h * 0.85)]                 # above the pill: widest row = diameter
        ys, xs = np.nonzero(rows)
        left, right, top = xs.min(), xs.max() + 1, ys.min()
        r = (right - left) / 2.0
        cx, cy = (left + right) / 2.0, top + r
        return np.clip(r + 0.5 - np.sqrt((xx - cx) ** 2 + (yy - cy) ** 2), 0, 1)
    if kind == "stadium":
        cols = mask[:, int(w * 0.10): int(w * 0.20)]  # far-left part: no coin pixels there
        ys = np.nonzero(cols.any(1))[0]
        top, bottom = ys.min(), ys.max() + 1
        xs = np.nonzero(mask[top:bottom].any(0))[0]
        left, right = xs.min(), xs.max() + 1
        r = (bottom - top) / 2.0
        cy = (top + bottom) / 2.0
        cx = np.clip(xx, left + r, right - r)
        inside = np.clip(r + 0.5 - np.sqrt((xx - cx) ** 2 + (yy - cy) ** 2), 0, 1)
        return inside * (yy >= top - 0.5)
    return np.ones_like(xx)


# ---------------------------------------------------------------------------- one sprite
def find_source(pattern: str) -> Path:
    hits = sorted(SRC_DIR.glob(pattern))
    if not hits:
        sys.exit(f"source not found: reference-assets/{pattern}")
    return hits[0]


def process(spec: dict, cache: dict) -> tuple[Image.Image, dict]:
    src = find_source(spec["src"])
    if src not in cache:
        cache[src] = Image.open(src).convert("RGB")
    sheet = cache[src]
    W, H = sheet.size
    x0, y0, x1, y1 = spec["rect"]
    box = (round(x0 * W), round(y0 * H), round(x1 * W), round(y1 * H))
    crop = sheet.crop(box)
    w, h = crop.size

    bg = np.array(estimate_bg(sheet), dtype=np.float32) / 255.0   # whole-sheet border = pure magenta
    rgb = np.asarray(crop, dtype=np.float32) / 255.0
    fg = fgness_map(rgb, bg)

    mask = fg > 0.5
    mask, specks = drop_small_components(mask, MIN_SPECK)
    holes, n_holes = enclosed_small_holes(mask, MAX_HOLE)
    mask |= holes
    if spec.get("shape"):
        mask &= shape_mask(spec["shape"], mask) > 0.5

    # shared soft alpha edge (slice_sprites.soft_alpha), then 1px erode + feather
    fg_img = Image.fromarray((np.maximum(fg, holes) * 255).astype(np.uint8))
    mask_img = Image.fromarray((mask * 255).astype(np.uint8))
    alpha = soft_alpha(mask_img, fg_img, FG_EDGE_LO, FG_EDGE_HI)
    feathered = alpha.filter(ImageFilter.MinFilter(3)).filter(ImageFilter.GaussianBlur(0.8))
    alpha = ImageChops.darker(alpha, feathered)
    a = np.asarray(alpha, dtype=np.float32)
    if spec.get("shape"):
        a = a * shape_mask(spec["shape"], mask)

    # colour: un-blend the fringe from the magenta (slice_sprites.unblend), then despill the edge band
    px = rgb * 255.0
    partial = (a > 0.5) & (a < 254.5)
    if partial.any():
        px[partial] = np.clip(unblend(px[partial], a[partial][:, None], bg * 255.0), 0, 255)
    if n_holes:  # filled pinholes take the average colour of their opaque surroundings
        solid = Image.fromarray(((a >= 254.5) * 255).astype(np.uint8))
        weight = np.asarray(solid.filter(ImageFilter.GaussianBlur(4)), dtype=np.float32) + 1e-3
        for ch in range(3):
            prem = Image.fromarray(np.clip(px[..., ch] * (a >= 254.5), 0, 255).astype(np.uint8)).filter(ImageFilter.GaussianBlur(4))
            px[..., ch] = np.where(holes, np.asarray(prem, dtype=np.float32) / weight * 255.0, px[..., ch])
    band = np.asarray(Image.fromarray(((a < 250) * 255).astype(np.uint8)).filter(ImageFilter.MaxFilter(5)), dtype=np.float32) / 255.0
    px = despill(px, band)

    rgba = np.dstack([np.clip(px, 0, 255), a]).astype(np.uint8)
    rgba[a < 1] = 0
    img = Image.fromarray(rgba, "RGBA")
    bbox = img.getchannel("A").getbbox()
    bx0, by0, bx1, by1 = bbox
    bx0, by0, bx1, by1 = max(0, bx0 - PAD), max(0, by0 - PAD), min(w, bx1 + PAD), min(h, by1 + PAD)
    img = img.crop((bx0, by0, bx1, by1))
    if spec.get("max") and max(img.size) > spec["max"]:
        k = spec["max"] / max(img.size)
        size = (max(1, round(img.width * k)), max(1, round(img.height * k)))
        img = img.convert("RGBa").resize(size, Image.LANCZOS).convert("RGBA")

    meta = {
        "name": spec["name"],
        "source": src.name,
        "sourceRect": [box[0] + bx0, box[1] + by0, box[0] + bx1, box[1] + by1],
        "size": [img.width, img.height],
        "background": "#%02X%02X%02X" % tuple(int(round(c * 255)) for c in bg),
        "specksRemoved": specks,
        "holesFilled": n_holes,
        "magentaResidue": round(magenta_residue(np.asarray(img)), 5),
    }
    if spec.get("slice"):
        basis, t, r, b, l = spec["slice"]
        ref_h = img.height
        ref_w = img.width if basis == "wh" else img.height
        top, bottom = round(t * ref_h), round(b * ref_h)
        right, left = round(r * ref_w), round(l * ref_w)
        meta["slice"] = {"top": top, "right": right, "bottom": bottom, "left": left}  # CSS border-image order
        meta["unityBorder"] = [left, bottom, right, top]                             # Sprite.border (L, B, R, T)
    return img, meta


# ---------------------------------------------------------------------------- backups + main
def backup_superseded() -> list[str]:
    """Copy (never move) the HUD sprites the new art replaces; existing backups are never overwritten."""
    done = []
    targets = [(ROOT / "web-playable" / "assets" / "sprites" / n, BACKUP_DIR / n) for n in SUPERSEDED]
    targets += [(OUT_UNITY / n, BACKUP_DIR / "unity" / n) for n in SUPERSEDED]
    for src, dst in targets:
        if src.exists() and not dst.exists():
            dst.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(src, dst)
            done.append(str(dst.relative_to(ROOT)))
    return done


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--only", nargs="*", help="process only these sprite names")
    ap.add_argument("--no-preview", action="store_true", help="skip the ui-shots/ preview sheets")
    args = ap.parse_args()

    for line in backup_superseded():
        print(f"  backup  {line}")
    OUT_WEB.mkdir(parents=True, exist_ok=True)
    OUT_UNITY.mkdir(parents=True, exist_ok=True)

    cache: dict = {}
    manifest, images, worst = [], [], 0.0
    for spec in SPRITES:
        if args.only and spec["name"] not in args.only:
            continue
        img, meta = process(spec, cache)
        for d in (OUT_WEB, OUT_UNITY):
            img.save(d / f"{spec['name']}.png", optimize=True)
        manifest.append(meta)
        images.append((spec["name"], img))
        worst = max(worst, meta["magentaResidue"])
        print(f"  {spec['name']:<18} {img.width:>4}x{img.height:<4} bg={meta['background']} specks={meta['specksRemoved']:<3} "
              f"holes={meta['holesFilled']:<2} magenta-residue={meta['magentaResidue'] * 100:.3f}%")

    if not args.only:
        doc = {"generatedBy": "tools/process_ai_art.py", "sources": "reference-assets/", "sprites": manifest}
        for d in (OUT_WEB, OUT_UNITY):
            (d / "ui_sprites.json").write_text(json.dumps(doc, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    if images and not args.no_preview:
        SHOTS_DIR.mkdir(exist_ok=True)
        contact_sheet(images, SHOTS_DIR / "ai_art_preview_dark.png", bg=(31, 36, 48), checker=False, cell=320, cols=5)
        contact_sheet(images, SHOTS_DIR / "ai_art_preview_light.png", bg=(255, 244, 224), checker=False, cell=320, cols=5)
        print(f"  preview -> {(SHOTS_DIR / 'ai_art_preview_dark.png').relative_to(ROOT)}, ai_art_preview_light.png")
    print(f"Processed {len(images)} sprites -> {OUT_WEB.relative_to(ROOT)} and {OUT_UNITY.relative_to(ROOT)}  "
          f"(worst magenta residue {worst * 100:.3f}%)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
