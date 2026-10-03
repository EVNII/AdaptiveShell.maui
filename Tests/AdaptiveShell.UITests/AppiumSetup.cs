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

    static readonly TimeSpan CommandTimeout = TimeSpan.FromMinutes(10);
    // iOS 首次会话需启动模拟器、安装 App 和编译 WebDriverAgent,
    // 整个会话创建预算须覆盖这些连续步骤,而不只是 WDA 启动超时。
    static readonly TimeSpan IosCommandTimeout = TimeSpan.FromMinutes(15);

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
        // 慢速 CI 模拟器上 adb install 可能超过默认 90s
        options.AddAdditionalAppiumOption("appium:androidInstallTimeout", 300000);
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
        // CI 已预启动无窗口的模拟器;避免 Appium 为显示窗口再次重启。
        options.AddAdditionalAppiumOption("appium:isHeadless", true);
        if (Form == "duo")
        {
            // 公开 Duo 诊断停在系统日志流启动；跳过该辅助流，保留驱动日志与全部界面断言。
            options.AddAdditionalAppiumOption("appium:skipLogCapture", true);
            // 保留 WebDriverAgent 的构建与启动输出，便于公开 CI 追踪。
            options.AddAdditionalAppiumOption("appium:showXcodeLog", true);
            // Duo CI 可显式使用提前构建的 WDA，避免模拟器启动时同时编译。
            var prebuilt = Environment.GetEnvironmentVariable("DUO_DIAGNOSTIC_USE_PREBUILT_WDA");
            var derivedData = Environment.GetEnvironmentVariable("DUO_DIAGNOSTIC_WDA_DERIVED_DATA_PATH");
            if (prebuilt is not null || derivedData is not null)
            {
                if (prebuilt != "true" || string.IsNullOrWhiteSpace(derivedData)
                    || !Path.IsPathFullyQualified(derivedData) || !Directory.Exists(derivedData))
                {
                    throw new InvalidOperationException("Duo prebuilt WDA requires explicit opt-in and an existing absolute derivedDataPath.");
                }
                var capabilities = new Dictionary<string, object>
                {
                    ["appium:usePrebuiltWDA"] = true,
                    ["appium:derivedDataPath"] = derivedData,
                    ["appium:usePreinstalledWDA"] = false,
                    ["appium:useSimpleBuildTest"] = false,
                };
                foreach (var capability in capabilities)
                    options.AddAdditionalAppiumOption(capability.Key, capability.Value);
                var results = Path.Combine(RepoRoot, "TestResults");
                Directory.CreateDirectory(results);
                File.WriteAllText(Path.Combine(results, "duo-wda-session-capabilities.json"),
                    System.Text.Json.JsonSerializer.Serialize(capabilities));
            }
        }
        if (Environment.GetEnvironmentVariable("UITEST_DUO_COMPATIBILITY_OPTIONS") is not null)
        {
            RequireDuoCompatibilityScope();
            var capabilities = new Dictionary<string, object>
            {
                ["appium:settings[enforceCustomSnapshots]"] = true,
                ["appium:screenshotQuality"] = 0,
            };
            foreach (var capability in capabilities)
                options.AddAdditionalAppiumOption(capability.Key, capability.Value);
            SaveDuoCompatibilityEvidence("duo-driver-compatibility-requested-options.json", new
            {
                schema_version = 1,
                status = "requested-before-ios-driver-construction",
                recorded_utc = DateTimeOffset.UtcNow,
                head_sha = Environment.GetEnvironmentVariable("GITHUB_SHA"),
                run_id = Environment.GetEnvironmentVariable("GITHUB_RUN_ID"),
                run_attempt = Environment.GetEnvironmentVariable("GITHUB_RUN_ATTEMPT"),
                device_udid = Environment.GetEnvironmentVariable("UITEST_DEVICE_UDID"),
                branch = Environment.GetEnvironmentVariable("GITHUB_REF_NAME"),
                workflow = Environment.GetEnvironmentVariable("GITHUB_WORKFLOW"),
                job = Environment.GetEnvironmentVariable("GITHUB_JOB"),
                capabilities,
                backend_settings_verified = false,
                ui_acceptance = false,
            });
        }
        options.AddAdditionalAppiumOption("appium:simulatorStartupTimeout", 600000);
        // 干净机器上首个会话要现场编译 WebDriverAgent,远超默认 60s 的启动超时
        options.AddAdditionalAppiumOption("appium:wdaLaunchTimeout", 600000);
        options.AddAdditionalAppiumOption("appium:newCommandTimeout", 300);
        return new IOSDriver(serverUri, options, IosCommandTimeout);
    }

    // The requested capability file is not backend readback. SessionHost calls
    // this after the original home wait, before its first WDA screenshot.
    public static void VerifyDuoCompatibilitySettings()
    {
        if (Environment.GetEnvironmentVariable("UITEST_DUO_COMPATIBILITY_OPTIONS") is null)
            return;

        var watch = System.Diagnostics.Stopwatch.StartNew();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var operations = new List<Dictionary<string, object?>>();
        var evidence = new Dictionary<string, object?>
        {
            ["schema_version"] = 1,
            ["started_utc"] = DateTimeOffset.UtcNow,
            ["budget_seconds"] = 10,
            ["head_sha"] = Environment.GetEnvironmentVariable("GITHUB_SHA"),
            ["run_id"] = Environment.GetEnvironmentVariable("GITHUB_RUN_ID"),
            ["run_attempt"] = Environment.GetEnvironmentVariable("GITHUB_RUN_ATTEMPT"),
            ["device_udid"] = Environment.GetEnvironmentVariable("UITEST_DEVICE_UDID"),
            ["operations"] = operations,
            ["backend_settings_verified"] = false,
            ["ui_acceptance"] = false,
        };
        try
        {
            RequireDuoCompatibilityScope();
            using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false })
                { Timeout = Timeout.InfiniteTimeSpan };
            System.Text.Json.JsonElement Get(string path, string rawFile)
            {
                deadline.Token.ThrowIfCancellationRequested();
                var operation = new Dictionary<string, object?>
                {
                    ["method"] = "GET", ["url"] = "http://127.0.0.1:8100" + path,
                    ["raw_file"] = rawFile, ["started_utc"] = DateTimeOffset.UtcNow,
                };
                operations.Add(operation);
                try
                {
                    using var response = http.GetAsync("http://127.0.0.1:8100" + path,
                        HttpCompletionOption.ResponseHeadersRead, deadline.Token).GetAwaiter().GetResult();
                    operation["http_status"] = (int)response.StatusCode;
                    var directory = Path.Combine(RepoRoot, "TestResults");
                    Directory.CreateDirectory(directory);
                    using (var output = new FileStream(Path.Combine(directory, rawFile), FileMode.CreateNew,
                        FileAccess.Write, FileShare.Read, 4096, FileOptions.Asynchronous))
                    using (var input = response.Content.ReadAsStreamAsync(deadline.Token).GetAwaiter().GetResult())
                        input.CopyToAsync(output, deadline.Token).GetAwaiter().GetResult();
                    var bytes = File.ReadAllBytes(Path.Combine(directory, rawFile));
                    operation["sha256"] = Convert.ToHexString(
                        System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
                    operation["bytes"] = bytes.Length;
                    response.EnsureSuccessStatusCode();
                    deadline.Token.ThrowIfCancellationRequested();
                    using var document = System.Text.Json.JsonDocument.Parse(bytes);
                    var root = document.RootElement;
                    if (!root.TryGetProperty("sessionId", out var session)
                        || session.ValueKind != System.Text.Json.JsonValueKind.String
                        || !Guid.TryParseExact(session.GetString(), "D", out var parsed) || parsed == Guid.Empty
                        || !root.TryGetProperty("value", out var value)
                        || value.ValueKind == System.Text.Json.JsonValueKind.Object && value.TryGetProperty("error", out _))
                        throw new InvalidOperationException("Actual WDA response has no valid backend session or contains an error.");
                    operation["status"] = "completed";
                    return root.Clone();
                }
                catch (Exception error)
                {
                    operation["status"] = "error";
                    operation["error"] = error.ToString();
                    throw;
                }
                finally { operation["finished_utc"] = DateTimeOffset.UtcNow; }
            }

            var screens = Get("/wda/screens", "duo-driver-compatibility-wda-screens.raw.json");
            var sessionId = screens.GetProperty("sessionId").GetString()!;
            evidence["wda_session_id"] = sessionId;
            var settings = Get("/session/" + Uri.EscapeDataString(sessionId) + "/appium/settings",
                "duo-driver-compatibility-wda-settings.raw.json");
            if (settings.GetProperty("sessionId").GetString() != sessionId)
                throw new InvalidOperationException("Actual WDA session changed between screens and settings readback.");
            var value = settings.GetProperty("value");
            if (value.ValueKind != System.Text.Json.JsonValueKind.Object
                || !value.TryGetProperty("enforceCustomSnapshots", out var snapshots)
                || snapshots.ValueKind != System.Text.Json.JsonValueKind.True
                || !value.TryGetProperty("screenshotQuality", out var quality)
                || quality.ValueKind != System.Text.Json.JsonValueKind.Number
                || !quality.TryGetInt32(out var actualQuality) || actualQuality != 0)
                throw new InvalidOperationException("Actual WDA settings must be enforceCustomSnapshots=true and screenshotQuality=0.");
            deadline.Token.ThrowIfCancellationRequested();
            evidence["settings"] = value.Clone();
            evidence["backend_settings_verified"] = true;
            evidence["status"] = "verified-backend-settings-only";
            evidence["finished_utc"] = DateTimeOffset.UtcNow;
            evidence["elapsed_seconds"] = watch.Elapsed.TotalSeconds;
            SaveDuoCompatibilityEvidence("duo-driver-compatibility-settings-proof.json", evidence);
        }
        catch (Exception error)
        {
            evidence["status"] = "error";
            evidence["error"] = error.ToString();
            evidence["finished_utc"] = DateTimeOffset.UtcNow;
            evidence["elapsed_seconds"] = watch.Elapsed.TotalSeconds;
            try { SaveDuoCompatibilityEvidence("duo-driver-compatibility-settings-error.json", evidence); }
            catch (Exception saveError) { throw new AggregateException("Duo settings verification and evidence save failed.", error, saveError); }
            throw;
        }
    }

    static void RequireDuoCompatibilityScope()
    {
        static string? Env(string key) => Environment.GetEnvironmentVariable(key);
        var head = Env("GITHUB_SHA");
        var udid = Env("UITEST_DEVICE_UDID");
        if (Env("UITEST_DUO_COMPATIBILITY_OPTIONS") != "true" || Platform != "ios" || Form != "duo"
            || !OperatingSystem.IsMacOS() || Env("GITHUB_ACTIONS") != "true"
            || Env("RUNNER_ENVIRONMENT") != "github-hosted"
            || Env("GITHUB_REPOSITORY") != "EVNII/AdaptiveShell.maui"
            || Env("GITHUB_JOB") != "uitest-ios-27-1-duo"
            || Env("GITHUB_REF_NAME") != "codex/duo-native-first-boot"
            || Env("GITHUB_WORKFLOW") != "Duo Native First Boot E2E"
            || head is not { Length: 40 } || head.Any(c => !"0123456789abcdef".Contains(c))
            || Env("GITHUB_WORKFLOW_SHA") != head
            || !ulong.TryParse(Env("GITHUB_RUN_ID"), out var run) || run == 0
            || !uint.TryParse(Env("GITHUB_RUN_ATTEMPT"), out var attempt) || attempt == 0
            || !Guid.TryParseExact(udid, "D", out _) || udid != Env("DUO_DEVICE_UDID"))
            throw new InvalidOperationException("Duo compatibility options require the explicitly opted-in owned hosted Duo diagnostic job.");
    }

    static void SaveDuoCompatibilityEvidence(string name, object value)
    {
        var directory = Path.Combine(RepoRoot, "TestResults");
        Directory.CreateDirectory(directory);
        using var file = new FileStream(Path.Combine(directory, name), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        System.Text.Json.JsonSerializer.Serialize(file, value,
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
    }

    static MacDriver CreateMacCatalyst(Uri serverUri)
    {
        var options = new AppiumOptions
        {
            PlatformName = "Mac",
            AutomationName = "Mac2",
        };
        options.AddAdditionalAppiumOption("appium:bundleId", BundleId);
        AddIfSet(options, "appium:appPath", Environment.GetEnvironmentVariable("UITEST_APP_PATH"));
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
