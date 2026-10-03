#!/usr/bin/env python3
"""Read-only Mac Appium PNG/AX button-color analysis (Python standard library).

Usage: python3 ashell-button-pixels.py ARTIFACT_ROOT [--strict]
Explicit pair: ARTIFACT_ROOT --xml AX.xml --png SHOT.png --stage light
Auto-pairs light/dark/light-restored, before and after the counter click.
Uncertain coordinates or ambiguous pairs are errors, never guessed sample points.
"""

import argparse
from collections import Counter
import json
import math
from pathlib import Path
import re
import struct
import sys
import xml.etree.ElementTree as ET
import zlib


EXPECTED = {"light": "#512BD4", "dark": "#AC99EA", "light-restored": "#512BD4"}


class PNG:
    def __init__(self, path, *, decode_pixels=True):
        data = path.read_bytes()
        if data[:8] != b"\x89PNG\r\n\x1a\n":
            raise ValueError("Not a PNG file")
        pos, compressed, header = 8, bytearray(), None
        while pos < len(data):
            if pos + 12 > len(data):
                raise ValueError("Truncated PNG chunk")
            size = struct.unpack(">I", data[pos:pos + 4])[0]
            kind = data[pos + 4:pos + 8]
            payload = data[pos + 8:pos + 8 + size]
            if pos + size + 12 > len(data):
                raise ValueError("Truncated PNG payload")
            crc = struct.unpack(">I", data[pos + size + 8:pos + size + 12])[0]
            if zlib.crc32(kind + payload) & 0xFFFFFFFF != crc:
                raise ValueError("PNG chunk CRC mismatch")
            pos += size + 12
            if kind == b"IHDR":
                header = struct.unpack(">IIBBBBB", payload)
            elif kind == b"IDAT":
                compressed.extend(payload)
            elif kind == b"IEND":
                break
        if header is None:
            raise ValueError("Missing PNG IHDR")
        self.width, self.height, depth, color, compression, filtering, interlace = header
        if (depth, color) not in ((8, 2), (8, 6)) or compression or filtering or interlace:
            raise ValueError("Supported PNG: noninterlaced RGB/RGBA, 8 bits, standard filters")
        self.channels = 3 if color == 2 else 4
        stride = self.width * self.channels
        raw = zlib.decompress(compressed)
        if len(raw) != self.height * (stride + 1):
            raise ValueError("Unexpected decompressed PNG size")
        self.rows = []
        self._pixels_decoded = decode_pixels
        if not decode_pixels:
            # Every CRC, IHDR, compressed byte, decompressed length and row filter
            # has the same validation as full decoding. No caller requests pixels.
            if any(raw[y * (stride + 1)] > 4 for y in range(self.height)):
                raise ValueError("Unsupported PNG row filter")
            return
        previous = bytearray(stride)
        for y in range(self.height):
            start = y * (stride + 1)
            mode = raw[start]
            if mode > 4:
                raise ValueError("Unsupported PNG row filter")
            row = bytearray(raw[start + 1:start + 1 + stride])
            if mode in (0, 2):
                # None is unchanged; Up adds the preceding reconstructed row.
                # A zero Up residual needs a copy so decoded rows never alias.
                if mode == 2:
                    row = (bytearray((a + b) & 255 for a, b in zip(row, previous))
                           if any(row) else previous.copy())
                self.rows.append(row)
                previous = row
                continue
            for i in range(stride):
                left = row[i - self.channels] if i >= self.channels else 0
                up = previous[i]
                upper_left = previous[i - self.channels] if i >= self.channels else 0
                if mode == 0:
                    predictor = 0
                elif mode == 1:
                    predictor = left
                elif mode == 2:
                    predictor = up
                elif mode == 3:
                    predictor = (left + up) // 2
                else:
                    p = left + up - upper_left
                    distances = (abs(p - left), abs(p - up), abs(p - upper_left))
                    predictor = (left, up, upper_left)[distances.index(min(distances))]
                row[i] = (row[i] + predictor) & 255
            self.rows.append(row)
            previous = row

    def pixel(self, x, y):
        if not self._pixels_decoded:
            raise ValueError("PNG pixel data was not decoded")
        at = x * self.channels
        channels = self.rows[y][at:at + self.channels]
        if self.channels == 4 and channels[3] != 255:
            raise ValueError("Sample contains transparent pixels; no background assumption made")
        return tuple(channels[:3])


def frame(element):
    result = {key: float(element.attrib[key]) for key in ("x", "y", "width", "height")}
    if not all(math.isfinite(value) for value in result.values()):
        raise ValueError("Nonfinite AX frame")
    if result["width"] <= 0 or result["height"] <= 0:
        raise ValueError("Empty AX frame")
    return result


def bounds(f):
    return f["x"], f["y"], f["x"] + f["width"], f["y"] + f["height"]


def contained(inner, outer, epsilon=1):
    a, b = bounds(inner), bounds(outer)
    return a[0] >= b[0] - epsilon and a[1] >= b[1] - epsilon and a[2] <= b[2] + epsilon and a[3] <= b[3] + epsilon


def ax_button(path):
    tree = ET.parse(path).getroot()
    parents = {child: parent for parent in tree.iter() for child in parent}
    candidates = [node for node in tree.iter() if node.get("identifier") == "counterBtn"]
    buttons = [node for node in candidates if node.tag.rsplit("}", 1)[-1].endswith("Button")]
    candidates = buttons or candidates
    if len(candidates) != 1:
        raise ValueError(f"Expected one AX counterBtn; found {len(candidates)}")
    node = candidates[0]
    button_frame = frame(node)
    ancestor, window_frame = parents.get(node), None
    while ancestor is not None:
        if ancestor.tag.rsplit("}", 1)[-1].endswith("Window"):
            try:
                possible = frame(ancestor)
                if contained(button_frame, possible):
                    window_frame = possible
                    break
            except (KeyError, ValueError):
                pass
        ancestor = parents.get(ancestor)
    if window_frame is None:
        raise ValueError("No containing AX Window with a usable frame")
    return {"identifier": "counterBtn", "type": node.tag, "enabled": node.get("enabled"),
            "label": node.get("label"), "title": node.get("title"),
            "frame": button_frame, "window_frame": window_frame}


def mapping(button, window, png, requested):
    # Support 1x/2x/3x captures. Cropped-window dimensions identify the origin.
    image_frame = {"x": 0, "y": 0, "width": png.width, "height": png.height}
    window_scales = [s for s in (1, 2, 3) if abs(png.width / s - window["width"]) <= 1 and abs(png.height / s - window["height"]) <= 1]
    if requested == "window" or (requested == "auto" and window_scales):
        if len(window_scales) != 1:
            raise ValueError("Window capture dimensions do not identify a unique scale")
        scale, origin = window_scales[0], (window["x"], window["y"])
        model, reason = "window_relative", "PNG dimensions match AX Window dimensions at the stated scale"
    else:
        choices = []
        for s in (1, 2, 3):
            transformed_window = {k: v * s for k, v in window.items()}
            # Auto requires an AX window edge to agree with a PNG screen edge.
            edge_agreement = abs(window["x"] + window["width"] - png.width / s) <= 1 or abs(window["y"] + window["height"] - png.height / s) <= 1
            if contained(transformed_window, image_frame, epsilon=s) and (requested == "screen" or edge_agreement):
                choices.append(s)
        if len(choices) != 1:
            raise ValueError("Cannot infer unique full-screen scale/origin from AX/PNG dimensions; use explicit coordinates only with verified capture metadata")
        scale, origin = choices[0], (0, 0)
        model, reason = "screen_absolute_inferred", "AX Window fits inside PNG and an absolute window edge matches a screen edge"
        if requested == "screen":
            reason = "Explicit screen-coordinate mode; containing window and button bounds validated"
    pixel_frame = {"x": (button["x"] - origin[0]) * scale, "y": (button["y"] - origin[1]) * scale,
                   "width": button["width"] * scale, "height": button["height"] * scale}
    if not contained(pixel_frame, image_frame, epsilon=0):
        raise ValueError("Mapped counterBtn frame lies outside PNG")
    return {"model": model, "reason": reason, "origin_ax": list(origin), "scale": scale,
            "window_edge_rounding_tolerance_ax": 1,
            "button_frame_pixels": pixel_frame}


def phase_key(name):
    normalized = re.sub(r"^\d+[-_]?", "", name.lower().replace("_", "-"))
    restored = re.search(r"(?:^|-)light-restored(?:-|$)|(?:^|-)restored(?:-|$)", normalized)
    match = restored or re.search(r"(?:^|-)(dark|light)(?:-|$)", normalized)
    if not match:
        return None
    stage = "light-restored" if restored else match.group(1)
    return stage, bool(re.search(r"(?:^|-)clicked(?:-|$)", normalized))


def find_pairs(root):
    pairs, errors = [], []
    for xml in sorted(root.rglob("*.xml")):
        if xml.parent.name != "TestResults" or not phase_key(xml.stem):
            continue
        key = phase_key(xml.stem)
        candidates = [p for p in xml.parent.rglob("*.png") if phase_key(p.stem) == key]
        normalized_xml = re.sub(r"^\d+[-_]?", "", xml.stem.lower())
        exact = [p for p in candidates if re.sub(r"^\d+[-_]?", "", p.stem.lower()) == normalized_xml]
        if exact:
            candidates = exact
        else:
            diagnostic = [p for p in candidates if "probe" in p.stem.lower() or "button" in p.stem.lower()]
            if diagnostic:
                candidates = diagnostic
        if len(candidates) != 1:
            errors.append({"xml": str(xml), "error": f"Expected one same-stage PNG; found {len(candidates)}",
                           "candidates": [str(p) for p in candidates]})
        else:
            pairs.append((xml, candidates[0], key[0], key[1]))
    return pairs, errors


def summarize_pixels(png, rectangle, expected, tolerance):
    x0, y0, x1, y1 = rectangle
    colors = Counter(png.pixel(x, y) for y in range(y0, y1) for x in range(x0, x1))
    count = sum(colors.values())
    if count < 16:
        raise ValueError("Sampling region is too small")
    target = tuple(int(expected[i:i + 2], 16) for i in (1, 3, 5))
    exact = colors[target]
    near = sum(n for rgb, n in colors.items() if max(abs(a - b) for a, b in zip(rgb, target)) <= tolerance)
    return {"rectangle_pixels": list(rectangle), "pixel_count": count,
            "expected_exact_pixels": exact, "expected_exact_fraction": round(exact / count, 6),
            "expected_within_tolerance_pixels": near, "expected_within_tolerance_fraction": round(near / count, 6),
            "most_common": [{"rgb": list(rgb), "hex": "#" + "".join(f"{channel:02X}" for channel in rgb),
                             "pixels": n, "fraction": round(n / count, 6)} for rgb, n in colors.most_common(5)]}


def analyze(xml, png_path, stage, clicked, options):
    ax = ax_button(xml)
    png = PNG(png_path)
    transform = mapping(ax["frame"], ax["window_frame"], png, options.coordinates)
    f = transform["button_frame_pixels"]
    # Central-height side strips exclude centered labels and rounded corners.
    rectangles = {name: (math.ceil(f["x"] + f["width"] * left), math.ceil(f["y"] + f["height"] * .40),
                         math.floor(f["x"] + f["width"] * right), math.floor(f["y"] + f["height"] * .60))
                  for name, left, right in (("left", .12, .30), ("right", .70, .88))}
    expected = EXPECTED[stage]
    samples = {name: summarize_pixels(png, rect, expected, options.tolerance) for name, rect in rectangles.items()}
    count = sum(s["pixel_count"] for s in samples.values())
    exact = sum(s["expected_exact_pixels"] for s in samples.values())
    near = sum(s["expected_within_tolerance_pixels"] for s in samples.values())
    return {"xml": str(xml), "png": str(png_path), "stage": stage, "after_click": clicked,
            "png_size": [png.width, png.height], "ax": ax, "coordinates": transform,
            "expected_rgb": expected, "tolerance_per_channel": options.tolerance, "samples": samples,
            "combined": {"pixel_count": count, "expected_exact_fraction": round(exact / count, 6),
                         "expected_within_tolerance_fraction": round(near / count, 6)},
            "color_status": "match" if near / count >= options.minimum_match else "mismatch"}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("artifact_root", type=Path)
    parser.add_argument("--xml", type=Path, help="Explicit same-stage AX snapshot; requires --png and --stage")
    parser.add_argument("--png", type=Path)
    parser.add_argument("--stage", choices=EXPECTED)
    parser.add_argument("--coordinates", choices=("auto", "screen", "window"), default="auto")
    parser.add_argument("--tolerance", type=int, default=0, help="Allowed difference per RGB channel (default: exact)")
    parser.add_argument("--minimum-match", type=float, default=.90)
    parser.add_argument("--strict", action="store_true", help="Exit 1 for color mismatch, 2 for pairing/analysis errors")
    options = parser.parse_args()
    if not options.artifact_root.is_dir():
        parser.error("artifact_root must be a directory")
    if not 0 <= options.tolerance <= 255 or not 0 <= options.minimum_match <= 1:
        parser.error("Invalid tolerance or minimum-match")
    if any((options.xml, options.png, options.stage)) and not all((options.xml, options.png, options.stage)):
        parser.error("Explicit pairing requires --xml, --png, and --stage together")
    if options.xml:
        pairs, errors = [(options.xml.resolve(), options.png.resolve(), options.stage, "clicked" in options.xml.stem)], []
    else:
        pairs, errors = find_pairs(options.artifact_root.resolve())
    if not options.xml:
        expected_phases = {(stage, clicked) for stage in EXPECTED for clicked in (False, True)}
        observed_phases = {(stage, clicked) for _, _, stage, clicked in pairs}
        for stage, clicked in sorted(expected_phases - observed_phases):
            errors.append({"error": f"Missing {stage} {'after' if clicked else 'before'} click AX/PNG evidence"})
    analyses = []
    for xml, png, stage, clicked in pairs:
        try:
            analyses.append(analyze(xml, png, stage, clicked, options))
        except (OSError, ValueError, KeyError, ET.ParseError, zlib.error, struct.error) as error:
            errors.append({"xml": str(xml), "png": str(png), "error": str(error)})
    if not pairs and not errors:
        errors.append({"error": "No same-stage light/dark/light-restored AX/PNG pairs found; launch AX is never reused for later screenshots"})
    summary = {"analyses": len(analyses), "matches": sum(a["color_status"] == "match" for a in analyses),
               "mismatches": sum(a["color_status"] == "mismatch" for a in analyses), "errors": len(errors)}
    print(json.dumps({"artifact_root": str(options.artifact_root.resolve()), "minimum_match_fraction": options.minimum_match,
                      "summary": summary, "results": analyses, "errors": errors}, indent=2, ensure_ascii=False))
    if options.strict:
        return 2 if errors else 1 if summary["mismatches"] else 0
    return 0


if __name__ == "__main__":
    sys.exit(main())
