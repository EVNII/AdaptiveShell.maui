#!/usr/bin/env python3
"""Verify the pinned Duo build and four default-pose captures using stdlib.

Usage: python3 verify-duo-evidence.py ARTIFACT_ROOT --strict
Exit 1 means contradictory evidence; exit 2 means missing, unreadable or
ambiguous evidence. This verifies the captured default pose, not fold coverage
or theme colors. WDA /wda/screens supplies displayId/isMain/scale/bounds/traits.
The captured iOS 27.1 XCUIScreen contract reports bounds in PIXELS. PNG
dimensions must equal those pixel bounds, while the
full application AX frame in POINTS multiplied by the reported screen scale
must equal the same bounds. These independent matches must identify the SAME
unique displayId. Residuals are in pixels. No point-bounds schema fallback,
guessed scale, crop, main-screen fallback or rotated bounds are accepted.
"""

import argparse
import json
import math
from pathlib import Path
import re
import struct
import sys
from uuid import UUID
import xml.etree.ElementTree as ET
import zlib


VERSION = "27.1"
BUILD = "24A94401"
RUNTIME = "com.apple.CoreSimulator.SimRuntime.iOS-27-1"
DEVICE_TYPE = "com.apple.CoreSimulator.SimDeviceType.iPhone-Duo"
MODEL = "iPhone19,4"
BUNDLE_ID = "com.companyname.exampleashellapp"
STAGES = {
    "launch": ("duo-launch.xml", ("01-launch.png", "1-launch.png")),
    "light": ("duo-light.xml", ("30-theme-light.png",)),
    "dark": ("duo-dark.xml", ("31-theme-dark.png",)),
    "light-restored": ("duo-light-restored.xml", ("32-theme-light-restored.png",)),
}


class EvidenceError(ValueError):
    """Required evidence cannot be read or interpreted uniquely."""


class Mismatch(ValueError):
    """Readable evidence contradicts the required device/build/capture."""


def require(value, expected, label):
    if value is None:
        raise EvidenceError(f"Missing {label}")
    if value != expected or type(value) is not type(expected):
        raise Mismatch(f"{label}: expected {expected!r}; observed {value!r}")


def unique(root, *names):
    candidates = sorted({p for name in names for p in root.rglob(name) if p.is_file()})
    if len(candidates) != 1:
        raise EvidenceError(f"Expected one {' or '.join(names)}; found {len(candidates)}")
    return candidates[0]


def read_text(path):
    try:
        value = path.read_text(encoding="utf-8-sig").strip()
    except (OSError, UnicodeError) as error:
        raise EvidenceError(f"Cannot read {path.name}: {error}") from error
    if not value:
        raise EvidenceError(f"Empty {path.name}")
    return value


def read_json(path):
    try:
        return json.loads(read_text(path))
    except json.JSONDecodeError as error:
        raise EvidenceError(f"Invalid JSON in {path.name}: {error}") from error


def object_value(value, label):
    if not isinstance(value, dict):
        raise EvidenceError(f"Missing object {label}")
    return value


def number(value, label, positive=False):
    if isinstance(value, bool) or not isinstance(value, (int, float)) or not math.isfinite(value):
        raise EvidenceError(f"{label} must be a finite number")
    if positive and value <= 0:
        raise EvidenceError(f"{label} must be positive")
    return float(value)


def frame(value, label):
    value = object_value(value, label)
    return {key: number(value.get(key), f"{label}.{key}", key in ("width", "height"))
            for key in ("x", "y", "width", "height")}


def xml_frame(node, label):
    try:
        value = {key: float(node.attrib[key]) for key in ("x", "y", "width", "height")}
    except (KeyError, ValueError) as error:
        raise EvidenceError(f"{label} has no usable AX bounds") from error
    return frame(value, label)


def environment(root):
    path = unique(root, "duo-environment.json")
    data = object_value(read_json(path), path.name)
    require(data.get("status"), "verified", "prepare-duo status")
    expected = object_value(data.get("expected"), "expected")
    for key, value in (("xcode_version", VERSION), ("runtime_identifier", RUNTIME),
                       ("runtime_version", VERSION), ("runtime_build", BUILD),
                       ("device_type_identifier", DEVICE_TYPE)):
        require(expected.get(key), value, "expected." + key)
    xcode = object_value(data.get("xcode"), "xcode")
    sdk = object_value(data.get("sdk"), "sdk")
    require(xcode.get("version"), VERSION, "Xcode version")
    require(sdk.get("version"), VERSION, "SDK version")
    require(sdk.get("canonical_name"), "iphonesimulator" + VERSION, "SDK canonical name")
    runtime = object_value(data.get("runtime"), "runtime")
    for key, value in (("identifier", RUNTIME), ("version", VERSION),
                       ("buildversion", BUILD), ("isAvailable", True)):
        require(runtime.get(key), value, "runtime." + key)
    supported = runtime.get("supportedDeviceTypes")
    if not isinstance(supported, list):
        raise EvidenceError("Missing runtime.supportedDeviceTypes")
    if sum(isinstance(item, dict) and item.get("identifier") == DEVICE_TYPE
           for item in supported) != 1:
        raise Mismatch("Pinned runtime must explicitly support the exact Duo device type once")
    device_type = object_value(data.get("device_type"), "device_type")
    for key, value in (("identifier", DEVICE_TYPE), ("name", "iPhone Duo"),
                       ("modelIdentifier", MODEL), ("productFamily", "iPhone")):
        require(device_type.get(key), value, "device_type." + key)
    device = object_value(data.get("device"), "device")
    require(device.get("deviceTypeIdentifier"), DEVICE_TYPE, "device.deviceTypeIdentifier")
    require(device.get("isAvailable"), True, "device.isAvailable")
    udid = device.get("udid")
    if not isinstance(udid, str) or not re.fullmatch(
            r"[0-9a-fA-F]{8}(?:-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}", udid):
        raise EvidenceError("Selected device has no canonical UUID UDID")
    udid = str(UUID(udid)).upper()
    require(read_text(unique(root, "duo-model.txt")), MODEL, "Booted simulator model")
    build_info = read_text(unique(root, "duo-build-info.txt"))
    sdk_names = re.findall(r'["\']?DTSDKName["\']?\s*(?:=>|=|:)\s*["\']?([^\s"\'<>]+)', build_info)
    if not sdk_names:
        raise EvidenceError("duo-build-info.txt has no readable DTSDKName")
    if sdk_names != ["iphonesimulator" + VERSION]:
        raise Mismatch(f"Built Info.plist must contain exactly DTSDKName=iphonesimulator{VERSION}; got {sdk_names}")
    native = read_text(unique(root, "duo-native-build.txt"))
    native_sdks = re.findall(r"^\s*sdk\s+([0-9.]+)\s*$", native, re.MULTILINE)
    if not native_sdks:
        raise EvidenceError("duo-native-build.txt has no native LC_BUILD_VERSION sdk")
    if any(value != VERSION for value in native_sdks):
        raise Mismatch(f"Every native binary slice must use SDK {VERSION}; got {native_sdks}")
    active_app = object_value(read_json(unique(root, "duo-active-app.json")), "active application")
    # mobile: activeAppInfo returns the value object directly; retain support
    # for an unmodified WDA HTTP response if that is saved instead.
    if isinstance(active_app.get("value"), dict):
        active_app = active_app["value"]
    require(active_app.get("bundleId"), BUNDLE_ID, "WDA active application bundleId")
    # prepare-duo itself asserts this UDID is unique in the exact runtime's
    # simctl readback. Its schema stores that selected object, not the raw list.
    return {"status": "verified", "json": str(path), "udid": udid,
            "model": MODEL, "runtime": RUNTIME, "runtime_version": VERSION,
            "runtime_build": BUILD, "xcode_version": VERSION,
            "sdk_name": sdk_names[0], "native_sdks": native_sdks,
            "active_application": active_app,
            "udid_uniqueness_source": "prepare-duo exact-runtime readback assertion"}


def screens(root):
    path = unique(root, "duo-screens.json")
    response = object_value(read_json(path), path.name)
    values = response.get("value")
    if not isinstance(values, list) or not values:
        raise EvidenceError("WDA /wda/screens response.value must be a nonempty screens array")
    result = []
    for index, value in enumerate(values):
        value = object_value(value, f"screen[{index}]")
        display_id = value.get("displayId")
        if isinstance(display_id, bool) or not isinstance(display_id, int):
            raise EvidenceError("WDA screen.displayId must be an integer")
        if not isinstance(value.get("isMain"), bool):
            raise EvidenceError("WDA screen.isMain must be boolean")
        traits = value.get("traits")
        if isinstance(traits, bool) or not isinstance(traits, int):
            raise EvidenceError("WDA screen.traits must be an integer")
        result.append({"displayId": display_id, "isMain": value["isMain"],
                       "scale": number(value.get("scale"), "screen.scale", positive=True),
                       "bounds": frame(value.get("bounds"), "screen.bounds"),
                       "bounds_unit": "pixels", "traits": traits})
    if len({value["displayId"] for value in result}) != len(result):
        raise EvidenceError("WDA screens contain duplicate display IDs")
    if sum(value["isMain"] for value in result) != 1:
        raise EvidenceError("WDA screens must identify exactly one main screen")
    return result


def png_reader():
    helper = Path(__file__).resolve().parents[3] / "Tests/AdaptiveShell.UITests/Scripts/verify_mac_button_colors.py"
    if not helper.is_file():
        raise EvidenceError("Missing repository stdlib PNG reader")
    namespace = {"__name__": "duo_png_reader", "__file__": str(helper)}
    # Load the existing stdlib reader without creating another repository file.
    exec(compile(helper.read_text(encoding="utf-8"), str(helper), "exec"), namespace)
    return namespace["PNG"]


def tag(node):
    return node.tag.rsplit("}", 1)[-1]


def contains(outer, inner, tolerance=1):
    return (inner["x"] >= outer["x"] - tolerance and inner["y"] >= outer["y"] - tolerance
            and inner["x"] + inner["width"] <= outer["x"] + outer["width"] + tolerance
            and inner["y"] + inner["height"] <= outer["y"] + outer["height"] + tolerance)


def analyze(root, stage, screen_values, PNG):
    xml_name, png_names = STAGES[stage]
    xml_path, png_path = unique(root, xml_name), unique(root, *png_names)
    if png_path.parent.name != "ios-duo" or png_path.parent.parent.name != "shots":
        raise EvidenceError(f"{png_path.name} must be in shots/ios-duo")
    if png_path.parent.parent.parent != xml_path.parent:
        raise EvidenceError("PNG and AX must belong to the same artifact directory")
    source = ET.parse(xml_path).getroot()
    apps = [node for node in source.iter() if tag(node) == "XCUIElementTypeApplication"]
    if len(apps) != 1:
        raise EvidenceError(f"Expected one AX Application; found {len(apps)}")
    app = apps[0]
    if "bundleId" in app.attrib:
        require(app.get("bundleId"), BUNDLE_ID, "AX application bundleId")
    require(app.get("visible"), "true", "AX application visible")
    app_frame = xml_frame(app, "Application")
    if abs(app_frame["x"]) > 1 or abs(app_frame["y"]) > 1:
        raise EvidenceError("Application is not a full-screen origin; cropped captures are unsupported")
    parents = {child: parent for parent in source.iter() for child in parent}
    elements = {}
    for identifier in ("home", "home2", "media", "counterBtn"):
        matches = [node for node in app.iter()
                   if identifier in (node.get("identifier"), node.get("name"))
                   and node.get("visible") == "true"]
        if len(matches) != 1:
            raise EvidenceError(f"Expected one visible AX {identifier}; found {len(matches)}")
        node = matches[0]
        node_frame = xml_frame(node, identifier)
        if not contains(app_frame, node_frame):
            raise Mismatch(f"AX {identifier} is outside the captured application")
        ancestor = parents.get(node)
        while ancestor is not None and tag(ancestor) != "XCUIElementTypeWindow":
            ancestor = parents.get(ancestor)
        if ancestor is None or not contains(xml_frame(ancestor, "Window"), node_frame):
            raise EvidenceError(f"AX {identifier} has no containing native Window")
        elements[identifier] = {"frame": node_frame, "type": tag(node),
                                "label": node.get("label"), "enabled": node.get("enabled")}
    png = PNG(png_path)
    png_candidates, ax_candidates = [], []
    for screen in screen_values:
        bounds, scale = screen["bounds"], screen["scale"]
        if (abs(png.width - bounds["width"]) <= 1
                and abs(png.height - bounds["height"]) <= 1):
            png_candidates.append(screen)
        if all(abs(app_frame[key] * scale - bounds[key]) <= 1
               for key in ("x", "y", "width", "height")):
            ax_candidates.append(screen)
    if not png_candidates or not ax_candidates:
        raise Mismatch("PNG pixels and AX points do not both match a real WDA screen at its native scale")
    if len(png_candidates) != 1 or len(ax_candidates) != 1:
        raise EvidenceError("PNG/AX dimensions cannot independently identify one unique WDA screen")
    require(png_candidates[0]["displayId"], ax_candidates[0]["displayId"], "PNG versus AX displayId")
    screen = png_candidates[0]
    return {"stage": stage, "evidence_status": "match", "xml": str(xml_path),
            "png": str(png_path), "png_size": [png.width, png.height],
            "application_frame": app_frame, "elements": elements,
            "screen": screen,
            "mapping": "WDA pixel bounds = PNG pixels = full-screen AX points × reported WDA scale",
            "coordinate_units": {"screen_bounds": "pixels", "png_size": "pixels",
                                 "application_frame": "points", "elements": "points",
                                 "residuals": "pixels"},
            "dimension_residual_pixels": [abs(png.width - screen["bounds"]["width"]),
                                          abs(png.height - screen["bounds"]["height"])],
            "application_residual_pixels": {
                key: abs(app_frame[key] * screen["scale"] - screen["bounds"][key])
                for key in ("x", "y", "width", "height")}}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("artifact_root", type=Path)
    parser.add_argument("--strict", action="store_true")
    options = parser.parse_args()
    root = options.artifact_root.resolve()
    results, errors, mismatches = [], [], []
    verified_environment, screen_values = None, []
    try:
        if not root.is_dir():
            raise EvidenceError("artifact_root must be an existing directory")
        verified_environment = environment(root)
        screen_values = screens(root)
        PNG = png_reader()
    except Mismatch as error:
        mismatches.append({"scope": "environment", "error": str(error)})
    except (OSError, ValueError, TypeError, KeyError) as error:
        errors.append({"scope": "environment", "error": str(error)})
    if not errors and not mismatches:
        for stage in STAGES:
            try:
                results.append(analyze(root, stage, screen_values, PNG))
            except Mismatch as error:
                results.append({"stage": stage, "evidence_status": "mismatch", "error": str(error)})
            except (OSError, ValueError, KeyError, TypeError, ET.ParseError, zlib.error, struct.error) as error:
                errors.append({"stage": stage, "error": str(error)})
        matched = [value for value in results if value["evidence_status"] == "match"]
        # A default-pose test must stay on its observed display; identical PNG
        # dimensions alone are insufficient if real display identities differ.
        if len({value["screen"]["displayId"] for value in matched}) > 1:
            mismatches.append({"scope": "captures", "error": "Default pose changed display IDs between stages"})
    summary = {"analyses": len(results),
               "matches": sum(value["evidence_status"] == "match" for value in results),
               "mismatches": len(mismatches) + sum(value["evidence_status"] == "mismatch" for value in results),
               "errors": len(errors)}
    print(json.dumps({"artifact_root": str(root), "scope": "Duo default-pose device/build/capture evidence",
                      "summary": summary, "environment": verified_environment,
                      "screens": screen_values, "results": results,
                      "mismatches": mismatches, "errors": errors}, indent=2, ensure_ascii=False))
    return (2 if errors else 1 if summary["mismatches"] else 0) if options.strict else 0


if __name__ == "__main__":
    sys.exit(main())
