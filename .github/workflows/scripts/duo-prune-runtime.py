#!/usr/bin/env python3
"""Remove only unused iOS 27.0 images in the dedicated public Duo CI VM.

This retains the resource conditions of the startup probe. It does not establish
that runtime removal caused or is necessary for a passing UI test.
"""
from datetime import datetime, timezone
import json
import os
from pathlib import Path
import signal
import subprocess
import time

RESULTS = Path("TestResults")


def event(stage, **details):
    RESULTS.mkdir(parents=True, exist_ok=True)
    row = {"utc": datetime.now(timezone.utc).isoformat(), "stage": stage, **details}
    value = json.dumps(row, ensure_ascii=False)
    print(value, flush=True)
    with (RESULTS / "duo-prune-events.jsonl").open("a", encoding="utf-8") as stream:
        stream.write(value + "\n")


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
    # Cleanup remains bounded and preserves command failure evidence,
    # even if the runner denies process-group operations.
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
            or os.environ.get("GITHUB_JOB") != "uitest-ios-27-1-duo"
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


if __name__ == "__main__":
    try:
        raise SystemExit(prune_unused_ios27())
    except (OSError, ValueError, RuntimeError) as error:
        event("runtime-prune-failed", error=str(error))
        raise SystemExit(1)
