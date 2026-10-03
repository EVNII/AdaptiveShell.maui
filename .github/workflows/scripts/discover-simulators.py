#!/usr/bin/env python3
"""从当前机器可用的 iOS 模拟器里挑测试矩阵 cell,输出 GitHub Actions matrix JSON。

用法: discover-simulators.py <major>     # major 如 18 / 26 / 27

规则:取目标 major 的最高 minor runtime,在其中挑
  - 首个普通 iPhone     -> form=compact(小屏)
  - 首个 iPad           -> form=wide(大屏)
目标 runtime 不存在时输出 {"include":[]} — 对应 cell 不生成,不算失败。
"""
import json
import re
import subprocess
import sys


def main() -> None:
    target_major = int(sys.argv[1])
    data = json.loads(
        subprocess.check_output(
            ["xcrun", "simctl", "list", "devices", "available", "-j"]))

    best_runtime = None
    best_minor = -1
    for runtime in data["devices"]:
        m = re.search(r"SimRuntime\.iOS-(\d+)-(\d+)", runtime)
        if not m or int(m.group(1)) != target_major:
            continue
        minor = int(m.group(2))
        if minor > best_minor:
            best_minor = minor
            best_runtime = runtime

    cells = []
    if best_runtime is not None:
        phone = pad = None
        for d in data["devices"][best_runtime]:
            name = d["name"]
            if name.startswith("iPhone") and "Duo" in name:
                # Duo 不参与自动 E2E 或发布门禁。
                continue
            elif name.startswith("iPhone"):
                phone = phone or d
            elif name.startswith("iPad"):
                pad = pad or d
        if phone:
            cells.append({"device": phone["name"], "udid": phone["udid"],
                          "form": "compact"})
        if pad:
            cells.append({"device": pad["name"], "udid": pad["udid"],
                          "form": "wide"})

    print(json.dumps({"include": cells}, separators=(",", ":")))


if __name__ == "__main__":
    main()
