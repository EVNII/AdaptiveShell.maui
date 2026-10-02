#!/usr/bin/env python3
"""Duo-only CI: build installed WDA before boot and verify its session options.

Uses the exact source-reviewed resource6 build/manifest/native-SDK checks.
Runtime pruning supplies the existing bounded owned-process cleanup helpers.
This script never selects or replaces UI tests.
"""
from datetime import datetime, timezone
import argparse
import importlib.util
import json
import os
from pathlib import Path
import shutil
import subprocess
import time

RESULTS = Path("TestResults")


def event(stage, **details):
    RESULTS.mkdir(parents=True, exist_ok=True)
    value = json.dumps({"utc": datetime.now(timezone.utc).isoformat(), "stage": stage, **details})
    print(value, flush=True)
    with (RESULTS / "duo-wda-prebuild-events.jsonl").open("a", encoding="utf-8") as stream:
        stream.write(value + "\n")


# One implementation of owned-group cleanup, shared with the CI runtime tool.
_path = Path(__file__).resolve().with_name("duo-prune-runtime.py")
_spec = importlib.util.spec_from_file_location("duo_ci_process_helpers", _path)
_process = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(_process)
_process.event = event
start_owned = _process.start_owned
stop_group = _process.stop_group


def capture(command, filename, timeout=15):
    path = RESULTS / filename
    process = None
    code = 1
    event("native-capture-begin", command=command, timeout_seconds=timeout)
    try:
        with path.open("wb") as stream:
            process = start_owned(command, stdout=stream, stderr=subprocess.STDOUT)
            try:
                code = process.wait(timeout=timeout)
            except subprocess.TimeoutExpired:
                code = 124
        event("native-capture-end", file=filename, exit_code=code)
    except OSError as error:
        path.write_text(str(error), encoding="utf-8")
        event("native-capture-error", file=filename, error=str(error))
    finally:
        if process is not None:
            stop_group(process, filename, grace=1 if code == 124 else 0)
    return code


def verify_prebuilt_wda_configuration(require_session=False):
    expected = (Path(os.environ["RUNNER_TEMP"]).resolve() / "duo-wda")
    selected = os.environ.get("DUO_DIAGNOSTIC_WDA_DERIVED_DATA_PATH")
    if (os.environ.get("DUO_DIAGNOSTIC_USE_PREBUILT_WDA") != "true"
            or selected != str(expected)):
        raise ValueError("Duo prebuilt-WDA opt-in and exact derivedDataPath are required")
    evidence = json.loads((RESULTS / "duo-wda-prebuild.json").read_text())
    if (evidence.get("status") != "verified"
            or evidence.get("derived_data_path") != selected
            or evidence.get("device_udid") != os.environ.get("DUO_DEVICE_UDID")
            or evidence.get("wda_version") != "16.12.11"
            or evidence.get("xcuitest_version") != "12.13.3"):
        raise ValueError("Prebuilt WDA identity/path does not match this Duo session")
    if not Path(evidence["runner_app"]).is_dir() or not all(
            Path(path).is_file() for path in evidence["xctestrun_files"]):
        raise ValueError("Prebuilt WDA products are missing")
    if require_session:
        # The Duo opt-in C# hook must save the options actually supplied to
        # new IOSDriver before the constructor is allowed to start the session.
        caps = json.loads((RESULTS / "duo-wda-session-capabilities.json").read_text())
        if (not isinstance(caps, dict)
                or caps.get("appium:usePrebuiltWDA") is not True
                or caps.get("appium:derivedDataPath") != selected
                or caps.get("appium:usePreinstalledWDA", False) is not False
                or caps.get("appium:useSimpleBuildTest", False) is not False):
            raise ValueError("Actual session options do not use the exact prebuilt WDA")
    event("prebuilt-wda-configuration-verified", derived_data_path=selected,
          actual_session_options=require_session)


def prebuild_wda():
    """Build the installed WDA once, while the verified Duo is still Shutdown."""
    import plistlib
    import re

    RESULTS.mkdir(parents=True, exist_ok=True)
    evidence = {"scope": "Duo CI prebuilt startup identity; UI assertions remain the quality gate",
                "status": "preparing", "build_budget_seconds": 780}
    evidence_file = RESULTS / "duo-wda-prebuild.json"

    def save():
        evidence_file.write_text(json.dumps(evidence, indent=2), encoding="utf-8")

    def run(command, filename, seconds):
        code = capture(command, filename, seconds)
        if code:
            raise ValueError(f"{filename} failed with exit {code}")
        return (RESULTS / filename).read_text(encoding="utf-8")

    def shutdown_snapshot(stage, udid):
        value = json.loads(run(["xcrun", "simctl", "list", "devices", "--json"],
                               f"duo-wda-devices-{stage}.json", 20))
        items = value.get("devices", {}).get("com.apple.CoreSimulator.SimRuntime.iOS-27-1", [])
        matching = [item for item in items if isinstance(item, dict) and item.get("udid") == udid]
        if (len(matching) != 1 or matching[0].get("state") != "Shutdown"
                or matching[0].get("isAvailable") is not True
                or matching[0].get("deviceTypeIdentifier") != "com.apple.CoreSimulator.SimDeviceType.iPhone-Duo"):
            raise ValueError(f"Exact Duo is not available and Shutdown {stage} WDA build")
        return matching[0]["state"]

    try:
        if (os.environ.get("GITHUB_ACTIONS") != "true"
                or os.environ.get("RUNNER_OS") != "macOS"
                or os.environ.get("GITHUB_JOB") != "uitest-ios-27-1-duo"):
            raise ValueError("WDA prebuild is only allowed in this dedicated Duo CI job")
        environment = json.loads((RESULTS / "duo-environment.json").read_text())
        udid = os.environ["DUO_DEVICE_UDID"]
        if (environment.get("status") != "verified"
                or environment.get("device", {}).get("udid") != udid
                or environment.get("sdk", {}).get("version") != "27.1"
                or environment.get("xcode", {}).get("version") != "27.1"
                or environment.get("runtime", {}).get("buildversion") != "24A94401"):
            raise ValueError("Missing exact Duo/Xcode/runtime environment verification")
        derived = Path(os.environ["RUNNER_TEMP"]).resolve() / "duo-wda"
        if derived.exists():
            raise ValueError("Unique Duo WDA derivedDataPath already exists; refusing reuse")
        derived.mkdir()
        evidence.update(device_udid=udid, derived_data_path=str(derived))
        save()
        # Resolve the dependency from the actual installed XCUITest package;
        # never use a downloaded substitute or another WDA checkout.
        node_script = r"""
        const p=require('path'),f=require('fs'),o=require('os');
        const a=process.env.APPIUM_HOME||p.join(o.homedir(),'.appium');
        const d=p.join(a,'node_modules','appium-xcuitest-driver');
        const q=require.resolve('appium-webdriveragent/package.json',{paths:[d]});
        const w=JSON.parse(f.readFileSync(q,'utf8'));
        const x=JSON.parse(f.readFileSync(p.join(d,'package.json'),'utf8'));
        process.stdout.write(JSON.stringify({wda_version:w.version,xcuitest_version:x.version,
          package_json:q,project:p.join(p.dirname(q),'WebDriverAgent.xcodeproj')}));
        """
        installed = json.loads(run(["node", "-e", node_script], "duo-wda-installed-identity.json", 15))
        if installed.get("wda_version") != "16.12.11" or installed.get("xcuitest_version") != "12.13.3":
            raise ValueError("This source-reviewed candidate requires installed WDA16.12.11/XCUITest12.13.3")
        project = Path(installed["project"])
        if not project.is_absolute() or not project.is_dir():
            raise ValueError("Actual installed WebDriverAgent.xcodeproj is missing")
        evidence.update(installed)
        evidence["device_state_before"] = shutdown_snapshot("before", udid)
        command = ["xcodebuild", "build-for-testing", "-project", str(project),
                   "-scheme", "WebDriverAgentRunner", "-configuration", "Debug",
                   "-sdk", "iphonesimulator27.1", "-destination", f"id={udid}",
                   "-derivedDataPath", str(derived), "IPHONEOS_DEPLOYMENT_TARGET=27.1",
                   "GCC_TREAT_WARNINGS_AS_ERRORS=0", "COMPILER_INDEX_STORE_ENABLE=NO"]
        (RESULTS / "duo-wda-build-command.json").write_text(json.dumps({
            "command": command, "cwd": str(Path.cwd()), "timeout_seconds": 780,
            "scope": "build-for-testing only; exact Shutdown Duo destination",
        }, indent=2), encoding="utf-8")
        save()
        run(command, "duo-wda-build.log", 780)
        evidence["device_state_after"] = shutdown_snapshot("after", udid)
        products = derived / "Build" / "Products"
        runner = products / "Debug-iphonesimulator" / "WebDriverAgentRunner-Runner.app"
        bundle = runner / "PlugIns" / "WebDriverAgentRunner.xctest"
        xctestruns = sorted(products.glob("*.xctestrun"))
        if not runner.is_dir() or not bundle.is_dir() or not xctestruns:
            raise ValueError("Successful build lacks WDA Runner.app, test bundle, or xctestrun products")
        for item in xctestruns:
            shutil.copyfile(item, RESULTS / ("duo-wda-" + item.name))
            with item.open("rb") as stream:
                if not isinstance(plistlib.load(stream), dict):
                    raise ValueError("Generated xctestrun is unreadable")
        shutil.copyfile(runner / "Info.plist", RESULTS / "duo-wda-runner-info.plist")
        shutil.copyfile(bundle / "Info.plist", RESULTS / "duo-wda-test-info.plist")
        with (runner / "Info.plist").open("rb") as stream:
            runner_info = plistlib.load(stream)
        with (bundle / "Info.plist").open("rb") as stream:
            bundle_info = plistlib.load(stream)
        (RESULTS / "duo-wda-sdk-info.json").write_text(json.dumps({
            "runner_DTSDKName": runner_info.get("DTSDKName"),
            "test_bundle_DTSDKName": bundle_info.get("DTSDKName"),
            "test_bundle_CFBundleExecutable": bundle_info.get("CFBundleExecutable"),
        }, indent=2), encoding="utf-8")
        if bundle_info.get("DTSDKName") != "iphonesimulator27.1":
            raise ValueError("WDA test bundle was not built with Simulator SDK27.1")
        # Xcode supplies this Runner host precompiled. Preserve its raw
        # identity; only our compiled test bundle must use SDK27.1.
        executable = bundle_info.get("CFBundleExecutable")
        if not isinstance(executable, str) or Path(executable).name != executable:
            raise ValueError("WDA test bundle executable is missing")
        native = run(["xcrun", "vtool", "-show-build", str(bundle / executable)],
                     "duo-wda-native-build.txt", 20)
        sdks = re.findall(r"(?m)^\s*sdk\s+(\S+)\s*$", native)
        if not sdks or set(sdks) != {"27.1"}:
            raise ValueError("Native WDA test bundle SDK is not exactly27.1")
        evidence.update(status="verified", runner_app=str(runner), test_bundle=str(bundle),
                        xctestrun_files=[str(item) for item in xctestruns], native_sdks=sdks,
                        session_capabilities={"appium:usePrebuiltWDA": True,
                                              "appium:derivedDataPath": str(derived)})
        save()
        # The Duo opt-in C# hook applies these only for ios + duo, writes actual
        # session options, and must reject partial opt-in before new IOSDriver.
        with Path(os.environ["GITHUB_ENV"]).open("a", encoding="utf-8") as stream:
            stream.write("DUO_DIAGNOSTIC_USE_PREBUILT_WDA=true\n")
            stream.write(f"DUO_DIAGNOSTIC_WDA_DERIVED_DATA_PATH={derived}\n")
        event("wda-prebuild-verified", **evidence)
        return 0
    except Exception as error:
        evidence.update(status="failed", error=str(error), error_type=type(error).__name__)
        save()
        event("wda-prebuild-failed", **evidence)
        return 1


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("action", choices=["build", "verify", "verify-session"])
    args = parser.parse_args()
    if (os.environ.get("GITHUB_ACTIONS") != "true"
            or os.environ.get("RUNNER_OS") != "macOS"
            or os.environ.get("GITHUB_JOB") != "uitest-ios-27-1-duo"):
        raise ValueError("Prebuilt WDA is allowed only in the dedicated public Duo CI job")
    if args.action == "build":
        return prebuild_wda()
    verify_prebuilt_wda_configuration(require_session=args.action == "verify-session")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (OSError, ValueError, RuntimeError, KeyError) as error:
        event("wda-prebuild-configuration-failed", error=str(error))
        raise SystemExit(1)
