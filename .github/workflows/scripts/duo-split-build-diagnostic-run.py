#!/usr/bin/env python3
"""Duo complete-suite diagnostic; all 12 tests and raw four-phase gate remain required.

The test source and Appium startup/retry timeouts remain unchanged. An external
watchdog bounds Appium readiness to 180s and the complete test process to 1200s.
Test/capture commands own process groups; the watchdog kills their descendants.
"""
from datetime import datetime, timezone
import json
import os
from pathlib import Path
import signal
import shutil
import shlex
import xml.etree.ElementTree as ET
import subprocess
import sys
import time

RESULTS = Path("TestResults")
LOG_OFFSETS = {}


def event(stage, **details):
    RESULTS.mkdir(parents=True, exist_ok=True)
    row = {"utc": datetime.now(timezone.utc).isoformat(), "stage": stage, **details}
    value = json.dumps(row, ensure_ascii=False)
    print(value, flush=True)
    with (RESULTS / "duo-startup-events.jsonl").open("a", encoding="utf-8") as stream:
        stream.write(value + "\n")


def flush_logs():
    for path in (Path("appium.log"), RESULTS / "duo-test-console.log"):
        if not path.exists():
            continue
        with path.open("rb") as stream:
            stream.seek(LOG_OFFSETS.get(str(path), 0))
            chunk = stream.read()
            LOG_OFFSETS[str(path)] = stream.tell()
        if chunk:
            # Keep complete logs in the artifact; bound each live excerpt.
            shown = chunk[-24000:].decode("utf-8", errors="replace")
            print(f"--- {path} new output ({len(chunk)} bytes) ---\n{shown}", flush=True)


def start_owned(command, **kwargs):
    process = subprocess.Popen(command, start_new_session=True, **kwargs)
    # Only process groups explicitly created by this helper are signal targets.
    process._duo_owned_pgid = process.pid
    return process


def signal_owned_group(process, sig):
    pgid = getattr(process, "_duo_owned_pgid", None)
    if pgid != process.pid or not isinstance(pgid, int) or pgid <= 1 or pgid == os.getpgrp():
        event("process-group-signal-refused", pid=process.pid, pgid=pgid)
        return "refused"
    try:
        os.killpg(pgid, sig)
        return "sent"
    except ProcessLookupError:
        return "absent"
    except PermissionError as error:
        event("process-group-permission-error", pgid=pgid, signal=int(sig), error=str(error))
        # Xcode can leave root-owned descendants in this explicitly created
        # group. CI-only, noninteractive sudo is bounded and never targets a
        # broad process name or another job's group.
        if os.environ.get("GITHUB_ACTIONS") == "true":
            name = signal.Signals(sig).name.removeprefix("SIG")
            try:
                completed = subprocess.run([
                    "sudo", "-n", "/bin/kill", "-s", name, "--", f"-{pgid}",
                ], capture_output=True, text=True, timeout=3)
                event("process-group-sudo-signal", pgid=pgid, signal=name,
                      exit_code=completed.returncode, stderr=completed.stderr[-2000:])
                return "sent" if completed.returncode == 0 else "failed"
            except (OSError, subprocess.TimeoutExpired) as sudo_error:
                event("process-group-sudo-error", pgid=pgid, signal=name, error=str(sudo_error))
        return "failed"
    except OSError as error:
        event("process-group-signal-error", pgid=pgid, signal=int(sig), error=str(error))
        return "failed"


def stop_group(process, label, grace=5):
    # Cleanup is diagnostic evidence: it must never prevent native capture or
    # the final summary, even if the runner denies process-group operations.
    if process is None:
        return
    event("stop-process-group", process=label, pgid=process.pid)
    try:
        status = signal_owned_group(process, signal.SIGTERM)
        if status in ("absent", "refused"):
            process.poll()
            return
        deadline = time.monotonic() + grace
        while time.monotonic() < deadline:
            try:
                os.killpg(process.pid, 0)
            except ProcessLookupError:
                process.poll()
                return
            except PermissionError:
                # The group can still exist with descendants of another uid;
                # continue to the bounded, owned-group KILL fallback.
                break
            time.sleep(min(0.1, max(0, deadline - time.monotonic())))
        signal_owned_group(process, signal.SIGKILL)
        try:
            process.wait(timeout=2)
        except subprocess.TimeoutExpired:
            event("process-reap-timeout", process=label, pid=process.pid)
    except Exception as error:
        event("process-group-cleanup-error", process=label, pid=process.pid,
              type=type(error).__name__, error=str(error))


def probe_json(url, output, timeout=2):
    try:
        # curl --max-time is a whole-transfer deadline, including a slow body.
        # This command has no background descendants; subprocess also bounds it.
        response = subprocess.run([
            "curl", "--fail", "--silent", "--show-error", "--max-time",
            str(timeout), url,
        ], capture_output=True, timeout=timeout)
        if response.returncode:
            raise ValueError(response.stderr.decode("utf-8", errors="replace"))
        if len(response.stdout) > 4 * 1024 * 1024:
            raise ValueError("Probe response too large")
        value = json.loads(response.stdout)
        (RESULTS / output).write_text(json.dumps(value, indent=2), encoding="utf-8")
        return value
    except (OSError, ValueError, subprocess.TimeoutExpired) as error:
        (RESULTS / output).write_text(json.dumps({"probe_error": str(error), "url": url}), encoding="utf-8")
        return None


def wait_ready(server, url, seconds, output="duo-appium-status.json"):
    deadline = time.monotonic() + seconds
    event("appium-readiness-begin", deadline_seconds=seconds, server_pid=None if server is None else server.pid)
    while time.monotonic() < deadline:
        if server is not None and server.poll() is not None:
            event("appium-exited-before-ready", exit_code=server.returncode)
            return False
        remaining = deadline - time.monotonic()
        status = probe_json(url, output, timeout=min(2, remaining))
        value = status.get("value") if isinstance(status, dict) else None
        if isinstance(value, dict) and value.get("ready") is True:
            event("appium-ready")
            flush_logs()
            return True
        flush_logs()
        time.sleep(min(2, max(0, deadline - time.monotonic())))
    event("appium-readiness-deadline", deadline_seconds=seconds)
    return False


def wait_test(process, seconds):
    deadline = time.monotonic() + seconds
    next_heartbeat = time.monotonic()
    event("test-begin", deadline_seconds=seconds, test_pid=process.pid,
          selection="complete assembly; no filter", expected_records=12)
    while process.poll() is None:
        remaining = deadline - time.monotonic()
        if remaining <= 0:
            event("test-deadline", deadline_seconds=seconds)
            stop_group(process, "dotnet-test")
            flush_logs()
            return 124
        if time.monotonic() >= next_heartbeat:
            event("test-running", remaining_seconds=round(remaining, 1),
                  log_tails=progress_tails())
            flush_logs()
            next_heartbeat = time.monotonic() + 15
        time.sleep(min(1, remaining))
    flush_logs()
    event("test-exit", exit_code=process.returncode)
    return process.returncode


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


def collect_native_evidence():
    RESULTS.mkdir(parents=True, exist_ok=True)
    capture(["ps", "-axo", "pid,ppid,pgid,etime,state,command"], "duo-host-processes.txt", 8)
    capture(["xcrun", "simctl", "list", "devices", "--json"], "duo-device-status.json", 8)
    udid = os.environ.get("DUO_DEVICE_UDID")
    if udid:
        capture(["xcrun", "simctl", "spawn", udid, "launchctl", "print", "system"],
                "duo-simulator-services.txt", 8)
        capture(["xcrun", "simctl", "spawn", udid, "launchctl", "list"],
                "duo-simulator-processes.txt", 8)
        # Independently capture native simulator pixels, without waiting for Appium.
        capture(["xcrun", "simctl", "io", udid, "screenshot", "--type=png",
                 str(RESULTS / "duo-native-startup.png")], "duo-native-screenshot-command.txt", 8)
    else:
        event("native-device-capture-skipped", reason="DUO_DEVICE_UDID not exported")
    probe_json("http://127.0.0.1:8100/status", "duo-wda-status.json", timeout=2)



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
        # The diagnostic-only C# hook must save the options actually supplied to
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
    evidence = {"scope": "optional Duo startup diagnostic; not a release quality gate",
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
                or os.environ.get("GITHUB_JOB") != "duo-startup-diagnostic"):
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
        # The C# diagnostic hook applies these only for ios + duo, writes actual
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


def progress_tails():
    tails = {}
    for path in (Path("appium.log"), RESULTS / "duo-test-console.log"):
        try:
            with path.open("rb") as stream:
                stream.seek(0, os.SEEK_END)
                size = stream.tell()
                stream.seek(max(0, size - 3000))
                tails[str(path)] = stream.read(3000).decode("utf-8", errors="replace")
        except OSError as error:
            tails[str(path)] = {"read_error": str(error)}
    return tails


def parse_owned_appium_identity(metadata, native_line, uid):
    pid = metadata.get("pid")
    if (type(pid) is not int or pid <= 1 or pid == os.getpgrp()
            or metadata.get("owned_process_group") != pid
            or metadata.get("start_new_session") is not True):
        raise ValueError("Missing explicit owned Appium process-group identity")
    command = metadata.get("command")
    if not isinstance(command, str) or not Path(command).is_absolute() or Path(command).name != "appium":
        raise ValueError("Recorded executable is not an absolute global Appium path")
    parts = native_line.strip().split(None, 8)
    if len(parts) != 9:
        raise ValueError("Native ps identity is missing or ambiguous")
    if int(parts[0]) != pid or int(parts[1]) != pid or int(parts[2]) != uid:
        raise ValueError("Native PID, PGID or owner does not match the recorded Appium leader")
    argv = shlex.split(parts[8])
    expected = {command, str(Path(command).resolve())}
    if not ((len(argv) == 2 and Path(argv[0]).name == "node" and argv[1] in expected)
            or (len(argv) == 1 and argv[0] in expected)):
        raise ValueError("Native command is not the exact recorded Appium executable")
    token = " ".join(parts[3:8])
    recorded = metadata.get("native_start_token")
    if recorded is not None and recorded != token:
        raise ValueError("Native process birth token changed; refusing reused PID")
    return {"pid": pid, "pgid": pid, "uid": uid, "command": parts[8], "native_start_token": token}


def native_appium_identity(metadata):
    pid = metadata.get("pid")
    if type(pid) is not int or pid <= 1:
        raise ValueError("Invalid Appium PID")
    completed = subprocess.run(
        ["ps", "-p", str(pid), "-o", "pid=,pgid=,uid=,lstart=,command="],
        capture_output=True, text=True, timeout=5,
        env={**os.environ, "LC_ALL": "C", "TZ": "UTC"})
    if completed.returncode != 0 or len(completed.stdout.strip().splitlines()) != 1:
        raise ValueError("Cannot establish one live native Appium process")
    return parse_owned_appium_identity(metadata, completed.stdout, os.getuid())


def stop_recorded_appium():
    """Only this CI-owned server; fresh PID/PGID/UID/argv/birth proof before each signal."""
    outcome = {"scope": "owned Appium cleanup only; no broad process-name signals"}
    try:
        if (os.environ.get("GITHUB_ACTIONS") != "true"
                or os.environ.get("RUNNER_OS") != "macOS"
                or os.environ.get("GITHUB_JOB") != "duo-startup-diagnostic"):
            raise ValueError("Recorded Appium cleanup is restricted to this diagnostic CI job")
        metadata = json.loads((RESULTS / "duo-appium-process.json").read_text())
        if not isinstance(metadata.get("native_start_token"), str):
            raise ValueError("No preboot native birth token; refusing cross-step PID cleanup")
        for sig in (signal.SIGTERM, signal.SIGKILL):
            proof = native_appium_identity(metadata)
            event("recorded-appium-signal-proof", signal=int(sig), **proof)
            # Reuse the D6 owned-group signal implementation, including its
            # bounded CI-only permission fallback, with an independently proved leader.
            class ProvedLeader:
                pass
            leader = ProvedLeader()
            leader.pid = proof["pid"]
            leader._duo_owned_pgid = proof["pgid"]
            status = signal_owned_group(leader, sig)
            outcome.update(status=status, pid=proof["pid"], last_signal=int(sig))
            if status in ("absent", "refused", "failed"):
                break
            if sig == signal.SIGTERM:
                time.sleep(1)
        event("recorded-appium-stop-complete", **outcome)
    except (OSError, ValueError, TypeError, subprocess.TimeoutExpired) as error:
        outcome.update(status="refused-or-already-exited", error=str(error))
        event("recorded-appium-stop-refused", **outcome)
    (RESULTS / "duo-appium-cleanup.json").write_text(json.dumps(outcome, indent=2), encoding="utf-8")


def verify_full_suite():
    expected_pass = {
        "Launch_ShowsAllTopLevelItems", "SelectItem_SwitchesContent", "Group_NavigatesToChildAndBack",
        "EveryNavItem_HasNonEmptyAccessibilityLabel", "NavItemIdentifiers_AreStableAcrossNavigation",
        "Chrome_MatchesExpectedFormFactor", "DarkMode_ShellAndGroupFlow",
    }
    expected_skip = {
        "WindowButtons_AreContainedInSidebar", "Button_EnabledAndClickableAcrossLightDarkLight",
        "WideForm_RailGroupOpensDrawer", "RailToggle_ExpandsAndCollapsesRail",
        "DarkMode_WideDrawerAndExpandedRailStayOpen",
    }
    root = ET.parse(RESULTS / "e2e.trx").getroot()
    tests = root.findall("./{*}Results/{*}UnitTestResult")
    names = [test.get("testName") for test in tests]
    if len(tests) != 12 or len(set(names)) != 12 or set(names) != expected_pass | expected_skip:
        raise ValueError("Duo full suite did not produce all 12 unique expected test records")
    for test in tests:
        expected = "Passed" if test.get("testName") in expected_pass else "NotExecuted"
        if test.get("outcome") != expected:
            raise ValueError(f"Unexpected Duo outcome for {test.get('testName')}: {test.get('outcome')}")
    event("full-suite-trx-verified", records=12, passed=7, failed=0, platform_skipped=5)


def main():
    RESULTS.mkdir(parents=True, exist_ok=True)
    test = None
    exit_code = 2
    phase = "prebuilt-wda-configuration"
    try:
        verify_prebuilt_wda_configuration()
        phase = "appium-readiness"
        # Probe the live server again after simulator boot. Preboot status is
        # evidence only and must never satisfy this fresh readiness check.
        if not wait_ready(None, "http://127.0.0.1:4723/status", 180):
            exit_code = 2
        else:
            phase = "complete-suite-test"
            command = [
                "dotnet", "test", "Tests/AdaptiveShell.UITests/AdaptiveShell.UITests.csproj",
                "--no-build", "--no-restore",
                "--logger", "trx;LogFileName=e2e.trx", "--logger", "console;verbosity=detailed",
                "--results-directory", "TestResults", "--diag", "TestResults/duo-vstest.log",
            ]
            with (RESULTS / "duo-test-console.log").open("wb") as test_log:
                test = start_owned(command, stdout=test_log, stderr=subprocess.STDOUT)
                exit_code = wait_test(test, 1200)
                if exit_code == 0:
                    phase = "prebuilt-wda-session-options"
                    verify_prebuilt_wda_configuration(require_session=True)
                    phase = "full-suite-completeness"
                    verify_full_suite()
    except Exception as error:
        exit_code = 2
        event("diagnostic-error", phase=phase, type=type(error).__name__, error=str(error))
    finally:
        # Keep failure health in the separate bounded workflow step. Stop the
        # actual test and proved preboot server first; never hide the original failure.
        try:
            stop_group(test, "dotnet-test", grace=1)
            if exit_code != 0:
                stop_recorded_appium()
        finally:
            # Independent finally: cleanup denial must not skip native evidence.
            try:
                collect_native_evidence()
            except Exception as capture_error:
                event("native-capture-unexpected-error", type=type(capture_error).__name__,
                      error=str(capture_error))
        try:
            flush_logs()
        except Exception as log_error:
            event("live-log-flush-error", error=str(log_error))
        result = {"scope": "bounded complete-suite diagnostic; strict raw four-phase gate remains required",
                  "phase": phase, "exit_code": exit_code,
                  "appium_ready_budget_seconds": 180, "test_budget_seconds": 1200,
                  "appium_start_path": "global Appium command; started and checked before simulator boot"}
        (RESULTS / "duo-full-diagnostic.json").write_text(json.dumps(result, indent=2), encoding="utf-8")
        event("diagnostic-complete", **result)
    return exit_code


# Appended to the existing diagnostic watchdog; all output is TestResults/duo-*.

def health(stage):
    if stage not in ("before-runtime", "preboot", "failure", "workflow-failure"):
        raise ValueError("Unknown health stage")
    if stage in ("failure", "workflow-failure"):
        # Four independent deadlines preserve finally progress on a slow host.
        commands = (
            ("vm-stat", ["vm_stat"]),
            ("swap", ["sysctl", "vm.swapusage"]),
            ("cpu-memory-processes", ["top", "-l", "1", "-n", "20", "-o", "cpu"]),
            ("processes", ["ps", "-axo", "pid,ppid,pgid,etime,state,command"]),
        )
        timeout = 10
    else:
        commands = (
            ("hardware", ["sysctl", "hw.ncpu", "hw.memsize", "hw.pagesize"]),
            ("cpu-memory", ["top", "-l", "1", "-n", "0"]),
            ("disk", ["df", "-h", "/"]),
            ("vm-stat", ["vm_stat"]),
            ("swap", ["sysctl", "vm.swapusage"]),
            ("runtime-images", ["xcrun", "simctl", "runtime", "list", "--json"]),
        )
        timeout = 15
    results = []
    for name, command in commands:
        code = capture(command, f"duo-health-{stage}-{name}.txt", timeout)
        results.append({"name": name, "exit_code": code, "timeout_seconds": timeout})
    files = sorted(RESULTS.glob(f"duo-health-{stage}-*.txt"))
    (RESULTS / f"duo-health-{stage}-summary.json").write_text(json.dumps({
        "stage": stage, "scope": "raw host observations; not an OOM determination",
        "files": [{"name": file.name, "bytes": file.stat().st_size} for file in files],
        "commands": results,
    }, indent=2), encoding="utf-8")
    event("health-stage-complete", health_stage=stage)
    return 0


def shutdown_build_servers():
    RESULTS.mkdir(parents=True, exist_ok=True)
    code = capture(["dotnet", "build-server", "shutdown"],
                   "duo-build-server-shutdown.txt", timeout=30)
    event("build-server-shutdown-complete", exit_code=code, deadline_seconds=30)
    return code


def start_appium_preboot():
    RESULTS.mkdir(parents=True, exist_ok=True)
    process = None
    exit_code = 2
    command_path = shutil.which("appium")
    metadata = {"scope": "preboot startup evidence; not a UI test result",
                "command": command_path, "ready_budget_seconds": 180}
    try:
        if not command_path:
            raise ValueError("The installed global Appium command was not found")
        with Path("appium.log").open("wb") as appium_log:
            process = start_owned([command_path], stdout=appium_log, stderr=subprocess.STDOUT)
        metadata.update({"pid": process.pid, "owned_process_group": process.pid,
                         "started_utc": datetime.now(timezone.utc).isoformat(),
                         "start_new_session": True})
        metadata["native_start_token"] = native_appium_identity(metadata)["native_start_token"]
        (RESULTS / "duo-appium-process.json").write_text(
            json.dumps(metadata, indent=2), encoding="utf-8")
        event("appium-preboot-started", **metadata)
        if wait_ready(process, "http://127.0.0.1:4723/status", 180,
                      output="duo-appium-preboot-status.json") and process.poll() is None:
            exit_code = 0
        metadata.update({"ready": exit_code == 0, "exit_code": exit_code,
                         "process_exit_code": process.poll()})
    except Exception as error:
        metadata.update({"ready": False, "exit_code": exit_code,
                         "error_type": type(error).__name__, "error": str(error)})
        event("appium-preboot-error", type=type(error).__name__, error=str(error))
    finally:
        if exit_code != 0:
            try:
                health("failure")
            except Exception as error:
                event("failure-health-unexpected-error", type=type(error).__name__, error=str(error))
            finally:
                stop_group(process, "appium-preboot", grace=1)
        try:
            flush_logs()
        except Exception as error:
            event("live-log-flush-error", error=str(error))
        (RESULTS / "duo-appium-process.json").write_text(
            json.dumps(metadata, indent=2), encoding="utf-8")
        event("appium-preboot-complete", **metadata)
    # On success the explicitly owned server remains alive for the later real
    # postboot readiness probe and complete suite; GitHub cleans job orphans.
    return exit_code


def prune_candidates(images, devices):
    target = "com.apple.CoreSimulator.SimRuntime.iOS-27-0"
    if not isinstance(images, dict) or not isinstance(devices, dict):
        raise ValueError("Runtime/device evidence is not an object")
    device_map = devices.get("devices")
    if not isinstance(device_map, dict):
        raise ValueError("Missing complete device-state map")
    target_devices = device_map.get(target, [])
    if not isinstance(target_devices, list) or any(
            not isinstance(device, dict) or device.get("state") != "Shutdown"
            for device in target_devices):
        raise ValueError("iOS 27.0 has a running/unknown device; refusing runtime deletion")
    selected = []
    for key, image in images.items():
        if not isinstance(image, dict):
            raise ValueError("Malformed runtime-image entry")
        if image.get("runtimeIdentifier") != target:
            continue
        if (image.get("version") != "27.0"
                or image.get("platformIdentifier") != "com.apple.platform.iphonesimulator"
                or image.get("deletable") is not True or image.get("state") != "Ready"):
            raise ValueError("iOS 27.0 image identity/deletability is uncertain")
        from uuid import UUID
        identifier = image.get("identifier")
        if not isinstance(identifier, str) or str(UUID(identifier)).upper() != identifier.upper() or key != identifier:
            raise ValueError("Runtime-image UUID is missing or ambiguous")
        selected.append(identifier)
    return sorted(selected)


def prune_unused_ios27():
    # Refuse any local or shared-purpose use; this dedicated workflow has one VM.
    if (os.environ.get("GITHUB_ACTIONS") != "true"
            or os.environ.get("RUNNER_OS") != "macOS"
            or os.environ.get("GITHUB_JOB") != "duo-startup-diagnostic"
            or os.environ.get("DUO_REMOVE_UNUSED_27_0_RUNTIME") != "true"):
        raise ValueError("Runtime removal is allowed only in the dedicated GitHub Duo CI job")
    RESULTS.mkdir(parents=True, exist_ok=True)
    def command(args, filename, timeout=30):
        with (RESULTS / filename).open("wb") as output:
            process = start_owned(args, stdout=output, stderr=subprocess.STDOUT)
            try:
                code = process.wait(timeout=timeout)
            except subprocess.TimeoutExpired:
                stop_group(process, filename, grace=1)
                raise ValueError(f"{filename} exceeded {timeout}s")
            finally:
                stop_group(process, filename, grace=0)
        if code:
            raise ValueError(f"{filename} failed with exit {code}")
        return (RESULTS / filename).read_text(encoding="utf-8")
    images = json.loads(command(["xcrun", "simctl", "runtime", "list", "--json"], "duo-prune-images-before.json"))
    devices = json.loads(command(["xcrun", "simctl", "list", "devices", "--json"], "duo-prune-devices-before.json"))
    selected = prune_candidates(images, devices)
    protected = {key: (image.get("runtimeIdentifier"), image.get("version"), image.get("build"))
                 for key, image in images.items() if key not in selected}
    (RESULTS / "duo-prune-plan.json").write_text(json.dumps({
        "scope": "dedicated CI VM; only unused iOS 27.0 runtime images",
        "preserved": "iOS 27.1 runtime build 24A94401 and every Xcode SDK",
        "selected_image_uuids": selected,
    }, indent=2), encoding="utf-8")
    for identifier in selected:
        command(["xcrun", "simctl", "runtime", "delete", identifier, "--dry-run"], f"duo-prune-{identifier}-dry-run.txt")
        command(["xcrun", "simctl", "runtime", "delete", identifier], f"duo-prune-{identifier}-delete.txt", timeout=90)
    # Deletion can be asynchronous; verify its result within a separate 120s budget.
    deadline = time.monotonic() + 120
    while True:
        remaining = deadline - time.monotonic()
        if remaining <= 0:
            raise ValueError("Deleted iOS 27.0 images still present after 120s")
        after = json.loads(command(["xcrun", "simctl", "runtime", "list", "--json"], "duo-prune-images-after.json", min(30, remaining)))
        if not isinstance(after, dict):
            raise ValueError("Invalid runtime-image readback")
        if not any(identifier in after for identifier in selected):
            for identifier, expected in protected.items():
                image = after.get(identifier)
                if not isinstance(image, dict) or (image.get("runtimeIdentifier"), image.get("version"), image.get("build")) != expected:
                    raise ValueError("A nonselected runtime image changed; refusing to claim successful prune")
            event("runtime-prune-complete", removed_image_uuids=selected,
                  changed=bool(selected))
            return 0
        time.sleep(min(2, max(0, deadline - time.monotonic())))



def resource_observation(stage):
    if stage not in ("before-boot", "after-boot"):
        raise ValueError("Unknown resource observation stage")
    RESULTS.mkdir(parents=True, exist_ok=True)
    commands = (
        ("memory-pressure", ["memory_pressure", "-Q"]),
        ("process-rss-cpu", ["ps", "-axo", "pid,ppid,pgid,rss,%cpu,state,etime,command"]),
        ("vm-stat", ["vm_stat"]),
        ("swap", ["sysctl", "vm.swapusage"]),
    )
    observations = []
    for name, command in commands:
        filename = f"duo-split-resources-{stage}-{name}.txt"
        code = capture(command, filename, timeout=10)
        observations.append({"name": name, "file": filename, "exit_code": code,
                             "timeout_seconds": 10})
    value = {"stage": stage, "scope": "raw bounded host observations; not an OOM determination",
             "commands": observations}
    (RESULTS / f"duo-split-resources-{stage}.json").write_text(
        json.dumps(value, indent=2), encoding="utf-8")
    event("resource-observation-complete", **value)
    return 0

if __name__ == "__main__":
    if len(sys.argv) == 3 and sys.argv[1] == "--resource-observation":
        sys.exit(resource_observation(sys.argv[2]))
    if len(sys.argv) == 3 and sys.argv[1] == "--health":
        sys.exit(health(sys.argv[2]))
    if sys.argv[1:] == ["--prebuild-wda"]:
        sys.exit(prebuild_wda())
    if sys.argv[1:] == ["--shutdown-build-servers"]:
        sys.exit(shutdown_build_servers())
    if sys.argv[1:] == ["--start-appium-preboot"]:
        sys.exit(start_appium_preboot())
    if sys.argv[1:] == ["--prune-unused-ios27"]:
        sys.exit(prune_unused_ios27())
    if sys.argv[1:]:
        raise SystemExit("Unknown diagnostic arguments")
    sys.exit(main())
