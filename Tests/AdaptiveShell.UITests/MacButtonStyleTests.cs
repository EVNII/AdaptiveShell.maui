using NUnit.Framework;
using OpenQA.Selenium;
using OpenQA.Selenium.Appium;
using OpenQA.Selenium.Support.UI;

namespace AdaptiveShell.UITests;

/// <summary>同一 MainPage 按钮用于官方 Shell 与 AShell 的 Mac 外观对照。</summary>
[TestFixture]
[NonParallelizable]
public class MacButtonStyleTests : BaseTest
{
    [Test]
    public void Button_EnabledAndClickableAcrossLightDarkLight()
    {
        if (AppiumSetup.Platform != "maccatalyst")
        {
            Assert.Ignore("This probe compares Mac Catalyst button appearance.");
        }

        try
        {
            string? expectedTitle = null;
            foreach (var stage in new[]
            {
                (Label: "light", Dark: false, Sequence: 20),
                (Label: "dark", Dark: true, Sequence: 22),
                (Label: "light-restored", Dark: false, Sequence: 24),
            })
            {
                // Appearance 在 Mac 上通过 osascript 切换系统主题并等待传播。
                Appearance.SetDark(stage.Dark);
                WaitForCounter();
                CaptureStage(stage.Label, stage.Sequence);
                var button = WaitForCounter();
                Assert.That(button.Enabled, Is.True,
                    $"Counter button should remain enabled in {stage.Label} mode.");
                var previousTitle = button.Text;
                Assert.That(previousTitle, Is.Not.Empty,
                    "The button must expose its caption before clicking.");
                if (expectedTitle is not null)
                {
                    Assert.That(previousTitle, Is.EqualTo(expectedTitle),
                        "Changing system appearance should preserve the current page and counter state.");
                }

                // 截图或主题更新可能替换 AX 节点;每次尝试重新定位,
                // 只有真实 Click 成功才继续等待标题变化。
                var clickWait = new WebDriverWait(Driver, TimeSpan.FromSeconds(30));
                clickWait.IgnoreExceptionTypes(typeof(StaleElementReferenceException));
                clickWait.Until(driver =>
                {
                    var current = FindVisibleCounter(driver);
                    if (current is null)
                    {
                        return false;
                    }

                    Assert.That(current.Enabled, Is.True,
                        $"Counter button should be clickable in {stage.Label} mode.");
                    current.Click();
                    return true;
                });

                var updated = WaitForCounter(element => element.Text != previousTitle);
                expectedTitle = updated.Text;
                Assert.That(expectedTitle, Does.Match(@"^Clicked [1-9][0-9]* times?$"),
                    "A successful counter click must update the button caption.");
                Assert.That(updated.Enabled, Is.True,
                    "Updating the caption should retain the button's enabled state.");
                CaptureStage($"{stage.Label}-clicked", stage.Sequence + 1);
                TestContext.Out.WriteLine($"{stage.Label}: enabled=true, '{previousTitle}' -> '{expectedTitle}'");
            }
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
        }
    }

    IWebElement WaitForCounter(Func<IWebElement, bool>? condition = null)
    {
        var wait = new WebDriverWait(Driver, TimeSpan.FromSeconds(30));
        wait.IgnoreExceptionTypes(typeof(StaleElementReferenceException));
        return wait.Until(driver =>
        {
            var button = FindVisibleCounter(driver);
            return button is not null && (condition is null || condition(button)) ? button : null;
        })!;
    }

    static IWebElement? FindVisibleCounter(IWebDriver driver) =>
        driver.FindElements(MobileBy.AccessibilityId("counterBtn"))
            .FirstOrDefault(element => element.Displayed);

    void CaptureStage(string label, int sequence)
    {
        var results = Path.Combine(AppiumSetup.RepoRoot, "TestResults");
        Directory.CreateDirectory(results);
        File.WriteAllText(Path.Combine(results, $"button-{label}.xml"), Driver.PageSource);
        // 对照证据缺失应使 probe 失败,因此不使用会吞掉截图错误的 BaseTest.Shot。
        Shots.Save(Driver, $"button-{label}", sequence);
    }
}
