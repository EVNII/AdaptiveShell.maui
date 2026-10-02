using System.Diagnostics;
using OpenQA.Selenium.Appium;

namespace AdaptiveShell.UITests;

/// <summary>
/// 切换系统深浅色外观。命令直接跑在测试进程所在机器(CI runner/本机):
/// Android 走 adb、iOS 走 simctl、Windows 改注册表(WinUI 应用实时跟随),
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
                Run(AdbPath(),
                    "-s", DeviceSerial(), "shell", "cmd", "uimode", "night", dark ? "yes" : "no");
                break;
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
                Run("reg",
                    @"add", @"HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                    "/v", "AppsUseLightTheme", "/t", "REG_DWORD", "/d", dark ? "0" : "1", "/f");
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
