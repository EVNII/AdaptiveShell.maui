#!/usr/bin/env python3
"""Duo complete-suite diagnostic; all 13 tests and raw four-phase gate remain required.

The test source and Appium startup/retry timeouts remain unchanged. An external
watchdog bounds Appium readiness to 180s and the complete test process to 1200s.
Test/capture commands own process groups; the watchdog kills their descendants.
"""
from datetime import datetime, timezone
import json
import os
import plistlib
from pathlib import Path
import signal
import hashlib
import secrets
import re
import runpy
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
          selection="complete assembly; no filter", expected_records=13)
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
    arguments = metadata.get("arguments")
    if arguments != ["--keep-alive-timeout", "900"]:
        raise ValueError("Missing exact recorded Appium connection-budget arguments")
    if not ((len(argv) == 4 and Path(argv[0]).name == "node" and argv[1] in expected
             and argv[2:] == arguments)
            or (len(argv) == 3 and argv[0] in expected and argv[1:] == arguments)):
        raise ValueError("Native command is not the exact recorded Appium executable and arguments")
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
                or os.environ.get("GITHUB_JOB") != "uitest-ios-27-1-duo"):
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
    transfer = runpy.run_path(".github/workflows/scripts/duo-split-build-transfer.py")
    transfer["verify_trx"]()
    event("full-suite-trx-verified", records=13, passed=8, failed=0, platform_skipped=5)


def remaining(deadline):
    value = deadline - time.monotonic()
    if value <= 0:
        raise TimeoutError("Complete TestHost/barrier/boot/session/suite exceeded its shared1200s budget")
    return value


def atomic_json(path, value):
    if path.exists():
        raise ValueError("Diagnostic barrier output already exists; refusing reuse")
    pending = path.with_name(path.name + ".tmp")
    with pending.open("x", encoding="utf-8") as stream:
        json.dump(value, stream, indent=2)
        stream.flush()
        os.fsync(stream.fileno())
    os.replace(pending, path)


def file_sha256(path):
    value = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            value.update(block)
    return value.hexdigest()


def wait_for_testhost(test, identity, deadline):
    path = RESULTS / "duo-testhost-arrived.json"
    # This does not change VSTest's original90s connection timeout. It also
    # bounds adapter/discovery/setup arrival after an actual host connection.
    arrival_deadline = min(deadline, time.monotonic() + 180)
    while not path.exists():
        remaining(arrival_deadline)
        if test.poll() is not None:
            raise RuntimeError(f"Complete test process exited {test.returncode} before OneTimeSetUp arrival")
        time.sleep(0.1)
    marker = json.loads(path.read_text(encoding="utf-8"))
    if not isinstance(marker, dict) or marker.get("status") != "before-create-driver":
        raise ValueError("Invalid actual TestHost arrival marker")
    for key, value in identity.items():
        if marker.get(key) != value or type(marker.get(key)) is not type(value):
            raise ValueError(f"Actual TestHost marker identity mismatch: {key}")
    pid = marker.get("testhost_pid")
    if type(pid) is not int or pid <= 1 or pid == test.pid or os.getpgid(pid) != test.pid:
        raise ValueError("Marker TestHost PID is not a distinct member of this owned test process group")
    args = marker.get("process_args")
    if not isinstance(args, list) or not args or not isinstance(args[0], str) or Path(args[0]).name != "testhost.dll":
        raise ValueError("Arrival did not come from the actual VSTest testhost.dll process")
    if marker.get("assembly_path") != str(Path(identity["assembly_path"]).resolve()):
        raise ValueError("Actual TestHost assembly path mismatch")
    if test.poll() is not None:
        raise RuntimeError("Complete test process exited while verifying TestHost arrival")
    event("preboot-testhost-arrival-verified", marker=marker, owned_test_pgid=test.pid,
          total_remaining_seconds=remaining(deadline))
    return marker


def boot_after_testhost(marker, deadline):
    udid = marker["device_udid"]
    if udid != os.environ.get("DUO_DEVICE_UDID"):
        raise ValueError("Barrier and prepared actual Duo UDIDs differ")
    prepared = json.loads((RESULTS / "duo-environment.json").read_text(encoding="utf-8"))
    if prepared.get("status") != "verified" or prepared.get("device", {}).get("udid") != udid:
        raise ValueError("Boot barrier lacks the same verified prepared Duo environment")
    def native(command, filename, seconds):
        code = capture(command, filename, timeout=min(seconds, remaining(deadline)))
        remaining(deadline)
        if code != 0:
            raise RuntimeError(f"Native boot proof failed: {filename}, exit {code}")
        return (RESULTS / filename).read_text(encoding="utf-8").strip()
    event("preboot-testhost-boot-begin", device_udid=udid,
          total_remaining_seconds=remaining(deadline))
    native(["xcrun", "simctl", "boot", udid], "duo-testhost-boot.txt", 60)
    native(["xcrun", "simctl", "bootstatus", udid, "-b"], "duo-testhost-bootstatus.txt", 600)
    model = native(["xcrun", "simctl", "getenv", udid, "SIMULATOR_MODEL_IDENTIFIER"], "duo-model.txt", 120)
    if model != "iPhone19,4":
        raise ValueError("Actual booted simulator model is not iPhone19,4")
    displays = native(["xcrun", "simctl", "io", udid, "enumerate"], "duo-displays.txt", 30)
    observed = json.loads(native(["xcrun", "simctl", "list", "-j"], "duo-testhost-native-after-boot.json", 30))
    # Reuse the unchanged prepare-duo exact runtime/device-type validator on
    # the fresh native readback; no device is created or selected here.
    prepare = runpy.run_path(".github/workflows/scripts/prepare-duo.py")
    runtime, device_type, devices = prepare["pinned_runtime"](observed)
    selected = [d for d in devices if isinstance(d, dict) and d.get("udid") == udid]
    all_selected = [d for group in observed["devices"].values() if isinstance(group, list)
                    for d in group if isinstance(d, dict) and d.get("udid") == udid]
    if (len(selected) != 1 or len(all_selected) != 1 or selected[0].get("state") != "Booted"
            or selected[0].get("isAvailable") is not True
            or selected[0].get("deviceTypeIdentifier") != prepare["DEVICE_TYPE"]
            or device_type.get("modelIdentifier") != model):
        raise ValueError("Fresh native environment is not the exact available Booted Duo")
    sdk = native(["xcrun", "--sdk", "iphonesimulator", "--show-sdk-version"], "duo-testhost-sdk-after-boot.txt", 30)
    xcode = native(["xcodebuild", "-version"], "duo-testhost-xcode-after-boot.txt", 30)
    if (sdk != "27.1" or prepared.get("sdk", {}).get("version") != sdk
            or prepared.get("xcode", {}).get("version_output") != xcode
            or re.fullmatch(r"Xcode 27\.1\s+Build version [A-Za-z0-9]+", xcode) is None):
        raise ValueError("Selected Xcode/simulator SDK changed during boot")
    screens = {}
    for match in re.finditer(r"(?ms)^\s+\((\d+)\) ([^:\n]+):\n(.*?)(?=^\s+\(\d+\) [^:\n]+:\n|^Port:|\Z)", displays):
        identifier, name, block = int(match[1]), match[2], match[3]
        kind = re.search(r"(?m)^\s+Screen Type: (\S+)\s*$", block)
        if kind is None or kind[1] != "Integrated":
            continue
        if identifier in screens:
            raise ValueError("Duplicate native integrated display ID")
        pixel = re.search(r"Pixel Size: \{(\d+), (\d+)\}", block)
        scale = re.search(r"Preferred UI Scale: (\d+)", block)
        actual_id = re.search(r"Screen ID: (\d+)", block)
        device = re.search(r"(?m)^\s+Device Name: (\S+)\s*$", block)
        if pixel is None or scale is None or actual_id is None or device is None or int(actual_id[1]) != identifier:
            raise ValueError("Incomplete actual integrated display evidence")
        screens[identifier] = {"id": identifier, "name": name, "device_name": device[1],
                               "width": int(pixel[1]), "height": int(pixel[2]), "scale": int(scale[1])}
    expected = {1: {"id": 1, "name": "LCD", "device_name": "primary", "width": 1398, "height": 2034, "scale": 3},
                3: {"id": 3, "name": "LCD-1", "device_name": "primary-1", "width": 2007, "height": 2853, "scale": 3}}
    if screens != expected:
        raise ValueError("Native default-pose dual displays do not exactly match the observed Duo contract")
    proof = {"status": "verified", "device_udid": udid, "model": model,
             "runtime_identifier": runtime["identifier"], "runtime_version": runtime["version"],
             "runtime_build": runtime["buildversion"], "device_type_identifier": device_type["identifier"],
             "bootstatus_exit_code": 0, "dual_displays_verified": True, "displays": list(screens.values()),
             "sdk_version": sdk, "xcode_version_output": xcode,
             "native_environment_sha256": file_sha256(RESULTS / "duo-testhost-native-after-boot.json"),
             "display_evidence_sha256": file_sha256(RESULTS / "duo-displays.txt")}
    atomic_json(RESULTS / "duo-testhost-boot-proof.json", proof)
    event("preboot-testhost-boot-verified", proof=proof,
          total_remaining_seconds=remaining(deadline))
    # Fresh live postboot status is required; preboot ready JSON is insufficient.
    if not wait_ready(None, "http://127.0.0.1:4723/status", min(180, remaining(deadline))):
        raise RuntimeError("Appium is not live after actual Duo boot")
    remaining(deadline)
    resource_observation("after-boot")
    remaining(deadline)
    return proof


def preinstall_verified_aut(marker, deadline):
    """Optional native setup only; original Appium reset/query/install stays intact."""
    if (os.environ.get("DUO_DIAGNOSTIC_PREINSTALL_AUT") != "true"
            or os.environ.get("GITHUB_ACTIONS") != "true"
            or os.environ.get("RUNNER_OS") != "macOS"
            or os.environ.get("GITHUB_JOB") != "uitest-ios-27-1-duo"):
        raise ValueError("Native preinstall requires explicit opt-in in this diagnostic CI job")
    path = RESULTS / "duo-aut-preinstall-proof.json"
    if path.exists():
        raise ValueError("Native preinstall proof already exists; refusing reuse")
    stage_deadline = min(deadline, time.monotonic() + 120)
    proof = {"status": "pending", "started_utc": datetime.now(timezone.utc).isoformat(),
             "identity": {key: marker[key] for key in
                          ("run_id", "run_attempt", "head_sha", "device_udid", "nonce", "testhost_pid", "assembly_sha256")},
             "budget_seconds": 120, "commands": [],
             "scope": "native setup evidence; original Appium installed query/reset/reinstall is unchanged"}

    def budget():
        remaining(deadline)
        value = stage_deadline - time.monotonic()
        if value <= 0:
            raise TimeoutError("Native AUT preinstall/identity proof exceeded its shared-clock120s stage limit")
        return value

    def native(command, name):
        seconds = budget()
        row = {"command": command, "started_utc": datetime.now(timezone.utc).isoformat(),
               "timeout_seconds": seconds, "stdout_file": name + ".stdout.txt", "stderr_file": name + ".stderr.txt"}
        proof["commands"].append(row)
        process = None
        code = 1
        event("aut-preinstall-command-begin", **row)
        try:
            with (RESULTS / row["stdout_file"]).open("wb") as output, (RESULTS / row["stderr_file"]).open("wb") as error:
                process = start_owned(command, stdout=output, stderr=error)
                row.update(pid=process.pid, owned_pgid=process.pid)
                try:
                    code = process.wait(timeout=seconds)
                except subprocess.TimeoutExpired:
                    code = 124
        finally:
            if process is not None:
                stop_group(process, name, grace=1 if code == 124 else 0)
            row.update(exit_code=code, finished_utc=datetime.now(timezone.utc).isoformat())
            event("aut-preinstall-command-end", **row)
        budget()
        if code != 0:
            raise RuntimeError(f"Native AUT setup failed: {name}, exit {code}")
        return (RESULTS / row["stdout_file"]).read_text(encoding="utf-8").strip()

    def bundle_info(app):
        info_path = app / "Info.plist"
        if info_path.is_symlink() or not info_path.is_file():
            raise ValueError("AUT Info.plist is not a regular file")
        with info_path.open("rb") as stream:
            info = plistlib.load(stream)
        expected = {"CFBundleIdentifier": "com.companyname.exampleashellapp",
                    "CFBundleExecutable": "ExampleAShellApp", "DTSDKName": "iphonesimulator27.1", "DTSDKBuild": "24A94403"}
        if any(info.get(key) != value for key, value in expected.items()):
            raise ValueError("Actual AUT bundle/executable/SDK27.1 build identity differs")
        executable = app / expected["CFBundleExecutable"]
        if executable.is_symlink() or not executable.is_file():
            raise ValueError("AUT executable is not a regular file")
        return {**expected, "app_path": str(app), "info_plist_sha256": file_sha256(info_path),
                "executable_sha256": file_sha256(executable), "executable_bytes": executable.stat().st_size}

    try:
        budget()
        handoff = json.loads((RESULTS / "duo-split-consumer.json").read_text(encoding="utf-8"))
        manifest_path = RESULTS / "duo-split-manifest.json"
        manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
        identity = handoff.get("identity", {})
        if (handoff.get("status") != "verified" or file_sha256(manifest_path) != handoff.get("manifest_sha256")
                or manifest.get("identity") != identity or manifest.get("test_count") != 13
                or any(identity.get(key) != marker[value] for key, value in
                       (("run_id", "run_id"), ("run_attempt", "run_attempt"), ("source_sha", "head_sha")))
                or handoff.get("test_assembly_sha256") != marker["assembly_sha256"]):
            raise ValueError("Native preinstall lacks the same immutable source/run/test handoff")
        workspace = Path(os.environ["GITHUB_WORKSPACE"]).resolve()
        app = Path(handoff["paths"]["app"])
        if (identity.get("workspace") != str(workspace) or app != workspace / manifest["app_path"]
                or app != Path(os.environ["UITEST_APP_PATH"]) or app.resolve() != app
                or not app.is_dir() or app.is_symlink()):
            raise ValueError("Native install input differs from the exact restored AUT path")
        prefix = "workspace/" + manifest["app_path"]
        rows = [row for row in manifest["entries"] if row["path"] == prefix or row["path"].startswith(prefix + "/")]
        transfer = runpy.run_path(".github/workflows/scripts/duo-split-build-transfer.py")
        transfer["verify_installed_tree"](app, rows, prefix)
        source = bundle_info(app)
        boot_path = RESULTS / "duo-testhost-boot-proof.json"
        boot = json.loads(boot_path.read_text(encoding="utf-8"))
        native_path = RESULTS / "duo-testhost-native-after-boot.json"
        observed = json.loads(native_path.read_text(encoding="utf-8"))
        udid = marker["device_udid"]
        devices = [device for group in observed["devices"].values() for device in group if device.get("udid") == udid]
        if (boot.get("status") != "verified" or boot.get("device_udid") != udid
                or udid != os.environ.get("DUO_DEVICE_UDID") or boot.get("runtime_version") != "27.1"
                or boot.get("runtime_build") != "24A94401" or boot.get("sdk_version") != "27.1"
                or boot.get("dual_displays_verified") is not True or boot.get("bootstatus_exit_code") != 0
                or boot.get("native_environment_sha256") != file_sha256(native_path)
                or len(devices) != 1 or devices[0].get("state") != "Booted" or devices[0].get("isAvailable") is not True):
            raise ValueError("Native preinstall lacks the same exact successful Duo boot proof")
        data_path = Path(devices[0]["dataPath"])
        if not data_path.is_absolute() or not data_path.is_dir():
            raise ValueError("Native exact device dataPath is absent")
        proof.update(source=source, manifest_sha256=file_sha256(manifest_path), artifact=handoff["artifact"],
                     boot_proof_sha256=file_sha256(boot_path), native_device_data_path=str(data_path))
        native(["xcrun", "simctl", "install", udid, str(app)], "duo-aut-preinstall-install")
        returned = native(["xcrun", "simctl", "get_app_container", udid, source["CFBundleIdentifier"], "app"],
                          "duo-aut-preinstall-container")
        installed = Path(returned)
        if (len(returned.splitlines()) != 1 or not installed.is_absolute() or installed.suffix != ".app"
                or not installed.is_dir() or installed.is_symlink() or installed.resolve() == app
                or not installed.resolve().is_relative_to(data_path.resolve())):
            raise ValueError("Native installed container is not one distinct app under the exact device dataPath")
        actual = bundle_info(installed)
        proof["installed"] = actual
        if actual["executable_sha256"] != source["executable_sha256"]:
            raise ValueError("Installed executable bytes differ from the immutable producer AUT")
        # Installer signature rewriting is not assumed away: reject any source
        # mutation or installed-executable difference, preserving raw evidence.
        transfer["verify_installed_tree"](app, rows, prefix)
        if bundle_info(app) != source:
            raise ValueError("Immutable source AUT changed during native installation")
        budget()
        proof.update(status="verified", installed=actual, finished_utc=datetime.now(timezone.utc).isoformat(),
                     stage_remaining_seconds=budget(), total_remaining_seconds=remaining(deadline))
        atomic_json(path, proof)
        event("aut-preinstall-verified", proof_sha256=file_sha256(path), **proof)
        return file_sha256(path)
    except Exception as error:
        proof.update(status="failed", finished_utc=datetime.now(timezone.utc).isoformat(),
                     error={"type": type(error).__name__, "message": str(error)})
        if not path.exists():
            atomic_json(path, proof)
        raise


def require_duo_ci_scope():
    if (os.environ.get("GITHUB_ACTIONS") != "true"
            or os.environ.get("RUNNER_OS") != "macOS"
            or os.environ.get("GITHUB_JOB") != "uitest-ios-27-1-duo"):
        raise ValueError("Native Duo suite startup is restricted to its dedicated macOS CI job")


def main():
    require_duo_ci_scope()
    RESULTS.mkdir(parents=True, exist_ok=True)
    test = None
    exit_code = 2
    phase = "prebuilt-wda-configuration"
    try:
        verify_prebuilt_wda_configuration()
        phase = "appium-readiness"
        # Require the live server before starting the actual TestHost. A second
        # fresh readiness check after native boot is required before release.
        if not wait_ready(None, "http://127.0.0.1:4723/status", 180):
            exit_code = 2
        else:
            phase = "complete-suite-testhost-preboot"
            if os.environ.get("DUO_DIAGNOSTIC_PREBOOT_HOST") != "true":
                raise ValueError("This diagnostic requires explicit preboot TestHost opt-in")
            for name in ("duo-testhost-arrived.json", "duo-testhost-release.json", "duo-testhost-boot-proof.json", "duo-aut-preinstall-proof.json"):
                if (RESULTS / name).exists():
                    raise ValueError("Diagnostic barrier files already exist; refusing a second test process")
            handoff = json.loads((RESULTS / "duo-split-consumer.json").read_text(encoding="utf-8"))
            assembly = Path("Tests/AdaptiveShell.UITests/bin/Debug/net10.0/AdaptiveShell.UITests.dll").resolve()
            if handoff.get("status") != "verified" or handoff.get("test_assembly_sha256") != file_sha256(assembly):
                raise ValueError("Actual assembly does not match the verified build handoff")
            identity = {"run_id": os.environ["GITHUB_RUN_ID"], "run_attempt": os.environ["GITHUB_RUN_ATTEMPT"],
                        "head_sha": os.environ["GITHUB_SHA"], "device_udid": os.environ["DUO_DEVICE_UDID"],
                        "nonce": secrets.token_hex(32), "assembly_path": str(assembly),
                        "assembly_sha256": handoff["test_assembly_sha256"]}
            environment = os.environ.copy()
            environment.update(DUO_DIAGNOSTIC_BARRIER_NONCE=identity["nonce"],
                               DUO_DIAGNOSTIC_ASSEMBLY_SHA256=identity["assembly_sha256"])
            command = [
                "dotnet", "test", "Tests/AdaptiveShell.UITests/AdaptiveShell.UITests.csproj",
                "--no-build", "--no-restore",
                "--logger", "trx;LogFileName=e2e.trx", "--logger", "console;verbosity=detailed",
                "--results-directory", "TestResults", "--diag", "TestResults/duo-vstest.log",
            ]
            # One clock begins BEFORE spawn and includes TestHost, barrier,
            # boot, postboot readiness, original Appium session and all tests.
            deadline = time.monotonic() + 1200
            event("preboot-testhost-single-process-begin", budget_seconds=1200,
                  scope="one unfiltered full suite; shared budget includes actual Duo boot")
            with (RESULTS / "duo-test-console.log").open("wb") as test_log:
                test = start_owned(command, stdout=test_log, stderr=subprocess.STDOUT, env=environment)
                marker = wait_for_testhost(test, identity, deadline)
                phase = "exact-duo-boot-after-testhost"
                boot_after_testhost(marker, deadline)
                preinstall_sha = None
                if os.environ.get("DUO_DIAGNOSTIC_PREINSTALL_AUT") == "true":
                    phase = "native-aut-preinstall-before-testhost-release"
                    preinstall_sha = preinstall_verified_aut(marker, deadline)
                if test.poll() is not None or os.getpgid(marker["testhost_pid"]) != test.pid:
                    raise RuntimeError("Actual waiting TestHost exited before boot release")
                release = {key: marker[key] for key in
                           ("run_id", "run_attempt", "head_sha", "device_udid", "nonce", "testhost_pid", "assembly_sha256")}
                release.update(status="boot-verified", boot_proof_sha256=file_sha256(RESULTS / "duo-testhost-boot-proof.json"))
                if preinstall_sha is not None:
                    release["preinstall_proof_sha256"] = preinstall_sha
                remaining(deadline)
                atomic_json(RESULTS / "duo-testhost-release.json", release)
                phase = "complete-suite-after-native-boot-release"
                event("preboot-testhost-atomic-release", testhost_pid=marker["testhost_pid"],
                      total_remaining_seconds=remaining(deadline))
                exit_code = wait_test(test, remaining(deadline))
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
                  "budget_scope": "one complete process: TestHost/barrier/actual boot/Appium session/all tests",
                  "appium_start_path": "global Appium command; started and checked before simulator boot"}
        (RESULTS / "duo-full-diagnostic.json").write_text(json.dumps(result, indent=2), encoding="utf-8")
        event("diagnostic-complete", **result)
    return exit_code


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
    require_duo_ci_scope()
    RESULTS.mkdir(parents=True, exist_ok=True)
    process = None
    exit_code = 2
    command_path = shutil.which("appium")
    metadata = {"scope": "preboot startup evidence; not a UI test result",
                "command": command_path, "arguments": ["--keep-alive-timeout", "900"], "ready_budget_seconds": 180}
    try:
        if not command_path:
            raise ValueError("The installed global Appium command was not found")
        with Path("appium.log").open("wb") as appium_log:
            process = start_owned([command_path, *metadata["arguments"]], stdout=appium_log, stderr=subprocess.STDOUT)
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
    event("resource-observation-complete", observation_stage=stage,
          scope=value["scope"], commands=observations)
    return 0



def parse_spotlight_status(text, volumes):
    """Accept only explicit per-volume native mdutil status, never an inferred state."""
    states = {}
    current = None
    allowed = {"Indexing enabled.": "enabled", "Indexing disabled.": "disabled",
               "Indexing and searching disabled.": "disabled"}
    for raw in text.splitlines():
        line = raw.strip()
        if not line:
            continue
        if line.endswith(":") and line[:-1] in volumes:
            current = line[:-1]
            if current in states:
                raise ValueError("Duplicate mdutil volume status")
            states[current] = None
        elif current is not None and line in allowed and states[current] is None:
            states[current] = allowed[line]
        else:
            raise ValueError("Unrecognized native mdutil status; refusing inferred indexing state")
    if set(states) != set(volumes) or any(state is None for state in states.values()):
        raise ValueError("Native mdutil status omitted a requested volume")
    return states


def disable_spotlight_indexing():
    """One ephemeral-CI indexing-only comparison, before runtime installation."""
    proof = {"scope": "CI indexing-only diagnostic; not a resource root cause or UI result",
             "status": "failed", "volumes": ["/", "/System/Volumes/Data"],
             "budget_seconds": 60, "commands": [],
             "started_utc": datetime.now(timezone.utc).isoformat()}
    output = RESULTS / "duo-spotlight-indexing.json"
    pending = output.with_name(output.name + ".tmp")
    if any(path.exists() or path.is_symlink() for path in (output, pending)):
        event("spotlight-indexing-refused-existing-proof", path=str(output))
        return 1
    deadline = time.monotonic() + 60

    def native(command, name, seconds):
        seconds_left = deadline - time.monotonic()
        if seconds_left <= 0:
            raise TimeoutError("Spotlight indexing setup exceeded its shared60s budget")
        row = {"command": command, "started_utc": datetime.now(timezone.utc).isoformat(),
               "stdout_file": f"duo-spotlight-{name}.stdout.txt",
               "stderr_file": f"duo-spotlight-{name}.stderr.txt",
               "timeout_seconds": min(seconds, seconds_left)}
        proof["commands"].append(row)
        process = None
        try:
            with (RESULTS / row["stdout_file"]).open("wb") as stdout, (RESULTS / row["stderr_file"]).open("wb") as stderr:
                process = start_owned(command, stdout=stdout, stderr=stderr,
                                      env={**os.environ, "LC_ALL": "C", "LANG": "C"})
                row.update(pid=process.pid, pgid=process.pid)
                event("spotlight-indexing-command-begin", **row)
                try:
                    row["exit_code"] = process.wait(timeout=row["timeout_seconds"])
                except subprocess.TimeoutExpired:
                    row["timed_out"] = True
                    stop_group(process, "spotlight-indexing-" + name, grace=1)
                    row["exit_code"] = process.poll()
            row["stdout_sha256"] = file_sha256(RESULTS / row["stdout_file"])
            row["stderr_sha256"] = file_sha256(RESULTS / row["stderr_file"])
        finally:
            if process is not None and process.poll() is None:
                stop_group(process, "spotlight-indexing-" + name, grace=1)
            row["finished_utc"] = datetime.now(timezone.utc).isoformat()
            event("spotlight-indexing-command-end", **row)
        if row.get("timed_out") or row.get("exit_code") != 0:
            raise ValueError("Native indexing setup command failed or exceeded the setup bound")
        return (RESULTS / row["stdout_file"]).read_text(encoding="utf-8")

    try:
        if (os.environ.get("GITHUB_ACTIONS") != "true" or sys.platform != "darwin"
                or os.environ.get("RUNNER_OS") != "macOS"
                or os.environ.get("RUNNER_ENVIRONMENT") != "github-hosted"
                or os.environ.get("GITHUB_JOB") != "uitest-ios-27-1-duo"
                or os.environ.get("GITHUB_REPOSITORY") != "EVNII/AdaptiveShell.maui"
                or os.environ.get("DUO_DIAGNOSTIC_DISABLE_SPOTLIGHT_INDEXING") != "true"):
            raise ValueError("Indexing comparison is restricted to the opted-in ephemeral Duo CI job")
        identity = {"repository": os.environ["GITHUB_REPOSITORY"],
                    "run_id": os.environ.get("GITHUB_RUN_ID"),
                    "run_attempt": os.environ.get("GITHUB_RUN_ATTEMPT"),
                    "source_sha": os.environ.get("GITHUB_SHA"),
                    "job": os.environ["GITHUB_JOB"]}
        if (not re.fullmatch(r"[1-9][0-9]*", identity["run_id"] or "")
                or not re.fullmatch(r"[1-9][0-9]*", identity["run_attempt"] or "")
                or not re.fullmatch(r"[0-9a-f]{40}", identity["source_sha"] or "")):
            raise ValueError("Missing exact CI run/attempt/head identity")
        handoff = json.loads((RESULTS / "duo-split-consumer.json").read_text(encoding="utf-8"))
        if handoff.get("status") != "verified" or any(handoff["identity"].get(key) != identity[key]
                for key in ("repository", "run_id", "run_attempt", "source_sha")):
            raise ValueError("Indexing setup does not belong to the verified same-run immutable handoff")
        workflow = Path(".github/workflows/release-uitest.yml")
        if handoff["identity"].get("workflow_sha256") != file_sha256(workflow):
            raise ValueError("Indexing setup workflow differs from the immutable handoff")
        proof.update(identity=identity, workflow_sha256=file_sha256(workflow),
                     handoff_sha256=file_sha256(RESULTS / "duo-split-consumer.json"))
        volumes = proof["volumes"]
        # APFS firmlinks can make Path.is_mount() false for the actual Data
        # mount; use the native mount table instead of stat-device inference.
        mounts = {}
        for line in native(["/sbin/mount"], "mounts", 5).splitlines():
            match = re.fullmatch(r"(\S+) on (/|/System/Volumes/Data) \(([^()]*)\)", line)
            if match is None:
                continue
            device, volume, options_text = match.groups()
            options = {value.strip() for value in options_text.split(",")}
            if volume in mounts or not device.startswith("/dev/") or not {"apfs", "local"} <= options:
                raise ValueError("Requested indexing volume is not a unique native local APFS mount")
            mounts[volume] = {"device": device, "mountpoint": volume,
                              "options": sorted(options), "native_line": line}
        if set(mounts) != set(volumes):
            raise ValueError("Native mount table omitted an exact requested indexing volume")
        proof["mounts"] = mounts
        proof["before"] = parse_spotlight_status(native(["/usr/bin/mdutil", "-s", *volumes], "before", 15), volumes)
        native(["sudo", "-n", "/usr/bin/mdutil", "-i", "off", *volumes], "disable", 30)
        proof["after"] = parse_spotlight_status(native(["/usr/bin/mdutil", "-s", *volumes], "after", 15), volumes)
        if any(state != "disabled" for state in proof["after"].values()):
            raise ValueError("Native readback did not confirm indexing disabled on both explicit volumes")
        proof["status"] = "verified"
    except Exception as error:
        proof.update(error=str(error), error_type=type(error).__name__)
        event("spotlight-indexing-failed", error=str(error), type=type(error).__name__)
    finally:
        proof["finished_utc"] = datetime.now(timezone.utc).isoformat()
        atomic_json(output, proof)
    event("spotlight-indexing-complete", status=proof["status"],
          scope=proof["scope"], proof_sha256=file_sha256(output))
    return 0 if proof["status"] == "verified" else 1


if __name__ == "__main__":
    if sys.argv[1:] == ["--disable-spotlight-indexing"]:
        sys.exit(disable_spotlight_indexing())
    if len(sys.argv) == 3 and sys.argv[1] == "--resource-observation":
        sys.exit(resource_observation(sys.argv[2]))
    if len(sys.argv) == 3 and sys.argv[1] == "--health":
        sys.exit(health(sys.argv[2]))
    if sys.argv[1:] == ["--shutdown-build-servers"]:
        sys.exit(shutdown_build_servers())
    if sys.argv[1:] == ["--start-appium-preboot"]:
        sys.exit(start_appium_preboot())
    if sys.argv[1:]:
        raise SystemExit("Unknown Duo suite arguments")
    sys.exit(main())
