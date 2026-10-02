#!/usr/bin/env python3
"""Verify three same-phase Windows Appium window captures, using only stdlib.

Usage: python3 verify_windows_theme.py ARTIFACT_ROOT --strict
Required: TestResults/windows-{light,dark,light-restored}.{json,xml}, plus the
PNG named by each JSON. JSON contains stage, png, coordinateSource, window and
elements frames. window is copied from that phase's native PageSource Window;
elements are the app-session Location/Size, checked against the unique matching
XML AutomationId. The PNG must match the XML Window's 1x dimensions (at most
one pixel of rounding). Window-position APIs can include invisible borders;
nativeWindow/sessionWindow are diagnostic records, never screenshot origins.
Arbitrary scaling, guessed offsets and launch-only frames are rejected.

Icon sampling deliberately supports the example's compact NavigationView only:
each menu item must be a near-square button, not a wide text-bearing row. The
central 60% x 64% excludes the left selection indicator and outer borders. This
proves contrasting pixels within the icon area; it does not identify the glyph.
Page-background sampling uses the blank content band below the counter button,
within that button's horizontal span. Insufficient blank space is an evidence
error rather than permission to guess another background region.
"""

import argparse
from collections import Counter
import json
import math
from pathlib import Path
import struct
import sys
import xml.etree.ElementTree as ET
import zlib

from verify_mac_button_colors import PNG


PHASES = {
    "light": ("30-theme-light.png", "#FFFFFF", "#512BD4"),
    "dark": ("31-theme-dark.png", "#1F1F1F", "#AC99EA"),
    "light-restored": ("32-theme-light-restored.png", "#FFFFFF", "#512BD4"),
}
ITEMS = ("home", "home2", "media")
MINIMUM_COLOR_MATCH = .90
MINIMUM_ICON_CONTRAST = 3.0


def rgb_hex(rgb):
    return "#" + "".join(f"{channel:02X}" for channel in rgb)


def rgb_tuple(value):
    return tuple(int(value[i:i + 2], 16) for i in (1, 3, 5))


def frame(value, name):
    if not isinstance(value, dict):
        raise ValueError(f"{name} must contain an actual Location/Size frame")
    result = {}
    for key in ("x", "y", "width", "height"):
        number = value.get(key)
        if isinstance(number, bool) or not isinstance(number, (int, float)) or not math.isfinite(number):
            raise ValueError(f"{name}.{key} must be a finite number")
        result[key] = float(number)
    if result["width"] <= 0 or result["height"] <= 0:
        raise ValueError(f"{name} has an empty frame")
    return result


def source_frame(node, name):
    try:
        value = {key: float(node.attrib[key]) for key in ("x", "y", "width", "height")}
    except (KeyError, ValueError) as error:
        raise ValueError(f"{name} XML node has no usable native bounds") from error
    return frame(value, name)


def map_frames(metadata, png, source):
    if metadata.get("coordinateSource") != "windows-page-source":
        raise ValueError("JSON must identify windows-page-source as its captured Window coordinate source")
    if source.tag.rsplit("}", 1)[-1] != "Window":
        raise ValueError("The app-session XML must have a top-level Window")
    window = frame(metadata.get("window"), "window")
    xml_window = source_frame(source, "Window")
    if window != xml_window:
        raise ValueError("JSON Window bounds do not agree exactly with the same-phase XML Window")
    # Actual captures establish the PageSource Window as the screenshot frame.
    # Native window-position APIs include invisible borders and are not used to
    # invent an offset. Every API element below must corroborate this XML tree.
    scale = 1
    residuals = [abs(png.width - window["width"]), abs(png.height - window["height"])]
    if max(residuals) > 1:
        raise ValueError("PNG dimensions do not match the PageSource Window at 1x pixels")
    elements = metadata.get("elements")
    if not isinstance(elements, dict):
        raise ValueError("Missing elements frames")
    mapped = {}
    xml_elements = {}
    for name in (*ITEMS, "counterBtn"):
        original = frame(elements.get(name), name)
        candidates = [node for node in source.iter() if node.get("AutomationId") == name]
        if len(candidates) != 1:
            raise ValueError(f"Expected one same-phase XML AutomationId {name}; found {len(candidates)}")
        xml_elements[name] = source_frame(candidates[0], name)
        if original != xml_elements[name]:
            raise ValueError(f"API {name} Location/Size does not agree exactly with its same-phase XML bounds")
        transformed = {"x": (original["x"] - window["x"]) * scale,
                       "y": (original["y"] - window["y"]) * scale,
                       "width": original["width"] * scale,
                       "height": original["height"] * scale}
        if (transformed["x"] < 0 or transformed["y"] < 0
                or transformed["x"] + transformed["width"] > png.width
                or transformed["y"] + transformed["height"] > png.height):
            raise ValueError(f"Recorded {name} frame is not wholly inside the captured Window")
        mapped[name] = transformed
    diagnostics = {name: frame(metadata[name], name)
                   for name in ("nativeWindow", "sessionWindow") if name in metadata}
    return mapped, {"model": "windows_page_source_window_pixels", "scale": scale,
                    "dimension_residual_pixels": residuals,
                    "origin_screen": [window["x"], window["y"]], "window": window,
                    "xml_window": xml_window, "xml_element_frames": xml_elements,
                    "api_window_diagnostics": diagnostics,
                    "element_frames_pixels": mapped}


def rectangle(value, left, top, right, bottom):
    return (math.ceil(value["x"] + value["width"] * left),
            math.ceil(value["y"] + value["height"] * top),
            math.floor(value["x"] + value["width"] * right),
            math.floor(value["y"] + value["height"] * bottom))


def pixel_counts(png, region, minimum_pixels=16):
    x0, y0, x1, y1 = region
    if not (0 <= x0 < x1 <= png.width and 0 <= y0 < y1 <= png.height):
        raise ValueError(f"Sampling rectangle is empty or outside PNG: {region}")
    if (x1 - x0) * (y1 - y0) < minimum_pixels:
        raise ValueError(f"Sampling rectangle has too little evidence: {region}")
    return Counter(png.pixel(x, y) for y in range(y0, y1) for x in range(x0, x1))


def color_check(png, regions, expected, minimum_pixels=16):
    target = rgb_tuple(expected)
    counts = Counter()
    samples = {}
    for name, region in regions.items():
        current = pixel_counts(png, region, minimum_pixels)
        count = sum(current.values())
        samples[name] = {"rectangle_pixels": list(region), "pixel_count": count,
                         "expected_exact_pixels": current[target],
                         "expected_exact_fraction": round(current[target] / count, 6)}
        counts.update(current)
    total = sum(counts.values())
    # Each side must pass independently; one correctly colored button half is
    # insufficient. Exact flat fills exclude anti-aliased labels and corners.
    matched = all(sample["expected_exact_pixels"] / sample["pixel_count"] >= MINIMUM_COLOR_MATCH
                  for sample in samples.values())
    return {"status": "match" if matched else "mismatch", "expected_rgb": expected,
            "minimum_match_fraction": MINIMUM_COLOR_MATCH, "samples": samples,
            "pixel_count": total, "expected_exact_pixels": counts[target],
            "expected_exact_fraction": round(counts[target] / total, 6),
            "most_common": [{"rgb": rgb_hex(rgb), "pixels": count}
                            for rgb, count in counts.most_common(4)]}


def luminance(rgb):
    channels = [value / 255 for value in rgb]
    linear = [value / 12.92 if value <= .04045 else ((value + .055) / 1.055) ** 2.4
              for value in channels]
    return sum(a * b for a, b in zip(linear, (.2126, .7152, .0722)))


def contrast(a, b):
    light, dark = sorted((luminance(a), luminance(b)), reverse=True)
    return (light + .05) / (dark + .05)


def icon_check(png, item, scale):
    aspect = item["width"] / item["height"]
    if not .65 <= aspect <= 1.90:
        raise ValueError("Menu item is not compact; its central region cannot identify the icon area")
    region = rectangle(item, .20, .18, .80, .82)
    counts = pixel_counts(png, region)
    background, background_count = counts.most_common(1)[0]
    total = sum(counts.values())
    if background_count / total < .40:
        raise ValueError("Icon region has no dominant flat background; contrast reference is uncertain")
    qualifying_colors = {rgb for rgb in counts
                         if contrast(rgb, background) >= MINIMUM_ICON_CONTRAST}
    x0, y0, x1, y1 = region
    points = [(x, y) for y in range(y0, y1) for x in range(x0, x1)
              if png.pixel(x, y) in qualifying_colors]
    minimum_count = max(12, math.ceil(total * .02), math.ceil(12 * scale * scale))
    minimum_columns = max(4, math.ceil((x1 - x0) * .15))
    minimum_rows = max(4, math.ceil((y1 - y0) * .15))
    columns, rows = len({x for x, _ in points}), len({y for _, y in points})
    matched = (len(points) >= minimum_count
               and columns >= minimum_columns and rows >= minimum_rows)
    return {"status": "match" if matched else "mismatch", "rectangle_pixels": list(region),
            "layout_assumption": "compact near-square menu item; central ROI excludes left selection indicator",
            "item_aspect_ratio": round(aspect, 6), "pixel_count": total,
            "background_rgb": rgb_hex(background),
            "background_fraction": round(background_count / total, 6),
            "minimum_contrast": MINIMUM_ICON_CONTRAST, "visible_pixels": len(points),
            "minimum_visible_pixels": minimum_count,
            "occupied_columns": columns, "minimum_occupied_columns": minimum_columns,
            "occupied_rows": rows, "minimum_occupied_rows": minimum_rows,
            "foreground_colors": [{"rgb": rgb_hex(rgb), "pixels": counts[rgb],
                                    "contrast": round(contrast(rgb, background), 6)}
                                   for rgb in sorted(qualifying_colors, key=lambda c: counts[c], reverse=True)[:4]]}


def unique_pairs(root):
    pairs, errors = [], []
    for stage, (expected_name, _, _) in PHASES.items():
        candidates = sorted(root.rglob(f"windows-{stage}.json"))
        if len(candidates) != 1:
            errors.append({"stage": stage, "error": f"Expected one phase JSON; found {len(candidates)}"})
            continue
        path = candidates[0]
        try:
            metadata = json.loads(path.read_text(encoding="utf-8-sig"))
            if not isinstance(metadata, dict) or metadata.get("stage") != stage:
                raise ValueError("JSON stage does not agree with its phase filename")
            xmls = sorted(root.rglob(f"windows-{stage}.xml"))
            if len(xmls) != 1 or xmls[0] != path.with_suffix(".xml"):
                raise ValueError("Expected one same-directory, same-phase XML snapshot")
            ET.parse(xmls[0])
            reference = metadata.get("png")
            if not isinstance(reference, str):
                raise ValueError("Missing same-phase PNG reference")
            relative = Path(reference.replace("\\", "/"))
            if relative.is_absolute() or ".." in relative.parts or relative.name != expected_name:
                raise ValueError("PNG reference must be the exact phase filename under the JSON directory")
            pngs = sorted(root.rglob(expected_name))
            target = (path.parent / relative).resolve()
            if len(pngs) != 1 or pngs[0].resolve() != target:
                raise ValueError("Expected one uniquely referenced same-phase PNG")
            pairs.append((path, xmls[0], target, stage, metadata))
        except (OSError, ValueError, ET.ParseError) as error:
            errors.append({"stage": stage, "json": str(path), "error": str(error)})
    return pairs, errors


def analyze(json_path, xml_path, png_path, stage, metadata):
    png = PNG(png_path)
    elements, coordinates = map_frames(metadata, png, ET.parse(xml_path).getroot())
    button = elements["counterBtn"]
    if button["width"] < png.width * .30:
        raise ValueError("Counter button is too narrow to establish the example's content column")
    page_band = (math.ceil(button["x"]),
                 math.ceil(button["y"] + button["height"] * 1.25),
                 math.floor(button["x"] + button["width"]), math.floor(png.height * .94))
    page = color_check(png, {"below_counter_content_band": page_band}, PHASES[stage][1],
                       minimum_pixels=max(1000, math.ceil(png.width * png.height * .02)))
    button_regions = {name: rectangle(button, left, .40, right, .60)
                      for name, left, right in (("left", .12, .30), ("right", .70, .88))}
    button_colors = color_check(png, button_regions, PHASES[stage][2])
    icons = {name: icon_check(png, elements[name], coordinates["scale"]) for name in ITEMS}
    checks = {"page_background": page, "counter_button_background": button_colors,
              "navigation_icons": icons}
    matched = all(check["status"] == "match" for check in (page, button_colors, *icons.values()))
    return {"stage": stage, "json": str(json_path), "xml": str(xml_path), "png": str(png_path),
            "png_size": [png.width, png.height], "coordinates": coordinates,
            "checks": checks, "color_status": "match" if matched else "mismatch"}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("artifact_root", type=Path)
    parser.add_argument("--strict", action="store_true", help="Exit 1 for visual mismatch, 2 for missing/uncertain evidence")
    options = parser.parse_args()
    root = options.artifact_root.resolve()
    results, errors = [], []
    if root.is_dir():
        pairs, errors = unique_pairs(root)
        for json_path, xml_path, png_path, stage, metadata in pairs:
            try:
                results.append(analyze(json_path, xml_path, png_path, stage, metadata))
            except (OSError, ValueError, KeyError, ET.ParseError, zlib.error, struct.error) as error:
                errors.append({"stage": stage, "json": str(json_path), "png": str(png_path), "error": str(error)})
    else:
        errors.append({"error": "artifact_root must be an existing directory"})
    summary = {"analyses": len(results), "matches": sum(r["color_status"] == "match" for r in results),
               "mismatches": sum(r["color_status"] == "mismatch" for r in results), "errors": len(errors)}
    print(json.dumps({"artifact_root": str(root), "summary": summary,
                      "results": results, "errors": errors}, indent=2, ensure_ascii=False))
    return (2 if errors else 1 if summary["mismatches"] else 0) if options.strict else 0


if __name__ == "__main__":
    sys.exit(main())
