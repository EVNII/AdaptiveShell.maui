using System.Net.Http;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenQA.Selenium.Appium;
using OpenQA.Selenium.Appium.Android;
using NUnit.Framework;

namespace AdaptiveShell.UITests;

// Opt-in diagnostic transfer only. It does not evaluate or change UI appearance.
internal static class AndroidNavigationAppearanceDiagnostics
{
    const string Flag = "UITEST_ANDROID_NAV_APPEARANCE_DIAGNOSTIC";
    const string RemotePath = "@com.companyname.exampleashellapp/files/ashell-navigation-appearance.jsonl";

    internal static void Capture(AppiumDriver driver, string stage, object deviceInfo)
    {
        if (Environment.GetEnvironmentVariable(Flag) != "true") return;
        var results = Path.Combine(AppiumSetup.RepoRoot, "TestResults");
        Directory.CreateDirectory(results);
        var rawPath = Path.Combine(results, $"android-navigation-appearance-{stage}.jsonl");
        var metaPath = Path.Combine(results, $"android-navigation-appearance-{stage}.json");
        var started = DateTimeOffset.UtcNow;
        var clock = Stopwatch.StartNew();
        string? rawSha = null;
        long? rawLength = null;
        var source = Environment.GetEnvironmentVariable("GITHUB_SHA");
        var ownedUdid = Environment.GetEnvironmentVariable("UITEST_DEVICE_UDID");
        string Cap(string key) => (driver.Capabilities.GetCapability(key)
            ?? driver.Capabilities.GetCapability($"appium:{key}"))?.ToString() ?? "";
        try
        {
            using var info = JsonDocument.Parse(JsonSerializer.Serialize(deviceInfo));
            if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true"
                || !OperatingSystem.IsLinux()
                || Environment.GetEnvironmentVariable("RUNNER_ENVIRONMENT") != "github-hosted"
                || Environment.GetEnvironmentVariable("GITHUB_REPOSITORY") != "EVNII/AdaptiveShell.maui"
                || Environment.GetEnvironmentVariable("GITHUB_JOB") != "uitest-android"
                || Environment.GetEnvironmentVariable("GITHUB_REF") != "refs/heads/codex/android36-nav-appearance-diagnostic"
                || Environment.GetEnvironmentVariable("GITHUB_WORKFLOW") != "Android 36 Navigation Appearance Diagnostic"
                || Environment.GetEnvironmentVariable("GITHUB_WORKFLOW_SHA") != source
                || source is null || source.Length != 40 || source.Any(c => !Uri.IsHexDigit(c))
                || AppiumSetup.Platform != "android" || AppiumSetup.Form != "compact"
                || driver is not AndroidDriver
                || ownedUdid != "emulator-5554" || Cap("deviceUDID") != ownedUdid
                || Cap("deviceApiLevel") != "36" || Cap("appPackage") != AppiumSetup.BundleId
                || info.RootElement.GetProperty("apiVersion").GetString() != "36"
                || stage is not ("light" or "dark" or "light-restored"))
                throw new InvalidOperationException("Android navigation diagnostic identity/CI/API36 compact guard failed.");
            if (File.Exists(rawPath) || File.Exists(metaPath))
                throw new InvalidOperationException("Diagnostic output already exists; it must not be overwritten.");
            var server = new Uri(Environment.GetEnvironmentVariable("UITEST_APPIUM_URL") ?? "http://127.0.0.1:4723");
            if (server.Scheme != "http" || !server.IsLoopback || server.Port != 4723
                || !string.IsNullOrEmpty(server.UserInfo) || !string.IsNullOrEmpty(server.Query))
                throw new InvalidOperationException("Diagnostic Appium endpoint is not the owned local server.");
            // Same standard endpoint used by AndroidDriver.PullFile, with a separate
            // cancellable request rather than the original driver's 10-minute budget.
            using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(
                Math.Max(1, 30000 - clock.ElapsedMilliseconds)));
            var session = driver.SessionId?.ToString()
                ?? throw new InvalidOperationException("Diagnostic has no actual Appium session.");
            var endpoint = new Uri(server.AbsoluteUri.TrimEnd('/')
                + $"/session/{Uri.EscapeDataString(session)}/appium/device/pull_file");
            using var body = new StringContent(JsonSerializer.Serialize(new { path = RemotePath }), Encoding.UTF8, "application/json");
            using var response = client.PostAsync(endpoint, body, deadline.Token).GetAwaiter().GetResult();
            var responseBytes = response.Content.ReadAsByteArrayAsync(deadline.Token).GetAwaiter().GetResult();
            File.WriteAllBytes(Path.Combine(results, $"android-navigation-appearance-{stage}-pull-response.json"), responseBytes);
            response.EnsureSuccessStatusCode();
            using var payload = JsonDocument.Parse(responseBytes);
            byte[] raw = Convert.FromBase64String(payload.RootElement.GetProperty("value").GetString()
                ?? throw new InvalidOperationException("Standard PullFile response has no file bytes."));
            rawLength = raw.LongLength;
            rawSha = Convert.ToHexString(SHA256.HashData(raw)).ToLowerInvariant();
            // Preserve exact raw bytes before validating errors or incomplete JSONL.
            File.WriteAllBytes(rawPath, raw);
            if (raw.Length == 0 || raw[^1] != (byte)'\n')
                throw new InvalidOperationException("Native diagnostic log is missing or has a partial final record.");
            int records = 0, before = 0, after = 0, preDraw = 0;
            foreach (var line in new UTF8Encoding(false, true).GetString(raw).Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                if (clock.ElapsedMilliseconds >= 30000)
                    throw new TimeoutException("The shared 30-second diagnostic capture deadline expired.");
                using var record = JsonDocument.Parse(line);
                var item = record.RootElement;
                if (item.GetProperty("schema").GetInt32() != 1
                    || item.GetProperty("source").GetString() != source
                    || item.GetProperty("bundle").GetString() != AppiumSetup.BundleId
                    || item.GetProperty("status").GetString() != "observed"
                    || item.GetProperty("state").GetProperty("sdk").GetInt32() != 36)
                    throw new InvalidOperationException("Raw native diagnostic record identity/status is invalid.");
                records++;
                var phase = item.GetProperty("phase").GetString();
                if (phase == "before-update") before++;
                if (phase == "after-update") after++;
                if (phase == "pre-draw") preDraw++;
            }
            if (before == 0 || after == 0 || preDraw == 0)
                throw new InvalidOperationException("Native update or actual pre-draw evidence is missing.");
            string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
            File.WriteAllText(metaPath, JsonSerializer.Serialize(new
            {
                schema = 1, stage, status = "captured", ui_acceptance = false,
                started, completed = DateTimeOffset.UtcNow, source, ownedUdid,
                run_id = Environment.GetEnvironmentVariable("GITHUB_RUN_ID"),
                run_attempt = Environment.GetEnvironmentVariable("GITHUB_RUN_ATTEMPT"),
                test_full_name = TestContext.CurrentContext.Test.FullName,
                test_id = TestContext.CurrentContext.Test.ID,
                workflow_sha256 = Hash(Path.Combine(AppiumSetup.RepoRoot, ".github", "workflows", "release-uitest.yml")),
                assembly_sha256 = Hash(typeof(AndroidNavigationAppearanceDiagnostics).Assembly.Location),
                session_id = session, bundle = AppiumSetup.BundleId,
                remote_path = RemotePath, transport = "standard-Appium-pullFile-endpoint",
                transport_side_effect = "Android driver chmods this diagnostic log and copies it to a temporary path before pulling/removing the temporary copy.",
                raw_sha256 = rawSha, raw_bytes = rawLength, records, before, after, preDraw,
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            if (!File.Exists(metaPath))
                File.WriteAllText(metaPath, JsonSerializer.Serialize(new
                {
                    schema = 1, stage, status = "capture_error", ui_acceptance = false,
                    started, completed = DateTimeOffset.UtcNow, source, ownedUdid,
                    raw_sha256 = rawSha, raw_bytes = rawLength, error = ex.ToString(),
                }, new JsonSerializerOptions { WriteIndented = true }));
            throw new InvalidOperationException("Opt-in Android navigation diagnostic capture failed.", ex);
        }
    }
}
