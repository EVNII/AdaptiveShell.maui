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
    const string NativePath = "files/ashell-navigation-appearance.jsonl";

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
        var operations = new List<Dictionary<string, object?>>();
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
                || Environment.GetEnvironmentVariable("GITHUB_REF") != "refs/heads/codex/android36-window-background-verification"
                || Environment.GetEnvironmentVariable("GITHUB_WORKFLOW") != "Android36 Window Background Verification"
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
            // Read the same debug AUT through its exact already-owned emulator.
            // No Appium security extension, root, provisioning or ADB-server action.
            using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(
                Math.Max(1, 30000 - clock.ElapsedMilliseconds)));
            var session = driver.SessionId?.ToString()
                ?? throw new InvalidOperationException("Diagnostic has no actual Appium session.");
            string Probe(string name, params string[] arguments)
            {
                string path = Path.Combine(results, $"android-navigation-appearance-{stage}-{name}.stdout");
                RunAdb(arguments, path, path + ".stderr", operations, deadline.Token);
                return new UTF8Encoding(false, true).GetString(File.ReadAllBytes(path)).Trim();
            }
            if (Probe("serial", "-s", ownedUdid!, "get-serialno") != ownedUdid
                || Probe("sdk", "-s", ownedUdid!, "shell", "getprop", "ro.build.version.sdk") != "36")
                throw new InvalidOperationException("The owned ADB serial or native SDK readback does not match the actual Appium session.");
            RunAdb(new[] { "-s", ownedUdid!, "exec-out", "run-as", AppiumSetup.BundleId,
                "cat", NativePath }, rawPath, rawPath + ".stderr", operations, deadline.Token);
            // stdout went straight into the raw file, before any decoding/validation.
            byte[] raw = File.ReadAllBytes(rawPath);
            rawLength = raw.LongLength;
            rawSha = Convert.ToHexString(SHA256.HashData(raw)).ToLowerInvariant();
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
                native_relative_path = NativePath, transport = "owned-adb-exec-out-run-as-cat",
                capture_budget_seconds = 30, elapsed_seconds = clock.Elapsed.TotalSeconds, operations,
                raw_sha256 = rawSha, raw_bytes = rawLength, records, before, after, preDraw,
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            if (File.Exists(rawPath))
            {
                rawLength = new FileInfo(rawPath).Length;
                rawSha = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(rawPath))).ToLowerInvariant();
            }
            if (!File.Exists(metaPath))
                File.WriteAllText(metaPath, JsonSerializer.Serialize(new
                {
                    schema = 1, stage, status = "capture_error", ui_acceptance = false,
                    started, completed = DateTimeOffset.UtcNow, source, ownedUdid,
                    capture_budget_seconds = 30, elapsed_seconds = clock.Elapsed.TotalSeconds, operations,
                    raw_sha256 = rawSha, raw_bytes = rawLength, error = ex.ToString(),
                }, new JsonSerializerOptions { WriteIndented = true }));
            throw new InvalidOperationException("Opt-in Android navigation diagnostic capture failed.", ex);
        }
    }

    static void RunAdb(string[] arguments, string stdoutPath, string stderrPath,
        List<Dictionary<string, object?>> operations, CancellationToken deadline)
    {
        deadline.ThrowIfCancellationRequested();
        var operation = new Dictionary<string, object?>
        {
            ["argv"] = new[] { "adb" }.Concat(arguments).ToArray(),
            ["started_utc"] = DateTimeOffset.UtcNow,
            ["stdout_path"] = Path.GetFileName(stdoutPath),
            ["stderr_path"] = Path.GetFileName(stderrPath),
            ["cleanup_policy"] = "only the spawned ADB client PID; never existing server/daemon/device/AUT",
        };
        operations.Add(operation);
        var start = new ProcessStartInfo("adb")
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        using var stdout = new FileStream(stdoutPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        using var stderr = new FileStream(stderrPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        try
        {
            deadline.ThrowIfCancellationRequested();
            if (!process.Start()) throw new InvalidOperationException("The owned ADB client did not start.");
            operation["client_pid"] = process.Id;
            operation["client_start_time_utc"] = process.StartTime.ToUniversalTime();
            var output = process.StandardOutput.BaseStream.CopyToAsync(stdout, deadline);
            var error = process.StandardError.BaseStream.CopyToAsync(stderr, deadline);
            Task.WhenAll(process.WaitForExitAsync(deadline), output, error).GetAwaiter().GetResult();
            operation["exit_code"] = process.ExitCode;
            if (process.ExitCode != 0)
                throw new InvalidOperationException("The owned ADB diagnostic command failed; exact stdout/stderr and exit are retained.");
        }
        catch (Exception ex)
        {
            operation["error"] = ex.ToString();
            if (operation.ContainsKey("client_pid") && !process.HasExited)
            {
                // Kill(false) addresses the process handle created above. ADB's
                // persistent server is deliberately outside this cleanup scope.
                try { process.Kill(entireProcessTree: false); operation["client_kill_sent"] = true; }
                catch (Exception cleanup) { operation["cleanup_error"] = cleanup.ToString(); }
            }
            throw;
        }
        finally
        {
            operation["completed_utc"] = DateTimeOffset.UtcNow;
            stdout.Flush(); stderr.Flush();
            operation["stdout_bytes"] = stdout.Length;
            operation["stderr_bytes"] = stderr.Length;
        }
    }
}
