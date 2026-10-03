#!/usr/bin/env python3
"""Verify status-bar and native three-key navigation foreground from actual Android PNGs.

Usage: python3 verify_android_system_bars.py ARTIFACT_ROOT --strict
Consumes exactly three android-system-bars-{stage}.json captures. System-bar
frames come from mobile:getSystemBars, which parses native dumpsys mFrame/Frame
coordinates unchanged; PNG pixels are compared directly, without density scaling.

Primary contract:
https://github.com/appium/appium-uiautomator2-driver#mobile-getsystembars
https://github.com/appium/appium-android-driver/blob/v14.2.0/lib/commands/system-bars.ts#L106-L124

The foreground test is layout-dependent presence evidence, not identification of
individual system icons. API 26/27 keep the stock clock on the right; API >=28
also requires contrasting foreground in the left clock area. Regions are derived
only from the captured native bar rectangle. No status-bar height is assumed.

Navigation buttons are independently scoped by original enableMultiWindows XML
before/after the PNG: displayed/clickable SystemUI or Launcher back/home/recent_apps
resource IDs, stable native screen bounds wholly within mobile:getSystemBars.
No PNG-derived crop, guessed density, or tablet-taskbar foreground substitutes.
The observed API32/33 wide alternative accepts an explicitly absent zero-frame
navigationBar only when original before/after XML independently supplies one
Launcher taskbar_container/navbuttons_view full-width bottom frame, one same-window
end_nav_buttons parent and all three actual clickable children. The pixels remain
scoped to each original button's own AX bounds; other taskbar icons cannot pass.
This does not change or repair the original run's TRX/CI failure.
https://github.com/appium/appium-uiautomator2-driver/blob/v8.7.0/README.md#settings-api
https://android.googlesource.com/platform/frameworks/base/+/refs/tags/android-15.0.0_r1/packages/SystemUI/res/layout/back.xml
https://android.googlesource.com/platform/packages/apps/Launcher3/+/refs/tags/android-15.0.0_r1/quickstep/src/com/android/launcher3/taskbar/NavbarButtonsViewController.java
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
import xml.etree.ElementTree as ET

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


NAVIGATION_BUTTONS = ("back", "home", "recent_apps")
NAVIGATION_PACKAGES = {"com.android.systemui", "com.android.launcher3"}


def native_navigation_bar(system_bars):
    if not isinstance(system_bars, dict):
        raise ValueError("mobile:getSystemBars must return an object")
    bar = system_bars["navigationBar"]
    if not isinstance(bar, dict) or bar.get("visible") is not True:
        raise ValueError("Native navigation bar must be present and visible")
    bounds = {key: integer(bar[key], f"navigationBar.{key}")
              for key in ("x", "y", "width", "height")}
    if (bounds["x"] < 0 or bounds["y"] < 0
            or bounds["width"] <= 0 or bounds["height"] <= 0):
        raise ValueError("Native navigation bar has invalid or empty bounds")
    return bounds


def absent_navigation_bar(system_bars):
    """The alternative contract requires the original, explicitly absent native bar."""
    if not isinstance(system_bars, dict) or not isinstance(system_bars.get("navigationBar"), dict):
        raise ValueError("Original navigationBar response is missing")
    bar = system_bars["navigationBar"]
    if bar.get("visible") is not False or any(
            integer(bar.get(key), f"navigationBar.{key}") != 0 for key in ("x", "y", "width", "height")):
        raise ValueError("Launcher taskbar requires an explicitly absent zero-frame navigationBar")


def native_launcher_taskbar(path, png, app_package):
    """Observed API32/33 wide contract: actual Launcher3 full-window AX ancestors.

    Read the original multi-window native bounds. Never infer a frame from the
    three button positions, PNG colors, viewport height, density or bar ratios.
    """
    data = path.read_bytes()
    if len(data) > 10 * 1024 * 1024 or b"<!DOCTYPE" in data or b"<!ENTITY" in data:
        raise ValueError("Native multi-window XML is too large or contains a DTD/entity")
    root = ET.fromstring(data)
    if root.tag != "hierarchy" or not any(node.get("package") == app_package for node in root.iter()):
        raise ValueError("Actual app/hierarchy is missing from native taskbar capture")
    parents = {child: parent for parent in root.iter() for child in parent}
    package = "com.android.launcher3"

    def unique(name, native_class):
        identifier = f"{package}:id/{name}"
        matches = [node for node in root.iter() if node.get("resource-id") == identifier]
        if len(matches) != 1:
            raise ValueError(f"Expected one native Launcher {name}; found {len(matches)}")
        node = matches[0]
        if (node.get("package") != package or node.get("class") != native_class or node.tag != native_class
                or node.get("displayed") != "true" or node.get("enabled") != "true"):
            raise ValueError(f"Native Launcher {name} identity/visibility differs")
        window = node.get("window-id", "")
        if not re.fullmatch(r"[1-9][0-9]*", window):
            raise ValueError(f"Native Launcher {name} has no actual window identity")
        match = re.fullmatch(r"\[(\d+),(\d+)\]\[(\d+),(\d+)\]", node.get("bounds", ""))
        if not match:
            raise ValueError(f"Native Launcher {name} bounds are missing/invalid")
        rectangle = tuple(map(int, match.groups()))
        if not (0 <= rectangle[0] < rectangle[2] <= png.width
                and 0 <= rectangle[1] < rectangle[3] <= png.height):
            raise ValueError(f"Native Launcher {name} frame is outside the original PNG")
        return node, window, rectangle

    taskbar, window, rectangle = unique("taskbar_container", "android.widget.FrameLayout")
    nav, nav_window, nav_rectangle = unique("navbuttons_view", "android.widget.FrameLayout")
    end, end_window, end_rectangle = unique("end_nav_buttons", "android.widget.LinearLayout")
    if (parents.get(taskbar) is not root or parents.get(nav) is not taskbar or parents.get(end) is not nav
            or window != nav_window or window != end_window or rectangle != nav_rectangle
            or rectangle[0] != 0 or rectangle[2] != png.width or rectangle[3] != png.height
            or not (rectangle[0] <= end_rectangle[0] < end_rectangle[2] <= rectangle[2]
                    and rectangle[1] <= end_rectangle[1] < end_rectangle[3] <= rectangle[3])):
        raise ValueError("Native Launcher taskbar/nav/end ancestors or full-width bottom frame disagree")
    for name in NAVIGATION_BUTTONS:
        node, key_window, key_rectangle = unique(name, "android.widget.ImageView")
        if (parents.get(node) is not end or key_window != window or node.get("clickable") != "true"
                or not (end_rectangle[0] <= key_rectangle[0] < key_rectangle[2] <= end_rectangle[2]
                        and end_rectangle[1] <= key_rectangle[1] < key_rectangle[3] <= end_rectangle[3])):
            raise ValueError(f"Native Launcher {name} is not a clickable child of this same taskbar window")
    bar = {"x": rectangle[0], "y": rectangle[1], "width": rectangle[2] - rectangle[0],
           "height": rectangle[3] - rectangle[1]}
    provenance = {"frame_source": "native_launcher_taskbar_hierarchy",
                  "coordinate_source": "enableMultiWindows native Launcher3 taskbar AX frame, unchanged physical pixels",
                  "package": package, "window_id": window,
                  "taskbar_resource_id": taskbar.get("resource-id"), "taskbar_rectangle_pixels": list(rectangle),
                  "navbuttons_resource_id": nav.get("resource-id"), "navbuttons_rectangle_pixels": list(nav_rectangle),
                  "end_buttons_resource_id": end.get("resource-id"), "end_buttons_rectangle_pixels": list(end_rectangle)}
    return bar, provenance


def native_xml_buttons(path, bar, png, app_package):
    data = path.read_bytes()
    if len(data) > 10 * 1024 * 1024 or b"<!DOCTYPE" in data or b"<!ENTITY" in data:
        raise ValueError("Native multi-window XML is too large or contains a DTD/entity")
    root = ET.fromstring(data)
    if root.tag != "hierarchy":
        raise ValueError("Expected the original Android native hierarchy root")
    if not any(node.get("package") == app_package for node in root.iter()):
        raise ValueError("The tested app is absent from the native multi-window capture")
    bar_rectangle = (bar["x"], bar["y"], bar["x"] + bar["width"], bar["y"] + bar["height"])
    if not (0 <= bar_rectangle[0] < bar_rectangle[2] <= png.width
            and 0 <= bar_rectangle[1] < bar_rectangle[3] <= png.height):
        raise ValueError("Native navigation bar is outside the full-resolution original PNG")
    result = {}
    for button in NAVIGATION_BUTTONS:
        expected_ids = {f"{package}:id/{button}" for package in NAVIGATION_PACKAGES}
        matches = [node for node in root.iter() if node.get("resource-id") in expected_ids]
        if len(matches) != 1:
            raise ValueError(f"Expected one native {button} node; found {len(matches)}")
        node = matches[0]
        package = node.get("package")
        if package not in NAVIGATION_PACKAGES or node.get("resource-id") != f"{package}:id/{button}":
            raise ValueError(f"Native {button} package/resource-id disagree")
        if any(node.get(attribute) != "true" for attribute in ("displayed", "enabled", "clickable")):
            raise ValueError(f"Native {button} must be displayed, enabled, and clickable")
        native_class = node.get("class")
        if not native_class or not isinstance(native_class, str):
            raise ValueError(f"Native {button} class is missing")
        match = re.fullmatch(r"\[(\d+),(\d+)\]\[(\d+),(\d+)\]", node.get("bounds", ""))
        if not match:
            raise ValueError(f"Native {button} bounds are missing or invalid")
        rectangle = tuple(map(int, match.groups()))
        if not (bar_rectangle[0] <= rectangle[0] < rectangle[2] <= bar_rectangle[2]
                and bar_rectangle[1] <= rectangle[1] < rectangle[3] <= bar_rectangle[3]):
            raise ValueError(f"Native {button} bounds are outside the native navigation bar")
        result[button] = {"package": package, "resource_id": node.get("resource-id"),
                          "class": native_class, "rectangle_pixels": list(rectangle)}
    if len({item["package"] for item in result.values()}) != 1:
        raise ValueError("The native navigation buttons belong to different system packages")
    for index, first in enumerate(NAVIGATION_BUTTONS):
        a = result[first]["rectangle_pixels"]
        for second in NAVIGATION_BUTTONS[index + 1:]:
            b = result[second]["rectangle_pixels"]
            if max(a[0], b[0]) < min(a[2], b[2]) and max(a[1], b[1]) < min(a[3], b[3]):
                raise ValueError(f"Native {first}/{second} bounds overlap")
    return result


def navigation_capture_path(metadata_path, value, expected):
    if not isinstance(value, str) or value != expected:
        raise ValueError(f"Expected original native capture {expected}")
    path = (metadata_path.parent / value).resolve()
    if not path.is_relative_to(metadata_path.parent.resolve()):
        raise ValueError("Native navigation XML path escapes its metadata directory")
    return path


def analyze_navigation_bar(metadata_path, metadata, stage, png):
    capture = metadata["multiWindowCapture"]
    if not isinstance(capture, dict):
        raise ValueError("Native navigation multi-window capture is missing")
    for key in ("enabledBefore", "enabledDuringBefore", "enabledDuringAfter", "enabledRestored"):
        if type(capture.get(key)) is not bool:
            raise ValueError(f"multiWindowCapture.{key} must be the actual boolean setting readback")
    if (capture["enabledDuringBefore"] is not True or capture["enabledDuringAfter"] is not True
            or capture["enabledRestored"] != capture["enabledBefore"]):
        raise ValueError("enableMultiWindows was not enabled throughout capture and restored exactly")
    app_package = capture["appPackage"]
    if not isinstance(app_package, str) or not re.fullmatch(r"[a-zA-Z0-9_]+(?:\.[a-zA-Z0-9_]+)+", app_package):
        raise ValueError("The actual tested app package is missing")
    if app_package in NAVIGATION_PACKAGES:
        raise ValueError("The tested app cannot pose as SystemUI or Launcher")
    real_size = metadata["deviceInfo"]["realDisplaySize"]
    if real_size != f"{png.width}x{png.height}":
        raise ValueError("Native realDisplaySize differs from full-resolution PNG dimensions")
    before_path = navigation_capture_path(metadata_path, capture["sourceBefore"],
        f"android-system-bars-{stage}-windows-before.xml")
    after_path = navigation_capture_path(metadata_path, capture["sourceAfter"],
        f"android-system-bars-{stage}-windows-after.xml")
    taskbar_provenance = None
    if metadata["systemBarsBefore"].get("navigationBar", {}).get("visible") is True:
        before = native_navigation_bar(metadata["systemBarsBefore"])
        after = native_navigation_bar(metadata["systemBarsAfter"])
    else:
        api_level = integer(int(metadata["deviceInfo"]["apiVersion"]), "apiVersion")
        if (api_level not in (32, 33)
                or metadata["png"] != f"shots/android-wide/{SEQUENCES[stage]:02}-theme-{stage}.png"):
            raise ValueError("An absent navigationBar is only supported by the observed API32/33 wide Launcher taskbar contract")
        absent_navigation_bar(metadata["systemBarsBefore"])
        absent_navigation_bar(metadata["systemBarsAfter"])
        before, taskbar_provenance = native_launcher_taskbar(before_path, png, app_package)
        after, after_provenance = native_launcher_taskbar(after_path, png, app_package)
        if taskbar_provenance != after_provenance:
            raise ValueError("Native Launcher taskbar identity/ancestry/bounds changed across the original PNG")
    if before != after:
        raise ValueError("Native navigation-region frame changed across screenshot capture")
    buttons = native_xml_buttons(before_path, before, png, app_package)
    after_buttons = native_xml_buttons(after_path, after, png, app_package)
    if buttons != after_buttons:
        raise ValueError("Native navigation-button identity/bounds changed across screenshot capture")
    checks = {f"navigation_{button}_foreground": foreground_presence(
                  png, tuple(buttons[button]["rectangle_pixels"]))
              for button in NAVIGATION_BUTTONS}
    result = {"bar": before, "buttons": buttons, "checks": checks,
              "source_before": str(before_path), "source_after": str(after_path)}
    if taskbar_provenance is not None:
        result["taskbar_provenance"] = taskbar_provenance
    return result


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
    navigation = analyze_navigation_bar(metadata_path, metadata, stage, png)
    checks.update(navigation["checks"])
    return {
        "stage": stage,
        "visibility_status": "match" if all(check["status"] == "match" for check in checks.values()) else "mismatch",
        "metadata": str(metadata_path),
        "png": str(png_path),
        "png_size": [png.width, png.height],
        "api_level": api_level,
        "coordinate_source": ("status: mobile:getSystemBars; navigation: enableMultiWindows native Launcher3 taskbar AX frame"
                              if "taskbar_provenance" in navigation else
                              "mobile:getSystemBars native frame, unchanged physical pixels"),
        **({"native_taskbar_provenance": navigation["taskbar_provenance"],
            "navigation_frame_source": "native_launcher_taskbar_hierarchy",
            "reported_navigation_bar_before": metadata["systemBarsBefore"]["navigationBar"],
            "reported_navigation_bar_after": metadata["systemBarsAfter"]["navigationBar"]}
           if "taskbar_provenance" in navigation else {}),
        "native_status_bar": before,
        "native_navigation_bar": navigation["bar"],
        "native_navigation_buttons": navigation["buttons"],
        "navigation_source_before": navigation["source_before"],
        "navigation_source_after": navigation["source_after"],
        "scope": "Clock/status presence plus each native SystemUI/Launcher three-key node; no taskbar/app-icon substitution",
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
        except (OSError, ValueError, KeyError, TypeError, ET.ParseError, zlib.error, struct.error) as error:
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
