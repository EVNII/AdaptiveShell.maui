using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NUnit.Framework;
using OpenQA.Selenium.Appium;

namespace AdaptiveShell.UITests;

// Diagnostic branch only. NUnit has finished all descendants; no later test reuses
// this session. This side evidence never changes an assertion or the original Quit.
internal static class DuoPostSuiteSnapshotComparison
{
    const string Bundle = "com.companyname.exampleashellapp";
    const string Setting = "enforceCustomSnapshots";
    const string Wda = "http://127.0.0.1:8100";
    static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    static void Need(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    static JsonElement Json(object? value) => JsonSerializer.SerializeToElement(value);
    static string? Env(string key) => Environment.GetEnvironmentVariable(key);
    static bool Equal(JsonElement a, JsonElement b) => JsonElement.DeepEquals(a, b);

    internal static void Capture(AppiumDriver? driver)
    {
        if (Env("UITEST_DUO_POST_SUITE_SNAPSHOT") is null) return;
        var watch = Stopwatch.StartNew();
        var directory = Path.Combine(AppiumSetup.RepoRoot, "TestResults", "duo-post-suite-snapshot");
        var operations = new List<object>();
        var evidence = new Dictionary<string, object?>
        {
            ["schema"] = 1, ["status"] = "capture_error", ["ui_acceptance"] = false,
            ["lifecycle"] = "SessionHost.OneTimeTearDown after NUnit descendants, before original Quit",
            ["trx_read_at_entry"] = false, ["started_utc"] = DateTimeOffset.UtcNow,
            ["absolute_budget_seconds"] = 30, ["work_deadline_seconds"] = 20, ["restore_reserve_seconds"] = 10,
            ["run_id"] = Env("GITHUB_RUN_ID"), ["run_attempt"] = Env("GITHUB_RUN_ATTEMPT"),
            ["head_sha"] = Env("GITHUB_SHA"), ["testhost_pid"] = Environment.ProcessId,
            ["nonce"] = Env("DUO_DIAGNOSTIC_BARRIER_NONCE"), ["operations"] = operations,
        };
        try
        {
            Need(!Directory.Exists(directory), "Comparison directory already exists; refusing reuse.");
            Directory.CreateDirectory(directory);
            void Save(string name, byte[] bytes)
            {
                Need(Path.GetFileName(name) == name, "Unsafe comparison filename.");
                using var file = new FileStream(Path.Combine(directory, name), FileMode.CreateNew, FileAccess.Write);
                file.Write(bytes);
            }
            JsonElement ReadJson(string name)
            {
                var bytes = File.ReadAllBytes(Path.Combine(AppiumSetup.RepoRoot, "TestResults", name));
                Save("identity-" + name, bytes); return JsonDocument.Parse(bytes).RootElement.Clone();
            }
            var head = Env("GITHUB_SHA"); var udid = Env("UITEST_DEVICE_UDID");
            Need(Env("UITEST_DUO_POST_SUITE_SNAPSHOT") == "true" && OperatingSystem.IsMacOS()
                && Env("GITHUB_ACTIONS") == "true" && Env("RUNNER_ENVIRONMENT") == "github-hosted"
                && Env("GITHUB_REPOSITORY") == "EVNII/AdaptiveShell.maui" && Env("GITHUB_JOB") == "uitest-ios-27-1-duo"
                && Env("GITHUB_REF_NAME") == "codex/duo-version-before-boot"
                && Env("GITHUB_WORKFLOW") == "Duo Version Before Boot E2E" && Env("GITHUB_WORKFLOW_SHA") == head
                && head is { Length: 40 } && head.All(c => "0123456789abcdef".Contains(c))
                && Guid.TryParseExact(udid, "D", out _) && udid == Env("DUO_DEVICE_UDID")
                && AppiumSetup.Platform == "ios" && AppiumSetup.Form == "duo" && AppiumSetup.BundleId == Bundle
                && driver is not null && driver.SessionId is not null
                && driver.Capabilities.GetCapability("udid")?.ToString() == udid
                && driver.Capabilities.GetCapability("usePrebuiltWDA") is true
                && driver.Capabilities.GetCapability("usePreinstalledWDA") is false
                && driver.Capabilities.GetCapability("useSimpleBuildTest") is false
                && Version.TryParse(driver.Capabilities.GetCapability("platformVersion")?.ToString(), out var version)
                && version.Major == 27 && version.Minor == 1,
                "Dedicated hosted Duo source/session/runtime scope rejected.");
            var release = ReadJson("duo-testhost-release.json");
            Need(release.GetProperty("status").GetString() == "boot-verified"
                && release.GetProperty("run_id").GetString() == Env("GITHUB_RUN_ID")
                && release.GetProperty("run_attempt").GetString() == Env("GITHUB_RUN_ATTEMPT")
                && release.GetProperty("head_sha").GetString() == head && release.GetProperty("device_udid").GetString() == udid
                && release.GetProperty("nonce").GetString() == Env("DUO_DIAGNOSTIC_BARRIER_NONCE")
                && release.GetProperty("testhost_pid").GetInt32() == Environment.ProcessId
                && release.GetProperty("assembly_sha256").GetString() == Hash(File.ReadAllBytes(typeof(SessionHost).Assembly.Location)),
                "Comparison does not belong to this released TestHost/assembly/source.");
            var native = ReadJson("duo-wda-prebuild.json");
            Need(native.GetProperty("status").GetString() == "verified" && native.GetProperty("device_udid").GetString() == udid
                && native.GetProperty("wda_version").GetString() == "16.12.11"
                && native.GetProperty("xcuitest_version").GetString() == "12.13.3"
                && native.GetProperty("native_sdks").EnumerateArray().All(x => x.GetString() == "27.1")
                && native.GetProperty("native_sdks").GetArrayLength() > 0, "Installed prebuilt native WDA toolchain differs.");
            var tools = ReadJson("duo-official-toolchain.json");
            Need(tools.GetProperty("status").GetString() == "verified" && tools.GetProperty("head_sha").GetString() == head
                && tools.GetProperty("run_id").GetString() == Env("GITHUB_RUN_ID")
                && tools.GetProperty("run_attempt").GetString() == Env("GITHUB_RUN_ATTEMPT")
                && tools.GetProperty("installed_appium").GetProperty("package").GetProperty("version").GetString() == "3.8.0"
                && tools.GetProperty("installed_resolution").GetProperty("driver").GetProperty("package").GetProperty("version").GetString() == "12.13.3"
                && tools.GetProperty("installed_resolution").GetProperty("wda").GetProperty("package").GetProperty("version").GetString() == "16.12.11",
                "Exact official npm tool proof differs.");
            var workflow = File.ReadAllBytes(Path.Combine(AppiumSetup.RepoRoot, ".github/workflows/release-uitest.yml"));
            Need(tools.GetProperty("workflow_sha256").GetString() == Hash(workflow), "Actual workflow differs from installation proof.");
            var environment = ReadJson("duo-environment.json");
            Need(environment.GetProperty("status").GetString() == "verified"
                && environment.GetProperty("sdk").GetProperty("version").GetString() == "27.1"
                && environment.GetProperty("sdk").GetProperty("build").GetString() == "24A94403"
                && environment.GetProperty("runtime").GetProperty("version").GetString() == "27.1"
                && environment.GetProperty("runtime").GetProperty("buildversion").GetString() == "24A94401"
                && environment.GetProperty("device").GetProperty("udid").GetString() == udid
                && environment.GetProperty("device_type").GetProperty("modelIdentifier").GetString() == "iPhone19,4", "Exact native model/runtime/SDK differs.");
            Need(native.GetProperty("package_json").GetString() == tools.GetProperty("installed_resolution").GetProperty("wda").GetProperty("path").GetString()
                && native.GetProperty("derived_data_path").GetString() == driver!.Capabilities.GetCapability("derivedDataPath")?.ToString(),
                "Actual session/prebuild uses another WDA installation or derived data path.");
            evidence["appium_session_id"] = driver!.SessionId!.ToString(); evidence["device_udid"] = udid;
            evidence["assembly_sha256"] = release.GetProperty("assembly_sha256").GetString(); evidence["workflow_sha256"] = Hash(workflow);
            var events = File.ReadAllBytes(Path.Combine(AppiumSetup.RepoRoot, "TestResults", "duo-startup-events.jsonl"));
            Save("identity-duo-startup-events.jsonl", events);
            var beginnings = Encoding.UTF8.GetString(events).Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(x => JsonDocument.Parse(x).RootElement.Clone())
                .Where(x => x.GetProperty("stage").GetString() == "preboot-testhost-single-process-begin").ToArray();
            Need(beginnings.Length == 1 && beginnings[0].GetProperty("budget_seconds").GetInt32() == 1200, "Single original 1200 second process clock is unproven.");
            var outerStart = DateTimeOffset.Parse(beginnings[0].GetProperty("utc").GetString()!, CultureInfo.InvariantCulture);
            var outerEstimate = (outerStart.AddSeconds(1200) - DateTimeOffset.UtcNow).TotalSeconds;
            evidence["outer_clock_utc_remaining_estimate_seconds"] = outerEstimate;
            evidence["outer_clock_authority"] = "Original Python monotonic 1200 second watchdog unchanged; UTC estimate only restricts mutation";
            var mutationAllowed = outerEstimate >= 35;
            using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            string? backendSession = null;
            JsonElement Request(string label, string path, object? body, double limit)
            {
                var left = limit - watch.Elapsed.TotalSeconds; Need(left > 0, "Absolute comparison deadline expired.");
                using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(left));
                var row = new Dictionary<string, object?> { ["method"] = body is null ? "GET" : "POST", ["path"] = path,
                    ["request_body"] = body, ["raw_file"] = label + ".json", ["started_utc"] = DateTimeOffset.UtcNow,
                    ["absolute_deadline_seconds"] = limit, ["status"] = "started" };
                operations.Add(row);
                try
                {
                    using var message = new HttpRequestMessage(body is null ? HttpMethod.Get : HttpMethod.Post, Wda + path);
                    if (body is not null) message.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
                    using var response = http.SendAsync(message, cancellation.Token).GetAwaiter().GetResult();
                    var bytes = response.Content.ReadAsByteArrayAsync(cancellation.Token).GetAwaiter().GetResult();
                    Save(label + ".json", bytes); row["sha256"] = Hash(bytes); row["http_status"] = (int)response.StatusCode;
                    response.EnsureSuccessStatusCode(); using var document = JsonDocument.Parse(bytes); var root = document.RootElement;
                    var id = root.GetProperty("sessionId").GetString(); Need(Guid.TryParse(id, out _), "Actual WDA session ID missing.");
                    if (backendSession is null) backendSession = id; else Need(id == backendSession, "Actual WDA session changed during comparison.");
                    var value = root.GetProperty("value");
                    Need(!(value.ValueKind == JsonValueKind.Object && value.TryGetProperty("error", out _)), "WDA returned an error response.");
                    row["status"] = "completed"; return value.Clone();
                }
                catch (Exception error) { row["status"] = "error"; row["error"] = error.ToString(); throw; }
                finally { row["finished_utc"] = DateTimeOffset.UtcNow; }
            }
            string? container = null;
            JsonElement Native(string phase, double limit)
            {
                if (container is null)
                {
                    var left = Math.Min(5, limit - watch.Elapsed.TotalSeconds); Need(left > 0, "Native query deadline expired.");
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(left));
                    var start = new ProcessStartInfo("xcrun") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
                    foreach (var arg in new[] { "simctl", "get_app_container", udid!, Bundle, "data" }) start.ArgumentList.Add(arg);
                    using var process = Process.Start(start) ?? throw new InvalidOperationException("Native query did not start.");
                    using var outputFile = new FileStream(Path.Combine(directory, "native-container.stdout"), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                    using var errorFile = new FileStream(Path.Combine(directory, "native-container.stderr"), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                    var stdout = process.StandardOutput.BaseStream.CopyToAsync(outputFile, cts.Token);
                    var stderr = process.StandardError.BaseStream.CopyToAsync(errorFile, cts.Token);
                    var operation = new Dictionary<string, object?> { ["argv"] = new[] { "xcrun", "simctl", "get_app_container", udid!, Bundle, "data" },
                        ["pid"] = process.Id, ["started_utc"] = DateTimeOffset.UtcNow, ["budget_seconds"] = left, ["status"] = "started" };
                    operations.Add(operation);
                    try
                    {
                        process.WaitForExitAsync(cts.Token).GetAwaiter().GetResult();
                        Task.WhenAll(stdout, stderr).WaitAsync(cts.Token).GetAwaiter().GetResult();
                        outputFile.FlushAsync(cts.Token).GetAwaiter().GetResult(); errorFile.FlushAsync(cts.Token).GetAwaiter().GetResult();
                        operation["status"] = "completed";
                    }
                    catch (Exception error)
                    {
                        cts.Cancel(); operation["status"] = "error"; operation["error"] = error.ToString();
                        if (!process.HasExited) { process.Kill(false); operation["owned_child_kill_requested"] = true; }
                        // Raw stream files already contain any received bytes. Do not
                        // wait beyond the shared clock for a failed native pipe.
                        _ = Task.WhenAll(stdout, stderr).ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
                        throw;
                    }
                    finally { operation["finished_utc"] = DateTimeOffset.UtcNow; }
                    outputFile.Dispose(); errorFile.Dispose();
                    var output = File.ReadAllText(Path.Combine(directory, "native-container.stdout"));
                    operation["exit_code"] = process.ExitCode;
                    operation["stdout_sha256"] = Hash(File.ReadAllBytes(Path.Combine(directory, "native-container.stdout")));
                    operation["stderr_sha256"] = Hash(File.ReadAllBytes(Path.Combine(directory, "native-container.stderr")));
                    Need(process.ExitCode == 0, "Owned native container query failed."); container = Path.GetFullPath(output.Trim());
                    var data = Path.GetFullPath(environment.GetProperty("device").GetProperty("dataPath").GetString()!);
                    Need(container.StartsWith(data + Path.DirectorySeparatorChar, StringComparison.Ordinal), "Native container escaped exact device.");
                }
                Need(watch.Elapsed.TotalSeconds < limit, "Native snapshot deadline expired.");
                var path = Path.Combine(container, "Documents", "ashell-duo-visibility.jsonl");
                Need(new FileInfo(path).LinkTarget is null && new FileInfo(path).Length <= 8 * 1024 * 1024, "Native log link/size invalid.");
                var bytes = File.ReadAllBytes(path); Save(phase + "-native.jsonl", bytes);
                DuoVisibilityDiagnostics.ValidateNativeRecords(bytes, head!);
                var records = Encoding.UTF8.GetString(bytes).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(x => JsonDocument.Parse(x).RootElement.Clone()).ToArray();
                var last = records[^1]; var now = DateTimeOffset.UtcNow;
                Need((now - DateTimeOffset.Parse(last.GetProperty("state_read_finished_utc").GetString()!, CultureInfo.InvariantCulture)).TotalSeconds is >= 0 and <= 2,
                    "Native state is not a fresh two-second observation.");
                var sequence = last.TryGetProperty("full_state_sequence", out var reference) ? reference.GetInt64() : last.GetProperty("sequence").GetInt64();
                var full = records.Single(x => x.GetProperty("process_id").GetInt32() == last.GetProperty("process_id").GetInt32()
                    && x.GetProperty("sequence").GetInt64() == sequence);
                var state = full.GetProperty("state"); var window = state.GetProperty("root").GetProperty("view").GetProperty("window");
                Need(window.GetProperty("is_key_window").GetBoolean() && window.GetProperty("scene_activation_state").GetString() == "ForegroundActive",
                    "Native key foreground window missing.");
                return Json(new { process_id = last.GetProperty("process_id").GetInt32(), source_sha = head,
                    state_sequence = sequence, observed_utc = last.GetProperty("state_read_finished_utc").GetString(),
                    state_sha256 = full.GetProperty("full_state_sha256").GetString(), selected_tab_id = state.GetProperty("selected_tab_id").GetString(),
                    root_view_id = state.GetProperty("root").GetProperty("view").GetProperty("id").GetString(),
                    window, pages = state.GetProperty("pages"), native_log_sha256 = Hash(bytes) });
            }
            var screens = Request("backend-screens-initial", "/wda/screens", null, 20);
            var comparison = Compare(Request, Native, () => watch.Elapsed.TotalSeconds, Save, mutationAllowed, backendSession!, screens);
            evidence["comparison"] = comparison; evidence["wda_session_id"] = backendSession;
            evidence["status"] = comparison["status"]?.ToString() == "compared" ? "comparison_captured" : comparison["status"];
        }
        catch (Exception error) { evidence["error"] = error.ToString(); }
        finally
        {
            evidence["finished_utc"] = DateTimeOffset.UtcNow; evidence["elapsed_seconds"] = watch.Elapsed.TotalSeconds;
            try { Directory.CreateDirectory(directory); File.WriteAllText(Path.Combine(directory, "comparison.json"), JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true })); }
            catch (Exception error) { TestContext.Progress.WriteLine("Post-suite side evidence could not be saved: " + error.Message); }
        }
    }

    // Transport/clock seams are only for TEMP mock mechanics. Runtime uses the
    // actual backend requests and fresh native log above; no mocked UI is accepted.
    internal static Dictionary<string, object?> Compare(Func<string, string, object?, double, JsonElement> request,
        Func<string, double, JsonElement> native, Func<double> elapsed, Action<string, byte[]> save, bool mutationAllowed, string actualBackendSession, JsonElement initialScreens)
    {
        var result = new Dictionary<string, object?> { ["ui_acceptance"] = false, ["status"] = "capture_error", ["mutation_attempted"] = false,
            ["restoration"] = "not_needed", ["visibility_predicate"] = "unchanged WDA isWDVisible/fb_isVisible Apple AX predicate",
            ["natural_negative_control"] = "Unavailable: detached native candidates do not supply a correlated WDA UUID; no UUID is invented" };
        JsonElement? baseline = null; bool attempted = false; string? session = null; JsonElement? firstNative = null;
        var originalRects = new Dictionary<string, JsonElement>();
        var originalNative = new Dictionary<string, JsonElement>();
        result["correlation_status"] = "not_established";
        void Correlate(bool ok, string reason)
        {
            if (ok) return; result["correlation_status"] = "uncorrelated"; result["correlation_error"] = reason;
            throw new InvalidOperationException(reason);
        }
        try
        {
            var screens = initialScreens;
            session = actualBackendSession;
            Need(Guid.TryParse(session, out _), "Actual backend session from standalone screen response is missing.");
            var path = "/session/" + Uri.EscapeDataString(session!) + "/appium/settings";
            baseline = request("S0-settings", path, null, 20);
            result["S0_settings"] = baseline.Value;
            Need(baseline.Value.GetProperty(Setting).ValueKind is JsonValueKind.True or JsonValueKind.False, "Actual backend setting is not boolean.");
            CapturePhase("S0", 20);
            if (originalRects.Count == 0 || !mutationAllowed || baseline.Value.GetProperty(Setting).GetBoolean() || elapsed() > 8)
            {
                result["status"] = "baseline_only"; result["reason"] = originalRects.Count == 0 ? "No original counter UUID: baseline only, no positive diagnosis" : !mutationAllowed ? "Insufficient original suite clock reserve" : "Already custom or less than twelve seconds of work budget remain";
                return result;
            }
            result["S2_capture"] = (Action)(() => CapturePhase("S2", 30));
            attempted = true; result["mutation_attempted"] = true;
            var changed = request("S1-setting-write", path, new { settings = new Dictionary<string, bool> { [Setting] = true } }, 20);
            var custom = request("S1-settings", path, null, 20);
            result["S1_settings"] = custom;
            Need(Equal(changed, custom), "POST did not read back the actual custom backend settings.");
            var wanted = baseline.Value.EnumerateObject().ToDictionary(x => x.Name, x => x.Name == Setting ? Json(true) : x.Value.Clone());
            Need(Equal(Json(wanted), custom), "Custom snapshot changed more than the single setting.");
            CapturePhase("S1", 20); result["status"] = "compared";

            void CapturePhase(string phase, double limit)
            {
                var nativeState = native(phase, limit); if (firstNative is null) firstNative = nativeState;
                else foreach (var key in new[] { "process_id", "source_sha", "selected_tab_id", "root_view_id", "window" })
                    Correlate(Equal(nativeState.GetProperty(key), firstNative.Value.GetProperty(key)), "Native PID/source/tab/root/window changed during comparison.");
                var active = request(phase + "-active-app", "/session/" + session + "/wda/activeAppInfo", null, limit);
                Need(active.GetProperty("bundleId").GetString() == Bundle && active.GetProperty("pid").GetInt32() == nativeState.GetProperty("process_id").GetInt32(), "Backend active AUT/PID differs from native state.");
                var phaseScreens = request(phase + "-screens", "/wda/screens", null, limit);
                Need(Equal(phaseScreens, screens), "Actual displays changed during comparison.");
                Need(phaseScreens.GetArrayLength() == 2 && phaseScreens.EnumerateArray().Select(x => x.GetProperty("displayId").GetInt32()).Distinct().Count() == 2, "Two unique actual native displays missing.");
                var main = phaseScreens.EnumerateArray().Where(x => x.GetProperty("isMain").GetBoolean()).ToArray(); Need(main.Length == 1, "Unique actual Main display missing.");
                var bounds = main[0].GetProperty("bounds"); var points = nativeState.GetProperty("window").GetProperty("screen_bounds_points");
                var scale = main[0].GetProperty("scale").GetDouble();
                Need(scale == nativeState.GetProperty("window").GetProperty("screen_scale").GetDouble()
                    && bounds.GetProperty("x").GetDouble() == points.GetProperty("x").GetDouble() * scale
                    && bounds.GetProperty("y").GetDouble() == points.GetProperty("y").GetDouble() * scale
                    && bounds.GetProperty("width").GetDouble() == points.GetProperty("width").GetDouble() * scale
                    && bounds.GetProperty("height").GetDouble() == points.GetProperty("height").GetDouble() * scale,
                    "Actual Duo Main pixel/point geometry differs.");
                var source = request(phase + "-AX", "/session/" + session + "/source", null, limit).GetString()!; save(phase + ".xml", Encoding.UTF8.GetBytes(source));
                var bytes = Convert.FromBase64String(request(phase + "-screenshot", "/session/" + session + "/screenshot", null, limit).GetString()!); save(phase + ".png", bytes);
                Need(bytes.Length >= 24 && bytes.AsSpan().StartsWith(new byte[] {137,80,78,71,13,10,26,10})
                    && System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(16,4)) == bounds.GetProperty("width").GetUInt32()
                    && System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(20,4)) == bounds.GetProperty("height").GetUInt32(), "Raw screenshot does not exactly match Main pixels.");
                // Re-query the actual S0 UUIDs first. A stale original cannot be
                // silently replaced by a new fresh match and called the same element.
                var details = new Dictionary<string, JsonElement>(); var sameOriginal = new List<JsonElement>();
                JsonElement ReadCounter(string id, string label)
                {
                    var displayed = request(label + "-displayed", "/session/" + session + "/element/" + Uri.EscapeDataString(id) + "/displayed", null, limit);
                    Need(displayed.ValueKind is JsonValueKind.True or JsonValueKind.False, "Displayed response is not the actual boolean predicate.");
                    var rect = request(label + "-rect", "/session/" + session + "/element/" + Uri.EscapeDataString(id) + "/rect", null, limit);
                    foreach (var key in new[] { "x", "y", "width", "height" }) Need(double.IsFinite(rect.GetProperty(key).GetDouble()), "Native counter rectangle is not finite.");
                    return Json(new { id, displayed, rect });
                }
                JsonElement NativeCounter(JsonElement rect)
                {
                    var candidates = new List<JsonElement>();
                    foreach (var page in nativeState.GetProperty("pages").EnumerateArray())
                    foreach (var item in page.GetProperty("native_counter_views").EnumerateArray())
                    {
                        var counter = item.GetProperty("counter");
                        if (counter.GetProperty("accessibility_id").GetString() != "counterBtn"
                            || counter.GetProperty("window_id").GetString() != nativeState.GetProperty("window").GetProperty("id").GetString()
                            || !item.GetProperty("native_view_ancestors").EnumerateArray().Any(x => x.GetProperty("id").GetString() == nativeState.GetProperty("root_view_id").GetString())) continue;
                        var frame = counter.GetProperty("accessibility_frame_screen_points");
                        var x = frame.GetProperty("x").GetDouble(); var y = frame.GetProperty("y").GetDouble();
                        var width = frame.GetProperty("width").GetDouble(); var height = frame.GetProperty("height").GetDouble();
                        if (!new[] { x, y, width, height }.All(double.IsFinite) || width <= 0 || height <= 0) continue;
                        // Official WDA16.12.11 wdFrame/rect uses CGRectIntegral.
                        // This is exact correspondence, never a tolerance or UUID mapping.
                        var integral = Json(new { x = Math.Floor(x), y = Math.Floor(y),
                            width = Math.Ceiling(x + width) - Math.Floor(x), height = Math.Ceiling(y + height) - Math.Floor(y) });
                        if (Equal(integral, rect)) candidates.Add(Json(new { native_counter_id = counter.GetProperty("id").GetString(),
                            root_view_id = nativeState.GetProperty("root_view_id").GetString(), window_id = counter.GetProperty("window_id").GetString(),
                            accessibility_frame_screen_points = frame, integral_api_rect = integral,
                            ancestor_ids = item.GetProperty("native_view_ancestors").EnumerateArray().Select(x => x.GetProperty("id").GetString()).ToArray() }));
                    }
                    Correlate(candidates.Count == 1, "Counter geometry does not uniquely correspond to an actual same-root/window native counter.");
                    return candidates[0];
                }
                if (phase != "S0") foreach (var original in originalRects)
                {
                    try
                    {
                        var value = ReadCounter(original.Key, phase + "-original-" + sameOriginal.Count);
                        Correlate(Equal(value.GetProperty("rect"), original.Value), "Original S0 UUID counter rectangle changed.");
                        var counter = NativeCounter(value.GetProperty("rect"));
                        Correlate(Equal(counter, originalNative[original.Key]), "Original native counter identity/geometry/ancestors changed.");
                        details[original.Key] = value; sameOriginal.Add(value);
                    }
                    catch (Exception error)
                    {
                        result["correlation_status"] = "uncorrelated"; result["correlation_error"] = error.ToString(); throw;
                    }
                }
                var matches = request(phase + "-counter-matches", "/session/" + session + "/elements", new { @using = "accessibility id", value = "counterBtn" }, limit);
                Need(matches.ValueKind == JsonValueKind.Array && matches.GetArrayLength() <= 4, "Counter all-matches shape/count is unsafe.");
                var freshIds = new List<string>(); var elements = new List<JsonElement>();
                foreach (var item in matches.EnumerateArray())
                {
                    var id = item.GetProperty("element-6066-11e4-a52e-4f735466cecf").GetString(); Need(!string.IsNullOrEmpty(id), "Actual returned counter UUID missing.");
                    Correlate(!freshIds.Contains(id!), "Fresh counter UUID set contains duplicates."); freshIds.Add(id!);
                    // Same-phase original reads are reused only for identical real IDs.
                    // A different fresh ID gets its own actual native requests.
                    if (!details.TryGetValue(id!, out var value)) value = ReadCounter(id!, phase + "-fresh-" + elements.Count);
                    details[id!] = value; elements.Add(value);
                }
                result[phase] = new { native = nativeState, active_app = active, screens = phaseScreens,
                    original_s0_uuid_reads = sameOriginal, fresh_match_ids = freshIds, fresh_elements = elements,
                    ax_sha256 = Hash(Encoding.UTF8.GetBytes(source)), png_sha256 = Hash(bytes), no_clicks = true,
                    control_limitation = phase == "S0" && elements.Count == 0 ? "No returned S0 UUID: positive diagnosis unavailable" : "Real S0 UUIDs are compared first; UIKit handles are not assumed to be WDA UUIDs" };
                if (phase == "S0" && elements.Count > 0)
                {
                    Correlate(elements.Count == 1, "Original counter UUID is ambiguous; no single positive comparison is established.");
                    var value = elements[0]; var id = value.GetProperty("id").GetString()!;
                    var counter = NativeCounter(value.GetProperty("rect"));
                    originalRects.Add(id, value.GetProperty("rect")); originalNative.Add(id, counter);
                    result["original_counter_uuid"] = id; result["original_counter_native_geometry"] = counter;
                    result["correlation_status"] = "established_actual_s0_uuid_and_exact_native_geometry";
                }

            }
            // Restore is always attempted in finally, including an unknown POST outcome.
        }
        catch (Exception error)
        {
            result["error"] = error.ToString();
            if (result["correlation_status"]?.ToString() == "uncorrelated") result["status"] = "uncorrelated";
        }
        finally
        {
            if (attempted)
            {
                result["restoration"] = "unknown_dirty";
                try
                {
                    var path = "/session/" + session + "/appium/settings";
                    var written = request("S2-setting-restore", path, new { settings = new Dictionary<string, bool> { [Setting] = baseline!.Value.GetProperty(Setting).GetBoolean() } }, 30);
                    var restored = request("S2-settings", path, null, 30);
                    result["S2_settings"] = restored;
                    Need(Equal(written, baseline.Value) && Equal(restored, baseline.Value), "Restore did not read back every original backend setting.");
                    result["restoration"] = "verified_all_original_settings";
                    if (result.TryGetValue("S2_capture", out var capture)) ((Action)capture!)();
                }
                catch (Exception error)
                {
                    result["restore_or_after_capture_error"] = error.ToString();
                    result["status"] = result["correlation_status"]?.ToString() == "uncorrelated" ? "uncorrelated" : "capture_error";
                }
            }
            result.Remove("S2_capture");
            result["elapsed_seconds"] = elapsed();
        }
        return result;
    }
}
