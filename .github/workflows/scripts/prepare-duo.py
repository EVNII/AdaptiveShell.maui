#!/usr/bin/env python3
"""Prepare one verified iPhone Duo on the pinned iOS 27.1 beta runtime.

Select Xcode 27.1 and install runtime build 24A94401 before invoking this
script. It never selects a newer minor version or returns an empty matrix.
Usage: python3 prepare-duo.py --output TestResults/duo-environment.json
GITHUB_ENV receives DUO_DEVICE_UDID and DUO_DEVICE_NAME only after verification.
For local fixture checks, supply --github-env /path/to/a/temporary/env-file.
"""

import argparse
from datetime import datetime, timezone
import json
import os
from pathlib import Path
import re
import subprocess
import sys
from uuid import UUID


VERSION = "27.1"
BUILD = "24A94401"
RUNTIME = "com.apple.CoreSimulator.SimRuntime.iOS-27-1"
DEVICE_TYPE = "com.apple.CoreSimulator.SimDeviceType.iPhone-Duo"
DEVICE_NAME = "iPhone Duo"


def command(*args):
    completed = subprocess.run(args, capture_output=True, text=True, timeout=60)
    if completed.returncode:
        detail = completed.stderr.strip() or completed.stdout.strip()
        raise ValueError(f"{' '.join(args)} failed ({completed.returncode}): {detail}")
    value = completed.stdout.strip()
    if not value:
        raise ValueError(f"{' '.join(args)} returned no evidence")
    return value


def snapshot():
    data = json.loads(command("xcrun", "simctl", "list", "-j"))
    if not isinstance(data, dict):
        raise ValueError("simctl list must return an object")
    for key, kind in (("runtimes", list), ("devicetypes", list), ("devices", dict)):
        if not isinstance(data.get(key), kind):
            raise ValueError(f"simctl list is missing {key} evidence")
    return data


def pinned_runtime(data):
    matches = [runtime for runtime in data["runtimes"]
               if isinstance(runtime, dict) and runtime.get("identifier") == RUNTIME
               and runtime.get("version") == VERSION and runtime.get("buildversion") == BUILD]
    if len(matches) != 1:
        raise ValueError(f"Expected one iOS {VERSION} runtime build {BUILD}; found {len(matches)}")
    runtime = matches[0]
    if runtime.get("isAvailable") is not True:
        raise ValueError(f"Pinned iOS {VERSION} runtime is not available")
    supported = runtime.get("supportedDeviceTypes")
    if not isinstance(supported, list) or not any(
            isinstance(item, dict) and item.get("identifier") == DEVICE_TYPE for item in supported):
        raise ValueError("Pinned runtime does not explicitly support iPhone Duo")
    types = [item for item in data["devicetypes"]
             if isinstance(item, dict) and item.get("identifier") == DEVICE_TYPE]
    if len(types) != 1 or types[0].get("name") != DEVICE_NAME:
        raise ValueError("Expected one installed, exact iPhone Duo device type")
    # A freshly imported runtime can omit its devices key until first create.
    devices = data["devices"].get(RUNTIME, [])
    if not isinstance(devices, list):
        raise ValueError("Pinned runtime has no devices list")
    return runtime, types[0], devices


def valid_udid(value):
    if not isinstance(value, str) or not re.fullmatch(
            r"[0-9a-fA-F]{8}(?:-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}", value):
        raise ValueError("simctl did not provide a canonical device UDID")
    return str(UUID(value)).upper()


def toolchain():
    version_output = command("xcodebuild", "-version")
    match = re.fullmatch(r"Xcode ([0-9.]+)\s+Build version ([A-Za-z0-9]+)", version_output)
    if match is None or match[1] != VERSION:
        raise ValueError(f"Expected selected Xcode {VERSION}; got {version_output!r}")
    executable = Path(command("xcrun", "--find", "xcodebuild"))
    if not executable.is_absolute() or executable.parts[-3:] != ("usr", "bin", "xcodebuild"):
        raise ValueError("Cannot establish the selected Xcode developer directory")
    developer = executable.parents[2].resolve()
    sdk = {
        "canonical_name": "iphonesimulator" + VERSION,
        "version": command("xcrun", "--sdk", "iphonesimulator", "--show-sdk-version"),
        "build": command("xcrun", "--sdk", "iphonesimulator", "--show-sdk-build-version"),
        "path": command("xcrun", "--sdk", "iphonesimulator", "--show-sdk-path"),
    }
    if sdk["version"] != VERSION or not re.fullmatch(r"[A-Za-z0-9]+", sdk["build"]):
        raise ValueError("Selected iPhoneSimulator SDK version/build is missing or not 27.1")
    sdk_path = Path(sdk["path"])
    if not sdk_path.is_absolute() or not sdk_path.resolve().is_relative_to(developer):
        raise ValueError("Simulator SDK does not belong to the selected Xcode")
    return {"version": match[1], "build": match[2], "developer_directory": str(developer),
            "executable": str(executable), "version_output": version_output}, sdk


def prepare():
    xcode, sdk = toolchain()
    initial = snapshot()
    _, _, devices = pinned_runtime(initial)
    available = [device for device in devices if isinstance(device, dict)
                 and device.get("deviceTypeIdentifier") == DEVICE_TYPE
                 and device.get("isAvailable") is True]
    created = not available
    if available:
        available.sort(key=lambda device: (device.get("name") != DEVICE_NAME,
                                           str(device.get("udid", ""))))
        udid = valid_udid(available[0].get("udid"))
    else:
        udid = valid_udid(command("xcrun", "simctl", "create", DEVICE_NAME, DEVICE_TYPE, RUNTIME))

    # Always read back the exact device under the pinned runtime, including
    # after create; a successful command alone does not establish availability.
    observed = snapshot()
    runtime, device_type, devices = pinned_runtime(observed)
    selected = [device for device in devices if isinstance(device, dict)
                and isinstance(device.get("udid"), str) and device["udid"].upper() == udid]
    if len(selected) != 1:
        raise ValueError("Chosen Duo UDID is missing or ambiguous in the pinned runtime readback")
    device = selected[0]
    if device.get("isAvailable") is not True or device.get("deviceTypeIdentifier") != DEVICE_TYPE:
        raise ValueError("Chosen device is unavailable or is not the exact iPhone Duo device type")
    name = device.get("name")
    if not isinstance(name, str) or not name.strip() or "\n" in name or "\r" in name:
        raise ValueError("Chosen Duo has no safe, nonempty device name")
    return {
        "status": "verified",
        "observed_at_utc": datetime.now(timezone.utc).isoformat(),
        "expected": {"xcode_version": VERSION, "runtime_identifier": RUNTIME,
                     "runtime_version": VERSION, "runtime_build": BUILD,
                     "device_type_identifier": DEVICE_TYPE},
        "xcode": xcode, "sdk": sdk, "runtime": runtime,
        "device_type": device_type, "device": device, "created_device": created,
    }, udid, name


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, default=Path("TestResults/duo-environment.json"))
    parser.add_argument("--github-env", type=Path, default=os.environ.get("GITHUB_ENV"),
                        help="GitHub environment file (defaults to GITHUB_ENV)")
    options = parser.parse_args()
    try:
        if options.github_env is None:
            raise ValueError("GITHUB_ENV or --github-env is required to export the verified Duo")
        evidence, udid, name = prepare()
        options.output.parent.mkdir(parents=True, exist_ok=True)
        options.output.write_text(json.dumps(evidence, indent=2, ensure_ascii=False) + "\n",
                                  encoding="utf-8")
        with options.github_env.open("a", encoding="utf-8") as stream:
            stream.write(f"DUO_DEVICE_UDID={udid}\nDUO_DEVICE_NAME={name}\n")
        print(f"Verified iPhone Duo: {name} ({udid}), iOS {VERSION} build {BUILD}")
        print(f"Environment evidence: {options.output}")
        return 0
    except (OSError, ValueError, TypeError, KeyError, subprocess.TimeoutExpired) as error:
        print(f"Duo environment preparation failed: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
