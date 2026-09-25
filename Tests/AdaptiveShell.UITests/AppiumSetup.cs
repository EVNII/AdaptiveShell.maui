using OpenQA.Selenium.Appium;
using OpenQA.Selenium.Appium.Android;
using OpenQA.Selenium.Appium.iOS;
using OpenQA.Selenium.Appium.Mac;
using OpenQA.Selenium.Appium.Windows;

namespace AdaptiveShell.UITests;

/// <summary>
/// 按 UITEST_PLATFORM(android|ios|maccatalyst|windows)创建 Appium session。
/// 可选环境变量:
///   UITEST_APPIUM_URL   Appium server 地址,默认 http://127.0.0.1:4723
///   UITEST_APP_PATH     被测包路径(apk/app/exe),缺省按约定路径解析
///   UITEST_DEVICE_NAME  目标设备/模拟器名
///   UITEST_DEVICE_UDID  目标设备 udid(iOS 指定具体模拟器时用)
///   UITEST_FORM         compact|wide,供形态相关断言读取
/// </summary>
public static class AppiumSetup
{
    public const string BundleId = "com.companyname.exampleashellapp";

    // iOS 首次会话要现场编译 WebDriverAgent,远超默认 60s 的 HTTP 超时
    static readonly TimeSpan CommandTimeout = TimeSpan.FromMinutes(10);

    public static string Platform =>
        Environment.GetEnvironmentVariable("UITEST_PLATFORM")?.ToLowerInvariant()
        ?? throw new InvalidOperationException(
            "Set UITEST_PLATFORM to android|ios|maccatalyst|windows before running UI tests.");

    public static string? Form =>
        Environment.GetEnvironmentVariable("UITEST_FORM")?.ToLowerInvariant();

    public static AppiumDriver CreateDriver()
    {
        var serverUri = new Uri(
            Environment.GetEnvironmentVariable("UITEST_APPIUM_URL") ?? "http://127.0.0.1:4723");

        return Platform switch
        {
            "android" => CreateAndroid(serverUri),
            "ios" => CreateIos(serverUri),
            "maccatalyst" => CreateMacCatalyst(serverUri),
            "windows" => CreateWindows(serverUri),
            _ => throw new InvalidOperationException($"Unknown UITEST_PLATFORM '{Platform}'."),
        };
    }

    static AndroidDriver CreateAndroid(Uri serverUri)
    {
        var options = new AppiumOptions
        {
            PlatformName = "Android",
            AutomationName = "UiAutomator2",
            DeviceName = Environment.GetEnvironmentVariable("UITEST_DEVICE_NAME") ?? "Android Emulator",
            App = ResolveAppPath(
                Path.Combine("Example", "ExampleAShellApp", "bin", "Debug", "net10.0-android",
                    $"{BundleId}-Signed.apk")),
        };
        AddIfSet(options, "appium:udid", Environment.GetEnvironmentVariable("UITEST_DEVICE_UDID"));
        // 总是重装,避免设备上残留旧的 fast-deploy 包(version 相同会跳过安装)
        options.AddAdditionalAppiumOption("appium:enforceAppInstall", true);
        options.AddAdditionalAppiumOption("appium:newCommandTimeout", 300);
        return new AndroidDriver(serverUri, options, CommandTimeout);
    }

    static IOSDriver CreateIos(Uri serverUri)
    {
        var options = new AppiumOptions
        {
            PlatformName = "iOS",
            AutomationName = "XCuiTest",
        };
        // 已装到 booted 模拟器时可直接用 bundleId 拉起,避免每次重装
        var appPath = TryResolveAppPath(
            Path.Combine("Example", "ExampleAShellApp", "bin", "Debug", "net10.0-ios26.5",
                "iossimulator-arm64", "ExampleAShellApp.app"));
        if (appPath is not null)
        {
            options.App = appPath;
        }
        else
        {
            options.AddAdditionalAppiumOption("appium:bundleId", BundleId);
        }
        AddIfSet(options, "appium:udid", Environment.GetEnvironmentVariable("UITEST_DEVICE_UDID"));
        options.DeviceName = Environment.GetEnvironmentVariable("UITEST_DEVICE_NAME") ?? "iPhone 16";
        options.AddAdditionalAppiumOption("appium:newCommandTimeout", 300);
        return new IOSDriver(serverUri, options, CommandTimeout);
    }

    static MacDriver CreateMacCatalyst(Uri serverUri)
    {
        var options = new AppiumOptions
        {
            PlatformName = "Mac",
            AutomationName = "Mac2",
        };
        options.AddAdditionalAppiumOption("appium:bundleId", BundleId);
        options.AddAdditionalAppiumOption("appium:newCommandTimeout", 300);
        return new MacDriver(serverUri, options, CommandTimeout);
    }

    static WindowsDriver CreateWindows(Uri serverUri)
    {
        var options = new AppiumOptions
        {
            PlatformName = "Windows",
            AutomationName = "Windows",
            App = ResolveAppPath(
                Path.Combine("Example", "ExampleAShellApp", "bin", "Debug",
                    "net10.0-windows10.0.19041.0", "win-x64", "ExampleAShellApp.exe")),
        };
        options.AddAdditionalAppiumOption("appium:newCommandTimeout", 300);
        return new WindowsDriver(serverUri, options, CommandTimeout);
    }

    static void AddIfSet(AppiumOptions options, string name, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            options.AddAdditionalAppiumOption(name, value);
        }
    }

    /// <summary>从测试程序集位置向上找到仓库根(含 AdaptiveShell.slnx 的目录)。</summary>
    public static string RepoRoot
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AdaptiveShell.slnx")))
            {
                dir = dir.Parent;
            }

            return dir?.FullName
                ?? throw new InvalidOperationException(
                    "Could not locate repo root (AdaptiveShell.slnx) above " + AppContext.BaseDirectory);
        }
    }

    static string ResolveAppPath(string relativePath)
    {
        return TryResolveAppPath(relativePath)
            ?? throw new FileNotFoundException(
                $"Test app not found at '{relativePath}'. " +
                "Build ExampleAShellApp for the target platform first, " +
                "or point UITEST_APP_PATH at an existing package.");
    }

    static string? TryResolveAppPath(string relativePath)
    {
        var fromEnv = Environment.GetEnvironmentVariable("UITEST_APP_PATH");
        if (!string.IsNullOrEmpty(fromEnv))
        {
            return Path.GetFullPath(fromEnv);
        }

        var candidate = Path.Combine(RepoRoot, relativePath);
        return File.Exists(candidate) || Directory.Exists(candidate)
            ? candidate
            : null;
    }
}
