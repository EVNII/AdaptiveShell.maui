#!/usr/bin/env python3
"""Bounded Duo-only CI startup; does not select or replace UI tests."""
import argparse
import json
import os
from pathlib import Path
import shutil
import signal
import subprocess
import time

RESULTS = Path("TestResults")
PROCESS = RESULTS / "duo-appium-process.json"


def save(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, indent=2) + "\n", encoding="utf-8")


def live(pid):
    try:
        os.kill(pid, 0)
        return True
    except ProcessLookupError:
        return False
    except PermissionError as error:
        raise RuntimeError("Cannot establish Appium process liveness") from error


def wait_ready(stage, seconds=180, process=None):
    metadata = json.loads(PROCESS.read_text(encoding="utf-8"))
    pid = metadata.get("pid")
    if not isinstance(pid, int) or isinstance(pid, bool) or pid <= 1:
        raise ValueError("Missing owned Appium process PID")
    deadline = time.monotonic() + seconds
    state = {"stage": stage, "pid": pid, "deadline_seconds": seconds, "ready": False}
    path = RESULTS / f"duo-appium-{stage}-status.json"
    while time.monotonic() < deadline:
        if (process is not None and process.poll() is not None) or not live(pid):
            state["error"] = "The Appium process exited before readiness"
            if process is not None:
                state["process_exit_code"] = process.returncode
            save(path, state)
            return 1
        remaining = deadline - time.monotonic()
        try:
            # curl's whole-transfer deadline prevents a slow response body from
            # extending the overall readiness deadline indefinitely.
            response = subprocess.run(
                ["curl", "--fail", "--silent", "--show-error", "--max-time",
                 str(min(2, remaining)), "http://127.0.0.1:4723/status"],
                capture_output=True, text=True, timeout=min(3, remaining),
                check=False)
            if response.returncode == 0:
                status = json.loads(response.stdout)
                if isinstance(status, dict) and isinstance(status.get("value"), dict) and status["value"].get("ready") is True:
                    if (process is not None and process.poll() is not None) or not live(pid):
                        state["error"] = "The Appium process exited during its status response"
                        save(path, state)
                        return 1
                    state.update({"ready": True, "status": status})
                    save(path, state)
                    print(f"Appium {stage}: PID {pid} alive and status.value.ready=true", flush=True)
                    return 0
                state["last_status"] = status
            else:
                state["last_curl_exit_code"] = response.returncode
                state["last_error"] = response.stderr[-1000:]
        except (OSError, ValueError, subprocess.TimeoutExpired) as error:
            state["last_error"] = str(error)[-1000:]
        time.sleep(min(2, max(0, deadline - time.monotonic())))
    state["error"] = f"Appium did not become ready within {seconds} seconds"
    save(path, state)
    print(state["error"], flush=True)
    return 1


def start():
    RESULTS.mkdir(parents=True, exist_ok=True)
    command = shutil.which("appium")
    if not command:
        raise RuntimeError("Installed global Appium executable was not found")
    if PROCESS.exists():
        raise RuntimeError("Appium process metadata already exists; refusing a second server")
    server_command = [command, "--keep-alive-timeout", "900"]
    with Path("appium.log").open("wb") as log:
        process = subprocess.Popen(
            server_command, stdin=subprocess.DEVNULL, stdout=log,
            stderr=subprocess.STDOUT, start_new_session=True)
    save(PROCESS, {"pid": process.pid, "command": server_command, "log": "appium.log",
                   "start_new_session": True, "created_at_unix": time.time()})
    return wait_ready("preboot", process=process)


def shutdown_build_servers():
    RESULTS.mkdir(parents=True, exist_ok=True)
    command = ["dotnet", "build-server", "shutdown"]
    state = {"command": command, "deadline_seconds": 30}
    process = None
    try:
        with (RESULTS / "duo-build-server-shutdown.txt").open("wb") as log:
            process = subprocess.Popen(command, stdout=log, stderr=subprocess.STDOUT,
                                       stdin=subprocess.DEVNULL, start_new_session=True)
            try:
                state["exit_code"] = process.wait(timeout=30)
            except subprocess.TimeoutExpired:
                state.update({"exit_code": 124, "error": "Build-server shutdown deadline exceeded"})
                try:
                    os.killpg(process.pid, signal.SIGKILL)
                except (ProcessLookupError, PermissionError) as error:
                    state["cleanup_error"] = str(error)
                try:
                    process.wait(timeout=1)
                except subprocess.TimeoutExpired:
                    state["cleanup_error"] = "Shutdown process did not exit after cleanup"
    except OSError as error:
        state.update({"exit_code": 1, "error": str(error)})
    finally:
        save(RESULTS / "duo-build-server-shutdown.json", state)
    return state["exit_code"]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("action", choices=["start", "wait", "shutdown-build-servers"])
    args = parser.parse_args()
    if args.action == "start":
        return start()
    if args.action == "wait":
        return wait_ready("postboot")
    return shutdown_build_servers()


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (OSError, ValueError, RuntimeError) as error:
        print(f"Duo CI startup failed: {error}", flush=True)
        raise SystemExit(1)
