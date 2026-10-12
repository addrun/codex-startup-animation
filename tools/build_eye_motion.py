"""Bake aligned eyelid keyframes into a motion atlas; not needed by the launcher."""
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
import cv2
import numpy as np

cv2.setNumThreads(2)
POSES = ["dragon-cinematic-closed.png", "dragon-eye-quarter.png", "dragon-eye-half.png", "dragon-eye-threequarter.png", "dragon-cinematic-open.png"]
BOXES = [(326, 606, 214, 132), (549, 548, 214, 132)]
CURVES = [
    ([345,360,380,400,420,440,460,480,500,520], [685,679,667,660,657,657,659,664,672,677], [685,687,697,703,704,700,693,682,673,677]),
    ([561,579,597,615,633,651,669,687,705,723], [642,629,616,607,601,596,594,593,595,598], [642,639,644,648,648,644,636,623,607,598]),
]


def read(name):
    image = cv2.imdecode(np.fromfile(ROOT / "assets" / name, dtype=np.uint8), cv2.IMREAD_UNCHANGED)
    if image is None or image.shape != (1536, 1024, 4):
        raise ValueError(f"Expected an aligned 1024x1536 RGBA pose: {name}")
    return image


def flow(a, b):
    solver = cv2.DISOpticalFlow_create(cv2.DISOPTICAL_FLOW_PRESET_MEDIUM)
    solver.setFinestScale(0)
    solver.setPatchSize(8)
    solver.setPatchStride(3)
    solver.setGradientDescentIterations(50)
    solver.setVariationalRefinementIterations(10)
    result = solver.calc(cv2.cvtColor(a, cv2.COLOR_BGRA2GRAY), cv2.cvtColor(b, cv2.COLOR_BGRA2GRAY), None)
    return cv2.GaussianBlur(result, (5, 5), 1.0)


def warp(image, displacement, amount, grid):
    coordinates = grid.copy()
    for _ in range(4):
        sampled = cv2.remap(displacement, coordinates[..., 0], coordinates[..., 1], cv2.INTER_LINEAR, borderMode=cv2.BORDER_REPLICATE)
        coordinates = grid - amount * sampled
    return cv2.remap(image, coordinates[..., 0], coordinates[..., 1], cv2.INTER_CUBIC, borderMode=cv2.BORDER_REPLICATE).astype(np.float32)


def lash_curve(patch, top, lower, amount, fit_start, fit_end):
    h, w = patch.shape[:2]
    rgb = patch[..., :3].astype(np.float32)
    gray = cv2.cvtColor(patch, cv2.COLOR_BGRA2GRAY).astype(np.float32)
    dark = np.clip(125-gray, 0, 100) * np.clip((rgb[..., 2]-rgb[..., 0]+35)/25, 0, 1)
    dark = cv2.GaussianBlur(dark, (3, 3), 0.7)
    result = np.zeros(w, np.float32)
    for x in range(w):
        low = max(0, int(top[x]-8))
        high = min(h, int(lower[x]+5))
        column = dark[low:high, x]
        if column.size and np.max(column) > 25:
            result[x] = low + np.argmax(column)
        else:
            result[x] = top[x] * amount + (lower[x]-10) * (1-amount)
    coordinates = np.arange(w, dtype=np.float32)
    chosen = (coordinates >= fit_start) & (coordinates <= fit_end)
    fitted = np.polyval(np.polyfit(coordinates[chosen]-w/2, result[chosen], 3), coordinates-w/2)
    return np.clip(fitted, top-5, lower+4).astype(np.float32)


def smooth(x):
    x = np.clip(x, 0, 1)
    return x*x*(3-2*x)


def main():
    poses = [read(name) for name in POSES]
    atlas = np.zeros((1452, 4708, 4), dtype=np.uint8)
    for eye, (x, y, w, h) in enumerate(BOXES):
        patches = [np.ascontiguousarray(p[y:y+h, x:x+w]) for p in poses]
        pairs = [(flow(a, b), flow(b, a)) for a, b in zip(patches, patches[1:])]
        gy, gx = np.mgrid[:h, :w].astype(np.float32)
        grid = np.stack((gx, gy), axis=-1)
        xs, upper_points, lower_points = CURVES[eye]
        reference_x = np.arange(w) + x
        full_top = np.interp(reference_x, xs, upper_points).astype(np.float32) - y
        full_bottom = np.interp(reference_x, xs, lower_points).astype(np.float32) - y
        curves = [lash_curve(p, full_top, full_bottom, amount, xs[2]-x, xs[-3]-x) for p, amount in zip(patches, [0,.4,.65,.85,1])]
        iris_source = patches[-1].astype(np.float32)
        for frame in range(121):
            interval = min(frame // 30, 3)
            t = (frame - interval * 30) / 30
            if t == 0:
                image = patches[interval]
            elif t == 1:
                image = patches[interval + 1]
            else:
                forward, backward = pairs[interval]
                a = warp(patches[interval], forward, t, grid)
                b = warp(patches[interval + 1], backward, 1-t, grid)
                image = np.clip(np.rint(a * (1-t) + b * t), 0, 255).astype(np.uint8)
            if frame not in (0, 120):
                lid = curves[interval]*(1-t) + curves[interval+1]*t
                bottom = (curves[0]+3)*(1-t) + full_bottom*t if interval == 0 else full_bottom
                reveal = smooth((gy-lid[None, :]-5)/2) * smooth((bottom[None, :]-gy-2)/2)
                reveal *= smooth((gy-full_top[None, :]-5)/2) * smooth((full_bottom[None, :]-gy-2)/2)
                reveal *= smooth((reference_x-xs[1])/8)[None, :] * smooth((xs[-2]-reference_x)/8)[None, :]
                image = np.clip(np.rint(image*(1-reveal[..., None]) + iris_source*reveal[..., None]), 0, 255).astype(np.uint8)
            ax = frame % 11 * 428 + eye * 214
            ay = frame // 11 * 132
            atlas[ay:ay+h, ax:ax+w] = image
    for frame, pose in [(0, poses[0]), (120, poses[4])]:
        for eye, (x, y, w, h) in enumerate(BOXES):
            ax = frame % 11 * 428 + eye * 214
            ay = frame // 11 * 132
            assert np.array_equal(atlas[ay:ay+h, ax:ax+w], pose[y:y+h, x:x+w]), "Keyframe changed"
    ok, encoded = cv2.imencode(".png", atlas)
    if not ok:
        raise RuntimeError("Could not encode the eyelid atlas")
    path = ROOT / "assets" / "dragon-eye-motion.png"
    encoded.tofile(path)
    print(f"PASS: 121 poses, stationary iris, exact closed/open endpoints. Atlas: {path}")


if __name__ == "__main__":
    main()
