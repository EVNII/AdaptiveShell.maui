using NUnit.Framework;
using OpenQA.Selenium;
using OpenQA.Selenium.Appium;
using OpenQA.Selenium.Support.UI;

namespace AdaptiveShell.UITests;

[TestFixture]
public class NavigationTests : BaseTest
{
    static readonly string[] PageIds = ["page-home", "page-home2", "page-music", "page-photos"];

    void WaitForOnlyVisiblePage(string targetId)
    {
        Driver.WaitForAccessibilityId(targetId);
        var wait = new WebDriverWait(Driver, TimeSpan.FromSeconds(15));
        wait.IgnoreExceptionTypes(typeof(StaleElementReferenceException));
        wait.Until(driver => PageIds.All(id => IsVisible(driver, id) == (id == targetId)));
    }

    static bool IsVisible(IWebDriver driver, string id) =>
        driver.FindElements(MobileBy.AccessibilityId(id)).Any(element => element.Displayed)
        || driver.FindElements(MobileBy.Id(id)).Any(element => element.Displayed);

    void WaitForLanding()
    {
        Driver.WaitForAccessibilityId("landing-music");
        var wait = new WebDriverWait(Driver, TimeSpan.FromSeconds(15));
        wait.IgnoreExceptionTypes(typeof(StaleElementReferenceException));
        wait.Until(driver => PageIds.All(id => !IsVisible(driver, id)));
    }

    AppiumElement? FindGroupBackAffordance()
    {
        var back = Driver.FindByAccessibilityIdOrDefault("Back", 5)
            ?? Driver.FindByAccessibilityIdOrDefault("媒体", 5)
            ?? (AppiumSetup.Platform == "ios"
                ? Driver.FindByAccessibilityIdOrDefault("BackButton", 5)
                : null);
        if (back is null && AppiumSetup.Platform is "ios" or "maccatalyst")
        {
            // Older navigation bars may expose Back without a stable identifier.
            back = Driver.FindOrDefault(
                By.XPath("//XCUIElementTypeNavigationBar//XCUIElementTypeButton[1]"), 5);
        }
        return back;
    }

    [Test]
    public void Launch_ShowsAllTopLevelItems()
    {
        Driver.WaitForAccessibilityId("home");
        Driver.WaitForAccessibilityId("home2");
        Driver.WaitForAccessibilityId("media");
    }

    [Test]
    public void SelectItem_SwitchesContent()
    {
        Driver.WaitForAccessibilityId("home").Click();
        WaitForOnlyVisiblePage("page-home");
        Driver.WaitForAccessibilityId("home2").Click();
        WaitForOnlyVisiblePage("page-home2");
        Shot("home2-selected");

        var counter = Driver.WaitForAccessibilityId("counterBtn");
        var before = counter.Text;
        counter.Click();

        // The app session is shared across tests, so compare against the actual prior value.
        var updatedText = Driver.WaitForAccessibilityIdText("counterBtn",
            text => !string.Equals(text, before, StringComparison.Ordinal));
        Assert.That(updatedText, Is.Not.EqualTo(before),
            "Counter button text should update after clicking.");
    }

    [Test]
    public void Group_NavigatesToChildAndBack()
    {
        // 组子项入口:Apple sidebar 形态(iPad/Mac)下组默认展开,子项直接可见;
        // 其它形态先点组(落地页/rail 抽屉/Windows 选中首叶)
        var childEntry = Driver.FindByAccessibilityIdOrDefault("music", 3)
            ?? Driver.FindByAccessibilityIdOrDefault("landing-music", 3);

        if (childEntry is null)
        {
            Driver.WaitForAccessibilityId("media").Click();
            // 主题切换等配置变更后无障碍树刷新可能滞后,给宽一点的等待
            childEntry = Driver.FindByAccessibilityIdOrDefault("landing-music", 30)
                ?? Driver.FindByAccessibilityIdOrDefault("music", 15);
        }
        Shot("group-opened");

        if (childEntry is null && AppiumSetup.Platform == "windows")
        {
            // Windows:点组即选中首个子页,内容已切换
            WaitForOnlyVisiblePage("page-music");
            Shot("group-child");
            return;
        }

        Assert.That(childEntry, Is.Not.Null,
            "Non-Windows platforms must expose the group child entry before testing navigation.");
        childEntry!.Click();
        WaitForOnlyVisiblePage("page-music");
        Shot("group-child");

        // rail 抽屉(Android 宽形态)选中即关抽屉、Windows 点组即选首叶,
        // 这些形态没有返回入口;紧凑形态/iOS tab 模式(落地页 -> 子页)必须能返回落地页
        if (AppiumSetup.Form == "wide" || AppiumSetup.Platform is "windows" or "maccatalyst")
        {
            return;
        }

        var back = FindGroupBackAffordance();
        Assert.That(back, Is.Not.Null,
            "Expected a back affordance (toolbar back / nav bar back) on the group child page.");
        back!.Click();
        WaitForLanding();
        Shot("group-back-to-landing");

        Driver.WaitForAccessibilityId("landing-music").Click();
        WaitForOnlyVisiblePage("page-music");
        Shot("group-same-child-reopened");
    }

    [Test]
    public void ReturnFromGroupLanding_ShowsHomePage()
    {
        if (AppiumSetup.Form == "wide" || AppiumSetup.Platform is "windows" or "maccatalyst")
            Assert.Ignore("This layout selects a group child directly.");

        Driver.WaitForAccessibilityId("home").Click();
        WaitForOnlyVisiblePage("page-home");
        Driver.WaitForAccessibilityId("media").Click();
        if (AppiumSetup.Platform == "ios"
            && Driver.FindByAccessibilityIdOrDefault("landing-music", 3) is null)
        {
            Assert.That(IsVisible(Driver, "page-music") || IsVisible(Driver, "page-photos"), Is.True,
                "The Media tab should show its landing or a retained Media child page.");
            var back = FindGroupBackAffordance();
            Assert.That(back, Is.Not.Null,
                "Expected Back to return from the retained Media child to its landing page.");
            back!.Click();
        }
        WaitForLanding();
        Driver.WaitForAccessibilityId("home").Click();
        WaitForOnlyVisiblePage("page-home");
    }

    [Test]
    public void WideDrawer_SelectingCurrentChildAgainClosesDrawer()
    {
        if (AppiumSetup.Platform != "android" || AppiumSetup.Form != "wide")
            Assert.Ignore("Android wide navigation rail owns the modal group drawer.");

        Driver.WaitForAccessibilityId("media").Click();
        Driver.WaitForAccessibilityId("music").Click();
        WaitForOnlyVisiblePage("page-music");

        Driver.WaitForAccessibilityId("media").Click();
        Driver.WaitForAccessibilityId("music").Click();
        WaitForOnlyVisiblePage("page-music");
        Assert.That(Driver.FindByAccessibilityIdOrDefault("music", 2), Is.Null,
            "Selecting the active child should dismiss the modal drawer.");
    }

    [Test]
    public void CompactLanding_SurvivesLandscapeRailAndReturnToPortrait()
    {
        if (AppiumSetup.Platform != "android" || AppiumSetup.Form != "compact")
            Assert.Ignore("This regression covers Android compact-to-wide navigation rebuilds.");

        var originalOrientation = Driver.Orientation;
        try
        {
            Driver.Orientation = ScreenOrientation.Portrait;
            Driver.WaitForAccessibilityId("home").Click();
            WaitForOnlyVisiblePage("page-home");
            Driver.WaitForAccessibilityId("media").Click();
            WaitForLanding();

            Driver.Orientation = ScreenOrientation.Landscape;
            Driver.WaitForAccessibilityId("Open navigation menu");

            Driver.Orientation = ScreenOrientation.Portrait;
            WaitForLanding();
            Driver.WaitForAccessibilityId("landing-music").Click();
            WaitForOnlyVisiblePage("page-music");
        }
        finally
        {
            Driver.Orientation = originalOrientation;
        }
    }
}
