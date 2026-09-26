#!/usr/bin/env python3
"""把各 cell 上传的截图 artifact 汇总成报告站点(report/index.html + report/report.md)。

输入: <dir>/shots/<artifact-name>/**.png(artifact 名即 cell 标识)
环境: NEEDS_JSON(各 needs job 的 result)、RUN_URL、RUN_SHA
"""
import html
import json
import os
import sys
from pathlib import Path

JOB_LABELS = {
    "uitest-ios-18": "iOS 18",
    "uitest-ios-26": "iOS 26",
    "uitest-ios-27": "iOS 27 (experimental)",
    "uitest-android": "Android",
    "uitest-windows": "Windows",
    "uitest-maccatalyst": "MacCatalyst (experimental)",
}


def main() -> None:
    root = Path(sys.argv[1])
    shots_root = root / "shots"
    needs = json.loads(os.environ.get("NEEDS_JSON", "{}"))
    run_url = os.environ.get("RUN_URL", "")
    sha = os.environ.get("RUN_SHA", "")[:8]

    # cell 名(artifact 名)-> 截图列表;cell 状态按前缀映射回 needs 的 job result
    cells = {}
    if shots_root.is_dir():
        for artifact_dir in sorted(shots_root.iterdir()):
            if not artifact_dir.is_dir():
                continue
            pngs = sorted(artifact_dir.rglob("*.png"))
            cells[artifact_dir.name] = pngs

    def job_result_for(cell_name: str) -> str:
        if cell_name.startswith("shots-android"):
            key = "uitest-android"
        elif cell_name.startswith("shots-ios18"):
            key = "uitest-ios-18"
        elif cell_name.startswith("shots-ios26"):
            key = "uitest-ios-26"
        elif cell_name.startswith("shots-ios27"):
            key = "uitest-ios-27"
        elif cell_name.startswith("shots-windows"):
            key = "uitest-windows"
        else:
            key = "uitest-maccatalyst"
        return needs.get(key, {}).get("result", "unknown")

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
        ".bad{color:#c00}.ok{color:#080}</style>",
        f"<h1>AdaptiveShell E2E 报告 <small>{sha}</small></h1>",
        f"<p><a href='{run_url}'>workflow run</a></p>",
    ]

    for cell, pngs in cells.items():
        result = job_result_for(cell)
        cls = "ok" if result == "success" else "bad"
        title = html.escape(cell.removeprefix("shots-"))
        md.append(f"## {title} — {result}")
        md.append("")
        html_parts.append(f"<section><h2>{title} <span class='{cls}'>{result}</span></h2>")
        for png in pngs:
            rel = png.relative_to(root).as_posix()
            label = html.escape(png.stem)
            md.append(f"![{label}]({rel})")
            html_parts.append(
                f"<a href='{rel}'><img src='{rel}' alt='{label}' title='{label}'></a>")
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
