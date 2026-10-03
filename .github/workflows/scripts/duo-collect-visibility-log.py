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
BRANCH = "codex/duo-post-suite-snapshot-comparison"
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


def validate_native_log(raw, head):
    """Validate diagnostic provenance; native state never substitutes for WDA Displayed."""
    require(isinstance(raw, bytes) and 0 < len(raw) <= 8 * 1024 * 1024
            and raw.endswith(b"\n"), "Native JSONL is empty, oversized, or partial")
    require(re.fullmatch(r"[0-9a-f]{40}", head), "Invalid native diagnostic source")
    decoder = json.JSONDecoder()
    def pairs(items):
        result = {}
        for key, value in items:
            require(key not in result, "Duplicate native JSON property")
            result[key] = value
        return result
    def raw_property(line, wanted):
        cursor = 1
        while True:
            while line[cursor].isspace() or line[cursor] == ',': cursor += 1
            key, end = decoder.raw_decode(line, cursor)
            cursor = end
            while line[cursor].isspace(): cursor += 1
            require(line[cursor] == ':', "Malformed native property separator")
            cursor += 1
            while line[cursor].isspace(): cursor += 1
            begin = cursor
            _, cursor = decoder.raw_decode(line, cursor)
            if key == wanted: return line[begin:cursor]
            while line[cursor].isspace(): cursor += 1
            require(line[cursor] != '}', "Required raw native state absent")
    common = {'schema', 'sequence', 'utc', 'process_id', 'bundle_id', 'simulator_model',
              'os_version', 'diagnostic_source_sha'}
    full_fields = common | {'state_read_started_utc', 'state_read_finished_utc', 'full_state_sha256', 'state'}
    heartbeat_fields = common | {'status', 'phase', 'state_read_started_utc', 'state_read_finished_utc',
                                'full_state_sequence', 'full_state_sha256', 'current_state_sha256'}
    full_state_fields = {'phase', 'provider_call', 'content_id', 'content_full_bleed', 'existing_parent_before',
                         'returned_controller', 'selected_tab_id', 'selected_controller_inspected',
                         'selected_controller', 'returned_is_selected', 'root', 'root_children', 'pages'}
    selection_fields = {'phase', 'selection_call', 'content_id', 'root_handle', 'target_tab_id', 'target_tab_handle',
                        'actual_selected_tab_before_id', 'actual_selected_tab_before_handle',
                        'actual_selected_tab_after_id', 'actual_selected_tab_after_handle',
                        'selection_write_skipped', 'selection_write_attempted'}
    full_phases = {'provider-before', 'provider-after', 'did-select-tab', 'post-layout', 'root-did-appear', 'tick'}
    last = {}; full = {}; last_tick = {}; selections = {}; after = set(); records = []
    counts = {'full_states': 0, 'fresh_equal_heartbeats': 0, 'selection_before': 0, 'selection_after': 0}
    def timestamp(value):
        require(isinstance(value, str) and re.fullmatch(r'\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?(?:Z|\+00:00)', value),
                "Native time must contain explicit UTC and native tick precision")
        parsed = datetime.fromisoformat(value.replace('Z', '+00:00'))
        require(parsed.utcoffset() is not None and parsed.utcoffset().total_seconds() == 0, "Native time must be UTC")
        fraction = re.search(r'\.(\d+)', value)
        return parsed.replace(microsecond=0), int(fraction[1].ljust(7, '0')) if fraction else 0
    def handle(value, nullable=False):
        if value is None and nullable: return
        require(isinstance(value, str) and re.fullmatch(r'(?:0x[0-9a-fA-F]+|[1-9][0-9]*)', value)
                and int(value, 16 if value.startswith('0x') else 10) > 0, "Native handle is missing or invalid")
    for raw_line in raw.splitlines():
        require(raw_line, "Empty native JSONL record")
        line = raw_line.decode('utf-8')
        record = json.loads(line, object_pairs_hook=pairs)
        require(type(record) is dict and record.get('schema') == 1 and type(record.get('schema')) is int
                and record.get('bundle_id') == BUNDLE and record.get('simulator_model') == MODEL
                and record.get('diagnostic_source_sha') == head
                and isinstance(record.get('os_version'), str)
                and re.fullmatch(r'27\.1(?:\.\d+)?', record['os_version']), "Native source/bundle/model/runtime mismatch")
        pid = record.get('process_id'); seq = record.get('sequence')
        require(type(pid) is int and pid > 1 and type(seq) is int and seq == last.get(pid, 0) + 1,
                "Native sequence must be complete and monotonic within the actual PID")
        last[pid] = seq
        utc_value = timestamp(record['utc'])
        if 'status' in record:
            require(record['status'] == 'fresh_equal_native_state' and set(record) == heartbeat_fields
                    and record['phase'] == 'tick', "Native error/log limit or unknown status is rejected")
            ref = record.get('full_state_sequence')
            require(type(ref) is int and (pid, ref) in full and ref == last_tick.get(pid)
                    and ref < seq and record['full_state_sha256'] == record['current_state_sha256'] == full[(pid, ref)],
                    "Fresh heartbeat must refer to this PID/source's most recent full tick and exact state hash")
            counts['fresh_equal_heartbeats'] += 1
        else:
            state = record.get('state'); require(type(state) is dict, "Native state is absent")
            phase = state.get('phase')
            if phase in full_phases:
                require(set(record) == full_fields and set(state) == full_state_fields
                        and type(state['provider_call']) is int and state['provider_call'] >= 0
                        and type(state['selected_controller_inspected']) is bool
                        and type(state['root']) is dict and type(state['root_children']) is list and type(state['pages']) is list,
                        "Malformed full native snapshot schema")
                hashed = hashlib.sha256(raw_property(line, 'state').encode('utf-8')).hexdigest()
                require(record['full_state_sha256'] == hashed, "Full native state bytes differ from their hash")
                full[(pid, seq)] = hashed
                if phase == 'tick': last_tick[pid] = seq
                counts['full_states'] += 1
            else:
                require(set(record) == common | {'state'} and set(state) == selection_fields
                        and phase in ('current-item-selection-before', 'current-item-selection-after')
                        and type(state['selection_call']) is int and state['selection_call'] > 0
                        and isinstance(state['content_id'], str) and state['content_id']
                        and type(state['selection_write_skipped']) is bool and type(state['selection_write_attempted']) is bool,
                        "Malformed current-item selection schema")
                for key in ('root_handle', 'target_tab_handle'): handle(state[key])
                for key in ('actual_selected_tab_before_handle', 'actual_selected_tab_after_handle'): handle(state[key], True)
                skipped = state['actual_selected_tab_before_handle'] is not None and state['actual_selected_tab_before_handle'] == state['target_tab_handle']
                require(state['selection_write_skipped'] == skipped, "Selection skip differs from actual native handles")
                key = (pid, state['root_handle'], state['selection_call'])
                if phase.endswith('-before'):
                    require(key not in selections and state['actual_selected_tab_after_handle'] is None
                            and state['actual_selected_tab_after_id'] is None and not state['selection_write_attempted'],
                            "Duplicate/malformed selection before record")
                    selections[key] = state
                    counts['selection_before'] += 1
                else:
                    require(key in selections and key not in after and state['selection_write_attempted'] == (not skipped),
                            "Selection after lacks unique before record or has incorrect write provenance")
                    before = selections[key]
                    for field in ('content_id', 'target_tab_id', 'target_tab_handle', 'actual_selected_tab_before_id',
                                  'actual_selected_tab_before_handle', 'selection_write_skipped'):
                        require(state[field] == before[field], "Selection references changed between before/after")
                    after.add(key)
                    counts['selection_after'] += 1
        if 'state_read_started_utc' in record:
            require(timestamp(record['state_read_started_utc']) <= timestamp(record['state_read_finished_utc']) <= utc_value,
                    "Fresh native read timestamps are inconsistent")
        records.append(record)
    require(records and counts['full_states'] > 0, "Native full snapshot evidence is missing")
    return records, {**counts, 'processes': len(last), 'pending_selection_before': len(selections) - len(after),
                     'scope': 'read-only native diagnostic provenance; never WDA visibility or UI acceptance'}


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
                and os.environ.get("GITHUB_WORKFLOW") == "Duo Post Suite Snapshot Comparison"
                and os.environ.get("RUNNER_ENVIRONMENT") == "github-hosted"
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
        records, native_summary = validate_native_log(raw, head)
        evidence["native_record_summary"] = native_summary
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
