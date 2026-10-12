"""Build mild detail textures offline; originals and their alpha remain intact.

Requires Pillow and NumPy in the interpreter used to run this script. The
launcher does not use Python. This improves local contrast, not source resolution.
"""
from __future__ import annotations

import hashlib
import json
from datetime import datetime, timezone
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw, ImageFilter, ImageFont

ROOT = Path(__file__).resolve().parents[1]
ASSETS = ROOT / "assets"
OUTPUT = ROOT / "output" / "portrait-detail"
RADIUS, PERCENT, THRESHOLD, MAX_CHANGE = 1.0, 75, 3, 12
PATCH_WIDTH, PATCH_HEIGHT, COLUMNS, FRAMES = 214, 132, 11, 121
EYE_BOXES = ((326, 606, 214, 132), (549, 548, 214, 132))


def sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest().upper()


def sharpen(image: Image.Image) -> Image.Image:
    original = np.array(image.convert("RGBA"))
    enhanced = np.array(image.convert("RGB").filter(
        ImageFilter.UnsharpMask(radius=RADIUS, percent=PERCENT, threshold=THRESHOLD)
    ))
    delta = np.clip(enhanced.astype(np.int16) - original[..., :3], -MAX_CHANGE, MAX_CHANGE)
    # Leave transparent edges untouched; taper sharpening through soft edges.
    strength = np.clip((original[..., 3].astype(np.float32) - 128.0) / 127.0, 0, 1)
    result = original.copy()
    result[..., :3] = np.clip(
        np.rint(original[..., :3] + delta * strength[..., None]), 0, 255
    ).astype(np.uint8)
    assert np.array_equal(result[..., 3], original[..., 3]), "Alpha changed"
    assert np.max(np.abs(result[..., :3].astype(np.int16) - original[..., :3])) <= MAX_CHANGE
    return Image.fromarray(result)


def texture_record(source: Path, result: Path, before_hash: str) -> dict:
    original = np.array(Image.open(source).convert("RGBA"))
    enhanced = np.array(Image.open(result).convert("RGBA"))
    assert sha256(source) == before_hash, "Original asset changed"
    assert original.shape == enhanced.shape, "Texture size changed"
    assert np.array_equal(original[..., 3], enhanced[..., 3]), "Alpha changed"
    delta = enhanced[..., :3].astype(np.int16) - original[..., :3]
    return {
        "source": str(source.relative_to(ROOT)), "output": str(result.relative_to(ROOT)),
        "sourceSha256": before_hash, "outputSha256": sha256(result),
        "width": original.shape[1], "height": original.shape[0],
        "alphaUnchanged": True, "originalUnchanged": True,
        "maximumChannelChange": int(np.max(np.abs(delta))),
        "meanAbsoluteChannelChange": float(np.mean(np.abs(delta))),
    }


def edge_contrast(image: Image.Image) -> float:
    values = np.array(image.convert("L"), dtype=np.float32)
    return float((np.abs(np.diff(values, axis=0)).mean() + np.abs(np.diff(values, axis=1)).mean()) / 2)


def main() -> None:
    OUTPUT.mkdir(parents=True, exist_ok=True)
    originals = [ASSETS / name for name in (
        "dragon-cinematic-open.png", "dragon-cinematic-closed.png", "dragon-eye-motion.png"
    )]
    source_hashes = {p: sha256(p) for p in originals}
    portrait_images = []
    records = []
    for source in originals[:2]:
        with Image.open(source) as opened:
            if opened.size != (1024, 1536):
                raise ValueError(f"Expected 1024x1536 portrait: {source}")
            detail = sharpen(opened)
        result = source.with_name(source.stem + "-detail.png")
        detail.save(result)
        portrait_images.append(detail)
        records.append(texture_record(source, result, source_hashes[source]))

    atlas_source = originals[2]
    with Image.open(atlas_source) as opened:
        atlas = opened.convert("RGBA")
    if atlas.size != (4708, 1452):
        raise ValueError("Expected 4708x1452 eyelid atlas")
    detail_atlas = Image.new("RGBA", atlas.size)
    for frame in range(FRAMES):
        for eye, (x, y, w, h) in enumerate(EYE_BOXES):
            ox = frame % COLUMNS * PATCH_WIDTH * 2 + eye * PATCH_WIDTH
            oy = frame // COLUMNS * PATCH_HEIGHT
            box = (ox, oy, ox + w, oy + h)
            patch = sharpen(atlas.crop(box))
            # Endpoints use the same full-image filter as the base portrait.
            if frame in (0, 120):
                portrait = portrait_images[1 if frame == 0 else 0]
                patch = portrait.crop((x, y, x + w, y + h))
            detail_atlas.paste(patch, (ox, oy))
    atlas_result = atlas_source.with_name(atlas_source.stem + "-detail.png")
    detail_atlas.save(atlas_result)
    records.append(texture_record(atlas_source, atlas_result, source_hashes[atlas_source]))
    for frame in (0, 120):
        for eye, (x, y, w, h) in enumerate(EYE_BOXES):
            ox = frame % COLUMNS * PATCH_WIDTH * 2 + eye * PATCH_WIDTH
            oy = frame // COLUMNS * PATCH_HEIGHT
            portrait = portrait_images[1 if frame == 0 else 0]
            assert np.array_equal(np.array(detail_atlas.crop((ox, oy, ox + w, oy + h))),
                                  np.array(portrait.crop((x, y, x + w, y + h))))

    crop_box = (250, 470, 785, 850)
    with Image.open(originals[0]) as opened:
        before_crop = opened.convert("RGBA").crop(crop_box)
    after_crop = portrait_images[0].crop(crop_box)
    before_contrast, after_contrast = edge_contrast(before_crop), edge_contrast(after_crop)
    before_zoom = before_crop.resize((1070, 760), Image.Resampling.LANCZOS)
    after_zoom = after_crop.resize(before_zoom.size, Image.Resampling.LANCZOS)
    comparison = Image.new("RGB", (2164, 810), (22, 31, 40))
    comparison.paste(before_zoom, (8, 42), before_zoom)
    comparison.paste(after_zoom, (1086, 42), after_zoom)
    try:
        font = ImageFont.truetype("C:/Windows/Fonts/consola.ttf", 22)
    except OSError:
        font = ImageFont.load_default()
    draw = ImageDraw.Draw(comparison)
    draw.text((16, 10), "Original / same 2x zoom", fill=(227, 235, 241), font=font)
    draw.text((1094, 10), "Mild detail / same 2x zoom", fill=(227, 235, 241), font=font)
    comparison.save(OUTPUT / "face-before-after.png")
    report = {
        "schemaVersion": 1, "completedUtc": datetime.now(timezone.utc).isoformat(),
        "passed": True, "method": "bounded unsharp mask; no rescale or generated detail",
        "settings": {"radius": RADIUS, "percent": PERCENT, "threshold": THRESHOLD,
                     "maximumChannelChange": MAX_CHANGE},
        "atlas": {"patchesProcessedIndividually": FRAMES * 2,
                  "openClosedEndpointsMatchPortrait": True},
        "textures": records,
        "faceCropContrast": {"before": before_contrast, "after": after_contrast,
                             "ratio": after_contrast / before_contrast},
        "comparison": str((OUTPUT / "face-before-after.png").relative_to(ROOT)),
        "scope": "Native waiting/fallback scene; existing encoded video remains unchanged",
    }
    (OUTPUT / "result.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
    print(f"PASS: {len(records)} detail textures; alpha and original files unchanged.")
    print(f"Source face-crop local contrast: {before_contrast:.3f} -> {after_contrast:.3f}.")
    print(f"Comparison: {OUTPUT / 'face-before-after.png'}")


if __name__ == "__main__":
    main()
