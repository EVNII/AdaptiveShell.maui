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
        var raw = Encoding.UTF8.GetString(bytes);
        if (!raw.EndsWith('\n')) throw new InvalidOperationException("Native JSONL ended in a partial record.");
        var lines = raw.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0) throw new InvalidOperationException("Native JSONL has no records.");
        foreach (var line in lines)
        {
            using var document = JsonDocument.Parse(line);
            var record = document.RootElement;
            if (record.TryGetProperty("status", out _)
                || record.GetProperty("bundle_id").GetString() != Bundle
                || record.GetProperty("simulator_model").GetString() != "iPhone19,4"
                || record.GetProperty("diagnostic_source_sha").GetString() != head)
                throw new InvalidOperationException("Native JSONL identity or capture error.");
        }
    }
}
