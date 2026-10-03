#!/usr/bin/env python3
"""CI-only exact-source app/test handoff for the production Duo split build.

Copies exact app/test bin+obj and their public test-package closure. Never copies
NuGet.Config, profile credentials, MAUI SDK packs, WDA products or device state.
No compilation, restore, simulator commands or process termination occurs here.
"""
import argparse
from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import re
import shutil
import stat
import subprocess
import tarfile
import tempfile
import xml.etree.ElementTree as ET
from urllib.parse import urlparse
from urllib.request import Request, urlopen

SCHEMA = 1
WORKFLOW = ".github/workflows/release-uitest.yml"
PRODUCER_JOB = "build-ios-27-1-duo"
CONSUMER_JOB = "uitest-ios-27-1-duo"
EXPECTED_PASS = frozenset({
    "Launch_ShowsAllTopLevelItems", "SelectItem_SwitchesContent", "Group_NavigatesToChildAndBack",
    "EveryNavItem_HasNonEmptyAccessibilityLabel", "NavItemIdentifiers_AreStableAcrossNavigation",
    "Chrome_MatchesExpectedFormFactor", "DarkMode_ShellAndGroupFlow", "DarkMode_GroupLandingPage",
})
EXPECTED_SKIP = frozenset({
    "WindowButtons_AreContainedInSidebar", "Button_EnabledAndClickableAcrossLightDarkLight",
    "WideForm_RailGroupOpensDrawer", "RailToggle_ExpandsAndCollapsesRail",
    "DarkMode_WideDrawerAndExpandedRailStayOpen",
})
EXPECTED_TESTS = EXPECTED_PASS | EXPECTED_SKIP
ROOTS = (
    "Example/ExampleAShellApp/bin", "Example/ExampleAShellApp/obj",
    "Tests/AdaptiveShell.UITests/bin", "Tests/AdaptiveShell.UITests/obj",
)
APP = "Example/ExampleAShellApp/bin/Debug/net10.0-ios26.5/iossimulator-arm64/ExampleAShellApp.app"
TEST_DIR = "Tests/AdaptiveShell.UITests/bin/Debug/net10.0"
ASSETS = "Tests/AdaptiveShell.UITests/obj/project.assets.json"
EVIDENCE = ("duo-build-info.txt", "duo-native-build.txt", "duo-producer-build.log",
            "duo-producer-test-build.log", "duo-producer-build-server-shutdown.txt")
EXCLUDED_PACKAGE_NAMES = {"nuget.config"}


def require(condition, message):
    if not condition:
        raise ValueError(message)


def digest(path):
    value = hashlib.sha256()
    with Path(path).open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            value.update(block)
    return value.hexdigest()


def dump(path, value):
    Path(path).write_text(json.dumps(value, indent=2, sort_keys=True) + "\n", encoding="utf-8")


def command(args, cwd=None, timeout=30):
    result = subprocess.run(args, cwd=cwd, capture_output=True, timeout=timeout)
    require(result.returncode == 0, f"Read-only command failed: {args[0]} exit {result.returncode}")
    return result.stdout.decode("utf-8").strip()


def safe_relative(value):
    path = PurePosixPath(value)
    require(isinstance(value, str) and value and not path.is_absolute()
            and all(part not in ("", ".", "..") for part in path.parts)
            and str(path) == value and "\\" not in value, "Unsafe relative path")
    return path


def under(path, root):
    try:
        Path(path).relative_to(root)
        return True
    except ValueError:
        return False


def iter_tree(root):
    require(root.is_dir() and not root.is_symlink(), f"Required tree missing or symbolic: {root}")
    yield root
    for directory, names, files in os.walk(root, followlinks=False):
        for name in sorted(names + files):
            yield Path(directory) / name


def entry(path, relative):
    info = path.lstat()
    row = {"path": relative, "mode": stat.S_IMODE(info.st_mode), "mtime_ns": info.st_mtime_ns}
    if stat.S_ISLNK(info.st_mode):
        row.update({"type": "symlink", "target": os.readlink(path)})
    elif stat.S_ISDIR(info.st_mode):
        row["type"] = "directory"
    elif stat.S_ISREG(info.st_mode):
        row.update({"type": "file", "bytes": info.st_size, "sha256": digest(path)})
    else:
        raise ValueError(f"Unsupported special file: {relative}")
    return row


def tree_entries(root, prefix, package=False):
    rows = []
    for path in iter_tree(root):
        # Config is deliberately not transferred even if a public package includes a sample.
        if package and (path.name.lower() in EXCLUDED_PACKAGE_NAMES or path.name.endswith(".nupkg")):
            require(not path.is_dir(), "Unexpected package configuration directory")
            continue
        relative = str(PurePosixPath(prefix) / path.relative_to(root).as_posix())
        rows.append(entry(path, relative))
    return rows


def tracked_source(workspace):
    names = subprocess.run(["git", "ls-files", "-z"], cwd=workspace,
                           capture_output=True, timeout=30, check=True).stdout.split(b"\0")
    rows = []
    for raw in names:
        if not raw:
            continue
        name = raw.decode("utf-8")
        safe_relative(name)
        path = workspace / name
        require(path.exists() or path.is_symlink(), f"Tracked source missing: {name}")
        row = entry(path, name)
        row.pop("mtime_ns")
        rows.append(row)
    return sorted(rows, key=lambda row: row["path"])


def ci_identity():
    require(os.environ.get("GITHUB_ACTIONS") == "true" and os.environ.get("RUNNER_OS") == "macOS",
            "This transfer command is restricted to ephemeral GitHub macOS CI")
    require(os.environ.get("GITHUB_REPOSITORY") == "EVNII/AdaptiveShell.maui"
            and os.environ.get("RUNNER_ENVIRONMENT") == "github-hosted"
            and os.environ.get("GITHUB_REF_NAME") == "codex/full31-split-review"
            and os.environ.get("GITHUB_WORKFLOW") == "Full31 Split Review"
            and os.environ.get("GITHUB_JOB") in (PRODUCER_JOB, CONSUMER_JOB),
            "Handoff is restricted to the exact controlled full31 split review workflow")
    workspace = Path(os.environ["GITHUB_WORKSPACE"]).resolve()
    source_sha = command(["git", "rev-parse", "HEAD"], workspace)
    require(re.fullmatch(r"[0-9a-f]{40}", source_sha) is not None
            and source_sha == os.environ["GITHUB_SHA"], "Checkout/source SHA mismatch")
    repository = os.environ["GITHUB_REPOSITORY"]
    workflow_ref = os.environ["GITHUB_WORKFLOW_REF"]
    require(workflow_ref.startswith(repository + "/"), "Unexpected workflow repository")
    workflow_path = workflow_ref[len(repository) + 1:].split("@", 1)[0]
    safe_relative(workflow_path)
    require(workflow_path == WORKFLOW, "This candidate must run from the registered release-uitest.yml")
    workflow_source_sha = os.environ["GITHUB_WORKFLOW_SHA"]
    require(workflow_source_sha == source_sha, "Executed workflow/source SHA mismatch")
    sdk = command(["dotnet", "--version"])
    require(command(["xcrun", "--sdk", "iphonesimulator", "--show-sdk-version"]) == "27.1",
            "Selected simulator SDK is not 27.1")
    nuget = Path(os.environ.get("NUGET_PACKAGES", str(Path.home() / ".nuget/packages"))).resolve()
    return {
        "repository": repository, "source_sha": source_sha,
        "workflow_ref": workflow_ref, "workflow_path": workflow_path,
        "workflow_sha256": digest(workspace / WORKFLOW),
        "workflow_source_sha": workflow_source_sha,
        "run_id": os.environ["GITHUB_RUN_ID"], "run_attempt": os.environ["GITHUB_RUN_ATTEMPT"],
        "workspace": str(workspace), "nuget_root": str(nuget),
        "dotnet_sdk": sdk, "simulator_sdk": "27.1",
    }


def package_closure(workspace, nuget):
    assets = json.loads((workspace / ASSETS).read_text(encoding="utf-8"))
    folders = [Path(folder).resolve() for folder in assets.get("packageFolders", {})]
    require(folders == [nuget], "Test assets do not use the one declared global package root")
    require(assets.get("project", {}).get("restore", {}).get("packagesPath") is not None,
            "Test assets have no original packages path")
    require(Path(assets["project"]["restore"]["packagesPath"]).resolve() == nuget,
            "Test restore package path mismatch")
    closure = []
    for name, library in sorted(assets.get("libraries", {}).items()):
        require(library.get("type") == "package", "Unexpected project/other dependency in test closure")
        path = library.get("path")
        relative = safe_relative(path)
        require(len(relative.parts) == 2, "A package path must identify one package/version")
        directory = nuget / path
        require(directory.is_dir() and not directory.is_symlink(), "Required test package is absent")
        metadata = json.loads((directory / ".nupkg.metadata").read_text(encoding="utf-8"))
        source = metadata.get("source")
        require(isinstance(source, str), "Package source provenance missing")
        parsed = urlparse(source)
        require(parsed.scheme == "https" and parsed.hostname in ("api.nuget.org", "www.nuget.org", "nuget.org")
                and parsed.port in (None, 443) and not parsed.username and not parsed.password
                and not parsed.query and not parsed.fragment, "Dependency is not from the allowed public NuGet feed")
        closure.append({"library": name, "path": path, "source": source,
                        "excluded": "NuGet.Config and .nupkg archive; no restore occurs on consumer"})
    require(closure and len({row["path"] for row in closure}) == len(closure), "Missing/duplicate package closure")
    return closure


def validate_links(rows, identity):
    names = {row["path"] for row in rows}
    links = {row["path"] for row in rows if row["type"] == "symlink"}
    require(len(names) == len(rows), "Duplicate archive path")
    mappings = [(identity["workspace"] + "/" + root, "workspace/" + root) for root in ROOTS]
    mappings.append((identity["nuget_root"], "nuget"))
    for row in rows:
        name = row["path"]
        safe_relative(name)
        require(not any(str(parent) in links for parent in PurePosixPath(name).parents),
                "Archive member is nested beneath a symbolic link")
        if row["type"] != "symlink":
            continue
        target = row["target"]
        require(isinstance(target, str) and target, "Empty symlink target")
        if target.startswith("/"):
            mapped = []
            for original, prefix in mappings:
                if target == original or target.startswith(original + "/"):
                    suffix = target[len(original):].lstrip("/")
                    mapped.append(prefix + ("/" + suffix if suffix else ""))
            require(len(mapped) == 1, "Absolute symlink escapes restored original trees")
            destination = mapped[0]
        else:
            require("\\" not in target, "Invalid symbolic link target")
            destination = os.path.normpath(str(PurePosixPath(name).parent / target))
            require(not destination.startswith("../") and not destination.startswith("/"),
                    "Symbolic link escapes archive")
        require(destination in names, "Symbolic link target is absent from transferred closure")
    # Resolve chains in archive namespace without reading producer/consumer filesystem targets.
    by_name = {row["path"]: row for row in rows}
    for original in links:
        name, seen = original, set()
        while by_name[name]["type"] == "symlink":
            require(name not in seen, "Symbolic link cycle")
            seen.add(name)
            target = by_name[name]["target"]
            if target.startswith("/"):
                candidates = [prefix + ("/" + target[len(root):].lstrip("/") if target != root else "")
                              for root, prefix in mappings if target == root or target.startswith(root + "/")]
                require(len(candidates) == 1, "Ambiguous absolute link")
                name = candidates[0]
            else:
                name = os.path.normpath(str(PurePosixPath(name).parent / target))
            require(name in by_name, "Unresolved symbolic link")


def source_test_names(workspace):
    names = []
    declarations = 0
    for path in (workspace / "Tests/AdaptiveShell.UITests").glob("*.cs"):
        text = path.read_text(encoding="utf-8")
        declarations += len(re.findall(r"\[Test\]", text))
        names.extend(re.findall(r"\[Test\]\s+public\s+(?:async\s+)?(?:void|Task)\s+([A-Za-z_]\w*)\s*\(", text))
    require(declarations == len(EXPECTED_TESTS) and len(names) == len(EXPECTED_TESTS)
            and len(set(names)) == len(names) and set(names) == EXPECTED_TESTS,
            "Duo source must contain exactly the 13 distinct current expected tests")
    return sorted(names)


def verify_trx():
    identity = ci_identity()
    require(os.environ.get("GITHUB_JOB") == CONSUMER_JOB, "Unexpected suite verification job")
    require(source_test_names(Path(identity["workspace"])) == sorted(EXPECTED_TESTS),
            "Duo suite source differs")
    results = Path(identity["workspace"]) / "TestResults"
    paths = sorted(results.rglob("*.trx"))
    require(len(paths) == 1 and paths[0] == results / "e2e.trx", "Expected one exact Duo e2e.trx")
    tree = ET.parse(paths[0]).getroot()
    summary = tree.find("./{*}ResultSummary")
    require(tree.tag.rsplit("}", 1)[-1] == "TestRun" and summary is not None
            and summary.get("outcome", "").lower() in ("completed", "passed"),
            "Duo test run did not complete successfully")
    tests = tree.findall("./{*}Results/{*}UnitTestResult")
    names = [test.get("testName") for test in tests]
    require(len(tests) == len(EXPECTED_TESTS) and len(set(names)) == len(names)
            and set(names) == EXPECTED_TESTS, "Duo TRX lacks all 13 distinct expected records")
    for test in tests:
        expected = "Passed" if test.get("testName") in EXPECTED_PASS else "NotExecuted"
        require(test.get("outcome") == expected, "Unexpected Duo outcome for " + str(test.get("testName")))
    counters = summary.find("./{*}Counters")
    require(counters is not None and int(counters.get("total", "-1")) == 13
            and int(counters.get("executed", "-1")) == 8
            and int(counters.get("passed", "-1")) == 8
            and int(counters.get("failed", "-1")) == 0
            and int(counters.get("notExecuted", "-1")) in (0, 5)
            and all(int(counters.get(key, "0")) == 0 for key in
                    ("error", "timeout", "aborted", "notRunnable", "disconnected", "passedButRunAborted")),
            "Duo TRX counters disagree with 8passed/5original skips")
    proof = {"status": "verified", "identity": identity, "trx": str(paths[0]),
             "trx_sha256": digest(paths[0]), "records": 13, "passed": 8, "skipped": 5, "failed": 0,
             "tests": [{"name": test.get("testName"), "outcome": test.get("outcome")} for test in tests],
             "scope": "suite completeness only; actual Duo4phase and Landing3phase raw gates remain required"}
    dump(results / "duo-split-suite.json", proof)
    print(json.dumps(proof), flush=True)


def pack(output):
    identity = ci_identity()
    workspace, nuget = Path(identity["workspace"]), Path(identity["nuget_root"])
    require(os.environ.get("GITHUB_JOB") == PRODUCER_JOB, "Unexpected producer job")
    output = Path(output).resolve()
    require(not output.exists(), "Producer output path must be fresh")
    output.mkdir(parents=True)
    closure = package_closure(workspace, nuget)
    rows, sources = [], {}
    for root in ROOTS:
        entries = tree_entries(workspace / root, "workspace/" + root)
        rows.extend(entries)
        for row in entries:
            sources[row["path"]] = workspace / row["path"].removeprefix("workspace/")
    for package in closure:
        entries = tree_entries(nuget / package["path"], "nuget/" + package["path"], package=True)
        rows.extend(entries)
        for row in entries:
            sources[row["path"]] = nuget / row["path"].removeprefix("nuget/")
    for filename in EVIDENCE:
        path = workspace / "TestResults" / filename
        require(path.is_file() and path.stat().st_size > 0, f"Producer build evidence missing: {filename}")
        relative = "evidence/" + filename
        rows.append(entry(path, relative))
        sources[relative] = path
    rows.sort(key=lambda row: row["path"])
    validate_links(rows, identity)
    require((workspace / TEST_DIR / "AdaptiveShell.UITests.dll").is_file(), "Test assembly missing")
    for filename in ("AdaptiveShell.UITests.deps.json", "AdaptiveShell.UITests.runtimeconfig.json", "testhost.dll"):
        require((workspace / TEST_DIR / filename).is_file(), f"No-build test dependency missing: {filename}")
    require((workspace / APP / "Info.plist").is_file(), "Built app missing")
    native = (workspace / "TestResults/duo-native-build.txt").read_text(encoding="utf-8")
    require(re.search(r"sdk\s+27\.1(?:\s|$)", native), "Native build evidence does not identify SDK27.1")
    test_names = source_test_names(workspace)
    test_count = len(test_names)
    manifest = {"schema": SCHEMA, "created_utc": datetime.now(timezone.utc).isoformat(),
                "scope": "app/test build handoff only; no WDA/runtime/device state", "identity": identity,
                "source": tracked_source(workspace), "trees": list(ROOTS), "package_closure": closure,
                "entries": rows, "app_path": APP, "test_assembly": TEST_DIR + "/AdaptiveShell.UITests.dll",
                "test_count": test_count, "test_names": test_names, "producer_job": PRODUCER_JOB}
    dump(output / "manifest.json", manifest)
    with tarfile.open(output / "bundle.tar.gz", "w:gz", format=tarfile.PAX_FORMAT, compresslevel=1) as archive:
        for row in rows:
            info = tarfile.TarInfo(row["path"])
            info.mode = row["mode"]
            info.mtime = row["mtime_ns"] / 1_000_000_000
            info.pax_headers = {"mtime": str(row["mtime_ns"] / 1_000_000_000)}
            if row["type"] == "directory":
                info.type = tarfile.DIRTYPE
                archive.addfile(info)
            elif row["type"] == "symlink":
                info.type, info.linkname = tarfile.SYMTYPE, row["target"]
                archive.addfile(info)
            else:
                info.size = row["bytes"]
                with sources[row["path"]].open("rb") as stream:
                    archive.addfile(info, stream)
    proof = {"status": "packed", "identity": identity, "files": len(rows),
             "manifest_sha256": digest(output / "manifest.json"), "archive_sha256": digest(output / "bundle.tar.gz"),
             "archive_bytes": (output / "bundle.tar.gz").stat().st_size,
             "test_assembly_sha256": digest(workspace / manifest["test_assembly"]), "package_count": len(closure)}
    dump(workspace / "TestResults/duo-split-producer.json", proof)
    with Path(os.environ["GITHUB_OUTPUT"]).open("a", encoding="utf-8") as stream:
        for name, value in ("dotnet_sdk", identity["dotnet_sdk"]), ("manifest_sha256", proof["manifest_sha256"]), ("archive_sha256", proof["archive_sha256"]):
            stream.write(f"{name}={value}\n")
    print(json.dumps(proof), flush=True)


def verify_artifact_api(artifact_id, artifact_name, expected_digest, identity):
    require(re.fullmatch(r"[1-9][0-9]*", artifact_id) is not None, "Invalid producer artifact ID")
    normalized = expected_digest.removeprefix("sha256:")
    require(re.fullmatch(r"[0-9a-f]{64}", normalized) is not None, "Missing producer artifact digest")
    token = os.environ["GITHUB_TOKEN"]
    request = Request(f"https://api.github.com/repos/{identity['repository']}/actions/artifacts/{artifact_id}",
                      headers={"Authorization": "Bearer " + token, "Accept": "application/vnd.github+json",
                               "X-GitHub-Api-Version": "2022-11-28"})
    with urlopen(request, timeout=30) as response:
        info = json.load(response)
    run = info.get("workflow_run", {})
    require(info.get("id") == int(artifact_id) and info.get("name") == artifact_name
            and info.get("expired") is False and run.get("id") == int(identity["run_id"])
            and run.get("head_sha") == identity["source_sha"]
            and info.get("digest", "").removeprefix("sha256:") == normalized,
            "GitHub artifact identity/run/source/digest mismatch")
    return {key: info[key] for key in ("id", "name", "digest", "size_in_bytes", "workflow_run")}


def checked_archive(input_dir, manifest, destination):
    rows = manifest["entries"]
    expected = {row["path"]: row for row in rows}
    require(len(expected) == len(rows), "Duplicate manifest member")
    validate_links(rows, manifest["identity"])
    with tarfile.open(input_dir / "bundle.tar.gz", "r:gz") as archive:
        members = archive.getmembers()
        require(len(members) == len(expected) and len({m.name for m in members}) == len(members),
                "Missing or duplicate archive members")
        for member in members:
            safe_relative(member.name)
            require(member.name in expected, "Unmanifested archive member")
            row = expected[member.name]
            actual_type = "directory" if member.isdir() else "file" if member.isfile() else "symlink" if member.issym() else "other"
            require(row["type"] == actual_type and member.mode == row["mode"], "Archive member type/mode mismatch")
            require(not member.islnk(), "Archive hard links are not allowed")
            if actual_type == "file":
                require(member.size == row["bytes"], "Archive member size mismatch")
            if actual_type == "symlink":
                require(member.linkname == row["target"], "Archive symlink changed")
        # Manual extraction never follows links. Create symbolic links last.
        for member in sorted(members, key=lambda m: (not m.isdir(), len(PurePosixPath(m.name).parts), m.name)):
            path = destination / member.name
            if member.isdir():
                path.mkdir(parents=True, exist_ok=True)
            elif member.isfile():
                path.parent.mkdir(parents=True, exist_ok=True)
                with archive.extractfile(member) as source, path.open("xb") as output:
                    shutil.copyfileobj(source, output)
            else:
                continue
            os.chmod(path, member.mode)
            row = expected[member.name]
            os.utime(path, ns=(row["mtime_ns"], row["mtime_ns"]))
        for member in members:
            if member.issym():
                path = destination / member.name
                path.parent.mkdir(parents=True, exist_ok=True)
                os.symlink(member.linkname, path)
                try:
                    os.chmod(path, member.mode, follow_symlinks=False)
                except (NotImplementedError, OSError):
                    pass
        for row in rows:
            path = destination / row["path"]
            info = entry(path, row["path"])
            require(all(info.get(key) == value for key, value in row.items() if key != "mtime_ns"),
                    "Extracted file/hash/mode/link mismatch")
    return expected


def verify_installed_tree(directory, rows, prefix, package=False):
    actual = tree_entries(directory, prefix, package=package)
    clean = lambda items: sorted(({k: v for k, v in row.items() if k != "mtime_ns"} for row in items), key=lambda row: row["path"])
    require(clean(actual) == clean(rows), "An existing/restored tree differs from producer files")


def consume(args):
    identity = ci_identity()
    require(os.environ.get("GITHUB_JOB") == CONSUMER_JOB, "Unexpected consumer job")
    input_dir = Path(args.input).resolve()
    require(input_dir.is_dir() and {path.name for path in input_dir.iterdir()} == {"manifest.json", "bundle.tar.gz"}
            and all(path.is_file() and not path.is_symlink() for path in input_dir.iterdir()),
            "Expected one flat immutable artifact with exactly manifest.json and bundle.tar.gz")
    require(digest(input_dir / "manifest.json") == args.manifest_sha256
            and digest(input_dir / "bundle.tar.gz") == args.archive_sha256, "Producer payload hash mismatch")
    manifest = json.loads((input_dir / "manifest.json").read_text(encoding="utf-8"))
    require(manifest.get("schema") == SCHEMA and manifest.get("identity") == identity,
            "Producer/consumer source/workflow/run/path/SDK identities differ; no relocation guessing is allowed")
    require(manifest.get("source") == tracked_source(Path(identity["workspace"])), "Consumer source files differ")
    require(manifest.get("trees") == list(ROOTS) and manifest.get("test_count") == len(EXPECTED_TESTS)
            and manifest.get("test_names") == source_test_names(Path(identity["workspace"]))
            and manifest.get("producer_job") == PRODUCER_JOB
            and manifest.get("app_path") == APP and manifest.get("test_assembly") == TEST_DIR + "/AdaptiveShell.UITests.dll",
            "Unexpected build handoff scope")
    artifact = verify_artifact_api(args.artifact_id, args.artifact_name, args.artifact_digest, identity)
    workspace, nuget = Path(identity["workspace"]), Path(identity["nuget_root"])
    require(all(not (workspace / root).exists() and not (workspace / root).is_symlink() for root in ROOTS),
            "Fresh consumer already has app/test build outputs; refusing merge")
    evidence = workspace / "TestResults"
    evidence.mkdir(exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="duo-split-extract-", dir=os.environ["RUNNER_TEMP"]) as temporary:
        extracted = Path(temporary)
        checked_archive(input_dir, manifest, extracted)
        for root in ROOTS:
            prefix = "workspace/" + root
            rows = [row for row in manifest["entries"] if row["path"] == prefix or row["path"].startswith(prefix + "/")]
            target = workspace / root
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copytree(extracted / prefix, target, symlinks=True)
            verify_installed_tree(target, rows, prefix)
        nuget.mkdir(parents=True, exist_ok=True)
        for package in manifest["package_closure"]:
            relative = str(safe_relative(package["path"]))
            prefix = "nuget/" + relative
            rows = [row for row in manifest["entries"] if row["path"] == prefix or row["path"].startswith(prefix + "/")]
            target = nuget / relative
            if target.exists() or target.is_symlink():
                require(not target.is_symlink(), "Existing package directory is a symlink")
                verify_installed_tree(target, rows, prefix, package=True)
            else:
                target.parent.mkdir(parents=True, exist_ok=True)
                shutil.copytree(extracted / prefix, target, symlinks=True)
                verify_installed_tree(target, rows, prefix, package=True)
        for name in EVIDENCE:
            require(not (evidence / name).exists(), "Consumer build evidence already exists")
            shutil.copy2(extracted / "evidence" / name, evidence / name)
    require(package_closure(workspace, nuget) == manifest["package_closure"], "Installed package provenance/closure mismatch")
    require((workspace / manifest["test_assembly"]).is_file() and (workspace / APP / "Info.plist").is_file(),
            "Restored app/test paths cannot be located")
    restore = json.loads((workspace / ASSETS).read_text(encoding="utf-8"))["project"]["restore"]
    require(Path(restore["projectPath"]).resolve() == workspace / "Tests/AdaptiveShell.UITests/AdaptiveShell.UITests.csproj"
            and Path(restore["outputPath"]).resolve() == workspace / "Tests/AdaptiveShell.UITests/obj",
            "No-build/no-restore project assets point to a different original workspace")
    sdk = command(["/usr/libexec/PlistBuddy", "-c", "Print :DTSDKName", str(workspace / APP / "Info.plist")])
    require(sdk == "iphonesimulator27.1", "Restored app SDK declaration differs")
    native = command(["xcrun", "vtool", "-show-build", str(workspace / APP / "ExampleAShellApp")])
    require(re.search(r"sdk\s+27\.1(?:\s|$)", native), "Restored native binary SDK differs")
    (evidence / "duo-split-consumer-native-build.txt").write_text(native + "\n", encoding="utf-8")
    shutil.copy2(input_dir / "manifest.json", evidence / "duo-split-manifest.json")
    proof = {"status": "verified", "identity": identity, "artifact": artifact,
             "manifest_sha256": args.manifest_sha256, "archive_sha256": args.archive_sha256,
             "test_assembly_sha256": digest(workspace / manifest["test_assembly"]),
             "entries_verified": len(manifest["entries"]), "package_count": len(manifest["package_closure"]),
             "paths": {"app": str(workspace / APP), "test_assembly": str(workspace / manifest["test_assembly"]),
                       "test_project": str(workspace / "Tests/AdaptiveShell.UITests/AdaptiveShell.UITests.csproj")},
             "test_command": ["dotnet", "test", "Tests/AdaptiveShell.UITests/AdaptiveShell.UITests.csproj", "--no-build", "--no-restore"],
             "scope": "handoff verified; UI success still requires all 13 unique tests (8pass/5original skips), strict Duo raw4/4 and Landing raw3/3"}
    dump(evidence / "duo-split-consumer.json", proof)
    print(json.dumps(proof), flush=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    modes = parser.add_subparsers(dest="mode", required=True)
    producer = modes.add_parser("pack")
    producer.add_argument("--output", required=True)
    consumer = modes.add_parser("consume")
    for name in ("input", "artifact-id", "artifact-name", "artifact-digest", "manifest-sha256", "archive-sha256"):
        consumer.add_argument("--" + name, required=True)
    modes.add_parser("verify-trx")
    args = parser.parse_args()
    try:
        if args.mode == "pack":
            pack(args.output)
        elif args.mode == "consume":
            consume(args)
        else:
            verify_trx()
    except Exception as error:
        # Exceptions from authenticated requests must not print request headers/tokens.
        print(json.dumps({"status": "error", "type": type(error).__name__, "error": str(error)}), flush=True)
        return 2
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
