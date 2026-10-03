#!/usr/bin/env python3
"""Hosted API35 pre-AUT/post-test raw observations; never a UI acceptance gate.

Appium keeps its original earlier workflow step. pre-aut proves no POST session,
no actual Android driver session, and no AUT PID before/after native captures.
No settings, themes, services, app state, or emulator configuration are changed.
"""
from pathlib import Path
import argparse
import datetime as dt
import hashlib
import importlib.util
import json
import os
import re
import signal
import subprocess
import sys
import time

BRANCH = 'codex/android35-pre-aut-status-diagnostic'
WORKFLOW = 'Android35 Pre-AUT Status Bar Diagnostic'
PACKAGE = 'com.companyname.exampleashellapp'
OUT = Path('TestResults/android35-pre-aut')
CAPTURE_LIMIT = 32 * 1024 * 1024

def need(value, message):
    if not value:
        raise ValueError(message)

def digest(data):
    return hashlib.sha256(data).hexdigest()

def utc():
    return dt.datetime.now(dt.timezone.utc).isoformat()

def new_bytes(path, data):
    with path.open('xb') as output:
        output.write(data)
    return {'path': str(path), 'bytes': len(data), 'sha256': digest(data)}

def appium_snapshot(stage, filename='appium.log'):
    path = Path('appium.log')
    need(path.is_file() and not path.is_symlink(), 'Missing original Appium log')
    need(path.stat().st_size <= CAPTURE_LIMIT, 'Appium log exceeds observation limit')
    data = path.read_bytes()
    record = new_bytes(stage / filename, data)
    text = re.sub(r'\x1b\[[0-9;-]*m', '', data.decode('utf-8', errors='strict'))
    posts = [i for i, line in enumerate(text.splitlines(), 1)
             if re.search(r'\[HTTP\]\s+--> POST /(?:wd/hub/)?session(?:\s|$)', line)]
    driver_lines = [i for i, line in enumerate(text.splitlines(), 1)
                    if re.search(r'\[(?:AndroidUiautomator2Driver|AndroidDriver)@', line)]
    record.update(observed_utc=utc(), post_session_lines=posts,
                  actual_android_driver_lines=driver_lines)
    return record, text

def native_statusbar_presence(text, size):
    """Only the observed API35 dumpsys schema, with native InsetsSource pixels.

    Requested layout heights/rotation alternatives are not actual frames. A
    ready 63px frame is accepted just as a ready 128px frame; cutout height need
    not equal bar height. This observes presence, never absence of clipping.
    """
    displays = re.findall(r'^  Display: mDisplayId=([0-9]+)\b.*$', text, re.M)
    need(displays == ['0'], 'Not the unique actual default native display')
    headers = list(re.finditer(r'^  Window #\d+ Window\{([0-9a-f]+) u0 StatusBar\}:$', text, re.M))
    if not headers:
        return {'ready': False, 'reason': 'No actual StatusBar Window yet'}
    need(len(headers) == 1, 'Actual StatusBar Window is ambiguous')
    header = headers[0]
    end = re.search(r'^  Window #\d+ Window\{', text[header.end():], re.M)
    block = text[header.end():header.end() + end.start()] if end else text[header.end():]
    need(re.findall(r'^    mDisplayId=([0-9]+)\b', block, re.M) == ['0']
         and re.search(r'^    mOwnerUid=\d+ .*\bpackage=com\.android\.systemui\b', block, re.M)
         and re.search(r'^    mAttrs=\{[^\n]*\bty=STATUS_BAR\b', block, re.M),
         'StatusBar owner/type/display is not exact')
    visible = [r'^    mHasSurface=true isReadyForDisplay\(\)=true\b',
               r'^      Surface: shown=true\b', r'^    isOnScreen=true$', r'^    isVisible=true$']
    if not all(re.search(pattern, block, re.M) for pattern in visible):
        return {'ready': False, 'reason': 'Native StatusBar surface is not yet shown/visible'}
    states = re.findall(r'^    InsetsState\n(.*?)(?=^    Control map:)', text, re.M | re.S)
    need(len(states) == 1, 'Native InsetsState is not unique')
    state = states[0]
    frames = re.findall(r'^      mDisplayFrame=Rect\((-?\d+), (-?\d+) - (-?\d+), (-?\d+)\)$', state, re.M)
    if not frames or tuple(map(int, frames[0])) == (0, 0, 0, 0):
        return {'ready': False, 'reason': 'Native display frame is not yet initialized'}
    need(len(frames) == 1 and tuple(map(int, frames[0])) == (0, 0, *size),
         'Native display frame differs from actual wm size')
    cutouts = [line for line in state.splitlines() if line.startswith('      mDisplayCutout=')]
    need(len(cutouts) == 1, 'Native display cutout record is not unique')
    dimensions = re.findall(r'(?<![A-Za-z])displayWidth=(\d+) displayHeight=(\d+) physicalDisplayWidth=(\d+) physicalDisplayHeight=(\d+)', cutouts[0])
    if not dimensions or tuple(map(int, dimensions[0])) == (0, 0, 0, 0):
        return {'ready': False, 'reason': 'Native cutout/display dimensions are not yet initialized'}
    need(len(dimensions) == 1 and tuple(map(int, dimensions[0])) == (*size, *size),
         'Native cutout belongs to a different display geometry')
    rect = r'\[(-?\d+),(-?\d+)\]\[(-?\d+),(-?\d+)\]'
    sources = re.findall(r'^        InsetsSource id=([0-9a-f]+) type=statusBars frame=' + rect + r' visible=(true|false)\b.*$', state, re.M)
    if not sources:
        return {'ready': False, 'reason': 'No native statusBars InsetsSource yet'}
    need(len(sources) == 1, 'Native statusBars InsetsSource is ambiguous')
    identity, left, top, right, bottom, shown = sources[0]
    frame = tuple(map(int, (left, top, right, bottom)))
    if frame == (0, 0, 0, 0) or shown != 'true':
        return {'ready': False, 'reason': 'Native statusBars source is not yet nonzero/visible'}
    need(frame[:2] == (0, 0) and frame[2] == size[0] and 0 < frame[3] <= size[1],
         'Native statusBars frame is not contained in the sole display')
    provider_parts = re.findall(r'^    InsetsSourceProviders:\n(.*?)(?=^  InsetsPolicy)', text, re.M | re.S)
    need(len(provider_parts) == 1, 'Native InsetsSourceProviders is not unique')
    providers = re.findall(r'^      (?:Ime)?InsetsSourceProvider\n(.*?)(?=^      (?:Ime)?InsetsSourceProvider|\Z)', provider_parts[0], re.M | re.S)
    matching = [p for p in providers if re.search(r'^        mSource=InsetsSource id=' + re.escape(identity) + r' type=statusBars\b', p, re.M)]
    need(len(matching) == 1, 'Native statusBars provider is not unique')
    provider = matching[0]
    same_source = re.findall(r'^        mSource=InsetsSource id=' + re.escape(identity) + r' type=statusBars frame=' + rect + r' visible=(true|false)\b.*$', provider, re.M)
    source_frames = re.findall(r'^        mSourceFrame=Rect\((-?\d+), (-?\d+) - (-?\d+), (-?\d+)\)$', provider, re.M)
    containers = re.findall(r'^        mWindowContainer=Window\{([0-9a-f]+) u0 StatusBar\}$', provider, re.M)
    need(len(same_source) == len(source_frames) == len(containers) == 1
         and tuple(map(int, same_source[0][:4])) == frame and same_source[0][4] == 'true'
         and tuple(map(int, source_frames[0])) == frame and containers[0] == header.group(1),
         'Native source/provider/frame/StatusBar Window identity differs')
    return {'ready': True, 'display_id': 0, 'display_size': list(size),
            'window_identity': header.group(1), 'package': 'com.android.systemui',
            'surface_shown': True, 'visible': True, 'insets_source_id': identity,
            'frame': list(frame), 'frame_source': 'native statusBars InsetsSource and same Window provider mSourceFrame',
            'cutout_raw': cutouts[0].strip(), 'clipping_or_clock_acceptance': False}

def collect(phase, test_exit_code=None):
    started = time.monotonic()
    deadline = started + 30
    proof = {'schema': 1, 'stage': phase, 'status': 'capture_error',
             'scope': 'Hosted single API35 compact raw pre-AUT/post-tests observations',
             'ui_acceptance': False, 'started_utc': utc(), 'commands': []}
    stage = OUT / phase
    stage_created = False

    def remaining():
        need(time.monotonic() < deadline, 'Shared 30-second observation deadline expired')
        return deadline - time.monotonic()

    def run(argv, label, allowed=(0,), seconds=10):
        timeout = min(seconds, remaining())
        row = {'argv': argv, 'label': label, 'started_utc': utc(), 'timeout_seconds': timeout}
        proof['commands'].append(row)
        process = None
        stdout = stderr = b''
        try:
            process = subprocess.Popen(argv, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                                       start_new_session=True)
            row['pid'] = process.pid
            try:
                group = os.getpgid(process.pid)
            except ProcessLookupError:
                group = None
            need(group == process.pid or (group is None and process.poll() is not None),
                 'Read-only client does not own its new process group')
            stdout, stderr = process.communicate(timeout=timeout)
            row['exit_code'] = process.returncode
            need(len(stdout) <= CAPTURE_LIMIT and len(stderr) <= CAPTURE_LIMIT,
                 'Native command exceeds observation byte limit')
            need(process.returncode in allowed, 'Native read failed: ' + label)
            row['status'] = 'completed'
            return stdout, stderr, process.returncode
        except subprocess.TimeoutExpired as error:
            stdout, stderr = error.output or b'', error.stderr or b''
            row.update(status='timeout', error=str(error))
            raise
        except Exception as error:
            row.update(status='error', error=repr(error))
            raise
        finally:
            if process is not None and process.poll() is None:
                try:
                    if os.getpgid(process.pid) == process.pid:
                        os.killpg(process.pid, signal.SIGKILL)
                        row['owned_client_group_kill_requested'] = True
                except (ProcessLookupError, PermissionError) as error:
                    row['cleanup_error'] = repr(error)
            row['stdout'] = new_bytes(stage / (label + '.stdout'), stdout)
            row['stderr'] = new_bytes(stage / (label + '.stderr'), stderr)
            row['finished_utc'] = utc()

    try:
        expected = {'GITHUB_ACTIONS': 'true', 'GITHUB_REPOSITORY': 'EVNII/AdaptiveShell.maui',
                    'GITHUB_JOB': 'uitest-android', 'GITHUB_REF': 'refs/heads/' + BRANCH,
                    'GITHUB_WORKFLOW': WORKFLOW, 'GITHUB_EVENT_NAME': 'workflow_dispatch',
                    'RUNNER_ENVIRONMENT': 'github-hosted', 'RUNNER_OS': 'Linux'}
        need(sys.platform == 'linux' and all(os.environ.get(k) == v for k, v in expected.items()),
             'Not the explicit hosted API35 diagnostic job')
        head = os.environ.get('GITHUB_SHA', '')
        need(re.fullmatch(r'[0-9a-f]{40}', head) and os.environ.get('GITHUB_WORKFLOW_SHA') == head,
             'Source/workflow SHA identity missing')
        run_id = os.environ.get('GITHUB_RUN_ID', '')
        attempt = os.environ.get('GITHUB_RUN_ATTEMPT', '')
        need(re.fullmatch(r'[1-9][0-9]*', run_id) and re.fullmatch(r'[1-9][0-9]*', attempt),
             'Run/attempt identity missing')
        serial = os.environ.get('ANDROID_SERIAL', '')
        port = os.environ.get('EMULATOR_PORT', '')
        need(re.fullmatch(r'[1-9][0-9]*', port) and serial == 'emulator-' + port,
             'Actual emulator-runner owned serial identity missing')
        need(not OUT.is_symlink() and not OUT.parent.is_symlink(), 'Observation directory is a symlink')
        stage.mkdir(parents=True, exist_ok=False)
        stage_created = True
        proof.update(source_sha=head, run_id=int(run_id), run_attempt=int(attempt),
                     workflow=WORKFLOW, branch=BRANCH, owned_serial=serial, bundle=PACKAGE,
                     collector_sha256=digest(Path(__file__).read_bytes()))
        got = run(['git', 'rev-parse', 'HEAD'], 'source-head', seconds=3)[0].decode().strip()
        need(got == head, 'Checkout source differs from the actual workflow source')
        clean = run(['git', 'status', '--porcelain', '--untracked-files=no'],
                    'source-clean', seconds=3)[0]
        need(not clean.strip(), 'Tracked source changed during diagnostic')
        adb = ['adb', '-s', serial]
        devices = run(['adb', 'devices', '-l'], 'actual-devices', seconds=3)[0].decode()
        lines = [line.split()[:2] for line in devices.splitlines()
                 if line.strip() and not line.startswith('List of devices attached')]
        need(lines == [[serial, 'device']], 'Not the unique actual ready owned emulator')
        for args, label, exact in [(['get-state'], 'device-state', 'device'),
                                   (['shell', 'getprop', 'ro.build.version.sdk'], 'device-sdk', '35'),
                                   (['shell', 'getprop', 'ro.kernel.qemu'], 'device-qemu', '1'),
                                   (['shell', 'getprop', 'sys.boot_completed'], 'device-boot', '1')]:
            value = run(adb + args, label, seconds=3)[0].decode().strip()
            need(value == exact, 'Actual native device guard failed: ' + label)
        before, err, code = run(adb + ['shell', 'pidof', PACKAGE], 'aut-pid-before', (0, 1), 3)
        proof['aut_pid_before'] = {'exit_code': code, 'stdout': before.decode(), 'stderr': err.decode()}
        log, text = appium_snapshot(stage)
        proof['original_appium_log'] = log
        if phase == 'pre-aut':
            need(code == 1 and not before.strip() and not err.strip(), 'AUT is already running before TestHost')
            need(not log['post_session_lines'] and not log['actual_android_driver_lines'],
                 'Actual Appium/Android session already started before TestHost')
        else:
            need(type(test_exit_code) is int and 0 <= test_exit_code <= 255, 'Original shell exit code is missing or invalid')
            previous_path = OUT / 'pre-aut/proof.json'
            previous = json.loads(previous_path.read_text())
            need(previous['status'] == 'captured' and previous['source_sha'] == head
                 and previous['run_id'] == int(run_id) and previous['run_attempt'] == int(attempt)
                 and previous['owned_serial'] == serial, 'Pre/post native identity is not exact')
            proof['pre_aut_proof'] = {'path': str(previous_path), 'sha256': digest(previous_path.read_bytes())}
            proof['original_shell_exit_code'] = test_exit_code
            sessions = []
            for line in text.splitlines():
                if 'Proxying [POST /session]' in line and ' with body: ' in line:
                    body = json.loads(line.split(' with body: ', 1)[1])
                    sessions.extend(body['capabilities']['firstMatch'])
            need(len(sessions) == 1 and sessions[0].get('deviceUDID') == serial
                 and sessions[0].get('appPackage') == PACKAGE
                 and sessions[0].get('automationName') == 'UiAutomator2',
                 'Actual original Appium session is not the owned emulator/AUT')
            proof['actual_android_session'] = {'deviceUDID': serial, 'appPackage': PACKAGE,
                                               'automationName': 'UiAutomator2'}
        size = run(adb + ['shell', 'wm', 'size'], 'native-display-size', seconds=3)[0].decode()
        sizes = re.findall(r'^(Physical|Override) size: ([1-9][0-9]*)x([1-9][0-9]*)$', size, re.M)
        need(len(sizes) in (1, 2) and sizes[0][0] == 'Physical'
             and (len(sizes) == 1 or sizes[1][0] == 'Override'), 'Native display size is ambiguous')
        native_size = [int(sizes[-1][1]), int(sizes[-1][2])]
        if phase == 'pre-aut':
            polls = []
            proof['systemui_readiness_polls'] = polls
            while True:
                label = 'readiness-window-' + str(len(polls) + 1).zfill(4)
                window, _, _ = run(adb + ['shell', 'dumpsys', 'window'], label)
                presence = native_statusbar_presence(window.decode('utf-8', errors='strict'), native_size)
                polls.append({'label': label, 'presence': presence})
                if presence['ready']:
                    proof['systemui_ready_before_png'] = presence
                    break
                time.sleep(min(0.25, remaining()))
            fresh, _ = appium_snapshot(stage, 'appium-ready.log')
            proof['systemui_ready_appium_log'] = fresh
            need(not fresh['post_session_lines'] and not fresh['actual_android_driver_lines'],
                 'Appium/Android session appeared during natural SystemUI readiness')
        png, _, _ = run(adb + ['exec-out', 'screencap', '-p'], 'native-screen')
        proof['native_png'] = new_bytes(stage / 'native-screen.png', png)
        script = Path('Tests/AdaptiveShell.UITests/Scripts/verify_mac_button_colors.py')
        spec = importlib.util.spec_from_file_location('original_png_reader', script)
        module = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(module)
        image = module.PNG(stage / 'native-screen.png', decode_pixels=phase == 'pre-aut')
        need([image.width, image.height] == native_size, 'Native PNG is not the actual complete native display')
        if phase == 'pre-aut':
            need(any(any(row[channel::image.channels]) for row in image.rows for channel in range(3)),
                 'The original pre-AUT PNG is wholly black; no presented SystemUI evidence')
            proof['native_png']['full_display_RGB_not_all_black'] = True
        proof['native_png'].update(width=image.width, height=image.height,
                                  complete_original_png_validated=True, display_size_source='native wm size raw')
        window, _, _ = run(adb + ['shell', 'dumpsys', 'window'], 'native-window')
        if phase == 'pre-aut':
            proof['systemui_after_png'] = native_statusbar_presence(window.decode('utf-8', errors='strict'), native_size)
        run(adb + ['shell', 'dumpsys', 'display'], 'native-display')
        after, err, code = run(adb + ['shell', 'pidof', PACKAGE], 'aut-pid-after', (0, 1), 3)
        proof['aut_pid_after'] = {'exit_code': code, 'stdout': after.decode(), 'stderr': err.decode()}
        if phase == 'pre-aut':
            need(code == 1 and not after.strip() and not err.strip(), 'AUT began running during pre-AUT capture')
        remaining()
        proof['status'] = 'captured'
    except Exception as error:
        proof['error'] = repr(error)
    finally:
        proof.update(finished_utc=utc(), elapsed_seconds=time.monotonic() - started)
        # Never alter an existing stage or follow a planted output symlink.
        if stage_created:
            path = stage / 'proof.json'
            new_bytes(path, (json.dumps(proof, indent=2) + '\n').encode())
    print(json.dumps({'stage': phase, 'status': proof['status'], 'error': proof.get('error'),
                      'ui_acceptance': False}))
    return 0 if proof['status'] == 'captured' else 2

if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--phase', choices=('pre-aut', 'post-tests'), required=True)
    parser.add_argument('--test-exit-code', type=int)
    args = parser.parse_args()
    raise SystemExit(collect(args.phase, args.test_exit_code))
