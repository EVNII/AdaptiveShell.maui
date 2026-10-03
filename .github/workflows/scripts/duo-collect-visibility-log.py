#!/usr/bin/env python3
"""Diagnostic-only, read-only post-test native log collection; never a UI gate.
Run after the original complete13 TestHost, before its always artifact upload.
All native commands share60s; each owned child, including get_app_container,
gets at most10s. A capture error exits2 and preserves available raw evidence.
"""
import hashlib
import json
import os
from pathlib import Path
import plistlib
import re
import runpy
import signal
import subprocess
import sys
import time
from datetime import datetime, timezone

BUNDLE = "com.companyname.exampleashellapp"
BRANCH = "codex/duo-current13-visibility-diagnostic"
MODEL = "iPhone19,4"
LOG = "ashell-duo-visibility.jsonl"


def utc():
    return datetime.now(timezone.utc).isoformat()


def digest(path):
    value = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            value.update(block)
    return value.hexdigest()


def require(condition, message):
    if not condition:
        raise ValueError(message)


def collect(workspace):
    started = time.monotonic()
    deadline = started + 60
    out = workspace / "TestResults/duo-visibility-post"
    require(not out.exists(), "Refusing to reuse earlier post-test evidence")
    out.mkdir(parents=True)
    evidence = {"schema": 1, "status": "capture_error", "started_utc": utc(),
                "scope": "post-test native logger collection only; not original-timeout snapshot or test success",
                "test_step_outcome": os.environ.get("DUO_ORIGINAL_TEST_OUTCOME"),
                "budget_seconds": 60, "commands": []}

    def command(argv, stem):
        command_started = time.monotonic()
        remaining = deadline - command_started
        require(remaining > 0, "Post-test collector shared60s deadline expired")
        seconds = min(10, remaining)
        row = {"argv": argv, "started_utc": utc(), "timeout_seconds": seconds,
               "status": "started", "stdout_file": stem + ".stdout", "stderr_file": stem + ".stderr"}
        evidence["commands"].append(row)
        process = None
        try:
            with (out / row["stdout_file"]).open("xb") as stdout, (out / row["stderr_file"]).open("xb") as stderr:
                process = subprocess.Popen(argv, stdout=stdout, stderr=stderr, start_new_session=True)
                row.update(pid=process.pid, owned_pgid=process.pid)
                process.wait(timeout=max(0.001, seconds - (time.monotonic() - command_started)))
            row["exit_code"] = process.returncode
            require(process.returncode == 0, "Native command failed: " + stem)
            row["status"] = "completed"
            return (out / row["stdout_file"]).read_text(encoding="utf-8").strip()
        except Exception as error:
            row.update(status="error", error={"type": type(error).__name__, "message": str(error)})
            if process is not None and process.poll() is None:
                try:
                    require(os.getpgid(process.pid) == process.pid, "Owned child process-group changed")
                    os.killpg(process.pid, signal.SIGKILL)
                    row["owned_group_kill_requested"] = True
                except (OSError, ValueError) as cleanup:
                    row["cleanup_error"] = str(cleanup)
            raise
        finally:
            row["finished_utc"] = utc()
            for key in ("stdout_file", "stderr_file"):
                path = out / row[key]
                if path.is_file():
                    row[key + "_bytes"] = path.stat().st_size
                    row[key + "_sha256"] = digest(path)

    try:
        head = os.environ.get("GITHUB_SHA", "")
        udid = os.environ.get("UITEST_DEVICE_UDID", "")
        require(os.environ.get("GITHUB_ACTIONS") == "true" and os.environ.get("RUNNER_OS") == "macOS"
                and sys.platform == "darwin" and os.environ.get("GITHUB_JOB") == "uitest-ios-27-1-duo"
                and os.environ.get("GITHUB_REPOSITORY") == "EVNII/AdaptiveShell.maui"
                and os.environ.get("GITHUB_REF_NAME") == BRANCH
                and os.environ.get("UITEST_DUO_VISIBILITY_DIAGNOSTIC") == "true"
                and os.environ.get("UITEST_PLATFORM") == "ios" and os.environ.get("UITEST_FORM") == "duo",
                "Collector is restricted to the opted-in diagnostic Duo macOS CI job")
        require(re.fullmatch(r"[0-9a-f]{40}", head) and os.environ.get("GITHUB_WORKFLOW_SHA") == head,
                "Executed source/workflow identity mismatch")
        require(re.fullmatch(r"[0-9A-F]{8}(?:-[0-9A-F]{4}){3}-[0-9A-F]{12}", udid)
                and udid == os.environ.get("DUO_DEVICE_UDID"), "Owned Duo UDIDs differ or are invalid")
        evidence.update(source_sha=head, workflow_source_sha=head, run_id=os.environ["GITHUB_RUN_ID"],
                        run_attempt=os.environ["GITHUB_RUN_ATTEMPT"], device_udid=udid, bundle_id=BUNDLE)
        handoff_path = workspace / "TestResults/duo-split-consumer.json"
        manifest_path = workspace / "TestResults/duo-split-manifest.json"
        handoff = json.loads(handoff_path.read_bytes())
        manifest = json.loads(manifest_path.read_bytes())
        identity = handoff["identity"]
        require(handoff.get("status") == "verified" and manifest["identity"] == identity
                and handoff["manifest_sha256"] == digest(manifest_path) and manifest["test_count"] == 13
                and identity["source_sha"] == head and identity["workflow_source_sha"] == head
                and identity["repository"] == os.environ["GITHUB_REPOSITORY"]
                and identity["run_id"] == os.environ["GITHUB_RUN_ID"]
                and identity["run_attempt"] == os.environ["GITHUB_RUN_ATTEMPT"]
                and identity["workspace"] == str(workspace), "Same-run immutable handoff identity differs")
        evidence["handoff"] = {"sha256": digest(handoff_path), "manifest_sha256": digest(manifest_path),
                               "artifact": handoff["artifact"], "identity": identity}
        observed = json.loads(command(["xcrun", "simctl", "list", "-j"], "native-devices"))
        prepare = runpy.run_path(str(workspace / ".github/workflows/scripts/prepare-duo.py"))
        runtime, device_type, devices = prepare["pinned_runtime"](observed)
        selected = [d for d in devices if isinstance(d, dict) and d.get("udid") == udid]
        all_selected = [d for group in observed["devices"].values() for d in group
                        if isinstance(d, dict) and d.get("udid") == udid]
        require(len(selected) == len(all_selected) == 1 and selected[0].get("state") == "Booted"
                and selected[0].get("isAvailable") is True
                and selected[0].get("deviceTypeIdentifier") == prepare["DEVICE_TYPE"], "Exact owned Duo is not Booted/available")
        require(command(["xcrun", "simctl", "getenv", udid, "SIMULATOR_MODEL_IDENTIFIER"], "native-model") == MODEL
                and device_type.get("modelIdentifier") == MODEL, "Native simulator model differs")
        require(command(["xcrun", "--sdk", "iphonesimulator", "--show-sdk-version"], "selected-sdk") == "27.1",
                "Selected native simulator SDK differs")
        data_root = Path(selected[0]["dataPath"])
        expected_root = Path.home() / "Library/Developer/CoreSimulator/Devices" / udid / "data"
        require(data_root.is_absolute() and data_root.is_dir() and data_root.resolve() == expected_root.resolve(),
                "Owned native device dataPath differs")
        containers = {}
        for kind in ("app", "data"):
            raw = command(["xcrun", "simctl", "get_app_container", udid, BUNDLE, kind], "container-" + kind)
            path = Path(raw)
            require(len(raw.splitlines()) == 1 and path.is_absolute() and path.is_dir()
                    and not path.is_symlink() and path.resolve().is_relative_to(data_root.resolve()), "Container escaped the owned native device")
            containers[kind] = path
        require(containers["app"].suffix == ".app"
                and containers["data"].parent.resolve() == (data_root / "Containers/Data/Application").resolve()
                and re.fullmatch(r"[0-9A-Fa-f]{8}(?:-[0-9A-Fa-f]{4}){3}-[0-9A-Fa-f]{12}", containers["data"].name),
                "Returned app/data container layout differs")
        info_path = containers["app"] / "Info.plist"
        require(info_path.is_file() and not info_path.is_symlink(), "Installed Info.plist is not a regular file")
        info_bytes = info_path.read_bytes()
        (out / "installed-info.plist").write_bytes(info_bytes)
        info = plistlib.loads(info_bytes)
        require(info.get("CFBundleIdentifier") == BUNDLE and info.get("CFBundleExecutable") == "ExampleAShellApp"
                and info.get("DTSDKName") == "iphonesimulator27.1" and info.get("DTSDKBuild") == "24A94403"
                and info.get("AShellDuoVisibilityDiagnostic") is True
                and info.get("AShellDuoVisibilityDiagnosticSource") == head, "Installed diagnostic sourceflag/bundle/SDK differs")
        executable = containers["app"] / info["CFBundleExecutable"]
        require(executable.is_file() and not executable.is_symlink(), "Installed executable is not regular")
        preinstall_path = workspace / "TestResults/duo-aut-preinstall-proof.json"
        preinstall = json.loads(preinstall_path.read_bytes())
        require(preinstall.get("status") == "verified" and preinstall["identity"]["head_sha"] == head
                and preinstall["identity"]["run_id"] == os.environ["GITHUB_RUN_ID"]
                and preinstall["identity"]["run_attempt"] == os.environ["GITHUB_RUN_ATTEMPT"]
                and preinstall["identity"]["device_udid"] == udid
                and preinstall["source"]["executable_sha256"] == digest(executable), "Installed executable differs from same-run preinstall source")
        native_sdk = command(["xcrun", "vtool", "-show-build", str(executable)], "installed-native-sdk")
        sdks = re.findall(r"(?m)^\s*sdk\s+(\S+)\s*$", native_sdk)
        require(sdks and all(s == "27.1" for s in sdks), "Actual installed executable native SDK differs")
        evidence.update(native_device=selected[0], runtime_identifier=runtime["identifier"], runtime_build=runtime["buildversion"],
                        model=MODEL, sdk="27.1", app_container=str(containers["app"]), data_container=str(containers["data"]),
                        installed_info_sha256=hashlib.sha256(info_bytes).hexdigest(), executable_sha256=digest(executable),
                        preinstall_proof_sha256=digest(preinstall_path))
        log_path = containers["data"] / "Documents" / LOG
        require(log_path.is_file() and not log_path.is_symlink(), "Actual native visibility log is absent/nonregular")
        raw = log_path.read_bytes()
        raw_path = out / LOG
        raw_path.write_bytes(raw)  # Preserve actual bytes before parsing; never synthesize a native record.
        evidence["raw_log"] = {"source_path": str(log_path), "file": LOG, "bytes": len(raw),
                               "sha256": hashlib.sha256(raw).hexdigest()}
        require(raw and raw.endswith(b"\n"), "Native JSONL is empty or has a partial final record")
        records = [json.loads(line) for line in raw.splitlines() if line]
        require(all(r.get("schema") == 1 and "status" not in r and r.get("bundle_id") == BUNDLE
                    and r.get("simulator_model") == MODEL and r.get("diagnostic_source_sha") == head for r in records),
                "Native JSONL contains capture errors or differs from the installed diagnostic identity")
        require(time.monotonic() < deadline, "Collector shared60s deadline exceeded")
        evidence.update(status="captured", records=len(records), first_record_utc=records[0]["utc"], last_record_utc=records[-1]["utc"])
        return_code = 0
    except Exception as error:
        evidence["error"] = {"type": type(error).__name__, "message": str(error)}
        return_code = 2
    finally:
        evidence.update(finished_utc=utc(), elapsed_seconds=time.monotonic() - started)
        (out / "capture.json").write_text(json.dumps(evidence, indent=2) + "\n", encoding="utf-8")
        print(json.dumps(evidence, indent=2), flush=True)
    return return_code


if __name__ == "__main__":
    workspace = Path(os.environ.get("GITHUB_WORKSPACE", ".")).resolve()
    require(workspace == Path.cwd().resolve(), "Collector must use the actual workflow checkout working directory")
    sys.exit(collect(workspace))
