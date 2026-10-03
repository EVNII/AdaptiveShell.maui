using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using NUnit.Framework;
using OpenQA.Selenium.Appium;

namespace AdaptiveShell.UITests;

// iOS 18's WDA image can omit one real display column. Use the owned simulator's
// complete PNG for this landing test; preserve the WDA bytes as a comparison.
internal static class IosLandingScreenshotProvider
{
    internal static object Save(AppiumDriver driver, string label, int sequence)
    {
        string? Capability(string name) => (driver.Capabilities.GetCapability(name)
            ?? driver.Capabilities.GetCapability("appium:" + name))?.ToString();
        var version = Capability("platformVersion");
        if (AppiumSetup.Platform != "ios" || AppiumSetup.Form != "compact"
            || !Version.TryParse(version, out var parsed) || parsed.Major != 18)
        {
            Shots.Save(driver, label, sequence);
            return new { provider = "appium_driver" };
        }
        if (!OperatingSystem.IsMacOS() || Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true"
            || Environment.GetEnvironmentVariable("RUNNER_ENVIRONMENT") != "github-hosted"
            || Environment.GetEnvironmentVariable("GITHUB_REPOSITORY") != "EVNII/AdaptiveShell.maui"
            || Environment.GetEnvironmentVariable("GITHUB_JOB") is not ("uitest-ios" or "uitest-ios-18")
            || TestContext.CurrentContext.Test.FullName != "AdaptiveShell.UITests.LandingPageDarkModeTests.DarkMode_GroupLandingPage")
            throw new InvalidOperationException("The iOS 18 landing provider requires this test's owned hosted simulator.");
        var udid = Capability("udid");
        if (string.IsNullOrWhiteSpace(udid) || udid != Environment.GetEnvironmentVariable("UITEST_DEVICE_UDID"))
            throw new InvalidOperationException("The actual Appium session must identify the owned simulator UDID.");
        var workflowRef = Environment.GetEnvironmentVariable("GITHUB_WORKFLOW_REF") ?? "";
        var workflowPath = workflowRef.Split('@')[0];
        const string repositoryPrefix = "EVNII/AdaptiveShell.maui/";
        if (!workflowPath.StartsWith(repositoryPrefix, StringComparison.Ordinal))
            throw new InvalidOperationException("An exact repository workflow identity is required.");
        workflowPath = workflowPath[repositoryPrefix.Length..];
        if (workflowPath is not (".github/workflows/uitest.yml" or ".github/workflows/release-uitest.yml"))
            throw new InvalidOperationException("An existing registered UI workflow is required.");
        var root = AppiumSetup.RepoRoot;
        var results = Path.Combine(root, "TestResults");
        var name = $"{sequence:00}-{label}.png";
        var primary = Path.Combine(results, "shots", "ios-compact", name);
        var comparisonDir = Path.Combine(results, "shots", "ios-compact", "provider-evidence");
        Directory.CreateDirectory(comparisonDir);
        var comparison = Path.Combine(comparisonDir, $"{sequence:00}-{label}-wda.png");
        var request = Path.Combine(results, $"landing-{sequence:00}-screenshot-provider-request.json");
        string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
        if (File.Exists(primary) || File.Exists(request) || File.Exists(comparison))
            throw new InvalidOperationException("Original provider evidence must not be overwritten.");
        // The WDA result never occupies the primary path.
        var original = driver.GetScreenshot().AsByteArray;
        using (var file = new FileStream(comparison, FileMode.CreateNew, FileAccess.Write)) file.Write(original);
        var size = driver.Manage().Window.Size;
        var active = JsonSerializer.SerializeToElement(driver.ExecuteScript("mobile:activeAppInfo"));
        if (!active.TryGetProperty("bundleId", out var bundle) || bundle.GetString() != AppiumSetup.BundleId)
            throw new InvalidOperationException("The current native application must be the tested sample.");
        var data = new
        {
            schema = 1, sequence, label, primary_file = $"shots/ios-compact/{name}",
            comparison_file = $"shots/ios-compact/provider-evidence/{sequence:00}-{label}-wda.png",
            comparison_sha256 = Hash(comparison), test_full_name = TestContext.CurrentContext.Test.FullName,
            appium_session_id = driver.SessionId.ToString(), actual_udid = udid,
            actual_platform_version = version, actual_device_name = Capability("deviceName"),
            window_size = new { width = size.Width, height = size.Height }, active_app = active,
            run_id = Environment.GetEnvironmentVariable("GITHUB_RUN_ID"),
            run_attempt = Environment.GetEnvironmentVariable("GITHUB_RUN_ATTEMPT"),
            head_sha = Environment.GetEnvironmentVariable("GITHUB_SHA"), workflow_path = workflowPath,
            workflow_ref = workflowRef, workflow_sha256 = Hash(Path.Combine(root, workflowPath)),
            assembly_path = typeof(IosLandingScreenshotProvider).Assembly.Location,
            assembly_sha256 = Hash(typeof(IosLandingScreenshotProvider).Assembly.Location),
        };
        using (var file = new FileStream(request, FileMode.CreateNew, FileAccess.Write))
            JsonSerializer.Serialize(file, data);
        var start = new ProcessStartInfo("python3")
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = root,
        };
        start.ArgumentList.Add(Path.Combine(root, "Tests", "AdaptiveShell.UITests", "Scripts", "capture_ios_landing_screenshot.py"));
        start.ArgumentList.Add(request);
        using var process = new Process { StartInfo = start };
        if (!process.Start()) throw new InvalidOperationException("Could not start the original native screenshot provider.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        var stdout = process.StandardOutput.ReadToEndAsync(deadline.Token);
        var stderr = process.StandardError.ReadToEndAsync(deadline.Token);
        try { Task.WhenAll(process.WaitForExitAsync(deadline.Token), stdout, stderr).GetAwaiter().GetResult(); }
        catch { if (!process.HasExited) process.Kill(entireProcessTree: true); throw; }
        TestContext.Out.WriteLine(stdout.GetAwaiter().GetResult());
        TestContext.Out.WriteLine(stderr.GetAwaiter().GetResult());
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"The native provider failed with exit {process.ExitCode}; no screenshot acceptance is established.");
        return new
        {
            provider = "simctl_internal", actual_udid = udid, appium_session_id = driver.SessionId.ToString(),
            actual_platform_version = version, run_id = Environment.GetEnvironmentVariable("GITHUB_RUN_ID"),
            run_attempt = Environment.GetEnvironmentVariable("GITHUB_RUN_ATTEMPT"),
            head_sha = Environment.GetEnvironmentVariable("GITHUB_SHA"),
        };
    }
}
