#!/usr/bin/env python3
"""Strict original PNG/native-AX verification for the group landing page.

Usage: verify_landing_theme.py ARTIFACT_ROOT --strict
Android native AX/API bounds are screen pixels, accepted only when the session
window size equals the unmodified PNG dimensions. iOS independently matches
each PNG and application AX frame to one real /wda/screens entry using the
actual runtime version. WDA bounds must equal original PNG pixels, and
Application AX points multiplied by the independently reported native scale.
No contract is selected from image ratios; unknown runtimes fail.

The iOS screenInfo fields are serialized unchanged by WDA16.12.11 FBScreen.m:
https://github.com/appium/WebDriverAgent/blob/v16.12.11/WebDriverAgentLib/Utilities/FBScreen.m#L25-L38
Its pixel contract was observed on iOS18.6, iOS26.5, iOS27.0 and Duo27.1. Each runtime
must independently prove the same native-screen/PNG/AX relationship from its
own original captures. For actual iOS18 compact landing captures, the complete simctl internal PNG is
selected by runtime and its immutable source/session provenance is also required.
Other platform/runtime providers are unchanged. This does not test folding or identify icon art.
"""
import argparse
import importlib.util
from collections import Counter
import json
import math
from pathlib import Path
import re
import sys
import struct
import zlib
import xml.etree.ElementTree as ET


STAGES = {"light": 40, "dark": 41, "light-restored": 42}
BUNDLE = "com.companyname.exampleashellapp"
BODY = "landing-media-body"
ROWS = ("landing-music", "landing-photos")
MARKERS = (BODY,) + ROWS + tuple(row + suffix for row in ROWS for suffix in ("-icon", "-title"))
EXPECTED_BG = {"light": (255, 255, 255), "dark": (31, 31, 31), "light-restored": (255, 255, 255)}


class Mismatch(ValueError):
    pass


def unique(root, name):
    paths = sorted(root.rglob(name))
    if len(paths) != 1:
        raise ValueError(f"Expected exactly one {name}; found {len(paths)}")
    return paths[0]


def read_json(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def number(value):
    if type(value) not in (int, float) or not math.isfinite(value):
        raise ValueError("Nonfinite or nonnumeric native coordinate")
    return float(value)


def frame(value):
    result = {key: number(value[key]) for key in ("x", "y", "width", "height")}
    if result["width"] <= 0 or result["height"] <= 0:
        raise ValueError("Native frame is empty")
    return result


def xml_frame(node, platform):
    if platform == "android":
        match = re.fullmatch(r"\[(-?\d+),(-?\d+)\]\[(-?\d+),(-?\d+)\]", node.get("bounds", ""))
        if not match:
            raise ValueError("Android AX marker has no native screen-pixel bounds")
        x, y, right, bottom = map(int, match.groups())
        return frame({"x": x, "y": y, "width": right - x, "height": bottom - y})
    return frame({key: float(node.attrib[key]) for key in ("x", "y", "width", "height")})


def matches_id(node, identifier):
    return any(node.get(key) == identifier for key in ("name", "identifier", "content-desc", "resource-id")) or (
        node.get("resource-id", "").endswith(":id/" + identifier))


def marker(root, identifier, platform):
    candidates = [node for node in root.iter() if matches_id(node, identifier)
                  and node.get("visible", node.get("displayed", "true")) == "true"]
    if len(candidates) != 1:
        raise ValueError(f"Expected one visible native AX {identifier}; found {len(candidates)}")
    node = candidates[0]
    if node.get("enabled") != "true":
        raise Mismatch(f"AX {identifier} is disabled")
    if platform == "ios" and node.get("visible") != "true":
        raise ValueError(f"iOS AX {identifier} lacks its visible state")
    return node


def contains(outer, inner, tolerance=1):
    return (inner["x"] >= outer["x"] - tolerance and inner["y"] >= outer["y"] - tolerance
            and inner["x"] + inner["width"] <= outer["x"] + outer["width"] + tolerance
            and inner["y"] + inner["height"] <= outer["y"] + outer["height"] + tolerance)


def inside_tree(node, ancestor, parents):
    while node in parents:
        node = parents[node]
        if node is ancestor:
            return True
    return False


def png_reader():
    path = Path(__file__).with_name("verify_mac_button_colors.py")
    values = {"__name__": "landing_png_reader", "__file__": str(path)}
    exec(compile(path.read_text(encoding="utf-8"), str(path), "exec"), values)
    return values["PNG"]


def ios_mapping(root, stage, source, png, metadata):
    apps = [node for node in source.iter() if node.tag.rsplit("}", 1)[-1] == "XCUIElementTypeApplication"]
    if len(apps) != 1 or apps[0].get("visible") != "true":
        raise ValueError("Expected one visible native Application")
    app = apps[0]
    if app.get("bundleId") is not None and app.get("bundleId") != BUNDLE:
        raise Mismatch("AX belongs to another app")
    active = read_json(unique(root, f"landing-{stage}-active-app.json"))
    if isinstance(active.get("value"), dict):
        active = active["value"]
    if active.get("bundleId") != BUNDLE:
        raise Mismatch("Native activeAppInfo belongs to another app")
    application = xml_frame(app, "ios")
    screens = read_json(unique(root, f"landing-{stage}-screens.json")).get("value")
    if not isinstance(screens, list) or not screens:
        raise ValueError("No real WDA screenInfo array")
    values = []
    for screen in screens:
        if type(screen.get("displayId")) is not int or type(screen.get("isMain")) is not bool:
            raise ValueError("Malformed native display identity")
        scale = number(screen.get("scale"))
        if scale <= 0 or type(screen.get("traits")) is not int:
            raise ValueError("Malformed native screen scale or traits")
        values.append({"displayId": screen["displayId"], "isMain": screen["isMain"],
                       "scale": scale, "bounds": frame(screen["bounds"]), "traits": screen["traits"]})
    if len({value["displayId"] for value in values}) != len(values) or sum(value["isMain"] for value in values) != 1:
        raise ValueError("Duplicate displays or missing unique main display")
    after = read_json(unique(root, f"landing-{stage}-screens-after.json")).get("value")
    if after != screens:
        raise ValueError("Native screenInfo changed while the original phase screenshot was captured")
    status = read_json(unique(root, f"landing-{stage}-wda-status.json"))["value"]
    native_version = status["os"]["version"]
    def version(value):
        if not isinstance(value, str) or not re.fullmatch(r"[0-9]+(?:\.[0-9]+){0,2}", value):
            raise ValueError("Missing actual iOS runtime version")
        parts = tuple(int(part) for part in value.split("."))
        return parts + (0,) * (3 - len(parts))
    observed = version(native_version)
    capability_version = metadata.get("platform_version")
    if capability_version is not None and version(capability_version) != observed:
        raise ValueError("Driver platformVersion disagrees with actual WDA operating-system version")
    if not (observed[0] in (18, 26) or observed[:2] in ((27, 0), (27, 1))):
        raise ValueError("This iOS runtime is outside the tested matrix")
    if metadata["form"] == "duo" and observed[:2] != (27, 1):
        raise ValueError("Duo must use the actual pinned27.1 runtime")
    png_matches = [value for value in values
                   if abs(png.width - value["bounds"]["width"]) < 1e-6
                   and abs(png.height - value["bounds"]["height"]) < 1e-6]
    ax_matches = [value for value in values if all(
        abs(application[key] * value["scale"] - value["bounds"][key]) < 1e-6
        for key in application)]
    if len(png_matches) != 1 or len(ax_matches) != 1:
        raise ValueError("PNG/native Application do not independently match the actual runtime's unique screen contract")
    if png_matches[0]["displayId"] != ax_matches[0]["displayId"]:
        raise Mismatch("PNG and AX refer to different real displays")
    screen = png_matches[0]
    if any(abs(value[key]) >= 1e-6 for value in (screen["bounds"], application) for key in ("x", "y")):
        raise ValueError("Full-screen landing capture requires zero native and Application origins; no offset inferred")
    return screen["scale"], {"contract": "WDA bounds pixels = PNG pixels = AX points × native scale",
        "bounds_unit": "pixels", "runtime_version": native_version, "driver_platform_version": capability_version,
        "screen": screen, "application": application,
        "png_residual_pixels": [abs(png.width - screen["bounds"]["width"]),
                                abs(png.height - screen["bounds"]["height"])],
        "ax_residual_native_units": {key: abs(application[key] * screen["scale"]
                                               - screen["bounds"][key]) for key in application}}



def ios18_provider(root, sequence, label, identity, mapping, device_udid, stage, png):
    if mapping.get("runtime_version", "").split(".")[0] != "18":
        return None
    if not isinstance(identity, dict) or identity.get("provider") != "simctl_internal":
        raise ValueError("Actual iOS18 landing captures require complete native provider provenance")
    if identity.get("actual_udid") != device_udid:
        raise ValueError("Actual screenshot session UDID differs from the native landing session")
    path = Path(__file__).with_name("capture_ios_landing_screenshot.py")
    spec = importlib.util.spec_from_file_location("landing_original_native_provider", path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    actual = module.verify_saved_capture(root, sequence, label, identity, png)
    if actual["screen"] != mapping["screen"]:
        raise ValueError("Screenshot provider and independently verified AX/PNG refer to different actual screens")
    screens = read_json(unique(root, f"landing-{stage}-screens.json"))
    if actual["wda_session_id"] != screens.get("sessionId"):
        raise ValueError("Screenshot provider belongs to another actual WDA session")
    return actual


def rectangle(f, scale, png):
    left, top = math.floor(f["x"] * scale), math.floor(f["y"] * scale)
    right, bottom = math.ceil((f["x"] + f["width"]) * scale), math.ceil((f["y"] + f["height"]) * scale)
    if not (0 <= left < right <= png.width and 0 <= top < bottom <= png.height):
        raise ValueError("Reported native frame is outside the original PNG")
    return left, top, right, bottom


def luminance(rgb):
    channels = [v / 255 for v in rgb]
    values = [v / 12.92 if v <= .04045 else ((v + .055) / 1.055) ** 2.4 for v in channels]
    return sum(weight * value for weight, value in zip((.2126, .7152, .0722), values))


def contrast(a, b):
    a, b = luminance(a), luminance(b)
    return (max(a, b) + .05) / (min(a, b) + .05)


def check_background(png, rect, expected, excluded=()):
    left, top, right, bottom = rect
    stride = max(1, int(math.sqrt((right - left) * (bottom - top) / 16000)))
    counts = Counter(png.pixel(x, y) for y in range(top, bottom, stride) for x in range(left, right, stride)
                     if not any(a <= x < c and b <= y < d for a, b, c, d in excluded))
    if not counts:
        raise ValueError("No unoccluded background samples inside actual native frame")
    actual, count = counts.most_common(1)[0]
    fraction = count / sum(counts.values())
    if fraction < .70 or any(abs(a - b) > 2 for a, b in zip(actual, expected)):
        raise Mismatch(f"Landing background is {actual}, expected {expected}; dominant fraction={fraction:.3f}")
    return {"rgb": actual, "fraction": fraction, "samples": sum(counts.values()), "stride": stride}


def check_foreground(png, rect, background, minimum_contrast):
    left, top, right, bottom = rect
    counts = Counter(png.pixel(x, y) for y in range(top, bottom) for x in range(left, right))
    colors = {rgb for rgb in counts if contrast(rgb, background) >= minimum_contrast}
    count = sum(counts[rgb] for rgb in colors)
    minimum = max(8, math.ceil((right - left) * (bottom - top) * .01))
    rows, columns = set(), set()
    for y in range(top, bottom):
        for x in range(left, right):
            if png.pixel(x, y) in colors:
                rows.add(y)
                columns.add(x)
    if count < minimum or len(rows) < 3 or len(columns) < 3:
        raise Mismatch(f"Visible icon/label foreground missing: {count} pixels meet contrast {minimum_contrast}, required {minimum}")
    return {"contrast_threshold": minimum_contrast, "foreground_pixels": count, "minimum_pixels": minimum,
            "occupied_rows": len(rows), "occupied_columns": len(columns)}


def analyze(root, stage, PNG):
    metadata_path = unique(root, f"landing-{stage}.json")
    data = read_json(metadata_path)
    platform, form = data.get("platform"), data.get("form")
    if data.get("window_size") != data.get("window_size_after"):
        raise ValueError("Native session window size changed during phase capture")
    if data.get("stage") != stage or not (platform == "android" and form == "compact"
                                         or platform == "ios" and form in ("compact", "duo")):
        raise ValueError("Unexpected landing platform/form/stage")
    png_path = unique(root, f"{STAGES[stage]:02}-landing-theme-{stage}.png")
    xml_path = unique(root, f"landing-{stage}.xml")
    if png_path.parent.name != f"{platform}-{form}" or png_path.parent.parent.name != "shots":
        raise ValueError("Landing PNG must retain its original platform/form artifact directory")
    if png_path.parent.parent.parent != metadata_path.parent or xml_path.parent != metadata_path.parent:
        raise ValueError("Landing PNG, XML and API metadata are not in the same artifact")
    png = PNG(png_path)
    source = ET.parse(xml_path).getroot()
    if platform == "android":
        size = data["window_size"]
        if png.width != number(size["width"]) or png.height != number(size["height"]):
            raise ValueError("Android original PNG dimensions differ from the native session window; no scale inferred")
        scale, mapping = 1, {"contract": "native Android AX/API screen pixels; PNG equals actual window size", "scale": 1}
    else:
        scale, mapping = ios_mapping(root, stage, source, png, data)
    provider = None
    if platform == "ios" and form == "compact":
        provider = ios18_provider(root, STAGES[stage], f"landing-theme-{stage}",
            data.get("screenshot_source"), mapping, data.get("device_udid"), stage, png)
    api = data["elements"]
    api_after = data["elements_after"]
    source_after = ET.parse(unique(root, f"landing-{stage}-after.xml")).getroot()
    if platform == "ios":
        _, after_mapping = ios_mapping(root, stage, source_after, png, data)
        if mapping["application"] != after_mapping["application"]:
            raise ValueError("Native Application frame changed while capturing the phase")
    nodes, frames = {}, {}
    parents = {child: parent for parent in source.iter() for child in parent}
    for identifier in MARKERS:
        node = marker(source, identifier, platform)
        actual = xml_frame(node, platform)
        recorded = api[identifier]
        after_node = marker(source_after, identifier, platform)
        after_frame = xml_frame(after_node, platform)
        recorded_after = api_after[identifier]
        if actual != after_frame or frame(recorded) != frame(recorded_after):
            raise ValueError(f"Native AX/API geometry changed during phase capture for {identifier}")
        if recorded_after.get("displayed") is not True or recorded_after.get("enabled") is not True:
            raise Mismatch(f"Native API {identifier} became hidden/disabled during capture")
        if any(abs(after_frame[key] - frame(recorded_after)[key]) > 1 for key in after_frame):
            raise ValueError(f"After-capture API and AX frames disagree for {identifier}")
        if recorded.get("displayed") is not True or recorded.get("enabled") is not True:
            raise Mismatch(f"Native API {identifier} is not displayed and enabled")
        if any(abs(actual[key] - frame(recorded)[key]) > 1 for key in actual):
            raise ValueError(f"API and AX frames disagree for {identifier}")
        if platform == "android" and node.get("package") not in (None, BUNDLE):
            raise Mismatch("Android AX marker belongs to another app")
        nodes[identifier], frames[identifier] = node, actual
    rects = {identifier: rectangle(value, scale, png) for identifier, value in frames.items()}
    for row in ROWS:
        if not contains(frames[BODY], frames[row]) or not inside_tree(nodes[row], nodes[BODY], parents):
            raise ValueError("Landing row is not inside its actual native page body")
        # Do not force the tappable layout into an accessibility grouping that
        # can hide its children. The contained real title is the readable label.
        for suffix in ("-icon", "-title"):
            child = row + suffix
            if suffix == "-title":
                expected_title = "音乐" if row == "landing-music" else "相册"
                if api[child].get("text") != expected_title or api_after[child].get("text") != expected_title:
                    raise Mismatch(f"Native API title does not equal actual child title {expected_title}")
                if any(expected_title not in [node.get(key) for key in ("text", "label", "value")]
                       for node in (nodes[child], marker(source_after, child, platform))):
                    raise Mismatch(f"Before/after AX title does not equal actual child title {expected_title}")
            if not contains(frames[row], frames[child]) or not inside_tree(nodes[child], nodes[row], parents):
                raise ValueError("Landing icon/title is not inside its native row")
    expected = EXPECTED_BG[stage]
    background = check_background(png, rects[BODY], expected, [rects[row] for row in ROWS])
    checks = {}
    for row in ROWS:
        row_bg = check_background(png, rects[row], expected, [rects[row + "-icon"], rects[row + "-title"]])
        checks[row] = {"readable_label_source": "contained native title (API and before/after AX)",
                       "readable_label": api[row + "-title"]["text"], "background": row_bg,
                       "icon": check_foreground(png, rects[row + "-icon"], row_bg["rgb"], 3),
                       "title": check_foreground(png, rects[row + "-title"], row_bg["rgb"], 4.5)}
    return {"stage": stage, "color_status": "match", "platform": platform, "form": form,
            "device_udid": data.get("device_udid"), "png": str(png_path), "xml": str(xml_path),
            "png_size": [png.width, png.height], "mapping": mapping, "background": background,
            "native_frames": frames, "pixel_rectangles": rects, "geometry_stable_before_after": True,
            "screenshot_source": data.get("screenshot_source"), "native_provider": provider,
            "checks": {"page_background": background, "rows": checks}, "rows": checks}


def navigation_mapping(source, platform, dark, png):
    if [png.width, png.height] != dark["png_size"]:
        raise ValueError("Original dark navigation screenshots changed display dimensions")
    if platform == "android":
        windows = [node for node in source if node.get("package") == BUNDLE
                   and node.get("displayed") == "true"]
        if len(windows) != 1:
            raise ValueError("Navigation requires one native application window")
        window = xml_frame(windows[0], platform)
        if rectangle(window, 1, png) != (0, 0, png.width, png.height):
            raise ValueError("Navigation AX window does not match the verified screen-pixel contract")
        return 1, windows[0]
    apps = [node for node in source.iter() if node.tag == "XCUIElementTypeApplication"
            and node.get("visible") == "true"]
    if len(apps) != 1 or apps[0].get("bundleId") not in (None, BUNDLE):
        raise ValueError("Navigation requires one visible native application")
    if xml_frame(apps[0], platform) != dark["mapping"]["application"]:
        raise ValueError("Navigation Application frame differs from the independently verified dark display")
    scale = dark["mapping"]["screen"]["scale"]
    if rectangle(xml_frame(apps[0], platform), scale, png) != (0, 0, png.width, png.height):
        raise ValueError("Navigation AX and PNG do not match the verified native screen scale")
    return scale, apps[0]


def child_background(source, platform, scale, png, counter):
    # Locate the real shared page container of the sample image and clicked
    # button. Exclude their native frames, whose authored colors are not the
    # page background; do not infer a crop from image colors or screen ratios.
    description = "dot net bot in a submarine number ten"
    images = [node for node in source.iter()
              if node.tag in ("android.widget.ImageView", "XCUIElementTypeImage")
              and node.get("visible", node.get("displayed", "true")) == "true"
              and description in (node.get("content-desc"), node.get("label"), node.get("name"))]
    if len(images) != 1:
        raise ValueError("The clicked child requires one visible native sample image")
    image = images[0]
    parents = {child: node for node in source.iter() for child in node}
    ancestors, current = set(), counter
    while current in parents:
        current = parents[current]
        ancestors.add(current)
    body = image
    while body not in ancestors:
        if body not in parents:
            raise ValueError("Child image and counter have no shared native page container")
        body = parents[body]
    actual = xml_frame(body, platform)
    excluded = []
    for node in (image, counter):
        native = xml_frame(node, platform)
        if not contains(actual, native):
            raise ValueError("Child content is outside its actual native page container")
        excluded.append(rectangle(native, scale, png))
    rect = rectangle(actual, scale, png)
    return {"native_frame": actual, "pixel_rectangle": rect,
            "excluded_native_content_rectangles": excluded,
            "background": check_background(png, rect, EXPECTED_BG["dark"], excluded)}


def returned_appearance(source, platform, scale, png):
    parents = {child: node for node in source.iter() for child in node}
    body = marker(source, BODY, platform)
    rows = {row: marker(source, row, platform) for row in ROWS}
    actual = xml_frame(body, platform)
    rects = {row: rectangle(xml_frame(node, platform), scale, png) for row, node in rows.items()}
    checks = {}
    for row, node in rows.items():
        row_frame = xml_frame(node, platform)
        if not contains(actual, row_frame) or not inside_tree(node, body, parents):
            raise ValueError("Returned landing row is outside its actual native page body")
        children = {suffix: marker(source, row + suffix, platform) for suffix in ("-icon", "-title")}
        child_rects = {}
        for suffix, child in children.items():
            native = xml_frame(child, platform)
            if not contains(row_frame, native) or not inside_tree(child, node, parents):
                raise ValueError("Returned landing icon/title is outside its native row")
            child_rects[suffix] = rectangle(native, scale, png)
        expected = "音乐" if row == "landing-music" else "相册"
        if expected not in [children["-title"].get(key) for key in ("text", "label", "value")]:
            raise Mismatch("Returned landing lacks its actual child title")
        background = check_background(png, rects[row], EXPECTED_BG["dark"], child_rects.values())
        checks[row] = {"background": background,
                       "icon": check_foreground(png, child_rects["-icon"], background["rgb"], 3),
                       "title": check_foreground(png, child_rects["-title"], background["rgb"], 4.5),
                       "readable_label": expected}
    return {"page_background": check_background(png, rectangle(actual, scale, png),
                                                EXPECTED_BG["dark"], rects.values()), "rows": checks}


def verify_navigation(root, results, PNG):
    data = read_json(unique(root, "landing-navigation.json"))
    children = data.get("children")
    if data.get("theme") != "dark" or not isinstance(children, list) or len(children) != 2:
        raise Mismatch("Both dark child navigation/Back records are required")
    if {value.get("child") for value in children} != {"music", "photos"}:
        raise Mismatch("Dark navigation must visit Music and Photos exactly once")
    platform = results[0]["platform"]
    verified = {**data, "children": []}
    if platform == "ios" and results[1]["form"] == "compact" and results[1]["mapping"].get("runtime_version", "").split(".")[0] == "18":
        identity = results[1]["screenshot_source"]
        keys = ("run_id", "run_attempt", "head_sha", "actual_udid", "appium_session_id", "actual_platform_version")
        if any(any(result["screenshot_source"].get(key) != identity.get(key) for key in keys)
               or result["native_provider"]["wda_session_id"] != results[1]["native_provider"]["wda_session_id"] for result in results):
            raise ValueError("All original iOS18 phases must belong to the same source/run and native sessions")
    for entry in children:
        name = entry["child"]
        if entry.get("returned_to_landing") is not True:
            raise Mismatch(f"Actual Back from {name} has not completed")
        before, after = entry.get("counter_before"), entry.get("counter_after")
        if not isinstance(before, str) or not isinstance(after, str) or not after.strip() or before == after:
            raise Mismatch(f"No actual dark {name} child counter state change")
        child = ET.parse(unique(root, f"landing-dark-{name}-child.xml")).getroot()
        sequence = 43 if name == "music" else 45
        child_png = PNG(unique(root, f"{sequence:02}-landing-dark-{name}-child-clicked.png"))
        child_scale, child = navigation_mapping(child, platform, results[1], child_png)
        child_provider = None
        if platform == "ios" and results[1]["form"] == "compact":
            child_provider = ios18_provider(root, sequence, f"landing-dark-{name}-child-clicked",
                results[1].get("screenshot_source"), results[1]["mapping"], results[1].get("device_udid"), "dark", child_png)
        counter = marker(child, "counterBtn", platform)
        if after not in [counter.get(key) for key in ("text", "label", "value")]:
            raise Mismatch(f"{name} child AX does not contain its recorded changed counter text")
        returned = ET.parse(unique(root, f"landing-dark-{name}-returned.xml")).getroot()
        return_png = PNG(unique(root, f"{sequence + 1:02}-landing-dark-{name}-returned.png"))
        return_scale, returned = navigation_mapping(returned, platform, results[1], return_png)
        return_provider = None
        if platform == "ios" and results[1]["form"] == "compact":
            return_provider = ios18_provider(root, sequence + 1, f"landing-dark-{name}-returned",
                results[1].get("screenshot_source"), results[1]["mapping"], results[1].get("device_udid"), "dark", return_png)
        for row in ROWS:
            marker(returned, row, platform)
        if any(matches_id(node, "counterBtn") and node.get("visible", node.get("displayed", "true")) == "true"
               for node in returned.iter()):
            raise Mismatch(f"Back from {name} did not return to the landing page")
        verified["children"].append({**entry, "child_screenshot_provider": child_provider, "returned_screenshot_provider": return_provider,
            "child_dark_appearance": child_background(child, platform, child_scale, child_png, counter),
            "returned_dark_appearance": returned_appearance(returned, platform, return_scale, return_png)})
    return verified


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("artifact_root", type=Path)
    parser.add_argument("--strict", action="store_true")
    options = parser.parse_args()
    root, results, errors, mismatches = options.artifact_root.resolve(), [], [], []
    navigation = None
    try:
        PNG = png_reader()
        for stage in STAGES:
            try:
                results.append(analyze(root, stage, PNG))
            except Mismatch as error:
                results.append({"stage": stage, "color_status": "mismatch", "error": str(error)})
            except (OSError, ValueError, TypeError, KeyError, ET.ParseError, struct.error, zlib.error) as error:
                errors.append({"stage": stage, "error": str(error)})
        if not errors and all(value["color_status"] == "match" for value in results):
            if len({(value["platform"], value["form"], value["device_udid"]) for value in results}) != 1:
                raise ValueError("Theme phases do not belong to one selected native device")
            if results[0]["platform"] == "ios" and len({value["mapping"]["screen"]["displayId"] for value in results}) != 1:
                raise Mismatch("Landing default pose switched native displays between phases")
            navigation = verify_navigation(root, results, PNG)
    except Mismatch as error:
        mismatches.append({"scope": "navigation-or-captures", "error": str(error)})
    except (OSError, ValueError, TypeError, KeyError, ET.ParseError, struct.error, zlib.error) as error:
        errors.append({"scope": "navigation-or-captures", "error": str(error)})
    summary = {"analyses": len(results), "matches": sum(value["color_status"] == "match" for value in results),
               "mismatches": len(mismatches) + sum(value["color_status"] == "mismatch" for value in results),
               "errors": len(errors)}
    print(json.dumps({"scope": "group landing original theme pixels and native child/Back evidence",
                      "summary": summary, "results": results, "navigation": navigation,
                      "mismatches": mismatches, "errors": errors}, indent=2, ensure_ascii=False))
    return (2 if errors else 1 if summary["mismatches"] else 0) if options.strict else 0


if __name__ == "__main__":
    sys.exit(main())
