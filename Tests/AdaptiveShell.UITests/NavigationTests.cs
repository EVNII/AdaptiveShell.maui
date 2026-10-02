using NUnit.Framework;
using OpenQA.Selenium;
using OpenQA.Selenium.Appium;

namespace AdaptiveShell.UITests;

[TestFixture]
public class NavigationTests : BaseTest
{
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
        Driver.WaitForAccessibilityId("home2").Click();
        Shot("home2-selected");

        var counter = Driver.WaitForAccessibilityId("counterBtn");
        counter.Click();

        // 点击触发重渲染后原引用可能失效,重新取一次再断言
        var updated = Driver.WaitForAccessibilityId("counterBtn");
        Assert.That(updated.Text, Does.Contain("1"),
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
            Driver.WaitForAccessibilityId("counterBtn");
            Shot("group-child");
            return;
        }

        Assert.That(childEntry, Is.Not.Null,
            "Non-Windows platforms must expose the group child entry before testing navigation.");
        childEntry!.Click();
        Driver.WaitForAccessibilityId("counterBtn");
        Shot("group-child");

        // rail 抽屉(Android 宽形态)选中即关抽屉、Windows 点组即选首叶,
        // 这些形态没有返回入口;紧凑形态/iOS tab 模式(落地页 -> 子页)必须能返回落地页
        if (AppiumSetup.Form == "wide" || AppiumSetup.Platform is "windows" or "maccatalyst")
        {
            return;
        }

        var back = Driver.FindByAccessibilityIdOrDefault("Back", 5)
            ?? Driver.FindByAccessibilityIdOrDefault("媒体", 5);
        if (back is null && AppiumSetup.Platform is "ios" or "maccatalyst")
        {
            // iOS 26+ 的返回按钮是 chevron 样式,无障碍名既不是 "Back" 也不是前一页标题,
            // 退化为导航栏第一个按钮
            back = Driver.FindOrDefault(
                By.XPath("//XCUIElementTypeNavigationBar//XCUIElementTypeButton[1]"), 5);
        }
        Assert.That(back, Is.Not.Null,
            "Expected a back affordance (toolbar back / nav bar back) on the group child page.");
        back!.Click();
        Driver.WaitForAccessibilityId("landing-music");
        Shot("group-back-to-landing");
    }
}
