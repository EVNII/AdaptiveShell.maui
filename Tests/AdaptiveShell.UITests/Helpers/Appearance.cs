using System.Diagnostics;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using OpenQA.Selenium.Appium;

namespace AdaptiveShell.UITests;

/// <summary>
/// 切换系统深浅色外观。命令直接跑在测试进程所在机器(CI runner/本机):
/// Android 走 adb、iOS 走 simctl、Windows 改系统设置并广播主题通知,
/// MacCatalyst 走 osascript(需自动化授权,实验位)。
/// </summary>
public static class Appearance
{
    static readonly TimeSpan IosAppearanceTimeout = TimeSpan.FromMinutes(2);

    public static void SetDark(bool dark)
    {
        string? iosSetterOutput = null;
        string? iosImmediateAppearance = null;
        switch (AppiumSetup.Platform)
        {
            case "android":
                Run(AdbPath(),
                    "-s", DeviceSerial(), "shell", "cmd", "uimode", "night", dark ? "yes" : "no");
                break;
            case "ios":
            {
                // CI 上 simctl 与模拟器服务的通信有时超过 30s。仍要求命令成功,
                // 并回读系统实际外观,避免超时放宽后把未生效的切换当成通过。
                var expected = dark ? "dark" : "light";
                var device = DeviceSerial();
                iosSetterOutput = Run("xcrun", IosAppearanceTimeout,
                    "simctl", "ui", device, "appearance", expected);
                var actual = Run("xcrun", IosAppearanceTimeout,
                    "simctl", "ui", device, "appearance").Trim();
                iosImmediateAppearance = actual;
                if (actual != expected)
                {
                    throw new InvalidOperationException(
                        $"Simulator appearance was '{actual}', expected '{expected}'.");
                }
                Console.WriteLine($"Simulator appearance confirmed: {actual}");
                break;
            }
            case "windows":
                SetWindowsAppearance(dark);
                break;
            case "maccatalyst":
                Run("osascript", "-e",
                    $"tell application \"System Events\" to tell appearance preferences to set dark mode to {dark.ToString().ToLowerInvariant()}");
                break;
            default:
                throw new InvalidOperationException($"Unknown platform {AppiumSetup.Platform}");
        }

        // 等主题传播与页面重渲染
        Thread.Sleep(2500);
        if (AppiumSetup.Platform == "ios"
            && Environment.GetEnvironmentVariable("UITEST_IOS_SYSTEM_THEME_DIAGNOSTIC") is not null)
            CaptureIosSystemTheme(dark, iosSetterOutput!, iosImmediateAppearance!);
    }

    static void CaptureIosSystemTheme(bool dark, string setterOutput, string immediateAppearance)
    {
        var device = DeviceSerial();
        var version = SessionHost.Driver.Capabilities.GetCapability("platformVersion")?.ToString();
        if (Environment.GetEnvironmentVariable("UITEST_IOS_SYSTEM_THEME_DIAGNOSTIC") != "true"
            || Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true"
            || Environment.GetEnvironmentVariable("GITHUB_JOB") != "uitest-ios-18"
            || AppiumSetup.Form != "compact" || version is null
            || !version.StartsWith("18.", StringComparison.Ordinal)
            || !Guid.TryParseExact(device, "D", out _)
            || AppiumSetup.BundleId != "com.companyname.exampleashellapp")
            throw new InvalidOperationException("Native theme capture requires the explicit iOS18 compact CI diagnostic.");
        var expected = dark ? "dark" : "light";
        var results = Path.Combine(AppiumSetup.RepoRoot, "TestResults");
        Directory.CreateDirectory(results);
        var prefix = $"ios-native-theme-{DateTime.UtcNow.Ticks}-{expected}";
        var record = new Dictionary<string, object?>
        {
            ["requested"] = expected, ["setter_output"] = setterOutput,
            ["test_full_name"] = NUnit.Framework.TestContext.CurrentContext.Test.FullName,
            ["test_id"] = NUnit.Framework.TestContext.CurrentContext.Test.ID,
            ["immediate_system_appearance"] = immediateAppearance,
            ["device_udid"] = device, ["bundle_id"] = AppiumSetup.BundleId,
            ["platform_version"] = version, ["run_id"] = Environment.GetEnvironmentVariable("GITHUB_RUN_ID"),
            ["run_attempt"] = Environment.GetEnvironmentVariable("GITHUB_RUN_ATTEMPT"),
            ["head_sha"] = Environment.GetEnvironmentVariable("GITHUB_SHA"),
            ["capture_begin_utc"] = DateTime.UtcNow.ToString("O"),
        };
        try
        {
            record["system_appearance_before_screenshots"] = Run("xcrun", IosAppearanceTimeout,
                "simctl", "ui", device, "appearance").Trim();
            record["active_app_info"] = SessionHost.Driver.ExecuteScript("mobile:activeAppInfo");
            var container = Run("xcrun", IosAppearanceTimeout, "simctl", "get_app_container",
                device, AppiumSetup.BundleId, "data").Trim();
            if (!Path.IsPathFullyQualified(container) || !Directory.Exists(container))
                throw new InvalidOperationException("Native app data container is not an existing absolute path.");
            var nativeLog = Path.Combine(container, "Documents", "ios-native-theme.jsonl");
            record["native_log_source"] = nativeLog;
            CopyNativeLog("before");
            var wda = Path.Combine(results, prefix + "-wda.png");
            record["wda_screenshot_begin_utc"] = DateTime.UtcNow.ToString("O");
            SessionHost.Driver.GetScreenshot().SaveAsFile(wda);
            record["wda_png"] = Path.GetFileName(wda);
            record["wda_screenshot_end_utc"] = DateTime.UtcNow.ToString("O");
            var native = Path.Combine(results, prefix + "-simctl.png");
            record["simctl_screenshot_begin_utc"] = DateTime.UtcNow.ToString("O");
            record["simctl_screenshot_output"] = Run("xcrun", IosAppearanceTimeout,
                "simctl", "io", device, "screenshot", native);
            if (!File.Exists(native) || new FileInfo(native).Length <= 24)
                throw new InvalidOperationException("Native simulator screenshot was not saved.");
            record["simctl_png"] = Path.GetFileName(native);
            record["simctl_screenshot_end_utc"] = DateTime.UtcNow.ToString("O");
            CopyNativeLog("after");
            record["system_appearance_after_screenshots"] = Run("xcrun", IosAppearanceTimeout,
                "simctl", "ui", device, "appearance").Trim();
            record["status"] = "captured";

            void CopyNativeLog(string when)
            {
                var bytes = File.ReadAllBytes(nativeLog);
                var path = Path.Combine(results, prefix + $"-{when}.jsonl");
                File.WriteAllBytes(path, bytes);
                record[$"native_log_{when}"] = Path.GetFileName(path);
                record[$"native_log_{when}_bytes"] = bytes.Length;
                if (bytes.Length == 0 || bytes[^1] != (byte)'\n')
                    throw new InvalidOperationException("Native theme JSONL capture is empty or ends in an incomplete record.");
            }
        }
        catch (Exception error)
        {
            record["status"] = "capture-error";
            record["error"] = error.ToString();
            throw;
        }
        finally
        {
            record["capture_end_utc"] = DateTime.UtcNow.ToString("O");
            File.WriteAllText(Path.Combine(results, prefix + ".json"),
                System.Text.Json.JsonSerializer.Serialize(record,
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
    }

    static void SetWindowsAppearance(bool dark)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Windows appearance requires a Windows runner.");

        const string personalize = @"HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Themes\Personalize";
        var expected = dark ? 0 : 1;
        Run("reg", "add", personalize,
            "/v", "AppsUseLightTheme", "/t", "REG_DWORD", "/d", expected.ToString(), "/f");
        var actual = Registry.GetValue(
            @"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\Themes\Personalize",
            "AppsUseLightTheme", null);
        if (actual is not int value || value != expected)
            throw new InvalidOperationException($"AppsUseLightTheme was '{actual}', expected {expected}.");

        // MAUI 的 Windows 生命周期只在 WM_SETTINGCHANGE/WM_THEMECHANGE 后重新读取
        // AppInfo.RequestedTheme。仅写注册表会让 WinUI 图标变化,AppThemeBinding 却保持旧值。
        // 通知所有顶层窗口,与系统设置切换主题时的传播路径一致。
        var sent = SendMessageTimeout(new IntPtr(0xffff), 0x001a, UIntPtr.Zero,
            "ImmersiveColorSet", 0x0002 | 0x0020, 5000, out _);
        if (sent == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Windows theme notification failed or timed out.");
        Console.WriteLine($"Windows appearance confirmed: {(dark ? "dark" : "light")}; setting-change broadcast sent.");
    }

    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr SendMessageTimeout(IntPtr window, uint message, UIntPtr wParam,
        string lParam, uint flags, uint timeout, out UIntPtr result);

    static string DeviceSerial()
    {
        return Environment.GetEnvironmentVariable("UITEST_DEVICE_UDID")
            ?? (AppiumSetup.Platform == "android" ? "emulator-5554" : null)
            ?? throw new InvalidOperationException(
                $"UITEST_DEVICE_UDID is required to switch appearance on {AppiumSetup.Platform}.");
    }

    static string AdbPath()
    {
        foreach (var candidate in new[]
        {
            Path.Combine(Environment.GetEnvironmentVariable("ANDROID_HOME") ?? "", "platform-tools", "adb"),
            Path.Combine(Environment.GetEnvironmentVariable("ANDROID_SDK_ROOT") ?? "", "platform-tools", "adb"),
            Path.Combine(Environment.GetEnvironmentVariable("HOME") ?? "", "Library/Android/sdk/platform-tools/adb"),
            "/usr/local/lib/android/sdk/platform-tools/adb",
        })
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        // 最后才指望 PATH 里的 adb
        return "adb";
    }

    static string Run(string fileName, params string[] args) =>
        Run(fileName, TimeSpan.FromSeconds(30), args);

    static string Run(string fileName, TimeSpan timeout, params string[] args)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start {fileName}");
        // 同时消费两个管道,防止输出缓冲区填满后子进程无法退出。
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit((int)timeout.TotalMilliseconds))
        {
            try
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
            catch (InvalidOperationException) { /* 已退出则忽略 */ }
            throw new InvalidOperationException(
                $"{fileName} {string.Join(' ', args)} timed out after {timeout.TotalSeconds:0}s");
        }
        var output = stdout.GetAwaiter().GetResult();
        var error = stderr.GetAwaiter().GetResult();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"{fileName} {string.Join(' ', args)} -> exit {process.ExitCode}: " +
                error);
        }
        return output;
    }
}
