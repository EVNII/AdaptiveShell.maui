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
        switch (AppiumSetup.Platform)
        {
            case "android":
            {
                var adb = AdbPath();
                var device = DeviceSerial();
                var expected = dark ? "yes" : "no";
                var rootTheme = Environment.GetEnvironmentVariable("UITEST_ANDROID_THEME_ROOT") == "true";
                if (rootTheme && (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true"
                    || Environment.GetEnvironmentVariable("GITHUB_JOB") != "uitest-android"
                    || !device.StartsWith("emulator-", StringComparison.Ordinal)
                    || Run(adb, "-s", device, "shell", "getprop", "ro.build.version.sdk").Trim() != "29"
                    || Run(adb, "-s", device, "shell", "getprop", "ro.kernel.qemu").Trim() != "1"
                    || Run(adb, "-s", device, "shell", "su", "root", "id", "-u").Trim() != "0"))
                    throw new InvalidOperationException("Privileged theme commands require the verified API29 CI emulator.");
                var setter = rootTheme
                    ? Run(adb, "-s", device, "shell", "su", "root", "cmd", "uimode", "night", expected)
                    : Run(adb, "-s", device, "shell", "cmd", "uimode", "night", expected);
                var actual = Run(adb, "-s", device, "shell", "cmd", "uimode", "night").Trim();
                var evidence = new
                {
                    requested = expected, setter_output = setter, actual, privileged_theme_command = rootTheme,
                    setup_complete = Run(adb, "-s", device, "shell", "settings", "get", "secure", "user_setup_complete").Trim(),
                    device_provisioned = Run(adb, "-s", device, "shell", "settings", "get", "global", "device_provisioned").Trim(),
                    uimode_service = Run(adb, "-s", device, "shell", "dumpsys", "uimode"),
                    activity_configuration = Run(adb, "-s", device, "shell", "am", "get-config"),
                };
                var json = System.Text.Json.JsonSerializer.Serialize(evidence,
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                var results = Path.Combine(AppiumSetup.RepoRoot, "TestResults");
                Directory.CreateDirectory(results);
                File.WriteAllText(Path.Combine(results, $"android-theme-command-{DateTime.UtcNow.Ticks}-{expected}.json"), json);
                Console.WriteLine(json);
                if (actual != $"Night mode: {expected}")
                    throw new InvalidOperationException($"Android system night mode was '{actual}', expected '{expected}'.");
                break;
            }
            case "ios":
            {
                // CI 上 simctl 与模拟器服务的通信有时超过 30s。仍要求命令成功,
                // 并回读系统实际外观,避免超时放宽后把未生效的切换当成通过。
                var expected = dark ? "dark" : "light";
                var device = DeviceSerial();
                Run("xcrun", IosAppearanceTimeout,
                    "simctl", "ui", device, "appearance", expected);
                var actual = Run("xcrun", IosAppearanceTimeout,
                    "simctl", "ui", device, "appearance").Trim();
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
