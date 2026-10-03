#!/usr/bin/env bash
# ReactiveCircus executes each input line in a new shell; this one wrapper owns
# the EXIT trap so post-test observations happen before its emulator cleanup.
set -e
pre_capture_exit=0
python3 .github/workflows/scripts/android35-pre-aut-capture.py --phase pre-aut || pre_capture_exit=$?
finish_observations() {
    original_exit=$?
    trap - EXIT
    set +e
    python3 .github/workflows/scripts/android35-pre-aut-capture.py --phase post-tests --test-exit-code "$original_exit"
    post_capture_exit=$?
    if [ "$original_exit" -ne 0 ]; then exit "$original_exit"; fi
    if [ "$pre_capture_exit" -ne 0 ]; then exit "$pre_capture_exit"; fi
    exit "$post_capture_exit"
}
trap finish_observations EXIT

adb shell settings put global anr_show_background 0 || true
until curl -s --max-time 2 http://127.0.0.1:4723/status >/dev/null; do sleep 2; done
UITEST_ANDROID_THEME_ROOT=false UITEST_PLATFORM=android UITEST_FORM=compact UITEST_APP_PATH="$GITHUB_WORKSPACE/app/com.companyname.exampleashellapp-Signed.apk" dotnet test Tests/AdaptiveShell.UITests/AdaptiveShell.UITests.csproj --logger "trx;LogFileName=e2e.trx" --results-directory TestResults
