"""Check owned on-screen tail samples for visible sway, fixed anchors and seams.

Uses Pillow and NumPy only in development. Captures come from
run-tail-screen.ps1; it never captures another application's content.
"""
import argparse
import json
from datetime import datetime, timezone
from pathlib import Path

import numpy as np
from PIL import Image


def read_image(path):
    with Image.open(path) as image:
        return np.asarray(image.convert("RGB"), dtype=np.int16)


def viewport(image, inset=0):
    height, width = image.shape[:2]
    full_width, full_height = width + 2 * inset, height + 2 * inset
    scale = min(full_width / 1280, full_height / 720)
    return scale, (full_width - 1280 * scale) / 2 - inset, (full_height - 720 * scale) / 2 - inset


def crop(image, rectangle, inset=0):
    scale, offset_x, offset_y = viewport(image, inset)
    x, y, width, height = rectangle
    x0 = max(0, round(x * scale + offset_x))
    x1 = min(image.shape[1], round((x + width) * scale + offset_x))
    y0 = max(0, round(y * scale + offset_y))
    y1 = min(image.shape[0], round((y + height) * scale + offset_y))
    if x0 >= x1 or y0 >= y1:
        raise ValueError("Empty region in captured viewport")
    return image[y0:y1, x0:x1]


def different_pixels(first, second, tolerance=0):
    return int(np.count_nonzero(np.max(np.abs(first - second), axis=2) > tolerance))


def edge_position(image, design_y, minimum_x, inset):
    scale, offset_x, offset_y = viewport(image, inset)
    y = round(design_y * scale + offset_y)
    x0 = round(minimum_x * scale + offset_x)
    x1 = round(975 * scale + offset_x)
    # Bright hair against the dark background. Average five rows to keep
    # single-pixel scan lines from changing the measured silhouette.
    values = image[y - 2:y + 3, x0:x1].mean(axis=(0, 2))
    points = np.flatnonzero(values > 75)
    if not len(points):
        raise ValueError("Outer hair silhouette was not found")
    return float((points[0] + x0 - offset_x) / scale)


def seam_score(image, inset=0):
    scale, offset_x, offset_y = viewport(image, inset)
    y0, y1 = round(170 * scale + offset_y), round(640 * scale + offset_y)
    brightness = image[y0:y1].mean(axis=2)
    neighbor = max(1, round(3 * scale))
    results = []
    # This is the reported straight vertical defect, not a general image
    # quality score. The scan straddles its location at multiple resolutions.
    for x in range(round(929 * scale + offset_x), round(933 * scale + offset_x) + 1):
        residual = (brightness[:, x - neighbor] + brightness[:, x + neighbor]) / 2 - brightness[:, x]
        results.append({"column": x, "meanDarkResidual": float(residual.mean()),
                        "darkRowFraction": float(np.mean(residual > 5))})
    worst = max(results, key=lambda value: value["meanDarkResidual"])
    return {"passed": worst["meanDarkResidual"] < 5 and worst["darkRowFraction"] < 0.45,
            "width": image.shape[1], "worst": worst, "columns": results}


def verify_screen(scenario, inset):
    records = scenario["captured"]
    images = [read_image(Path(record["path"])) for record in records]
    if len(images) < 4 or any(image.shape != images[0].shape for image in images):
        raise ValueError("Need at least four equal-size on-screen samples")
    hair_regions = {"upperHair": (515, 162, 410, 245), "lowerHair": (510, 623, 442, 32)}
    fixed_regions = {"innerHair": (1000, 140, 95, 140), "ear": (665, 424, 305, 180),
                     "horn": (615, 0, 175, 148), "cheek": (1150, 505, 115, 130),
                     "header": (25, 18, 520, 154)}
    motion = {}
    for name, rectangle in hair_regions.items():
        samples = [crop(image, rectangle, inset) for image in images]
        changes = [different_pixels(first, second, 2) for first, second in zip(samples, samples[1:])]
        minimum = max(100, samples[0].shape[0] * samples[0].shape[1] * 0.002)
        motion[name] = {"passed": all(change > minimum for change in changes), "pairChangedPixels": changes}
    anchors = {}
    for name, rectangle in fixed_regions.items():
        samples = [crop(image, rectangle, inset) for image in images]
        changes = [different_pixels(samples[0], sample) for sample in samples[1:]]
        anchors[name] = {"passed": not any(changes), "changedPixelsFromFirst": changes}
    excursions = {}
    scale, _, _ = viewport(images[0], inset)
    for name, y, minimum_x, required in [("upperHair", 360, 565, 8), ("lowerHair", 640, 480, 3)]:
        positions = [edge_position(image, y, minimum_x, inset) for image in images]
        distance = max(positions) - min(positions)
        excursions[name] = {"passed": distance >= required, "positionsDesignPixels": positions,
                            "excursionDesignPixels": distance, "excursionScreenPixels": distance * scale}
    seams = [seam_score(image, inset) for image in images]
    passed = all(record["windowOwnedVisible"] for record in records) and scenario["activationRequests"] == 0 and \
        scenario["filmTailConfirmed"] and all(value["passed"] for value in motion.values()) and \
        all(value["passed"] for value in anchors.values()) and all(value["passed"] for value in excursions.values()) and \
        all(value["passed"] for value in seams)
    return {"mode": scenario["mode"], "passed": passed, "visibleMotion": motion, "fixedAnchors": anchors,
            "silhouetteExcursions": excursions, "seams": seams, "activationRequests": scenario["activationRequests"]}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("capture_folder", type=Path)
    parser.add_argument("--art-folder", type=Path, required=True)
    parser.add_argument("--baseline", type=Path, help="Optional old 1280x720 frame for comparison")
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[1]
    capture = json.loads((args.capture_folder / "capture.json").read_text(encoding="utf-8-sig"))
    scenarios = [verify_screen(scenario, capture.get("screenInsetPixels", 16)) for scenario in capture["scenarios"]]
    stills = []
    for path in [root / "output/tail-test/waiting-open.png", args.art_folder / "waiting-detail-2k.png"]:
        stills.append({"path": str(path.resolve()), **seam_score(read_image(path))})
    baseline = {"path": str(args.baseline.resolve()), **seam_score(read_image(args.baseline))} if args.baseline else None
    report = {"completedUtc": datetime.now(timezone.utc).isoformat(), "passed": all(s["passed"] for s in scenarios + stills),
              "launcherSha256": capture["launcherSha256"], "screenCapture": str((args.capture_folder / "capture.json").resolve()),
              "scenarios": scenarios, "nativeStillSeams": stills, "oldReferenceSeam": baseline,
              "limits": "Owned animation screen evidence; does not prove a real official-icon cold start or final GPU frame rate."}
    output = args.capture_folder / "visual-results.json"
    output.write_text(json.dumps(report, indent=2), encoding="utf-8")
    for scenario in scenarios:
        excursions = scenario["silhouetteExcursions"]
        fixed_changes = sum(sum(value["changedPixelsFromFirst"]) for value in scenario["fixedAnchors"].values())
        print(f"{'PASS' if scenario['passed'] else 'FAIL'}: {scenario['mode']} screen; "
              f"upper/lower silhouette excursion={excursions['upperHair']['excursionScreenPixels']:.1f}/"
              f"{excursions['lowerHair']['excursionScreenPixels']:.1f} screen pixels; fixed anchor differences={fixed_changes}")
    for still in stills:
        print(f"{'PASS' if still['passed'] else 'FAIL'}: {still['width']}px seam residual={still['worst']['meanDarkResidual']:.3f}")
    if baseline:
        print(f"Old reference: seam residual={baseline['worst']['meanDarkResidual']:.3f}; seam check passed={baseline['passed']}")
    print(output.resolve())
    return 0 if report["passed"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
