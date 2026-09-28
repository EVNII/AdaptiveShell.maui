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
    public static void SetDark(bool dark)
    {
        switch (AppiumSetup.Platform)
        {
            case "android":
                Run(AdbPath(),
                    "-s", DeviceSerial(), "shell", "cmd", "uimode", "night", dark ? "yes" : "no");
                break;
            case "ios":
                Run("xcrun",
                    "simctl", "ui", DeviceSerial(), "appearance", dark ? "dark" : "light");
                break;
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

    static void Run(string fileName, params string[] args)
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
        process.WaitForExit(15000);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"{fileName} {string.Join(' ', args)} -> exit {process.ExitCode}: " +
                process.StandardError.ReadToEnd());
        }
    }
}
