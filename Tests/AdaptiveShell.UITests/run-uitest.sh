#!/usr/bin/env bash
# 本地跑 E2E 测试(macOS):构建示例 App -> 启动 Appium/模拟器 -> dotnet test
#
# 用法:
#   ./run-uitest.sh android [compact|wide]   # 需已有 Android 模拟器在线,或用 ANDROID_AVD 指定 AVD 名
#   ./run-uitest.sh ios [compact|wide]       # 默认 iPhone 16,用 IOS_DEVICE 覆盖
#   ./run-uitest.sh maccatalyst              # 需本机已运行过一次示例 App(LaunchServices 已注册)
#
# 前置: npm i -g appium && appium driver install uiautomator2 xcuitest mac2
set -euo pipefail

PLATFORM="${1:?usage: run-uitest.sh <android|ios|maccatalyst> [compact|wide]}"
FORM="${2:-compact}"
REPO_ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
cd "$REPO_ROOT"

export UITEST_PLATFORM="$PLATFORM" UITEST_FORM="$FORM"

ensure_appium() {
  if curl -s --max-time 2 http://127.0.0.1:4723/status >/dev/null 2>&1; then
    return
  fi
  echo ">> Starting appium server (log: /tmp/appium-uitest.log)"
  npx --yes appium >/tmp/appium-uitest.log 2>&1 &
  for _ in $(seq 1 60); do
    sleep 1
    curl -s --max-time 2 http://127.0.0.1:4723/status >/dev/null 2>&1 && return
  done
  echo "!! appium server did not come up; see /tmp/appium-uitest.log" >&2
  exit 1
}

case "$PLATFORM" in
  android)
    export ANDROID_HOME="${ANDROID_HOME:-$HOME/Library/Android/sdk}"
    export ANDROID_SDK_ROOT="${ANDROID_SDK_ROOT:-$ANDROID_HOME}"
    export PATH="$ANDROID_HOME/emulator:$ANDROID_HOME/platform-tools:$PATH"
    if ! adb devices | grep -qw device; then
      AVD="${ANDROID_AVD:-$(emulator -list-avds | head -n1)}"
      [ -n "$AVD" ] || { echo "!! no emulator online and no AVD found" >&2; exit 1; }
      echo ">> Booting Android emulator: $AVD"
      emulator -avd "$AVD" -no-snapshot-save >/tmp/emulator-uitest.log 2>&1 &
      adb wait-for-device
      until [ "$(adb shell getprop sys.boot_completed 2>/dev/null | tr -d '\r')" = "1" ]; do sleep 2; done
    fi
    # EmbedAssembliesIntoApk:否则 Debug 是 fast-deploy 包(无内嵌程序集),手动安装后启动即崩
    dotnet build Example/ExampleAShellApp/ExampleAShellApp.csproj -c Debug -f net10.0-android \
      -p:EmbedAssembliesIntoApk=true
    export UITEST_APP_PATH="$REPO_ROOT/Example/ExampleAShellApp/bin/Debug/net10.0-android/com.companyname.exampleashellapp-Signed.apk"
    ;;
  ios)
    DEVICE_NAME="${IOS_DEVICE:-iPhone 16}"
    UDID="$(xcrun simctl list devices available -j | python3 -c "
import sys, json
data = json.load(sys.stdin)
for runtimes in data['devices'].values():
    for d in runtimes:
        if d['name'] == '$DEVICE_NAME':
            print(d['udid']); sys.exit(0)
")"
    [ -n "$UDID" ] || { echo "!! simulator '$DEVICE_NAME' not found" >&2; exit 1; }
    echo ">> Booting iOS simulator: $DEVICE_NAME ($UDID)"
    xcrun simctl boot "$UDID" 2>/dev/null || true
    open -a Simulator
    dotnet build Example/ExampleAShellApp/ExampleAShellApp.csproj -c Debug -f net10.0-ios26.5 \
      -p:RuntimeIdentifier=iossimulator-arm64
    export UITEST_APP_PATH="$REPO_ROOT/Example/ExampleAShellApp/bin/Debug/net10.0-ios26.5/iossimulator-arm64/ExampleAShellApp.app"
    export UITEST_DEVICE_NAME="$DEVICE_NAME" UITEST_DEVICE_UDID="$UDID"
    ;;
  maccatalyst)
    dotnet build Example/ExampleAShellApp/ExampleAShellApp.csproj -c Debug -f net10.0-maccatalyst26.5
    ;;
  *)
    echo "!! unsupported platform '$PLATFORM' (use android|ios|maccatalyst)" >&2
    exit 1
    ;;
esac

ensure_appium
dotnet test Tests/AdaptiveShell.UITests/AdaptiveShell.UITests.csproj "$@"
