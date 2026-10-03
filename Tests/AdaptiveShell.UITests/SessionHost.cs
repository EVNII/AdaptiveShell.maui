using NUnit.Framework;
using OpenQA.Selenium;
using OpenQA.Selenium.Appium;
using System.Xml.Linq;
using System.Text.Json;

namespace AdaptiveShell.UITests;

/// <summary>
/// 整个测试程序集共享一个 Appium 会话。
/// iOS 上每个会话都要重启 WebDriverAgent,反复建会话既慢又容易在
/// teardown/重启竞态里挂掉("Reset: failed to terminate" + ECONNREFUSED)。
/// </summary>
[SetUpFixture]
public class SessionHost
{
    public static AppiumDriver Driver = null!;

    [OneTimeSetUp]
    public void CreateSession()
    {
        WaitForDiagnosticDuoBoot();
        Driver = AppiumSetup.CreateDriver();

        // 等壳的首个导航项出现,说明首屏已渲染。
        // 资源紧张的模拟器上系统弹窗(如启动器 ANR "isn't responding")会挡住
        // 无障碍树,等待过程里由 WaitFor 轮询顺带点掉(见 ElementExtensions)
        for (int attempt = 0; attempt < 6; attempt++)
        {
            ElementExtensions.DismissAnrDialogs(Driver);
            try
            {
                Driver.WaitForAccessibilityId("home", 30);
                AppiumSetup.VerifyDuoCompatibilitySettings();
                Shots.Save(Driver, "launch", 1);
                if (AppiumSetup.Platform == "ios" && AppiumSetup.Form == "duo")
                {
                    var results = Path.Combine(AppiumSetup.RepoRoot, "TestResults");
                    XDocument.Parse(Driver.PageSource).Save(Path.Combine(results, "duo-launch.xml"));
                    File.WriteAllText(Path.Combine(results, "duo-active-app.json"),
                        JsonSerializer.Serialize(Driver.ExecuteScript("mobile:activeAppInfo")));
                    using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
                    File.WriteAllText(Path.Combine(results, "duo-screens.json"),
                        client.GetStringAsync("http://127.0.0.1:8100/wda/screens").GetAwaiter().GetResult());
                }
                return;
            }
            catch (WebDriverTimeoutException)
            {
            }
        }

        Driver.WaitForAccessibilityId("home");
        AppiumSetup.VerifyDuoCompatibilitySettings();
        Shots.Save(Driver, "launch", 1);
    }

    // Diagnostic opt-in only: wait for exact native setup before CreateDriver.
    // Native-first is a separate explicit order; the default host-first remains.
    static void WaitForDiagnosticDuoBoot()
    {
        var enabled = Environment.GetEnvironmentVariable("DUO_DIAGNOSTIC_PREBOOT_HOST");
        if (enabled is null)
        {
            if (Environment.GetEnvironmentVariable("DUO_DIAGNOSTIC_NATIVE_FIRST_BOOT") is not null)
                throw new InvalidOperationException("Native-first boot requires the original TestHost barrier opt-in.");
            return;
        }
        if (enabled != "true" || AppiumSetup.Platform != "ios" || AppiumSetup.Form != "duo"
            || Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true"
            || Environment.GetEnvironmentVariable("GITHUB_JOB") != "uitest-ios-27-1-duo"
            || Environment.GetEnvironmentVariable("GITHUB_REPOSITORY") != "EVNII/AdaptiveShell.maui"
            || Environment.GetEnvironmentVariable("GITHUB_REF_NAME") != "codex/duo-native-first-boot"
            || Environment.GetEnvironmentVariable("GITHUB_WORKFLOW") != "Duo Native First Boot E2E")
            throw new InvalidOperationException("The preboot TestHost barrier requires explicit Duo CI opt-in.");

        static string Required(string key) => Environment.GetEnvironmentVariable(key)
            is { Length: > 0 } value ? value
            : throw new InvalidOperationException($"Missing preboot barrier identity: {key}");
        static string HashFile(string path) => Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
        static void Match(JsonElement root, string key, string expected)
        {
            if (!root.TryGetProperty(key, out var item) || item.ValueKind != JsonValueKind.String
                || item.GetString() != expected)
                throw new InvalidOperationException($"Preboot barrier {key} mismatch.");
        }

        var nonce = Required("DUO_DIAGNOSTIC_BARRIER_NONCE");
        var runId = Required("GITHUB_RUN_ID");
        var attempt = Required("GITHUB_RUN_ATTEMPT");
        var head = Required("GITHUB_SHA");
        var udid = Required("UITEST_DEVICE_UDID");
        if (nonce.Length != 64 || nonce.Any(c => !Uri.IsHexDigit(c))
            || head.Length != 40 || head.Any(c => !Uri.IsHexDigit(c))
            || !ulong.TryParse(runId, out var parsedRun) || parsedRun == 0
            || !uint.TryParse(attempt, out var parsedAttempt) || parsedAttempt == 0
            || !Guid.TryParseExact(udid, "D", out _)
            || udid != Required("DUO_DEVICE_UDID"))
            throw new InvalidOperationException("Invalid preboot barrier identity.");
        var assembly = typeof(SessionHost).Assembly.Location;
        var assemblySha = HashFile(assembly);
        if (assemblySha != Required("DUO_DIAGNOSTIC_ASSEMBLY_SHA256"))
            throw new InvalidOperationException("Actual TestHost assembly differs from the verified handoff.");
        var pid = Environment.ProcessId;
        var results = Path.Combine(AppiumSetup.RepoRoot, "TestResults");
        Directory.CreateDirectory(results);
        var arrived = Path.Combine(results, "duo-testhost-arrived.json");
        var release = Path.Combine(results, "duo-testhost-release.json");
        var proof = Path.Combine(results, "duo-testhost-boot-proof.json");
        var nativeFirstOption = Environment.GetEnvironmentVariable("DUO_DIAGNOSTIC_NATIVE_FIRST_BOOT");
        var nativeFirst = nativeFirstOption is not null;
        if (nativeFirst && (nativeFirstOption != "true"
            || Environment.GetEnvironmentVariable("DUO_DIAGNOSTIC_PREINSTALL_AUT") != "true"
            || Environment.GetEnvironmentVariable("DUO_DIAGNOSTIC_DEFER_APPIUM_UNTIL_INSTALLED") != "true"))
            throw new InvalidOperationException("Native-first boot requires explicit isolated setup opt-ins.");
        if (File.Exists(arrived) || File.Exists(release) || (!nativeFirst && File.Exists(proof)))
            throw new InvalidOperationException("Preboot TestHost barrier files already exist.");
        var identity = new Dictionary<string, object>
        {
            ["status"] = "before-create-driver",
            ["run_id"] = runId, ["run_attempt"] = attempt, ["head_sha"] = head,
            ["device_udid"] = udid, ["nonce"] = nonce, ["testhost_pid"] = pid,
            ["assembly_path"] = assembly, ["assembly_sha256"] = assemblySha,
            ["process_args"] = Environment.GetCommandLineArgs(),
        };
        void ValidateNativeFirstBoot(JsonElement boot)
        {
            Match(boot, "startup_order", "native-boot-before-testhost");
            if (!boot.TryGetProperty("testhost_spawned", out var spawned) || spawned.ValueKind != JsonValueKind.False
                || boot.TryGetProperty("testhost_pid", out _)
                || !boot.TryGetProperty("identity", out var nativeIdentity) || nativeIdentity.ValueKind != JsonValueKind.Object
                || nativeIdentity.EnumerateObject().Count() != 7 || nativeIdentity.TryGetProperty("testhost_pid", out _))
                throw new InvalidOperationException("Native-first proof must not claim a preexisting TestHost PID.");
            foreach (var key in new[] { "run_id", "run_attempt", "head_sha", "device_udid", "nonce", "assembly_path", "assembly_sha256" })
                Match(nativeIdentity, key, (string)identity[key]);
            Match(boot, "status", "verified");
            Match(boot, "device_udid", udid);
            Match(boot, "model", "iPhone19,4");
            Match(boot, "runtime_identifier", "com.apple.CoreSimulator.SimRuntime.iOS-27-1");
            Match(boot, "runtime_version", "27.1");
            Match(boot, "runtime_build", "24A94401");
            Match(boot, "device_type_identifier", "com.apple.CoreSimulator.SimDeviceType.iPhone-Duo");
            Match(boot, "sdk_version", "27.1");
            if (!boot.TryGetProperty("bootstatus_exit_code", out var exit) || !exit.TryGetInt32(out var code) || code != 0
                || !boot.TryGetProperty("dual_displays_verified", out var dual) || dual.ValueKind != JsonValueKind.True
                || !boot.TryGetProperty("boot_started_utc", out var start) || !start.TryGetDateTimeOffset(out var started)
                || !boot.TryGetProperty("boot_finished_utc", out var finish) || !finish.TryGetDateTimeOffset(out var finished)
                || started > finished || finished > DateTimeOffset.UtcNow)
                throw new InvalidOperationException("Native-first boot proof is incomplete or has invalid stage times.");
        }
        string? nativeBootSha = null;
        if (nativeFirst)
        {
            nativeBootSha = Required("DUO_DIAGNOSTIC_NATIVE_BOOT_PROOF_SHA256");
            if (nativeBootSha.Length != 64 || nativeBootSha.Any(c => !"0123456789abcdef".Contains(c))
                || !File.Exists(proof) || HashFile(proof) != nativeBootSha)
                throw new InvalidOperationException("Actual TestHost lacks its immutable native-first boot proof.");
            using var nativeDocument = JsonDocument.Parse(File.ReadAllBytes(proof));
            ValidateNativeFirstBoot(nativeDocument.RootElement);
            identity["startup_order"] = "native-boot-before-testhost";
            identity["preexisting_boot_proof_sha256"] = nativeBootSha;
        }
        var pending = arrived + ".tmp-" + pid;
        File.WriteAllText(pending, JsonSerializer.Serialize(identity));
        File.Move(pending, arrived, false);
        TestContext.Progress.WriteLine($"Duo TestHost reached OneTimeSetUp before CreateDriver (PID {pid}).");
        var wait = System.Diagnostics.Stopwatch.StartNew();
        while (!File.Exists(release))
        {
            if (wait.Elapsed >= TimeSpan.FromSeconds(1200))
                throw new TimeoutException("Duo boot release never arrived; no Appium session was created.");
            Thread.Sleep(100);
        }
        using var releaseDocument = JsonDocument.Parse(File.ReadAllText(release));
        var released = releaseDocument.RootElement;
        Match(released, "status", "boot-verified");
        foreach (var key in new[] { "run_id", "run_attempt", "head_sha", "device_udid", "nonce", "assembly_sha256" })
            Match(released, key, (string)identity[key]);
        if (!released.TryGetProperty("testhost_pid", out var releasedPid)
            || releasedPid.ValueKind != JsonValueKind.Number || !releasedPid.TryGetInt32(out var actualPid)
            || actualPid != pid || !File.Exists(proof))
            throw new InvalidOperationException("Boot release belongs to another TestHost or lacks native proof.");
        Match(released, "boot_proof_sha256", HashFile(proof));
        using var proofDocument = JsonDocument.Parse(File.ReadAllText(proof));
        var boot = proofDocument.RootElement;
        if (nativeFirst)
        {
            Match(released, "startup_order", "native-boot-before-testhost");
            if (HashFile(proof) != nativeBootSha)
                throw new InvalidOperationException("Native-first boot proof changed after actual TestHost arrival.");
            ValidateNativeFirstBoot(boot);
        }
        Match(boot, "status", "verified");
        Match(boot, "device_udid", udid);
        Match(boot, "model", "iPhone19,4");
        Match(boot, "runtime_identifier", "com.apple.CoreSimulator.SimRuntime.iOS-27-1");
        Match(boot, "runtime_version", "27.1");
        Match(boot, "runtime_build", "24A94401");
        Match(boot, "device_type_identifier", "com.apple.CoreSimulator.SimDeviceType.iPhone-Duo");
        if (!boot.TryGetProperty("bootstatus_exit_code", out var exitCode) || exitCode.GetInt32() != 0
            || !boot.TryGetProperty("dual_displays_verified", out var displays) || displays.ValueKind != JsonValueKind.True)
            throw new InvalidOperationException("Native bootstatus or dual displays were not verified.");
        TestContext.Progress.WriteLine("Exact Duo boot proof accepted; creating the original Appium session.");
    }

    [OneTimeTearDown]
    public void CloseSession()
    {
        if (Environment.GetEnvironmentVariable("UITEST_DUO_POST_SUITE_SNAPSHOT") is null)
        {
            Driver?.Quit();
            Driver = null!;
            return;
        }
        try { DuoPostSuiteSnapshotComparison.Capture(Driver); }
        catch (Exception error)
        {
            // A side collector failure must not replace any original NUnit outcome.
            try { TestContext.Progress.WriteLine("Post-suite snapshot side evidence failed: " + error.Message); }
            catch { }
        }
        finally
        {
            Driver?.Quit();
            Driver = null!;
        }
    }
}
