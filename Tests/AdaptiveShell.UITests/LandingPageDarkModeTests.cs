using NUnit.Framework;
using OpenQA.Selenium;
using OpenQA.Selenium.Appium;
using OpenQA.Selenium.Support.UI;
using System.Diagnostics;
using System.Text.Json;
using System.Xml.Linq;

namespace AdaptiveShell.UITests;

[TestFixture]
public class LandingPageDarkModeTests : BaseTest
{
    const string UnsupportedMessage = "Dedicated group landing pages exist only on Android compact and iOS compact/Duo; native sidebar, drawer, or direct-leaf navigation does not expose this landing page.";
    static readonly string[] Markers =
    {
        "landing-media-body", "landing-music", "landing-photos",
        "landing-music-icon", "landing-music-title",
        "landing-photos-icon", "landing-photos-title",
    };

    [Test]
    public void DarkMode_GroupLandingPage()
    {
        var supported = AppiumSetup.Platform == "android" && AppiumSetup.Form == "compact"
            || AppiumSetup.Platform == "ios" && AppiumSetup.Form is "compact" or "duo";
        if (!supported)
            Assert.Ignore(UnsupportedMessage);

        try
        {
            Appearance.SetDark(false);
            RestartApp();
            Driver.WaitForAccessibilityId("media").Click();
            WaitForLanding();
            CaptureLanding("light", 40);

            // Keep the same landing page alive while the system changes its theme.
            Appearance.SetDark(true);
            WaitForLanding();
            CaptureLanding("dark", 41);

            var navigation = new[] { VisitDarkChild("music", 43), VisitDarkChild("photos", 45) };
            File.WriteAllText(Path.Combine(Results, "landing-navigation.json"), JsonSerializer.Serialize(new
            {
                theme = "dark", children = navigation,
            }, JsonOptions));

            Appearance.SetDark(false);
            WaitForLanding();
            CaptureLanding("light-restored", 42);
            VerifyLandingPixels();
        }
        finally
        {
            try { Appearance.SetDark(false); }
            catch (Exception ex) { TestContext.Out.WriteLine($"Failed to restore light appearance: {ex.Message}"); }
            // The landing page has no counterBtn. Reset to Home before waiting for it.
            try { RestartApp(); }
            catch (Exception ex) { TestContext.Out.WriteLine($"Failed to restart after landing theme test: {ex.Message}"); }
        }
    }

    static string Results => Path.Combine(AppiumSetup.RepoRoot, "TestResults");
    static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    void RestartApp()
    {
        Driver.TerminateApp(AppiumSetup.BundleId);
        Driver.ActivateApp(AppiumSetup.BundleId);
        Driver.WaitForAccessibilityId("home");
    }

    void WaitForLanding()
    {
        foreach (var id in Markers)
        {
            var element = Driver.WaitForAccessibilityId(id, 30);
            Assert.That(element.Displayed, Is.True, $"Landing marker {id} must be visible.");
            Assert.That(element.Enabled, Is.True, $"Landing marker {id} must be enabled.");
            // A tappable layout can be a nonaccessible container. Its exact
            // visible native title supplies the readable child-entry label.
            if (id is "landing-music-title" or "landing-photos-title")
            {
                Assert.That(element.AccessibilityLabel() ?? element.Text, Is.Not.Null.And.Not.Empty,
                    "Each child entry must expose its actual readable native title.");
                Assert.That(element.Text, Is.EqualTo(id == "landing-music-title" ? "音乐" : "相册"),
                    "Landing titles must contain the actual child title, not just an AutomationId.");
            }
        }
        var visibleCounter = new[] { MobileBy.AccessibilityId("counterBtn"), MobileBy.Id("counterBtn") }
            .SelectMany(locator => Driver.FindElements(locator)).Any(element => element.Displayed);
        Assert.That(visibleCounter, Is.False, "The landing page must remain active, rather than its child page.");
    }

    AppiumElement FindChildBack()
    {
        var back = Driver.FindByAccessibilityIdOrDefault("Back", 5)
            ?? Driver.FindByAccessibilityIdOrDefault("媒体", 5);
        if (back is null && AppiumSetup.Platform == "ios")
            back = Driver.FindOrDefault(By.XPath("//XCUIElementTypeNavigationBar//XCUIElementTypeButton[1]"), 5);
        Assert.That(back, Is.Not.Null, "The dark child page must expose a real native Back affordance.");
        return back!;
    }

    object VisitDarkChild(string child, int sequence)
    {
        Driver.WaitForAccessibilityId($"landing-{child}").Click();
        var counter = Driver.WaitForAccessibilityId("counterBtn");
        var before = counter.Text;
        counter.Click();
        var changed = new WebDriverWait(Driver, TimeSpan.FromSeconds(30));
        changed.IgnoreExceptionTypes(typeof(StaleElementReferenceException));
        var after = changed.Until(driver =>
        {
            foreach (var locator in new[] { MobileBy.AccessibilityId("counterBtn"), MobileBy.Id("counterBtn") })
            {
                var element = driver.FindElements(locator).FirstOrDefault(e => e.Displayed && e.Enabled);
                if (element is not null && element.Text != before)
                    return element.Text;
            }
            return null;
        });
        Assert.That(after, Is.Not.Null.And.Not.Empty.And.Not.EqualTo(before),
            $"The {child} child counter must respond to an actual click in dark mode.");
        Shots.Save(Driver, $"landing-dark-{child}-child-clicked", sequence);
        SaveSource($"landing-dark-{child}-child.xml");
        FindChildBack().Click();
        WaitForLanding();
        Shots.Save(Driver, $"landing-dark-{child}-returned", sequence + 1);
        SaveSource($"landing-dark-{child}-returned.xml");
        return new { child, counter_before = before, counter_after = after, returned_to_landing = true };
    }

    void SaveSource(string filename) => XDocument.Parse(Driver.PageSource).Save(Path.Combine(Results, filename));

    void CaptureLanding(string stage, int sequence)
    {
        Directory.CreateDirectory(Results);
        var elements = CaptureElements();

        var size = Driver.Manage().Window.Size;
        SaveSource($"landing-{stage}.xml");
        if (AppiumSetup.Platform == "ios")
            CaptureIosScreens(stage, "");
        Shots.Save(Driver, $"landing-theme-{stage}", sequence);
        SaveSource($"landing-{stage}-after.xml");
        var elementsAfter = CaptureElements();
        var sizeAfter = Driver.Manage().Window.Size;
        if (AppiumSetup.Platform == "ios")
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            CaptureIosScreens(stage, "-after");
            File.WriteAllText(Path.Combine(Results, $"landing-{stage}-wda-status.json"),
                client.GetStringAsync("http://127.0.0.1:8100/status").GetAwaiter().GetResult());
            File.WriteAllText(Path.Combine(Results, $"landing-{stage}-active-app.json"),
                JsonSerializer.Serialize(Driver.ExecuteScript("mobile:activeAppInfo")));
        }
        File.WriteAllText(Path.Combine(Results, $"landing-{stage}.json"), JsonSerializer.Serialize(new
        {
            platform = AppiumSetup.Platform, form = AppiumSetup.Form, stage,
            device_udid = Environment.GetEnvironmentVariable("UITEST_DEVICE_UDID"),
            platform_version = (Driver.Capabilities.GetCapability("platformVersion")
                ?? Driver.Capabilities.GetCapability("appium:platformVersion"))?.ToString(),
            window_size = new { width = size.Width, height = size.Height },
            window_size_after = new { width = sizeAfter.Width, height = sizeAfter.Height },
            elements, elements_after = elementsAfter,
        }, JsonOptions));
    }

    object CaptureElements() => Markers.ToDictionary(id => id, id =>
        {
            var element = Driver.WaitForAccessibilityId(id, 30);
            var location = element.Location;
            var size = element.Size;
            return new
            {
                x = location.X, y = location.Y, width = size.Width, height = size.Height,
                displayed = element.Displayed, enabled = element.Enabled,
                label = element.AccessibilityLabel(), text = element.Text,
            };
        });

    void CaptureIosScreens(string stage, string suffix)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        File.WriteAllText(Path.Combine(Results, $"landing-{stage}-screens{suffix}.json"),
            client.GetStringAsync("http://127.0.0.1:8100/wda/screens").GetAwaiter().GetResult());
    }

    void VerifyLandingPixels()
    {
        var evidence = Path.Combine(Results, "landing-theme-checks.json");
        var startInfo = new ProcessStartInfo("python3")
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add(Path.Combine(AppiumSetup.RepoRoot, "Tests", "AdaptiveShell.UITests",
            "Scripts", "verify_landing_theme.py"));
        startInfo.ArgumentList.Add(Results);
        startInfo.ArgumentList.Add("--strict");
        var elapsed = Stopwatch.StartNew();
        using var process = new Process { StartInfo = startInfo };
        var started = false;
        try
        {
            started = process.Start();
            if (!started)
                throw new InvalidOperationException("Could not start the landing pixel verifier.");
            using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(
                Math.Max(1, 58000 - elapsed.ElapsedMilliseconds)));
            var stdout = process.StandardOutput.ReadToEndAsync(deadline.Token);
            var stderr = process.StandardError.ReadToEndAsync(deadline.Token);
            Task.WhenAll(process.WaitForExitAsync(deadline.Token), stdout, stderr).GetAwaiter().GetResult();
            var output = stdout.GetAwaiter().GetResult();
            var error = stderr.GetAwaiter().GetResult();
            File.WriteAllText(evidence, output);
            TestContext.Out.WriteLine(output);
            if (!string.IsNullOrWhiteSpace(error))
                TestContext.Out.WriteLine(error);
            Assert.That(process.ExitCode, Is.EqualTo(0),
                $"Landing background, both icons and both labels must be visible in all three themes. {error}");
        }
        catch (Exception ex) when (ex is not AssertionException)
        {
            File.WriteAllText(evidence, JsonSerializer.Serialize(new { verification_error = ex.Message }, JsonOptions));
            Assert.Fail($"Landing pixel verification did not complete within its 60-second budget: {ex.Message}");
        }
        finally
        {
            if (started && !process.HasExited)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit((int)Math.Max(0, 60000 - elapsed.ElapsedMilliseconds));
                }
                catch (InvalidOperationException) { /* The owned child already exited. */ }
            }
        }
    }
}
