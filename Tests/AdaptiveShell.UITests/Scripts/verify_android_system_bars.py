#!/usr/bin/env python3
"""Verify visible status-bar foreground from actual Android theme PNG captures.

Usage: python3 verify_android_system_bars.py ARTIFACT_ROOT --strict
Consumes exactly three android-system-bars-{stage}.json captures. System-bar
frames come from mobile:getSystemBars, which parses native dumpsys mFrame/Frame
coordinates unchanged; PNG pixels are compared directly, without density scaling.

Primary contract:
https://github.com/appium/appium-uiautomator2-driver#mobile-getsystembars
https://github.com/appium/appium-android-driver/blob/v14.0.8/lib/commands/system-bars.ts#L106-L124

The foreground test is layout-dependent presence evidence, not identification of
individual system icons. API 26/27 keep the stock clock on the right; API >=28
also requires contrasting foreground in the left clock area. Regions are derived
only from the captured native bar rectangle. No status-bar height is assumed.
"""

import argparse
from collections import Counter
import json
import math
from pathlib import Path
import re
import struct
import sys
import zlib

from verify_mac_button_colors import PNG


STAGES = ("light", "dark", "light-restored")
SEQUENCES = {"light": 30, "dark": 31, "light-restored": 32}
MINIMUM_CONTRAST = 3.0
MINIMUM_FOREGROUND_FRACTION = 0.001


def integer(value, name):
    if (type(value) not in (int, float) or not math.isfinite(value)
            or value != int(value)):
        raise ValueError(f"{name} must be a finite native integer coordinate")
    return int(value)


def native_status_bar(system_bars):
    if not isinstance(system_bars, dict):
        raise ValueError("mobile:getSystemBars must return an object")
    bar = system_bars["statusBar"]
    if not isinstance(bar, dict) or bar.get("visible") is not True:
        raise ValueError("Native status bar must be present and visible")
    bounds = {key: integer(bar[key], f"statusBar.{key}")
              for key in ("x", "y", "width", "height")}
    if (bounds["x"] < 0 or bounds["y"] < 0
            or bounds["width"] <= 0 or bounds["height"] <= 0):
        raise ValueError("Native status bar has invalid or empty bounds")
    return bounds


def luminance(rgb):
    channels = [channel / 255 for channel in rgb]
    linear = [value / 12.92 if value <= 0.04045 else ((value + 0.055) / 1.055) ** 2.4
              for value in channels]
    return sum(weight * value for weight, value in zip((0.2126, 0.7152, 0.0722), linear))


def contrast(first, second):
    a, b = luminance(first), luminance(second)
    return (max(a, b) + 0.05) / (min(a, b) + 0.05)


def foreground_presence(png, rectangle):
    left, top, right, bottom = rectangle
    if not (0 <= left < right <= png.width and 0 <= top < bottom <= png.height):
        raise ValueError("Foreground region is outside the original PNG")
    colors = Counter(png.pixel(x, y) for y in range(top, bottom) for x in range(left, right))
    pixel_count = (right - left) * (bottom - top)
    background, background_count = colors.most_common(1)[0]
    background_fraction = background_count / pixel_count
    if background_fraction < 0.60:
        raise ValueError("Status-bar background is not sufficiently uniform to establish contrast")
    foreground_colors = {rgb for rgb in colors if contrast(rgb, background) >= MINIMUM_CONTRAST}
    foreground_count = sum(colors[rgb] for rgb in foreground_colors)
    rows, columns = set(), set()
    for y in range(top, bottom):
        for x in range(left, right):
            if png.pixel(x, y) in foreground_colors:
                rows.add(y)
                columns.add(x)
    minimum_pixels = max(8, math.ceil(pixel_count * MINIMUM_FOREGROUND_FRACTION))
    # Require extent across rows/columns; a border or a few isolated pixels must
    # not stand in for a visible clock/status glyph.
    minimum_rows = max(3, math.ceil((bottom - top) * 0.10))
    matched = (foreground_count >= minimum_pixels
               and len(rows) >= minimum_rows and len(columns) >= 3)
    return {
        "status": "match" if matched else "mismatch",
        "rectangle_pixels": list(rectangle),
        "background_rgb": list(background),
        "background_fraction": background_fraction,
        "pixel_count": pixel_count,
        "foreground_pixels": foreground_count,
        "foreground_fraction": foreground_count / pixel_count,
        "minimum_contrast_ratio": MINIMUM_CONTRAST,
        "minimum_foreground_pixels": minimum_pixels,
        "foreground_rows": len(rows),
        "foreground_columns": len(columns),
        "minimum_foreground_rows": minimum_rows,
    }


def analyze_status_bar(png, bar, api_level):
    """Pixel-only analysis; caller must establish the native rectangle's provenance."""
    x, y, width, height = (bar[key] for key in ("x", "y", "width", "height"))
    if x != 0 or y != 0 or width != png.width or height >= png.height:
        raise ValueError("Native top status-bar frame does not match the full-resolution screenshot")
    # Insets and partitions are fractions of the captured native frame, never
    # device-density conversions or a fabricated status-bar height. Excluding
    # the top/bottom edge prevents a page separator from posing as a glyph.
    inset = math.ceil(height * 0.10)
    top, bottom = y + inset, y + height - inset
    checks = {
        "native_bar_foreground": foreground_presence(png, (x, top, x + width, bottom)),
        "right_status_foreground": foreground_presence(
            png, (x + math.floor(width * 0.65), top, x + width, bottom)),
    }
    if api_level >= 28:
        checks["left_clock_area_foreground"] = foreground_presence(
            png, (x, top, x + math.ceil(width * 0.35), bottom))
    return checks


def analyze(metadata_path, stage):
    metadata = json.loads(metadata_path.read_text(encoding="utf-8-sig"))
    if (type(metadata.get("schemaVersion")) is not int or metadata["schemaVersion"] != 1
            or metadata.get("stage") != stage
            or metadata.get("coordinateSource") != "mobile:getSystemBars"):
        raise ValueError("Metadata must describe this stage's original mobile:getSystemBars capture")
    before = native_status_bar(metadata["systemBarsBefore"])
    after = native_status_bar(metadata["systemBarsAfter"])
    if before != after:
        raise ValueError("Native status-bar frame changed across screenshot capture")
    api_value = metadata["deviceInfo"]["apiVersion"]
    if not re.fullmatch(r"[0-9]+", str(api_value)) or int(api_value) < 26:
        raise ValueError("Native mobile:deviceInfo apiVersion is missing or unsupported")
    api_level = int(api_value)
    relative = Path(metadata["png"])
    if (relative.is_absolute() or relative.name != f"{SEQUENCES[stage]:02}-theme-{stage}.png"):
        raise ValueError("PNG must be the exact required same-stage theme capture")
    png_path = (metadata_path.parent / relative).resolve()
    if not png_path.is_relative_to(metadata_path.parent.resolve()):
        raise ValueError("PNG path escapes its captured metadata directory")
    png = PNG(png_path)
    screen = metadata["screenSize"]
    if (integer(screen["width"], "screenSize.width") != png.width
            or integer(screen["height"], "screenSize.height") != png.height):
        raise ValueError("Native screen dimensions differ from original PNG dimensions")
    checks = analyze_status_bar(png, before, api_level)
    return {
        "stage": stage,
        "visibility_status": "match" if all(check["status"] == "match" for check in checks.values()) else "mismatch",
        "metadata": str(metadata_path),
        "png": str(png_path),
        "png_size": [png.width, png.height],
        "api_level": api_level,
        "coordinate_source": "mobile:getSystemBars native frame, unchanged physical pixels",
        "native_status_bar": before,
        "scope": "Layout-dependent clock/status foreground presence; no individual-icon identity claim",
        "checks": checks,
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("artifact_root", type=Path)
    parser.add_argument("--strict", action="store_true")
    options = parser.parse_args()
    root = options.artifact_root.resolve()
    results, errors = [], []
    software = None
    try:
        versions = list(root.rglob("android-driver-versions.json")) if root.is_dir() else []
        if len(versions) != 1:
            raise ValueError("Expected one record of the actually installed Android driver versions")
        software = json.loads(versions[0].read_text(encoding="utf-8-sig"))
        for key, name in (("uiautomator2", "appium-uiautomator2-driver"),
                          ("android", "appium-android-driver")):
            package = software[key]
            if (package["name"] != name or not isinstance(package["version"], str)
                    or not re.fullmatch(r"[0-9]+\.[0-9]+\.[0-9]+(?:[-+].+)?", package["version"])):
                raise ValueError(f"Invalid installed {name} version record")
    except (OSError, ValueError, KeyError, TypeError) as error:
        errors.append({"scope": "installed-driver-versions", "error": str(error)})
    for stage in STAGES:
        paths = list(root.rglob(f"android-system-bars-{stage}.json")) if root.is_dir() else []
        try:
            if len(paths) != 1:
                raise ValueError(f"Expected one original {stage} native metadata capture; found {len(paths)}")
            results.append(analyze(paths[0], stage))
        except (OSError, ValueError, KeyError, TypeError, zlib.error, struct.error) as error:
            errors.append({"stage": stage, "error": str(error)})
    if len({result["api_level"] for result in results}) > 1:
        errors.append({"scope": "captures", "error": "Device API level changed between theme captures"})
    if len({tuple(result["png_size"]) for result in results}) > 1:
        errors.append({"scope": "captures", "error": "Full screenshot dimensions changed between theme captures"})
    summary = {"analyses": len(results),
               "matches": sum(result["visibility_status"] == "match" for result in results),
               "mismatches": sum(result["visibility_status"] == "mismatch" for result in results),
               "errors": len(errors)}
    print(json.dumps({"artifact_root": str(root), "installed_drivers": software, "summary": summary,
                      "results": results, "errors": errors}, indent=2, ensure_ascii=False))
    return (2 if errors else 1 if summary["mismatches"] else 0) if options.strict else 0


if __name__ == "__main__":
    sys.exit(main())
