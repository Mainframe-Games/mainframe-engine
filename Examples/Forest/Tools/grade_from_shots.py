#!/usr/bin/env python3
"""Fits the Forest's colour grade (forest-morning.cube, ADR 0175) to ungraded renders of the reference shots R1–R7.

The grade itself is ForestGrade.Grade (Forest/Src/World/ForestGrade.cs): white balance, a tone curve, split toning, a
foliage hue push and a saturation shape, applied to display values after the tonemap. This script measures how the
ungraded shots actually look and writes the numbers that make them land on the targets below into
Forest/Src/World/ForestGradeFit.cs (generated; do not edit). Then `--write-scenes` writes the .cube.

    # 1. Render the shots without the grade (display awake, LFS art):
    FOREST_SHOTS_OUT=/tmp/ungraded build/forest-screenshots.sh 300 --set Environment.PostProcess.AdjustmentEnabled=false
    # 2. Fit, then regenerate the LUT:
    python3 Examples/Forest/Tools/grade_from_shots.py /tmp/ungraded/*.png
    dotnet run --project Examples/Forest/Forest.Desktop -c Release -- --write-scenes Examples/Forest/Content/Scenes

The targets are measured from reference images (ADR 0178: Brogan's five Unreal forest screenshots, not in the repo) with the
same metrics; `--reference ref1.png ref2.png ... --` re-measures them and prints them (paste them below) before fitting.

Measured over all shots together (each weighted equally, the vignette's corners left out):
- the luminance percentiles p5, p50, p95: the curve maps them to TARGET_LOW / TARGET_MID / TARGET_HIGH (between the
  fixed black lift and white roll-off), so the set's shade stays readable and its highlights soft;
- the foliage (green-dominant pixels): its mean saturation scales to TARGET_FOLIAGE_SATURATION (a natural green, not a
  game's), its hue is pushed towards olive by the share TARGET_FOLIAGE_WARMTH;
- the shade (luminance below 0.25) and the light (above 0.6): their mean chroma is moved halfway to the targets (a
  faintly cool-green shade, a warm light).

Needs Pillow (and numpy).
"""

import os
import sys

import numpy as np
from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.normpath(os.path.join(HERE, "..", "Forest", "Src", "World", "ForestGradeFit.cs"))

# Targets (display values, 0–1), measured from the five Unreal forest references (ADR 0178; `--reference` prints them):
# open, lifted shade, soft highlights, olive and sage greens, a neutral-green shade and a warm light. ADR 0175's guesses
# were 0.045 / 0.255 / 0.82, foliage 0.42 and much cooler casts: the references never crush their blacks.
TARGET_LOW, TARGET_MID, TARGET_HIGH = 0.147, 0.364, 0.786
TARGET_FOLIAGE_SATURATION = 0.457
TARGET_FOLIAGE_WARMTH = 0.5
TARGET_SHADE_CHROMA = np.array([0.0037, 0.0067, -0.0769])   # rgb minus luminance
TARGET_LIGHT_CHROMA = np.array([0.0070, 0.0149, -0.1679])
BLACK_LIFT, WHITE_ROLL = 0.03, 0.975
LUMA = np.array([0.2126, 0.7152, 0.0722])


def load(path):
    img = np.asarray(Image.open(path).convert("RGB"), dtype=np.float64) / 255.0
    h, w, _ = img.shape
    # The middle 80 % (the vignette and grain-darkened corners would bias the shade).
    return img[int(h * 0.1):int(h * 0.9), int(w * 0.1):int(w * 0.9)].reshape(-1, 3)


def saturation(rgb):
    mx = rgb.max(axis=1)
    mn = rgb.min(axis=1)
    return np.where(mx > 1e-4, (mx - mn) / np.maximum(mx, 1e-4), 0.0)


def measure(paths):
    """The set's luminance percentiles, foliage saturation and shade / light chroma (each image weighted equally)."""
    images = [load(p) for p in paths]
    # Equal weight per image: subsample each to the same count.
    n = min(len(s) for s in images)
    rng = np.random.default_rng(7)
    pixels = np.concatenate([s[rng.choice(len(s), n, replace=False)] for s in images])
    lum = pixels @ LUMA
    p5, p50, p95 = np.percentile(lum, [5, 50, 95])
    green = (pixels[:, 1] > pixels[:, 0] * 1.05) & (pixels[:, 1] > pixels[:, 2] * 1.2) & (lum > 0.05)
    foliage_sat = float(saturation(pixels[green]).mean()) if green.any() else TARGET_FOLIAGE_SATURATION
    shade = lum < 0.25
    light = lum > 0.6
    shade_chroma = (pixels[shade] - lum[shade, None]).mean(axis=0) if shade.any() else TARGET_SHADE_CHROMA
    light_chroma = (pixels[light] - lum[light, None]).mean(axis=0) if light.any() else TARGET_LIGHT_CHROMA
    return float(p5), float(p50), float(p95), foliage_sat, float(green.mean()), shade_chroma, light_chroma


def main():
    global TARGET_LOW, TARGET_MID, TARGET_HIGH, TARGET_FOLIAGE_SATURATION, TARGET_SHADE_CHROMA, TARGET_LIGHT_CHROMA
    args = sys.argv[1:]
    if args[:1] == ["--reference"]:
        end = args.index("--")
        r5, r50, r95, rsat, _, rshade, rlight = measure(args[1:end])
        TARGET_LOW, TARGET_MID, TARGET_HIGH, TARGET_FOLIAGE_SATURATION = r5, r50, r95, rsat
        TARGET_SHADE_CHROMA, TARGET_LIGHT_CHROMA = rshade, rlight
        print(f"references: TARGET_LOW, TARGET_MID, TARGET_HIGH = {r5:.3f}, {r50:.3f}, {r95:.3f}; "
              f"TARGET_FOLIAGE_SATURATION = {rsat:.3f}; shade chroma {np.round(rshade, 4).tolist()}, "
              f"light chroma {np.round(rlight, 4).tolist()}")
        args = args[end + 1:]
    paths = args
    if not paths:
        sys.exit(__doc__)
    p5, p50, p95, foliage_sat, green_share, shade_chroma, light_chroma = measure(paths)

    foliage_scale = float(np.clip(TARGET_FOLIAGE_SATURATION / max(foliage_sat, 1e-3), 0.6, 1.1))
    shade_tint = np.clip((TARGET_SHADE_CHROMA - shade_chroma) * 0.5, -0.03, 0.03)
    light_tint = np.clip((TARGET_LIGHT_CHROMA - light_chroma) * 0.5, -0.04, 0.04)

    # Curve control points: fixed lift and roll-off, the measured percentiles moved to the targets (kept monotone).
    xs = [0.0, float(p5), float(p50), float(p95), 1.0]
    ys = [BLACK_LIFT, TARGET_LOW, TARGET_MID, TARGET_HIGH, WHITE_ROLL]
    for i in range(1, len(xs)):
        xs[i] = max(xs[i], xs[i - 1] + 0.02)
        ys[i] = max(ys[i], ys[i - 1] + 0.005)

    def f(v):
        return f"{v:.4f}f"

    report = (f"p5 {p5:.3f}, p50 {p50:.3f}, p95 {p95:.3f}; foliage saturation {foliage_sat:.3f} ({green_share * 100:.0f} % of pixels); "
              f"shade chroma {np.round(shade_chroma, 4).tolist()}, light chroma {np.round(light_chroma, 4).tolist()}")
    code = f"""// <auto-generated>
// Fitted by Examples/Forest/Tools/grade_from_shots.py from ungraded renders of the reference shots (ADR 0175); do not edit.
// Measured: {report}.
// </auto-generated>
namespace Forest;

/// <summary>The forest-morning grade's numbers fitted to the R1–R7 renders (<see cref="ForestGrade"/> applies them).</summary>
internal static class ForestGradeFit
{{
    /// <summary>The tone curve's control points (display in → out): the shots' p5, p50 and p95 luminance to the targets.</summary>
    public static readonly float[] CurveX = [{", ".join(f(x) for x in xs)}];

    public static readonly float[] CurveY = [{", ".join(f(y) for y in ys)}];

    /// <summary>Scales the foliage's saturation towards a natural green.</summary>
    public const float FoliageSaturation = {f(foliage_scale)};

    /// <summary>How far the foliage's hue moves towards olive (0–1).</summary>
    public const float FoliageWarmth = {f(TARGET_FOLIAGE_WARMTH)};

    /// <summary>Added to the shade (rgb), moving its cast halfway to a faintly cool green.</summary>
    public static readonly System.Numerics.Vector3 ShadeTint = new({f(shade_tint[0])}, {f(shade_tint[1])}, {f(shade_tint[2])});

    /// <summary>Added to the light (rgb), moving its cast halfway to a warm low sun.</summary>
    public static readonly System.Numerics.Vector3 LightTint = new({f(light_tint[0])}, {f(light_tint[1])}, {f(light_tint[2])});
}}
"""
    with open(OUT, "w", newline="\n") as fh:
        fh.write(code)
    print(report)
    print(f"wrote {OUT}")


if __name__ == "__main__":
    main()
