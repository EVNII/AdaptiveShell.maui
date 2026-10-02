#!/usr/bin/env python3
"""Record the locked API29 emulator's shell and privileged native theme calls."""
import json
import os
import subprocess
import time
from pathlib import Path

output = Path("TestResults/android-theme-permission-probe.json")
output.parent.mkdir(parents=True, exist_ok=True)
records = []


def call(*args):
    result = subprocess.run(["adb", "-s", "emulator-5554", "shell", *args],
                            capture_output=True, text=True, timeout=30)
    records.append({"arguments": list(args), "exit_code": result.returncode,
                    "stdout": result.stdout, "stderr": result.stderr})
    output.write_text(json.dumps({"records": records}, indent=2), encoding="utf-8")
    result.check_returncode()
    return result.stdout.strip()


if (os.environ.get("GITHUB_ACTIONS") != "true"
        or os.environ.get("GITHUB_JOB") != "uitest-android"
        or call("getprop", "ro.build.version.sdk") != "29"
        or call("getprop", "ro.kernel.qemu") != "1"
        or call("getprop", "sys.boot_completed") != "1"):
    raise ValueError("This probe is restricted to the booted API29 CI emulator")

call("id")
call("getprop", "ro.build.fingerprint")
call("getprop", "ro.debuggable")
call("dumpsys", "package", "com.android.shell")
call("cmd", "uimode", "night", "yes")
call("cmd", "uimode", "night")
call("dumpsys", "uimode")
call("logcat", "-d", "-s", "UiModeManagerService")
if call("su", "root", "id", "-u") != "0":
    raise ValueError("The exact emulator did not expose its privileged test shell")
call("su", "root", "cmd", "uimode", "night", "yes")
if call("cmd", "uimode", "night") != "Night mode: yes":
    raise ValueError("The privileged native system command did not enable night mode")
time.sleep(2.5)
configuration = call("am", "get-config")
if "-night-" not in configuration or "-notnight-" in configuration:
    raise ValueError("Actual Android configuration did not enter night mode")
call("dumpsys", "uimode")
call("su", "root", "cmd", "uimode", "night", "no")
if call("cmd", "uimode", "night") != "Night mode: no":
    raise ValueError("The privileged native system command did not restore light mode")
time.sleep(2.5)
if "-notnight-" not in call("am", "get-config"):
    raise ValueError("Actual Android configuration did not restore light mode")
output.write_text(json.dumps({"status": "verified", "records": records}, indent=2), encoding="utf-8")
print("Recorded shell permissions and actual privileged system light/dark changes; full 13-test suite remains required.")
