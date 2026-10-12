"""Analyze an owned-window burst around the MP4 -> waiting-scene handoff.

The black-frame check uses actual screen pixels. Timeline checks separately
prove that the scene was ready before the handoff, rather than inferring that
from a smooth GIF. No packages are installed by this script.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import sys
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw, ImageFont


def read_capture(folder: Path):
    capture = json.loads((folder / "capture.json").read_text(encoding="utf-8-sig"))
    samples = []
    arrays = []
    for item in capture["frames"]:
        with Image.open(item["path"]) as opened:
            pixels = np.asarray(opened.convert("RGB"))
        arrays.append(pixels)
        luma = pixels @ np.array([0.2126, 0.7152, 0.0722], dtype=np.float32)
        samples.append({
            "index": item["index"], "startupSeconds": item["startupElapsedSeconds"],
            "utc": item["utc"], "ownershipVerified": item["ownershipVerified"],
            "meanLuma": float(luma.mean()), "p95Luma": float(np.percentile(luma, 95)),
            "brightFraction": float((luma > 45).mean()),
            "pixelSha256": hashlib.sha256(pixels.tobytes()).hexdigest(),
            "path": item["path"],
        })
    return capture, samples, arrays


def event_map(capture):
    result = {}
    for event in capture["events"]:
        result.setdefault(event["event"], event)
    return result


def annotate_frame(pixels, text):
    image = Image.fromarray(pixels)
    annotated = Image.new("RGB", (image.width, image.height + 32), (14, 22, 29))
    annotated.paste(image, (0, 32))
    try:
        font = ImageFont.truetype("C:/Windows/Fonts/consola.ttf", 13)
    except OSError:
        font = ImageFont.load_default()
    ImageDraw.Draw(annotated).text((8, 8), text, font=font, fill=(225, 240, 244))
    return annotated


def save_gif(images, times, path):
    if not images:
        return
    # Use one palette for every frame so encoding cannot introduce false
    # flicker in the fixed face or hide a one-frame black handoff.
    stride = max(1, len(images) // 16)
    contact = Image.new("RGB", (images[0].width * 4, images[0].height * 4))
    for index, sample in enumerate(images[::stride][:16]):
        contact.paste(sample, ((index % 4) * sample.width, (index // 4) * sample.height))
    palette = contact.quantize(colors=256, method=Image.Quantize.MEDIANCUT)
    quantized = [frame.quantize(palette=palette, dither=Image.Dither.NONE) for frame in images]
    origin = times[0]
    ticks = [round((point - origin) * 100) for point in times]
    durations = [max(10, (ticks[index + 1] - ticks[index]) * 10)
                 for index in range(len(times) - 1)]
    durations.append(durations[-1] if durations else 20)
    quantized[0].save(path, save_all=True, append_images=quantized[1:],
                      duration=durations, loop=0, disposal=1, optimize=False)


def longest_duplicate_run(samples, start, end):
    longest = 0.0
    longest_indices = []
    run_first = None
    previous = None
    for sample in samples:
        if not start <= sample["startupSeconds"] <= end:
            continue
        if previous and sample["pixelSha256"] == previous["pixelSha256"]:
            if run_first is None:
                run_first = previous
            duration = sample["startupSeconds"] - run_first["startupSeconds"]
            if duration > longest:
                longest = duration
                longest_indices = [run_first["index"], sample["index"]]
        else:
            run_first = None
        previous = sample
    return longest, longest_indices


def landmark(events):
    event = events.get("tail_revealed") or events.get("animation_tail")
    return event["seconds"] if event else None


def write_comparison(folder, baseline_folder, capture, samples, arrays):
    baseline, old_samples, old_arrays = read_capture(baseline_folder)
    new_epoch = landmark(event_map(capture))
    old_epoch = landmark(event_map(baseline))
    if new_epoch is None or old_epoch is None or not old_arrays or not arrays:
        return {"created": False, "reason": "Missing transition landmark or sampled frames"}
    offsets = np.arange(-0.85, 0.801, 1 / 30)
    frames = []
    width = max(arrays[0].shape[1], old_arrays[0].shape[1])
    height = max(arrays[0].shape[0], old_arrays[0].shape[0])
    for offset in offsets:
        old_index = min(range(len(old_samples)), key=lambda i: abs(old_samples[i]["startupSeconds"] - old_epoch - offset))
        new_index = min(range(len(samples)), key=lambda i: abs(samples[i]["startupSeconds"] - new_epoch - offset))
        old_image = annotate_frame(old_arrays[old_index], f"Before  {offset:+.2f} s / {old_samples[old_index]['index']}")
        new_image = annotate_frame(arrays[new_index], f"After   {offset:+.2f} s / {samples[new_index]['index']}")
        frame = Image.new("RGB", (width * 2 + 12, height + 32), (14, 22, 29))
        frame.paste(old_image, (0, 0)); frame.paste(new_image, (width + 12, 0))
        frames.append(frame)
    path = folder / "transition-before-after.gif"
    save_gif(frames, list(offsets), path)
    return {
        "created": True, "path": str(path), "baseline": str(baseline_folder),
        "baselineLauncherSha256": baseline["launcherSha256"],
        "newLauncherSha256": capture["launcherSha256"],
        "alignment": "tail_revealed log, or legacy animation_tail log; the actual present can lag that log by a screen frame",
        "seconds": [float(offsets[0]), float(offsets[-1])], "fps": 30,
    }


def analyze(folder, baseline_folder=None):
    capture, samples, arrays = read_capture(folder)
    events = event_map(capture)
    checks = []

    def check(name, passed, detail):
        checks.append({"name": name, "passed": bool(passed), "detail": detail})

    check("owned-window-only", samples and not capture["failure"] and
          all(sample["ownershipVerified"] for sample in samples),
          {"frames": len(samples), "captureFailure": capture["failure"]})
    activation_count = sum(item["event"] == "activation_requested" for item in capture["events"])
    check("passive-no-activation", activation_count == 0 and "observation_started" in events,
          {"activationRequests": activation_count})
    fallback = [item for item in capture["events"] if "tail_fallback" in item["event"]]
    check("real-video-to-tail", "animation_tail" in events and not fallback and "media_failed" not in events,
          {"tailConfirmed": "animation_tail" in events, "fallback": fallback})

    times = [sample["startupSeconds"] for sample in samples]
    gaps = np.diff(times) if len(times) > 1 else np.array([])
    max_gap = float(gaps.max()) if len(gaps) else None
    measured_fps = ((len(times) - 1) / (times[-1] - times[0])) if len(times) > 1 and times[-1] > times[0] else 0
    check("sampling-covers-short-blackout", len(samples) >= 60 and max_gap is not None and max_gap <= 0.067,
          {"requestedFps": capture["requestedFps"], "measuredFps": measured_fps,
           "maxGapSeconds": max_gap, "limitSeconds": 0.067})

    epoch = landmark(events)
    handoff = events.get("tail_handoff_started")
    handoff_start = handoff["seconds"] if handoff else (epoch - 1.2 if epoch else 0)
    if epoch is not None and times:
        check("before-and-after-covered", times[0] <= handoff_start - 0.15 and times[-1] >= epoch + 0.8,
              {"samplesStart": times[0], "samplesEnd": times[-1], "handoffStarted": handoff_start,
               "tailLogLandmark": epoch, "requiredPostTailSeconds": 0.8})
    else:
        check("before-and-after-covered", False, "No handoff landmark or samples")

    # This ROI is bright throughout the final MP4 segment and waiting scene.
    # Check both absolute near-black pixels and a dramatic relative darkening.
    reference_mean = float(np.percentile([sample["meanLuma"] for sample in samples], 75)) if samples else 0
    reference_p95 = float(np.percentile([sample["p95Luma"] for sample in samples], 75)) if samples else 0
    black_frames = []
    for sample in samples:
        absolute = sample["meanLuma"] < 12 and sample["p95Luma"] < 22 and sample["brightFraction"] < 0.02
        relative = sample["meanLuma"] < reference_mean * 0.18 and sample["p95Luma"] < max(22, reference_p95 * 0.25)
        sample["blackCandidate"] = bool(absolute or relative)
        if sample["blackCandidate"]:
            black_frames.append({key: sample[key] for key in ("index", "startupSeconds", "meanLuma", "p95Luma", "path")})
    check("character-roi-no-black-frames", bool(samples) and not black_frames,
          {"blackFrames": black_frames, "referenceMeanLuma": reference_mean, "referenceP95Luma": reference_p95})

    repeat_seconds, repeat_indices = longest_duplicate_run(samples, handoff_start - 0.3, (epoch or 0) + 0.5)
    check("handoff-no-long-frozen-frame", epoch is not None and repeat_seconds <= 0.25,
          {"longestExactDuplicateSeconds": repeat_seconds, "frameIndices": repeat_indices,
           "limitSeconds": 0.25, "note": "Identical actual screen pixels; use the GIF to assess smaller visual pauses."})

    legacy = bool(capture["legacyBaseline"])
    timeline = {}
    for name in ("tail_prepare_started", "tail_prepared", "tail_layout_ready", "tail_handoff_started", "tail_revealed"):
        event = events.get(name)
        timeline[name] = event
    if not legacy:
        check("prepared-tail-timeline-present", all(timeline.values()), timeline)
        prepared = timeline["tail_prepared"]
        layout = timeline["tail_layout_ready"]
        revealed = timeline["tail_revealed"]
        check("prepared-before-handoff", prepared and handoff and prepared["seconds"] < handoff["seconds"],
              {"prepared": prepared, "handoff": handoff})
        check("layout-before-handoff", layout and handoff and layout["seconds"] <= handoff["seconds"],
              {"layout": layout, "handoff": handoff})
        latency = (revealed["seconds"] - handoff["seconds"]) if revealed and handoff else None
        check("first-frame-handoff-under-200ms", latency is not None and 0 <= latency <= 0.2,
              {"seconds": latency, "limitSeconds": 0.2,
               "note": "Logged reveal timing complements the actual-screen samples; neither alone proves full-page readiness."})

    if arrays:
        selected = [i for i, sample in enumerate(samples) if epoch is None or handoff_start - 0.3 <= sample["startupSeconds"] <= epoch + 0.8]
        if not selected:
            selected = list(range(len(samples)))
        images = [annotate_frame(arrays[i], f"screen {samples[i]['startupSeconds']:.3f} s / frame {i}") for i in selected]
        save_gif(images, [times[i] for i in selected], folder / "transition-screen.gif")
        # Contact sheet retains individual PNGs and timestamps for a brief
        # black frame that a looping browser preview could make hard to see.
        near = [i for i, sample in enumerate(samples) if epoch is not None and abs(sample["startupSeconds"] - epoch) <= 0.16]
        if not near:
            near = selected[:12]
        near = near[:12]
        sheet = Image.new("RGB", (arrays[0].shape[1] * 4, (arrays[0].shape[0] + 32) * 3), (14, 22, 29))
        for number, index in enumerate(near):
            sheet.paste(annotate_frame(arrays[index], f"{times[index]:.3f} s / frame {index}"),
                        ((number % 4) * arrays[index].shape[1], (number // 4) * (arrays[index].shape[0] + 32)))
        sheet.save(folder / "handoff-contact-sheet.png")

    comparison = write_comparison(folder, baseline_folder, capture, samples, arrays) if baseline_folder else None
    report = {
        "variant": "legacy-baseline" if legacy else "prepared-tail",
        "passed": all(item["passed"] for item in checks), "checks": checks,
        "launcherSha256": capture["launcherSha256"], "frames": len(samples),
        "measuredFps": measured_fps, "maxGapSeconds": max_gap,
        "timeline": timeline, "samples": samples,
        "artifacts": {"gif": str(folder / "transition-screen.gif"),
                      "contactSheet": str(folder / "handoff-contact-sheet.png"), "comparison": comparison},
        "limits": "Only the owned character ROI at the recorded cadence was checked. This does not prove absence of a sub-sample blackout, audio quality, or real Codex page readiness.",
    }
    (folder / "analysis.json").write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    for item in checks:
        print(f"{'PASS' if item['passed'] else 'FAIL'}: {item['name']}")
    print(f"{report['variant']}: {len(samples)} screen frames, {measured_fps:.1f} Hz, max gap {max_gap}")
    print(folder / "analysis.json")
    return report


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("folder", type=Path)
    parser.add_argument("--baseline", type=Path)
    args = parser.parse_args()
    report = analyze(args.folder.resolve(), args.baseline.resolve() if args.baseline else None)
    sys.exit(0 if report["passed"] or report["variant"] == "legacy-baseline" else 1)


if __name__ == "__main__":
    main()
