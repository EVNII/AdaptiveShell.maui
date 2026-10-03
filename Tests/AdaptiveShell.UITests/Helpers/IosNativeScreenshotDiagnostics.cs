using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using NUnit.Framework;
using OpenQA.Selenium.Appium;

namespace AdaptiveShell.UITests;

// Diagnostic-only independent simulator pixels. The original WDA evidence and
// its strict, zero-residual geometry gate remain the UI acceptance boundary.
internal static class IosNativeScreenshotDiagnostics
{
    internal static void EnsureScope()
    {
        if (!OperatingSystem.IsMacOS() || AppiumSetup.Platform != "ios" || AppiumSetup.Form != "compact"
            || Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true"
            || Environment.GetEnvironmentVariable("RUNNER_ENVIRONMENT") != "github-hosted"
            || Environment.GetEnvironmentVariable("GITHUB_REPOSITORY") != "EVNII/AdaptiveShell.maui"
            || Environment.GetEnvironmentVariable("GITHUB_JOB") != "uitest-ios"
            || Environment.GetEnvironmentVariable("GITHUB_WORKFLOW") != "iOS 18 Screenshot Provider Diagnostic"
            || Environment.GetEnvironmentVariable("GITHUB_REF") != "refs/heads/codex/ios18-screenshot-provider-diagnostic"
            || Environment.GetEnvironmentVariable("UITEST_DEVICE_NAME") != "iPhone 16")
            throw new InvalidOperationException("Native screenshot diagnostics are limited to the exact hosted iOS 18 diagnostic job.");
    }

    internal static void Capture(AppiumDriver driver, string stage, int sequence)
    {
        if (Environment.GetEnvironmentVariable("IOS_SCREENSHOT_PROVIDER_DIAGNOSTIC") != "true") return;
        EnsureScope();
        var fullName = TestContext.CurrentContext.Test.FullName;
        if (fullName != "AdaptiveShell.UITests.LandingPageDarkModeTests.DarkMode_GroupLandingPage")
            throw new InvalidOperationException("Only the original landing theme test may capture independent native evidence.");
        string? Capability(string name) => (driver.Capabilities.GetCapability(name)
            ?? driver.Capabilities.GetCapability("appium:" + name))?.ToString();
        var udid = Capability("udid");
        if (string.IsNullOrWhiteSpace(udid) || udid != Environment.GetEnvironmentVariable("UITEST_DEVICE_UDID")
            || Capability("platformVersion") != "18.5" || Capability("deviceName") != "iPhone 16")
            throw new InvalidOperationException("The actual Appium session must identify the exact original iPhone 16 iOS 18.5 UDID.");
        var root = AppiumSetup.RepoRoot;
        var results = Path.Combine(root, "TestResults");
        var requestPath = Path.Combine(results, $"landing-{stage}-native-screenshot-request.json");
        var workflow = Path.Combine(root, ".github", "workflows", "release-uitest.yml");
        string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
        if (File.Exists(requestPath)) throw new InvalidOperationException("Native screenshot phase evidence already exists.");
        File.WriteAllText(requestPath, JsonSerializer.Serialize(new
        {
            schema = 1, stage, sequence, test_full_name = fullName, test_id = TestContext.CurrentContext.Test.ID,
            appium_session_id = driver.SessionId.ToString(), actual_udid = udid,
            actual_platform_version = Capability("platformVersion"), actual_device_name = Capability("deviceName"),
            run_id = Environment.GetEnvironmentVariable("GITHUB_RUN_ID"),
            run_attempt = Environment.GetEnvironmentVariable("GITHUB_RUN_ATTEMPT"),
            head_sha = Environment.GetEnvironmentVariable("GITHUB_SHA"), workflow_sha256 = Hash(workflow),
            assembly_path = typeof(IosNativeScreenshotDiagnostics).Assembly.Location,
            assembly_sha256 = Hash(typeof(IosNativeScreenshotDiagnostics).Assembly.Location),
        }));
        var start = new ProcessStartInfo("python3")
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = root,
        };
        start.ArgumentList.Add(Path.Combine(root, "Tests", "AdaptiveShell.UITests", "Scripts", "capture_ios_native_screenshot.py"));
        start.ArgumentList.Add(requestPath);
        using var process = new Process { StartInfo = start };
        if (!process.Start()) throw new InvalidOperationException("Could not start native screenshot evidence capture.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        var stdout = process.StandardOutput.ReadToEndAsync(deadline.Token);
        var stderr = process.StandardError.ReadToEndAsync(deadline.Token);
        try
        {
            Task.WhenAll(process.WaitForExitAsync(deadline.Token), stdout, stderr).GetAwaiter().GetResult();
        }
        catch
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            throw;
        }
        TestContext.Out.WriteLine(stdout.GetAwaiter().GetResult());
        TestContext.Out.WriteLine(stderr.GetAwaiter().GetResult());
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"Independent native screenshot capture failed with exit {process.ExitCode}; UI success is not established.");
    }
}
