import json
import sys
from datetime import datetime
from pathlib import Path

import numpy as np
from PIL import Image

folder = Path(sys.argv[1])
metadata = json.loads((folder / "capture.json").read_text(encoding="utf-8-sig"))
rows = (folder / "output/startup-timing.log").read_text(encoding="utf-8-sig").splitlines()[1:]
events = [{"seconds": float(row.split("\t", 2)[0]), "event": row.split("\t", 2)[1], "detail": row.split("\t", 2)[2]} for row in rows]
epoch = next(item for item in events if item["event"] == "startup_started")
start_utc = datetime.fromisoformat(epoch["detail"].split("utc=", 1)[1].replace("Z", "+00:00")).timestamp() - epoch["seconds"]
fade_start = next(item["seconds"] for item in events if item["event"] == "ready_fade_started")
fade_end = next(item["seconds"] for item in events if item["event"] == "ready_fade_completed")
frames = metadata["frames"]
times = np.array([datetime.fromisoformat(frame["utc"].replace("Z", "+00:00")).timestamp() - start_utc for frame in frames])
images = [np.asarray(Image.open(frame["file"]).convert("RGB"), dtype=np.float64) / 255 for frame in frames]
before = np.flatnonzero(times < fade_start)
after = np.flatnonzero(times > fade_end + 0.08)
if not before.size or not after.size:
    raise RuntimeError("Fade burst lacks opaque or target-only reference samples")
opaque, target = images[before[-1]], images[after[-1]]
delta = opaque - target
denominator = float(np.sum(delta * delta))
if denominator < 100:
    raise RuntimeError("Source and target references have insufficient contrast")
alphas = np.array([float(np.sum((frame - target) * delta) / denominator) for frame in images])
inside = np.flatnonzero((times >= fade_start) & (times <= fade_end + 0.04))
middle = [int(index) for index in inside if 0.1 < alphas[index] < 0.9]
residuals = [float(np.mean(np.abs(images[index] - (target + np.clip(alphas[index], 0, 1) * delta)))) for index in inside]
black = [index for index, frame in enumerate(images) if np.mean(frame) < 0.01 and np.mean(np.max(frame, axis=2) < 0.04) > 0.98]
upward = max([alphas[second] - alphas[first] for first, second in zip(inside[:-1], inside[1:])] + [0])
checks = [
    {"name": "owned-screen-region-only", "passed": not metadata["error"]},
    {"name": "no-black-frame-in-sampled-transition", "passed": not black},
    {"name": "visible-intermediate-transparency", "passed": len(middle) >= 4},
    {"name": "fade-progress-is-forward", "passed": upward < 0.12},
    {"name": "source-dissolves-into-loaded-target", "passed": bool(residuals) and max(residuals) < 0.07 and np.mean(alphas[after]) < 0.08},
    {"name": "adequate-sampling", "passed": metadata["actualFps"] >= 25 and metadata["maximumGapMilliseconds"] < 100},
]
for check in checks:
    check["passed"] = bool(check["passed"])
report = {"passed": all(item["passed"] for item in checks), "checks": checks, "sampleCount": len(frames),
          "actualFps": metadata["actualFps"], "maximumGapMilliseconds": metadata["maximumGapMilliseconds"],
          "intermediateFrameCount": len(middle), "blackFrameIndices": black, "maximumAlphaIncrease": upward,
          "maximumMeanResidual": max(residuals), "fadeSeconds": fade_end - fade_start,
          "boundary": "Samples of owned synthetic page and overlay only; no official Codex screen captured.",
          "samples": [{"seconds": float(time), "estimatedOpacity": alpha, "file": frame["file"]} for time, alpha, frame in zip(times, alphas, frames)]}
(folder / "visual-results.json").write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
selected = [before[-1]] + [middle[min(len(middle) - 1, int(len(middle) * fraction))] for fraction in (0.2, 0.5, 0.8)] + [after[-1]] if middle else [before[-1], after[-1]]
contact = Image.new("RGB", (images[0].shape[1] * len(selected), images[0].shape[0]))
for index, selected_frame in enumerate(selected):
    contact.paste(Image.open(frames[selected_frame]["file"]).convert("RGB"), (index * images[0].shape[1], 0))
contact.save(folder / "fade-contact-sheet.png")
print(json.dumps({key: value for key, value in report.items() if key != "samples"}, indent=2))
sys.exit(0 if report["passed"] else 1)
