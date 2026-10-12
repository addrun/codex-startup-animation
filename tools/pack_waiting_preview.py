"""Package offscreen PNGs as a small preview GIF and a native-render comparison."""
from pathlib import Path
import argparse
import json

from PIL import Image, ImageDraw, ImageFont


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("folder", type=Path)
    parser.add_argument("--fps", type=int, default=15)
    args = parser.parse_args()
    files = sorted((args.folder / "frames").glob("waiting-*.png"))
    if not files:
        raise ValueError("No rendered waiting frames")
    frames = []
    for path in files:
        with Image.open(path) as opened:
            frames.append(opened.convert("RGB").resize((960, 540), Image.Resampling.LANCZOS))
    contact = Image.new("RGB", (1280, 720))
    for n in range(16):
        sample = frames[min(len(frames) - 1, n * len(frames) // 16)]
        contact.paste(sample.resize((320, 180)), ((n % 4) * 320, (n // 4) * 180))
    palette = contact.quantize(colors=256, method=Image.Quantize.MEDIANCUT)
    # One shared palette avoids palette-induced flicker in the frozen face.
    quantized = [frame.quantize(palette=palette, dither=Image.Dither.NONE) for frame in frames]
    # Alternate 60/70 ms frame durations; the whole preview retains real time.
    durations = [10 * (round((n + 1) * 100 / args.fps) - round(n * 100 / args.fps)) for n in range(len(frames))]
    quantized[0].save(args.folder / "waiting-hair-preview.gif", save_all=True,
                      append_images=quantized[1:], duration=durations, loop=0,
                      optimize=False, disposal=1)

    before = Image.open(args.folder / "waiting-original-2k.png").convert("RGB")
    after = Image.open(args.folder / "waiting-detail-2k.png").convert("RGB")
    # Identical unscaled native-render eye/hair crops: compare detail fairly.
    box = (1610, 370, 2560, 1000)
    before, after = before.crop(box), after.crop(box)
    comparison = Image.new("RGB", (before.width * 2 + 24, before.height + 48), (22, 31, 40))
    comparison.paste(before, (8, 40))
    comparison.paste(after, (before.width + 16, 40))
    try:
        font = ImageFont.truetype("C:/Windows/Fonts/consola.ttf", 20)
    except OSError:
        font = ImageFont.load_default()
    draw = ImageDraw.Draw(comparison)
    draw.text((16, 10), "Original / native QHD crop", font=font, fill=(230, 238, 243))
    draw.text((before.width + 24, 10), "Mild detail / native QHD crop", font=font, fill=(230, 238, 243))
    comparison.save(args.folder / "native-detail-comparison.png")
    (args.folder / "preview-manifest.json").write_text(json.dumps({
        "frames": len(frames), "seconds": len(frames) / args.fps,
        "sampleFps": args.fps, "previewSize": [960, 540],
        "gif": "waiting-hair-preview.gif", "comparison": "native-detail-comparison.png",
        "note": "GIF is an illustration; native preview runs without GIF palette compression.",
    }, indent=2), encoding="utf-8")
    print(args.folder / "waiting-hair-preview.gif")
    print(args.folder / "native-detail-comparison.png")


if __name__ == "__main__":
    main()
