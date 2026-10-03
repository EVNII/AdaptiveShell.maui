using NUnit.Framework;
using OpenQA.Selenium;
using OpenQA.Selenium.Appium;
using OpenQA.Selenium.Support.UI;
using System.Text.Json;
using System.Text;
using System.Xml.Linq;
using System.Globalization;
using System.Diagnostics;

namespace AdaptiveShell.UITests;

[TestFixture]
public class DarkModeTests : BaseTest
{
    [Test]
    public void DarkMode_ShellAndGroupFlow()
    {
        try
        {
            if (AppiumSetup.Platform == "windows")
            {
                // 用非零位置验证窗口截图坐标,避免原点恰为 (0,0) 时掩盖换算错误。
                Driver.Manage().Window.Position = new System.Drawing.Point(100, 80);
            }
            Appearance.SetDark(false);
            var isIosWide = AppiumSetup.Platform == "ios" && AppiumSetup.Form == "wide";
            if (isIosWide)
            {
                // 从展开侧栏的初始页面开始,避免此前导航留下收起的系统侧栏。
                Driver.TerminateApp(AppiumSetup.BundleId);
                try
                {
                    Driver.ActivateApp(AppiumSetup.BundleId);
                }
                catch (WebDriverException ex) when (ex.Message.Contains(
                    "Timed out attempting to launch app", StringComparison.Ordinal))
                {
                    TestContext.Out.WriteLine("Retrying initial iPad app launch once after an XCTest launch timeout.");
                    Driver.TerminateApp(AppiumSetup.BundleId);
                    Driver.ActivateApp(AppiumSetup.BundleId);
                }
            }

            Driver.WaitForAccessibilityId("home");
            Driver.WaitForAccessibilityId("home2");
            Driver.WaitForAccessibilityId("media");

            if (!isIosWide)
            {
                Driver.WaitForAccessibilityId("home2").Click();
                WaitForA11ySettled();
                Shot("light-home2-selected");
            }

            // Apple sidebar 形态下子项可能已展开,不再点组将其收起。
            var childEntry = Driver.FindByAccessibilityIdOrDefault("music", 3)
                ?? Driver.FindByAccessibilityIdOrDefault("landing-music", 3);
            if (childEntry is null)
            {
                Driver.WaitForAccessibilityId("media").Click();
                childEntry = Driver.FindByAccessibilityIdOrDefault("landing-music", 8)
                    ?? Driver.FindByAccessibilityIdOrDefault("music", 4);
            }
            Shot("light-group-opened");
            if (AppiumSetup.Platform == "windows" && childEntry is null)
            {
                // Windows 点组直接选中首个子页。
                WaitForA11ySettled();
            }
            else
            {
                Assert.That(childEntry, Is.Not.Null,
                    "Expected the Music group child entry before selecting its page.");
                // 截图/抽屉动画期间原生节点可能被替换;每次重试都重新定位,
                // 只有真实可见条目的 Click 成功后才继续后面的页面状态断言。
                var selectChildWait = new WebDriverWait(Driver, TimeSpan.FromSeconds(30));
                selectChildWait.IgnoreExceptionTypes(typeof(StaleElementReferenceException));
                selectChildWait.Until(driver =>
                {
                    foreach (var locator in new[]
                    {
                        MobileBy.AccessibilityId("music"), MobileBy.Id("music"),
                        MobileBy.AccessibilityId("landing-music"), MobileBy.Id("landing-music"),
                    })
                    {
                        var entry = driver.FindElements(locator)
                            .FirstOrDefault(element => element.Displayed && element.Enabled);
                        if (entry is not null)
                        {
                            entry.Click();
                            return true;
                        }
                    }

                    return false;
                });
            }

            // 在子页留下状态,主题切换期间不重新导航或重启 App。
            var counter = Driver.WaitForAccessibilityId("counterBtn");
            var previousText = counter.Text;
            counter.Click();
            var wait = new WebDriverWait(Driver, TimeSpan.FromSeconds(30));
            wait.IgnoreExceptionTypes(typeof(StaleElementReferenceException), typeof(WebDriverTimeoutException));
            var counterText = wait.Until(_ =>
            {
                var text = Driver.WaitForAccessibilityId("counterBtn", 5).Text;
                return text != previousText ? text : null;
            })!;
            CaptureThemeStage("light", 30, "light-group-child");

            Appearance.SetDark(true);
            AssertGroupChildState(counterText);
            Driver.WaitForAccessibilityId("home");
            Driver.WaitForAccessibilityId("home2");
            Driver.WaitForAccessibilityId("media");
            CaptureThemeStage("dark", 31, "dark-group-child");

            Appearance.SetDark(false);
            AssertGroupChildState(counterText);
            CaptureThemeStage("light-restored", 32, "light-group-child-restored");

            if (AppiumSetup.Platform == "android")
            {
                VerifyAndroidSystemBars();
            }

            if (isIosWide)
            {
                // iPad 选择 Home2 会自动收起侧栏,因此在 Music 主题状态验证之后执行。
                Driver.WaitForAccessibilityId("home2").Click();
                WaitForA11ySettled();
                Shot("light-home2-selected");
            }
        }
        finally
        {
            try
            {
                Appearance.SetDark(false);
                // 同样吸收恢复浅色后的陈旧窗口期,避免影响后续测试
                WaitForA11ySettled();
            }
            catch (Exception ex)
            {
                TestContext.Out.WriteLine($"Failed to restore light mode: {ex.Message}");
            }
        }
    }

    [Test]
    public void DarkMode_WideDrawerAndExpandedRailStayOpen()
    {
        if (AppiumSetup.Platform != "android" || AppiumSetup.Form != "wide")
        {
            Assert.Ignore("Expanded rail and group drawer are Android wide-form behavior.");
        }

        try
        {
            Appearance.SetDark(false);
            Driver.WaitForAccessibilityId("home");

            var toggle = Driver.FindByAccessibilityIdOrDefault("Open navigation menu", 3)
                ?? Driver.WaitForAccessibilityId("Expand navigation rail");
            toggle.Click();
            Driver.WaitForAccessibilityId("Collapse navigation rail");
            Driver.WaitForAccessibilityId("media").Click();
            AssertWideDrawerState();
            Shot("light-expanded-rail-drawer");

            Appearance.SetDark(true);
            AssertWideDrawerState();
            Shot("dark-expanded-rail-drawer");

            Appearance.SetDark(false);
            AssertWideDrawerState();
            Shot("light-expanded-rail-drawer-restored");
        }
        finally
        {
            try
            {
                Appearance.SetDark(false);
            }
            catch (Exception ex)
            {
                TestContext.Out.WriteLine($"Failed to restore light mode: {ex.Message}");
            }

            try
            {
                if (Driver.FindByAccessibilityIdOrDefault("music", 3) is not null)
                {
                    Driver.Navigate().Back();
                }
                Driver.FindByAccessibilityIdOrDefault("Collapse navigation rail", 3)?.Click();
            }
            catch (Exception ex)
            {
                TestContext.Out.WriteLine($"Failed to close drawer and collapse rail: {ex.Message}");
            }
        }
    }

    void AssertWideDrawerState()
    {
        Driver.WaitForAccessibilityId("music");
        Driver.WaitForAccessibilityId("Collapse navigation rail");
    }

    OpenQA.Selenium.Appium.Android.Interfaces.IHasSettings AndroidWindowSettings =>
        Driver as OpenQA.Selenium.Appium.Android.Interfaces.IHasSettings
        ?? throw new InvalidOperationException("Android driver does not expose its native settings API.");

    bool ReadMultiWindowSetting()
    {
        using var settings = JsonDocument.Parse(JsonSerializer.Serialize(AndroidWindowSettings.Settings));
        if (!settings.RootElement.TryGetProperty("enableMultiWindows", out var value)
            || value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            Assert.Fail("The driver did not return an actual boolean enableMultiWindows setting.");
        }
        return value.GetBoolean();
    }

    void CaptureThemeStage(string stage, int sequence, string ordinaryLabel)
    {
        if (AppiumSetup.Platform == "android")
        {
            var androidResults = Path.Combine(AppiumSetup.RepoRoot, "TestResults");
            Directory.CreateDirectory(androidResults);
            // mobile:getSystemBars returns native dumpsys window frames, not the
            // older /system_bars response containing only a status-bar height.
            var enabledBefore = ReadMultiWindowSetting();
            bool enabledDuringBefore = false, enabledDuringAfter = false, enabledRestored = false;
            object systemBarsBefore = null!, systemBarsAfter = null!, deviceInfo = null!;
            System.Drawing.Size screenSize = default;
            var sourceBefore = $"android-system-bars-{stage}-windows-before.xml";
            var sourceAfter = $"android-system-bars-{stage}-windows-after.xml";
            try
            {
                AndroidWindowSettings.SetSetting("enableMultiWindows", true);
                enabledDuringBefore = ReadMultiWindowSetting();
                Assert.That(enabledDuringBefore, Is.True, "Native system navigation capture requires all windows.");
                systemBarsBefore = Driver.ExecuteScript("mobile: getSystemBars");
                deviceInfo = Driver.ExecuteScript("mobile: deviceInfo");
                screenSize = Driver.Manage().Window.Size;
                File.WriteAllText(Path.Combine(androidResults, sourceBefore), Driver.PageSource);
                Shot(ordinaryLabel);
                // Required capture: an ordinary Shot can swallow screenshot failures.
                Shots.Save(Driver, $"theme-{stage}", sequence);
                AndroidNavigationAppearanceDiagnostics.Capture(Driver, stage, deviceInfo);
                File.WriteAllText(Path.Combine(androidResults, sourceAfter), Driver.PageSource);
                systemBarsAfter = Driver.ExecuteScript("mobile: getSystemBars");
                enabledDuringAfter = ReadMultiWindowSetting();
                Assert.That(enabledDuringAfter, Is.True, "Native window setting changed during capture.");
            }
            finally
            {
                // Restore the exact captured setting before any subsequent app locator/action.
                AndroidWindowSettings.SetSetting("enableMultiWindows", enabledBefore);
                enabledRestored = ReadMultiWindowSetting();
                Assert.That(enabledRestored, Is.EqualTo(enabledBefore), "Native window setting was not restored.");
            }
            var androidEvidence = new
            {
                schemaVersion = 1,
                stage,
                png = $"shots/android-{AppiumSetup.Form ?? "default"}/{sequence:00}-theme-{stage}.png",
                coordinateSource = "mobile:getSystemBars",
                systemBarsBefore,
                systemBarsAfter,
                deviceInfo,
                screenSize = new { width = screenSize.Width, height = screenSize.Height },
                multiWindowCapture = new
                {
                    enabledBefore, enabledDuringBefore, enabledDuringAfter, enabledRestored,
                    appPackage = AppiumSetup.BundleId,
                    sourceBefore, sourceAfter,
                },
            };
            File.WriteAllText(Path.Combine(androidResults, $"android-system-bars-{stage}.json"),
                JsonSerializer.Serialize(androidEvidence, new JsonSerializerOptions { WriteIndented = true }));
            return;
        }
        if (AppiumSetup.Platform == "ios" && AppiumSetup.Form == "duo")
        {
            var duoResults = Path.Combine(AppiumSetup.RepoRoot, "TestResults");
            Directory.CreateDirectory(duoResults);
            XDocument.Parse(Driver.PageSource).Save(Path.Combine(duoResults, $"duo-{stage}.xml"));
            Shots.Save(Driver, $"theme-{stage}", sequence);
            return;
        }
        if (AppiumSetup.Platform != "windows")
        {
            Shot(ordinaryLabel);
            return;
        }

        var results = Path.Combine(AppiumSetup.RepoRoot, "TestResults");
        Directory.CreateDirectory(results);
        var window = Driver.FindElement(By.XPath("/*"));
        var source = Driver.PageSource;
        var sourceWindow = XDocument.Parse(source).Root
            ?? throw new InvalidOperationException("Windows page source has no root.");
        Assert.That(sourceWindow.Name.LocalName, Is.EqualTo("Window"),
            "The Windows app-session source must have a top-level Window.");
        var sessionWindow = Driver.Manage().Window;
        var sessionLocation = sessionWindow.Position;
        var sessionSize = sessionWindow.Size;
        var evidence = new
        {
            stage,
            png = $"shots/windows-{AppiumSetup.Form ?? "default"}/{sequence:00}-theme-{stage}.png",
            coordinateSource = "windows-page-source",
            // 位置 API 与 source 的窗口边界可能不同;使用完整原生 source
            // 矩形,并由验证器核对同一 XML 内所有元素与 API bounds。
            window = SourceBounds(sourceWindow),
            nativeWindow = Bounds(window),
            sessionWindow = new { x = sessionLocation.X, y = sessionLocation.Y,
                width = sessionSize.Width, height = sessionSize.Height },
            elements = new
            {
                home = Bounds(Driver.WaitForAccessibilityId("home")),
                home2 = Bounds(Driver.WaitForAccessibilityId("home2")),
                media = Bounds(Driver.WaitForAccessibilityId("media")),
                counterBtn = Bounds(Driver.WaitForAccessibilityId("counterBtn")),
            },
        };
        // WinAppDriver 的 XML 声明为 UTF-16;保存时保留相同的实际编码。
        File.WriteAllText(Path.Combine(results, $"windows-{stage}.xml"), source, Encoding.Unicode);
        // 缺失截图或原生坐标必须失败;不用会吞掉截图错误的普通 Shot。
        Shots.Save(Driver, $"theme-{stage}", sequence);
        File.WriteAllText(Path.Combine(results, $"windows-{stage}.json"),
            JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }));
    }

    static object Bounds(IWebElement element)
    {
        var location = element.Location;
        var size = element.Size;
        return new { x = location.X, y = location.Y, width = size.Width, height = size.Height };
    }

    static object SourceBounds(XElement window)
    {
        double Value(string name) => double.Parse(window.Attribute(name)?.Value
            ?? throw new InvalidOperationException($"Window source is missing {name}."), CultureInfo.InvariantCulture);
        return new { x = Value("x"), y = Value("y"), width = Value("width"), height = Value("height") };
    }

    void VerifyAndroidSystemBars()
    {
        var results = Path.Combine(AppiumSetup.RepoRoot, "TestResults");
        var evidencePath = Path.Combine(results, "android-system-bars-checks.json");
        var script = Path.Combine(AppiumSetup.RepoRoot, "Tests", "AdaptiveShell.UITests",
            "Scripts", "verify_android_system_bars.py");
        var startInfo = new ProcessStartInfo("python3")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add(script);
        startInfo.ArgumentList.Add(results);
        startInfo.ArgumentList.Add("--strict");
        var deadline = Stopwatch.StartNew();
        using var process = new Process { StartInfo = startInfo };
        var started = false;
        try
        {
            started = process.Start();
            if (!started)
                throw new InvalidOperationException("Could not start the Android system-bar verifier.");
            // Reserve two seconds of the total 60-second budget for owned-child cleanup.
            using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(
                Math.Max(1, 58000 - deadline.ElapsedMilliseconds)));
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            Task.WhenAll(process.WaitForExitAsync(timeout.Token), stdout, stderr)
                .GetAwaiter().GetResult();
            var output = stdout.GetAwaiter().GetResult();
            var error = stderr.GetAwaiter().GetResult();
            File.WriteAllText(evidencePath, output);
            TestContext.Out.WriteLine(output);
            if (!string.IsNullOrWhiteSpace(error))
                TestContext.Out.WriteLine(error);
            Assert.That(process.ExitCode, Is.EqualTo(0),
                $"Android status-bar foreground and each native system-navigation key must remain visible in all three themes. {error}");
        }
        catch (Exception ex) when (ex is not AssertionException)
        {
            File.WriteAllText(evidencePath, JsonSerializer.Serialize(new
            {
                verificationError = ex is OperationCanceledException
                    ? "Android system-bar verification exceeded the 60-second total deadline."
                    : $"Android system-bar verification failed (python3 is required): {ex.Message}",
            }, new JsonSerializerOptions { WriteIndented = true }));
            Assert.Fail($"Android system-bar verifier did not complete: {ex.Message}");
        }
        finally
        {
            if (started && !process.HasExited)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit((int)Math.Max(0, 60000 - deadline.ElapsedMilliseconds));
                }
                catch (InvalidOperationException) { /* The owned child already exited. */ }
            }
        }
    }

    // 当前页面(无论 home 还是组子页)都有 counterBtn,适合当探针
    void WaitForA11ySettled() => Driver.WaitForAccessibilityId("counterBtn", 30);

    void AssertGroupChildState(string counterText)
    {
        WaitForA11ySettled();
        Assert.That(Driver.WaitForAccessibilityId("counterBtn").Text, Is.EqualTo(counterText),
            "Theme changes should preserve the active group child page and its counter state.");
        if (AppiumSetup.Platform == "android" && AppiumSetup.Form == "compact")
        {
            Driver.WaitForAccessibilityId("Back");
        }
    }

    [TearDown]
    public void RestartAppAfterThemeChurn()
    {
        // 主题切换会让 uiautomator 的 resource-id 查找进入长时间不可靠状态
        // (counterBtn/landing-* 这类 MAUI AutomationId 节点迟迟查不到),
        // 重启 App 让后续 fixture 拿到干净的初始状态
        try
        {
            if (AppiumSetup.Platform is "android" or "ios")
            {
                Driver.TerminateApp(AppiumSetup.BundleId);
                Driver.ActivateApp(AppiumSetup.BundleId);
                Driver.WaitForAccessibilityId("home");
            }
        }
        catch (Exception ex)
        {
            TestContext.Out.WriteLine($"Failed to restart app: {ex.Message}");
        }
    }
}
