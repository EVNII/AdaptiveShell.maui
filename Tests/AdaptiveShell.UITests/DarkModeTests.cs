using NUnit.Framework;
using OpenQA.Selenium;
using OpenQA.Selenium.Appium;
using OpenQA.Selenium.Support.UI;

namespace AdaptiveShell.UITests;

[TestFixture]
public class DarkModeTests : BaseTest
{
    [Test]
    public void DarkMode_ShellAndGroupFlow()
    {
        try
        {
            Appearance.SetDark(false);

            Driver.WaitForAccessibilityId("home");
            Driver.WaitForAccessibilityId("home2");
            Driver.WaitForAccessibilityId("media");

            Driver.WaitForAccessibilityId("home2").Click();
            WaitForA11ySettled();
            Shot("light-home2-selected");

            if (AppiumSetup.Platform == "ios" && AppiumSetup.Form == "wide")
            {
                // iPad 选择页面后系统可能自动收起 sidebar,此时 music 不在可见树中。
                // 只点 Show Sidebar 动作,避免把已显示的侧栏反向收起。
                var showSidebar = Driver.FindOrDefault(By.XPath(
                    "//XCUIElementTypeButton[@label='Show Sidebar']"), 5);
                if (showSidebar is not null)
                {
                    showSidebar.Click();
                    Driver.WaitForAccessibilityId("music");
                }
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
            Shot("light-group-child");

            Appearance.SetDark(true);
            AssertGroupChildState(counterText);
            Driver.WaitForAccessibilityId("home");
            Driver.WaitForAccessibilityId("home2");
            Driver.WaitForAccessibilityId("media");
            Shot("dark-group-child");

            Appearance.SetDark(false);
            AssertGroupChildState(counterText);
            Shot("light-group-child-restored");
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
