#!/usr/bin/env python3
"""把各 cell 上传的截图 artifact 汇总成报告站点(report/index.html + report/report.md)。

输入: <dir>/shots/<artifact-name>/**.png 和 **.trx(artifact 名即 cell 标识)
环境: NEEDS_JSON(各 needs job 的 result)、RUN_URL、RUN_SHA
"""
import html
import json
import os
import subprocess
import sys
from pathlib import Path
from xml.etree import ElementTree

JOB_LABELS = {
    "uitest-ios-18": "iOS 18",
    "uitest-ios-26": "iOS 26",
    "uitest-ios-27": "iOS 27 (experimental)",
    "uitest-android": "Android",
    "uitest-windows": "Windows",
    "uitest-maccatalyst": "MacCatalyst (experimental)",
}
LANDING_SHOT_LABELS = {
    "40-landing-theme-light": "Landing page · 浅色",
    "41-landing-theme-dark": "Landing page · 深色",
    "42-landing-theme-light-restored": "Landing page · 恢复浅色",
    "43-landing-dark-music-child-clicked": "深色 · 音乐子页面（已点击）",
    "44-landing-dark-music-returned": "深色 · 从音乐返回 Landing page",
    "45-landing-dark-photos-child-clicked": "深色 · 相册子页面（已点击）",
    "46-landing-dark-photos-returned": "深色 · 从相册返回 Landing page",
}


def trx_result(artifact_dir: Path) -> dict:
    """只用本 cell 的 TRX 报告结果,不把矩阵汇总状态当成单个 cell 的结果。"""
    unknown = {"result": "unknown", "passed": None, "failed": None, "skipped": None}
    trxs = sorted(artifact_dir.rglob("*.trx"))
    if not trxs:
        return {**unknown, "detail": "缺少 TRX; 测试是否运行或完成未知"}
    if len(trxs) != 1:
        return {**unknown, "detail": "存在多个 TRX; 无法确定本 cell 的唯一结果"}

    try:
        test_run = ElementTree.parse(trxs[0]).getroot()
        summary = test_run.find("./{*}ResultSummary")
        counters = summary.find("./{*}Counters") if summary is not None else None
        if test_run.tag.rsplit("}", 1)[-1] != "TestRun" or counters is None:
            raise ValueError("缺少 TestRun/ResultSummary/Counters")

        # NUnit/VSTest 的跳过结果可能只出现在 UnitTestResult 中,而
        # Counters.notExecuted 仍为 0。逐条核对结果,避免把缺失记录当成跳过。
        counts = {name: int(counters.attrib[name])
                  for name in ("total", "executed", "passed", "failed", "notExecuted")}
        failure_counts = [int(counters.get(name, "0")) for name in
                          ("error", "timeout", "aborted", "notRunnable", "disconnected",
                           "passedButRunAborted")]
        if any(value < 0 for value in (*counts.values(), *failure_counts)):
            raise ValueError("测试计数不能为负数")

        tests = test_run.findall("./{*}Results/{*}UnitTestResult")
        outcomes = [test.attrib["outcome"].lower() for test in tests]
        passed = outcomes.count("passed")
        skipped = outcomes.count("notexecuted")
        failed = sum(outcome in ("failed", "error", "timeout", "aborted", "notrunnable",
                                 "disconnected", "passedbutrunaborted") for outcome in outcomes)
        if len(tests) != counts["total"]:
            raise ValueError("测试结果记录不完整或 total 不一致")
        if passed + failed + skipped != len(tests):
            raise ValueError("存在未完成或未知的测试结果")
        if (passed != counts["passed"]
                or failed != counts["failed"] + sum(failure_counts)
                or passed + failed != counts["executed"]
                or counts["notExecuted"] not in (0, skipped)):
            raise ValueError("测试结果记录与汇总计数不一致")

        outcome = summary.attrib["outcome"].lower()
        if failed or outcome in ("failed", "error", "aborted", "timeout"):
            result, detail = "failure", ""
        elif outcome not in ("completed", "passed", "notexecuted"):
            result, detail = "unknown", "TRX 未确认所有测试完成"
        elif counts["executed"] == 0 and skipped == counts["total"]:
            result, detail = "skipped", "未执行任何测试"
        elif outcome in ("completed", "passed") and passed + skipped == counts["total"]:
            result, detail = "success", ""
        else:
            result, detail = "unknown", "TRX 未确认所有测试完成"
        return {"result": result, "passed": passed, "failed": failed, "skipped": skipped,
                "detail": detail}
    except (OSError, ElementTree.ParseError, KeyError, ValueError) as error:
        return {**unknown, "detail": f"TRX 无效: {error}"}


def cell_result(artifact_dir: Path, job_result: str | None = None) -> dict:
    report = platform_result(artifact_dir, job_result)
    if artifact_dir.name.startswith("shots-android-api"):
        raw_theme_result(artifact_dir, report, "android-system-bars-checks.json",
                         "verify_android_system_bars.py", "visibility_status", "Android 状态栏与系统导航键")
    if ((artifact_dir.name.startswith("shots-android-api") and artifact_dir.name.endswith("-compact"))
            or (artifact_dir.name.startswith("shots-ios") and "-compact-" in artifact_dir.name)):
        raw_theme_result(artifact_dir, report, "landing-theme-checks.json",
                         "verify_landing_theme.py", "color_status", "Landing page 背景、入口图标、文字和深色子页面")
    return report


def raw_theme_result(artifact_dir: Path, report: dict, filename: str,
                     script: str, status_key: str, label: str) -> None:
    """汇总和下载后的原始证据均需通过；缺失深色 landing 不能显示绿灯。"""
    def validate(checks: dict) -> None:
        expected = {"analyses": 3, "matches": 3, "mismatches": 0, "errors": 0}
        if any(type(checks["summary"][key]) is not int or checks["summary"][key] != value
               for key, value in expected.items()):
            raise ValueError("三阶段核对未全部通过")
        results = checks["results"]
        if (not isinstance(results, list) or len(results) != 3
                or not isinstance(checks["errors"], list) or checks["errors"]
                or {result["stage"] for result in results} != {"light", "dark", "light-restored"}
                or any(result[status_key] != "match" for result in results)):
            raise ValueError("缺少完整的浅色、深色、恢复浅色证据")

    try:
        paths = sorted(artifact_dir.rglob(filename))
        if len(paths) != 1:
            raise ValueError("缺少或存在多个截图核对结果")
        validate(json.loads(paths[0].read_text(encoding="utf-8-sig")))
        verifier = Path(__file__).resolve().parents[3] / "Tests/AdaptiveShell.UITests/Scripts" / script
        verified = subprocess.run([sys.executable, str(verifier), str(artifact_dir), "--strict"],
                                  capture_output=True, text=True, timeout=60)
        if verified.returncode != 0:
            raise ValueError("下载后的原始截图、原生坐标或导航证据未通过重新核对")
        validate(json.loads(verified.stdout))
        detail = f"{label}：3/3 通过（浅色、深色、恢复浅色）"
    except (OSError, json.JSONDecodeError, KeyError, TypeError, ValueError, subprocess.TimeoutExpired) as error:
        report["result"] = "failure"
        detail = f"{label}：未通过；{error}"
    previous = report.get("appearance_detail", "")
    report["appearance_detail"] = (previous + "；" if previous else "") + detail


def platform_result(artifact_dir: Path, job_result: str | None = None) -> dict:
    report = trx_result(artifact_dir)
    if artifact_dir.name == "shots-windows":
        return windows_result(artifact_dir, report, job_result)
    if artifact_dir.name != "shots-maccatalyst":
        return report

    # Mac 外观检查在 dotnet test 之后运行，不能仅凭成功的 TRX 显示绿灯。
    checks = sorted(artifact_dir.rglob("button-colors.json"))
    try:
        if len(checks) != 1:
            raise ValueError("缺少或存在多个按钮颜色核对结果")
        colors = json.loads(checks[0].read_text(encoding="utf-8"))
        summary = colors["summary"]
        expected = {"analyses": 6, "matches": 6, "mismatches": 0, "errors": 0}
        if any(type(summary[key]) is not int for key in expected):
            raise ValueError("按钮颜色核对计数无效")
        if any(summary[key] != value for key, value in expected.items()):
            raise ValueError(
                f"按钮颜色核对未通过：匹配 {summary['matches']}/6，"
                f"颜色不符 {summary['mismatches']}，证据错误 {summary['errors']}")
        results = colors["results"]
        phases = {(stage, clicked) for stage in ("light", "dark", "light-restored")
                  for clicked in (False, True)}
        if (len(results) != 6 or colors["errors"]
                or any(result["color_status"] != "match" for result in results)
                or {(result["stage"], result["after_click"]) for result in results} != phases):
            raise ValueError("按钮颜色核对缺少完整的浅色、深色、恢复浅色点击前后证据")
        report["appearance_detail"] = "按钮颜色：6/6 通过（浅色、深色、恢复浅色，点击前后）"
    except (OSError, json.JSONDecodeError, KeyError, TypeError, ValueError) as error:
        report["result"] = "failure"
        report["appearance_detail"] = f"按钮颜色：未通过；{error}"

    if job_result is not None and job_result != "success":
        report["result"] = "failure"
        previous = report["detail"]
        report["detail"] = (previous + "；" if previous else "") + f"Mac E2E job 为 {job_result}，完整门禁未通过"
    return report


def windows_result(artifact_dir: Path, report: dict, job_result: str | None) -> dict:
    # 原生导航变色并不证明 MAUI 页面已跟随主题;同时核对背景、按钮及图标。
    checks = sorted(artifact_dir.rglob("windows-theme.json"))
    try:
        if len(checks) != 1:
            raise ValueError("缺少或存在多个 Windows 主题截图核对结果")
        theme = json.loads(checks[0].read_text(encoding="utf-8-sig"))
        summary = theme["summary"]
        expected = {"analyses": 3, "matches": 3, "mismatches": 0, "errors": 0}
        if any(type(summary[key]) is not int or summary[key] != value
               for key, value in expected.items()):
            raise ValueError("Windows 主题截图核对未完整通过 3 个阶段")
        results = theme["results"]
        if (len(results) != 3 or theme["errors"]
                or {result["stage"] for result in results} != {"light", "dark", "light-restored"}
                or any(result["color_status"] != "match" for result in results)):
            raise ValueError("缺少完整的浅色、深色、恢复浅色截图证据")
        for result in results:
            visual = result["checks"]
            if (visual["page_background"]["status"] != "match"
                    or visual["counter_button_background"]["status"] != "match"
                    or set(visual["navigation_icons"]) != {"home", "home2", "media"}
                    or any(icon["status"] != "match" for icon in visual["navigation_icons"].values())):
                raise ValueError("页面背景、按钮或导航图标未通过截图核对")
        # 下载后的原始证据再核对一次;只有成功 JSON、截图缺失也不能显示绿灯。
        verifier = Path(__file__).resolve().parents[3] / "Tests/AdaptiveShell.UITests/Scripts/verify_windows_theme.py"
        verified = subprocess.run([sys.executable, str(verifier), str(artifact_dir), "--strict"],
                                  capture_output=True, text=True, timeout=60)
        if verified.returncode != 0:
            raise ValueError("下载后的 Windows 原始截图或坐标未通过重新核对")
        report["appearance_detail"] = "Windows 主题：3/3 通过（浅色、深色、恢复浅色；背景、按钮、导航图标）"
    except (OSError, json.JSONDecodeError, KeyError, TypeError, ValueError, subprocess.TimeoutExpired) as error:
        report["result"] = "failure"
        report["appearance_detail"] = f"Windows 主题：未通过；{error}"
    if job_result != "success":
        report["result"] = "failure"
        previous = report["detail"]
        report["detail"] = (previous + "；" if previous else "") + f"Windows E2E job 为 {job_result}，完整门禁未通过"
    return report


def main() -> None:
    root = Path(sys.argv[1])
    shots_root = root / "shots"
    needs = json.loads(os.environ.get("NEEDS_JSON", "{}"))
    run_url = os.environ.get("RUN_URL", "")
    sha = os.environ.get("RUN_SHA", "")[:8]

    # artifact 名即 cell 标识;截图和 TRX 结果来自同一个 artifact。
    cells = {}
    if shots_root.is_dir():
        for artifact_dir in sorted(shots_root.iterdir()):
            if not artifact_dir.is_dir():
                continue
            # Keep provider comparison PNGs in the downloadable evidence, while
            # the gallery shows the original screenshots used by the UI checks.
            pngs = sorted(png for png in artifact_dir.rglob("*.png")
                          if "provider-evidence" not in png.relative_to(artifact_dir).parts)
            job_key = "uitest-windows" if artifact_dir.name == "shots-windows" else "uitest-maccatalyst"
            job_result = needs.get(job_key, {}).get("result")
            cells[artifact_dir.name] = (pngs, cell_result(artifact_dir, job_result))

    md = ["# AdaptiveShell E2E 报告", "",
          f"- 运行: {run_url}", f"- commit: `{sha}`", "", "## Job 状态", ""]
    md.append("| Job | 结果 |")
    md.append("|---|---|")
    for key, label in JOB_LABELS.items():
        result = needs.get(key, {}).get("result", "unknown")
        md.append(f"| {label} | {result} |")
    md.append("")

    html_parts = [
        "<!doctype html><meta charset='utf-8'>",
        "<title>AdaptiveShell E2E 报告</title>",
        "<style>body{font-family:system-ui;margin:2rem} "
        "section{margin-bottom:2.5rem} img{max-width:320px;margin:4px;"
        "border:1px solid #ddd;border-radius:6px} "
        ".bad{color:#c00}.ok{color:#080}.unknown{color:#666}</style>",
        f"<h1>AdaptiveShell E2E 报告 <small>{sha}</small></h1>",
        f"<p><a href='{run_url}'>workflow run</a></p>",
        "<h2>Job 状态</h2><table><thead><tr><th>Job</th><th>结果</th></tr></thead><tbody>",
    ]
    for key, label in JOB_LABELS.items():
        result = needs.get(key, {}).get("result", "unknown")
        cls = "ok" if result == "success" else "bad" if result in ("failure", "cancelled") else "unknown"
        html_parts.append(f"<tr><td>{html.escape(label)}</td><td class='{cls}'>{html.escape(result)}</td></tr>")
    html_parts.append("</tbody></table>")

    for cell, (pngs, report) in cells.items():
        result = report["result"]
        cls = "ok" if result == "success" else "bad" if result == "failure" else "unknown"
        title = html.escape(cell.removeprefix("shots-"))
        md.append(f"## {title} — {result}")
        md.append("")
        html_parts.append(f"<section><h2>{title} <span class='{cls}'>{result}</span></h2>")
        if report["passed"] is not None:
            counts = (f"通过 {report['passed']} · 失败 {report['failed']} · "
                      f"跳过 {report['skipped']}")
            md.extend([counts, ""])
            html_parts.append(f"<p>{counts}</p>")
        if report["detail"]:
            detail = html.escape(report["detail"])
            md.extend([detail, ""])
            html_parts.append(f"<p>{detail}</p>")
        if report.get("appearance_detail"):
            md.extend([report["appearance_detail"], ""])
            html_parts.append(f"<p>{html.escape(report['appearance_detail'])}</p>")
        for png in pngs:
            rel = png.relative_to(root).as_posix()
            label = html.escape(LANDING_SHOT_LABELS.get(png.stem, png.stem))
            md.append(f"![{label}]({rel})")
            picture = f"<a href='{rel}'><img src='{rel}' alt='{label}' title='{label}'></a>"
            if png.stem in LANDING_SHOT_LABELS:
                html_parts.append(f"<figure style='display:inline-block;vertical-align:top;margin:4px;max-width:320px'>"
                                  f"{picture}<figcaption>{label}</figcaption></figure>")
            else:
                html_parts.append(picture)
        md.append("")
        html_parts.append("</section>")

    if not cells:
        md.append("(没有截图 artifact)")
        html_parts.append("<p>没有截图 artifact</p>")

    (root / "report.md").write_text("\n".join(md), encoding="utf-8")
    (root / "index.html").write_text("\n".join(html_parts), encoding="utf-8")
    print(f"report generated: {len(cells)} cells")


if __name__ == "__main__":
    main()
