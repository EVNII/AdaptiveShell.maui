using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NUnit.Framework;
using OpenQA.Selenium.Appium;

namespace AdaptiveShell.UITests;

// No capture during polling. Preserve the original timeout exception after one bounded,
// read-only capture per test, before its finally/TearDown can restart the application.
internal static class DuoVisibilityDiagnostics
{
    const string Bundle = "com.companyname.exampleashellapp";
    const string NativeLog = "ashell-duo-visibility.jsonl";
    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> CapturedTests = new();

    internal static void CaptureCounterTimeout(AppiumDriver driver, int timeoutSeconds)
    {
        if (Environment.GetEnvironmentVariable("UITEST_DUO_VISIBILITY_DIAGNOSTIC") is null) return;
        var test = TestContext.CurrentContext.Test;
        var key = test.FullName + ":" + test.ID;
        if (!CapturedTests.TryAdd(key, 0)) return;
        var name = "duo-visibility-timeout-" + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant()[..16];
        var directory = Path.Combine(AppiumSetup.RepoRoot, "TestResults", name);
        Directory.CreateDirectory(directory);
        var started = DateTimeOffset.UtcNow;
        var watch = Stopwatch.StartNew();
        var operations = new List<object>();
        var evidence = new Dictionary<string, object?>
        {
            ["schema"] = 1, ["status"] = "capture_error",
            ["scope"] = "read-only counter timeout evidence before original finally/TearDown; not an assertion override",
            ["test_full_name"] = test.FullName, ["test_id"] = test.ID,
            ["original_timeout_seconds"] = timeoutSeconds,
            ["started_utc"] = started, ["capture_budget_seconds"] = 30,
            ["run_id"] = Environment.GetEnvironmentVariable("GITHUB_RUN_ID"),
            ["run_attempt"] = Environment.GetEnvironmentVariable("GITHUB_RUN_ATTEMPT"),
            ["source_sha"] = Environment.GetEnvironmentVariable("GITHUB_SHA"),
            ["operations"] = operations,
        };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            var udid = Environment.GetEnvironmentVariable("UITEST_DEVICE_UDID");
            var head = Environment.GetEnvironmentVariable("GITHUB_SHA");
            var server = new Uri(Environment.GetEnvironmentVariable("UITEST_APPIUM_URL") ?? "http://127.0.0.1:4723");
            if (Environment.GetEnvironmentVariable("UITEST_DUO_VISIBILITY_DIAGNOSTIC") != "true"
                || Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true"
                || Environment.GetEnvironmentVariable("GITHUB_JOB") != "uitest-ios-27-1-duo"
                || Environment.GetEnvironmentVariable("GITHUB_REPOSITORY") != "EVNII/AdaptiveShell.maui"
                || Environment.GetEnvironmentVariable("GITHUB_REF_NAME") != "codex/duo-deferred-appium-post-suite"
                || Environment.GetEnvironmentVariable("GITHUB_WORKFLOW") != "Duo Deferred Appium Post Suite Diagnostic"
                || Environment.GetEnvironmentVariable("GITHUB_WORKFLOW_SHA") != head
                || AppiumSetup.Platform != "ios" || AppiumSetup.Form != "duo" || AppiumSetup.BundleId != Bundle
                || !Guid.TryParseExact(udid, "D", out _)
                || udid != Environment.GetEnvironmentVariable("DUO_DEVICE_UDID")
                || head is not { Length: 40 } || !head.All(Uri.IsHexDigit)
                || !Version.TryParse(driver.Capabilities.GetCapability("platformVersion")?.ToString(), out var version)
                || version.Major != 27 || version.Minor != 1
                || server.Scheme != "http" || server.Host != "127.0.0.1" || server.Port != 4723
                || server.AbsolutePath != "/" || server.Query.Length != 0 || server.UserInfo.Length != 0)
                throw new InvalidOperationException("Duo visibility capture identity/CI/loopback guard rejected.");
            evidence["device_udid"] = udid;
            evidence["session_id"] = driver.SessionId.ToString();
            evidence["platform_version"] = version.ToString();
            var container = GetContainer(udid!, operations, deadline.Token);
            var nativeErrors = new List<object>();
            evidence["native_validation_errors"] = nativeErrors;
            void CaptureNative(string name)
            {
                try { CopyNativeLog(container, directory, name, head!); }
                catch (Exception error)
                {
                    // Preserve erroneous/partial native bytes and continue AX/PNG evidence.
                    // A native validation failure can never produce status=captured.
                    nativeErrors.Add(new { raw_file = name, utc = DateTimeOffset.UtcNow, error = error.ToString() });
                }
            }
            CaptureNative("native-before.jsonl");
            using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            var session = "/session/" + Uri.EscapeDataString(driver.SessionId.ToString());

            JsonElement Get(string origin, string path, string file, object? body = null)
            {
                deadline.Token.ThrowIfCancellationRequested();
                var commandStarted = DateTimeOffset.UtcNow;
                using var request = new HttpRequestMessage(body is null ? HttpMethod.Get : HttpMethod.Post, origin + path);
                if (body is not null)
                    request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
                var operation = new Dictionary<string, object?>
                {
                    ["method"] = request.Method.Method, ["path"] = path, ["request_body"] = body,
                    ["started_utc"] = commandStarted, ["status"] = "started", ["raw_file"] = file,
                    ["shared_deadline_utc"] = started.AddSeconds(30),
                };
                operations.Add(operation);
                try
                {
                    using var response = http.SendAsync(request, deadline.Token).GetAwaiter().GetResult();
                    var bytes = response.Content.ReadAsByteArrayAsync(deadline.Token).GetAwaiter().GetResult();
                    File.WriteAllBytes(Path.Combine(directory, file), bytes);
                    operation["http_status"] = (int)response.StatusCode;
                    operation["sha256"] = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
                    operation["bytes"] = bytes.Length;
                    response.EnsureSuccessStatusCode();
                    using var document = JsonDocument.Parse(bytes);
                    operation["status"] = "completed";
                    return document.RootElement.GetProperty("value").Clone();
                }
                catch (Exception error)
                {
                    operation["status"] = "error";
                    operation["cancelled"] = deadline.IsCancellationRequested;
                    operation["error"] = error.ToString();
                    throw;
                }
                finally { operation["finished_utc"] = DateTimeOffset.UtcNow; }
            }

            var appium = server.GetLeftPart(UriPartial.Authority);
            var active = Get(appium, session + "/execute/sync", "active-app.json",
                new { script = "mobile:activeAppInfo", args = Array.Empty<object>() });
            if (active.GetProperty("bundleId").GetString() != Bundle)
                throw new InvalidOperationException("Timeout capture active bundle differs from expected AUT.");
            Get("http://127.0.0.1:8100", "/wda/screens", "screens-before.json");
            var source = Get(appium, session + "/source", "source-before-response.json").GetString()
                ?? throw new InvalidOperationException("Missing original native source.");
            File.WriteAllText(Path.Combine(directory, "source-before.xml"), source);
            var png = Convert.FromBase64String(Get(appium, session + "/screenshot", "screenshot-response.json").GetString()
                ?? throw new InvalidOperationException("Missing original screenshot."));
            if (!png.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
                throw new InvalidOperationException("Original screenshot is not PNG.");
            File.WriteAllBytes(Path.Combine(directory, "original.png"), png);

            var groups = new List<object>();
            var details = new Dictionary<string, object>();
            foreach (var strategy in new[] { "accessibility id", "id" })
            {
                var matches = Get(appium, session + "/elements", "matches-" + strategy.Replace(' ', '-') + ".json",
                    new { @using = strategy, value = "counterBtn" });
                if (matches.ValueKind != JsonValueKind.Array)
                    throw new InvalidOperationException("All-matches response is not an array.");
                var ids = matches.EnumerateArray().Select(element =>
                    element.GetProperty("element-6066-11e4-a52e-4f735466cecf").GetString()
                        ?? throw new InvalidOperationException("Missing returned element UUID.")).ToArray();
                groups.Add(new { strategy, ids });
                foreach (var id in ids)
                {
                    if (details.ContainsKey(id)) continue;
                    var escaped = Uri.EscapeDataString(id);
                    var stem = "element-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id))).ToLowerInvariant()[..16];
                    var displayed = Get(appium, session + "/element/" + escaped + "/displayed", stem + "-displayed.json");
                    var rect = Get(appium, session + "/element/" + escaped + "/rect", stem + "-rect.json");
                    details.Add(id, new { displayed, rect });
                }
            }
            evidence["all_matches"] = groups;
            evidence["actual_elements"] = details;
            var after = Get(appium, session + "/source", "source-after-response.json").GetString()
                ?? throw new InvalidOperationException("Missing final native source.");
            File.WriteAllText(Path.Combine(directory, "source-after.xml"), after);
            Get("http://127.0.0.1:8100", "/wda/screens", "screens-after.json");
            CaptureNative("native-after.jsonl");
            evidence["status"] = nativeErrors.Count == 0 ? "captured" : "capture_error";
        }
        catch (Exception error)
        {
            evidence["error"] = error.ToString();
            TestContext.Out.WriteLine("Duo visibility capture failed; preserving original counter timeout: " + error.Message);
        }
        finally
        {
            evidence["finished_utc"] = DateTimeOffset.UtcNow;
            evidence["elapsed_seconds"] = watch.Elapsed.TotalSeconds;
            File.WriteAllText(Path.Combine(directory, "capture.json"), JsonSerializer.Serialize(evidence,
                new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    static string GetContainer(string udid, List<object> operations, CancellationToken token)
    {
        using var stage = CancellationTokenSource.CreateLinkedTokenSource(token);
        stage.CancelAfter(TimeSpan.FromSeconds(10));
        var start = new ProcessStartInfo("xcrun")
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
        };
        foreach (var argument in new[] { "simctl", "get_app_container", udid, Bundle, "data" })
            start.ArgumentList.Add(argument);
        var started = DateTimeOffset.UtcNow;
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Native container query did not start.");
        var stdout = process.StandardOutput.ReadToEndAsync(stage.Token);
        var stderr = process.StandardError.ReadToEndAsync(stage.Token);
        var operation = new Dictionary<string, object?>
        {
            ["command"] = new[] { "xcrun", "simctl", "get_app_container", udid, Bundle, "data" },
            ["started_utc"] = started, ["pid"] = process.Id, ["stage_budget_seconds"] = 10,
            ["status"] = "started",
        };
        operations.Add(operation);
        try { process.WaitForExitAsync(stage.Token).GetAwaiter().GetResult(); }
        catch (Exception failure)
        {
            operation["status"] = "error";
            operation["cancelled"] = stage.IsCancellationRequested;
            operation["error"] = failure.ToString();
            try
            {
                if (!process.HasExited) { process.Kill(entireProcessTree: true); operation["owned_tree_kill_requested"] = true; }
            }
            catch (Exception cleanup) { operation["cleanup_error"] = cleanup.ToString(); }
            throw;
        }
        finally { operation["finished_utc"] = DateTimeOffset.UtcNow; }
        var output = stdout.GetAwaiter().GetResult().Trim();
        var error = stderr.GetAwaiter().GetResult();
        operation["status"] = "completed";
        operation["exit_code"] = process.ExitCode;
        operation["stdout"] = output;
        operation["stderr"] = error;
        if (process.ExitCode != 0) throw new InvalidOperationException("Native container query failed: " + error);
        var parent = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Developer",
            "CoreSimulator", "Devices", udid, "data", "Containers", "Data", "Application") + Path.DirectorySeparatorChar;
        var container = Path.GetFullPath(output);
        if (!container.StartsWith(parent, StringComparison.Ordinal)
            || !Guid.TryParse(Path.GetFileName(container), out _))
            throw new InvalidOperationException("Native data container is outside the owned UDID.");
        return container;
    }

    static void CopyNativeLog(string container, string directory, string name, string head)
    {
        var path = Path.Combine(container, "Documents", NativeLog);
        var bytes = File.ReadAllBytes(path);
        File.WriteAllBytes(Path.Combine(directory, name), bytes);
        ValidateNativeRecords(bytes, head);
    }
    // This validates raw diagnostic provenance only. It never accepts a UIKit
    // native state as proof that the original Appium/WDA Displayed check passed.
    internal static void ValidateNativeRecords(byte[] bytes, string head)
    {
        static void Require(bool valid, string message)
        {
            if (!valid) throw new InvalidOperationException(message);
        }
        static void Fields(JsonElement value, params string[] expected)
        {
            Require(value.ValueKind == JsonValueKind.Object
                && value.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal)
                    .SetEquals(expected), "Native diagnostic schema differs.");
        }
        static void Unique(JsonElement value)
        {
            if (value.ValueKind == JsonValueKind.Object)
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in value.EnumerateObject())
                {
                    Require(names.Add(property.Name), "Duplicate native JSON property.");
                    Unique(property.Value);
                }
            }
            else if (value.ValueKind == JsonValueKind.Array)
                foreach (var item in value.EnumerateArray()) Unique(item);
        }
        static string? Text(JsonElement value, string key)
        {
            var item = value.GetProperty(key);
            Require(item.ValueKind is JsonValueKind.String or JsonValueKind.Null, "Native text/handle schema differs.");
            return item.ValueKind == JsonValueKind.Null ? null : item.GetString();
        }
        static DateTimeOffset Utc(JsonElement value, string key)
        {
            var text = Text(value, key);
            Require(text is not null && (text.EndsWith('Z') || text.EndsWith("+00:00", StringComparison.Ordinal)),
                "Native timestamp is not explicit UTC.");
            Require(DateTimeOffset.TryParse(text, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out var result)
                && result.Offset == TimeSpan.Zero, "Malformed native timestamp.");
            return result;
        }
        static void Handle(string? value, bool nullable = false)
        {
            if (value is null && nullable) return;
            Require(value is not null, "Native handle is missing.");
            var hex = value!.StartsWith("0x", StringComparison.Ordinal);
            Require(ulong.TryParse(hex ? value[2..] : value,
                hex ? System.Globalization.NumberStyles.AllowHexSpecifier : System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var number) && number > 0,
                "Native handle is invalid.");
        }
        var common = new[] { "schema", "sequence", "utc", "process_id", "bundle_id", "simulator_model",
            "os_version", "diagnostic_source_sha" };
        var fullFields = common.Concat(new[] { "state_read_started_utc", "state_read_finished_utc",
            "full_state_sha256", "state" }).ToArray();
        var heartbeatFields = common.Concat(new[] { "status", "phase", "state_read_started_utc",
            "state_read_finished_utc", "full_state_sequence", "full_state_sha256", "current_state_sha256" }).ToArray();
        var stateFields = new[] { "phase", "provider_call", "content_id", "content_full_bleed", "existing_parent_before",
            "returned_controller", "selected_tab_id", "selected_controller_inspected", "selected_controller",
            "returned_is_selected", "root", "root_children", "pages" };
        var selectionFields = new[] { "phase", "selection_call", "content_id", "root_handle", "target_tab_id",
            "target_tab_handle", "actual_selected_tab_before_id", "actual_selected_tab_before_handle",
            "actual_selected_tab_after_id", "actual_selected_tab_after_handle", "selection_write_skipped",
            "selection_write_attempted" };
        var fullPhases = new HashSet<string>(StringComparer.Ordinal)
            { "provider-before", "provider-after", "did-select-tab", "post-layout", "root-did-appear", "tick" };
        Require(bytes.Length is > 0 and <= 8 * 1024 * 1024 && bytes[^1] == (byte)'\n'
            && head.Length == 40 && head.All(c => "0123456789abcdef".Contains(c)),
            "Native JSONL is empty, oversized, partial, or has invalid source identity.");
        var raw = new UTF8Encoding(false, true).GetString(bytes);
        var lines = raw.Split('\n');
        var last = new Dictionary<int, long>();
        var full = new Dictionary<(int Pid, long Sequence), string>();
        var lastTick = new Dictionary<int, long>();
        var selections = new Dictionary<(int Pid, string Root, long Call), JsonElement>();
        var after = new HashSet<(int Pid, string Root, long Call)>();
        foreach (var line in lines[..^1])
        {
            Require(line.Length > 0, "Empty native JSONL record.");
            using var document = JsonDocument.Parse(line);
            var record = document.RootElement;
            Unique(record);
            Require(record.GetProperty("schema").GetInt32() == 1
                && Text(record, "bundle_id") == Bundle && Text(record, "simulator_model") == "iPhone19,4"
                && Text(record, "diagnostic_source_sha") == head
                && Version.TryParse(Text(record, "os_version"), out var os)
                && os.Major == 27 && os.Minor == 1, "Native source/bundle/model/runtime mismatch.");
            var pid = record.GetProperty("process_id").GetInt32();
            var sequence = record.GetProperty("sequence").GetInt64();
            Require(pid > 1 && sequence == last.GetValueOrDefault(pid) + 1,
                "Native sequence must be complete and monotonic within the actual PID.");
            last[pid] = sequence;
            var utc = Utc(record, "utc");
            if (record.TryGetProperty("status", out var status))
            {
                Fields(record, heartbeatFields);
                Require(status.GetString() == "fresh_equal_native_state" && Text(record, "phase") == "tick",
                    "Native error/log limit or unknown status is rejected.");
                var reference = record.GetProperty("full_state_sequence").GetInt64();
                Require(full.TryGetValue((pid, reference), out var hash)
                    && lastTick.GetValueOrDefault(pid) == reference && reference < sequence
                    && Text(record, "full_state_sha256") == hash && Text(record, "current_state_sha256") == hash,
                    "Fresh heartbeat must refer to this PID/source's most recent full tick and exact state hash.");
            }
            else
            {
                var state = record.GetProperty("state");
                var phase = Text(state, "phase");
                if (phase is not null && fullPhases.Contains(phase))
                {
                    Fields(record, fullFields); Fields(state, stateFields);
                    Require(state.GetProperty("provider_call").GetInt64() >= 0
                        && state.GetProperty("selected_controller_inspected").ValueKind is JsonValueKind.True or JsonValueKind.False
                        && state.GetProperty("root").ValueKind == JsonValueKind.Object
                        && state.GetProperty("root_children").ValueKind == JsonValueKind.Array
                        && state.GetProperty("pages").ValueKind == JsonValueKind.Array, "Malformed full native snapshot schema.");
                    var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(state.GetRawText()))).ToLowerInvariant();
                    Require(Text(record, "full_state_sha256") == hash, "Full native state bytes differ from their hash.");
                    full[(pid, sequence)] = hash;
                    if (phase == "tick") lastTick[pid] = sequence;
                }
                else
                {
                    Fields(record, common.Concat(new[] { "state" }).ToArray()); Fields(state, selectionFields);
                    Require(phase is "current-item-selection-before" or "current-item-selection-after"
                        && !string.IsNullOrEmpty(Text(state, "content_id")), "Malformed current-item selection phase.");
                    var call = state.GetProperty("selection_call").GetInt64();
                    var root = Text(state, "root_handle"); var target = Text(state, "target_tab_handle");
                    var beforeHandle = Text(state, "actual_selected_tab_before_handle");
                    Handle(root); Handle(target); Handle(beforeHandle, true); Handle(Text(state, "actual_selected_tab_after_handle"), true);
                    Require(call > 0, "Invalid native selection call.");
                    var skipped = beforeHandle is not null && beforeHandle == target;
                    Require(state.GetProperty("selection_write_skipped").GetBoolean() == skipped,
                        "Selection skip differs from actual native handles.");
                    var key = (pid, root!, call);
                    if (phase == "current-item-selection-before")
                    {
                        Require(!selections.ContainsKey(key) && Text(state, "actual_selected_tab_after_handle") is null
                            && Text(state, "actual_selected_tab_after_id") is null
                            && !state.GetProperty("selection_write_attempted").GetBoolean(), "Duplicate/malformed selection before record.");
                        selections[key] = state.Clone();
                    }
                    else
                    {
                        Require(selections.TryGetValue(key, out var before) && after.Add(key)
                            && state.GetProperty("selection_write_attempted").GetBoolean() == !skipped,
                            "Selection after lacks unique before record or has incorrect write provenance.");
                        foreach (var field in new[] { "content_id", "target_tab_id", "target_tab_handle",
                            "actual_selected_tab_before_id", "actual_selected_tab_before_handle", "selection_write_skipped" })
                            Require(state.GetProperty(field).GetRawText() == before.GetProperty(field).GetRawText(),
                                "Selection references changed between before/after.");
                    }
                }
            }
            if (record.TryGetProperty("state_read_started_utc", out _))
                Require(Utc(record, "state_read_started_utc") <= Utc(record, "state_read_finished_utc")
                    && Utc(record, "state_read_finished_utc") <= utc, "Fresh native read timestamps are inconsistent.");
        }
        Require(full.Count > 0, "Native full snapshot evidence is missing.");
    }

}
