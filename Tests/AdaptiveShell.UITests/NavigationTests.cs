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

        Assert.That(counter.Text, Does.Contain("1"),
            "Counter button text should update after clicking.");
    }

    [Test]
    public void Group_NavigatesToChildAndBack()
    {
        Driver.WaitForAccessibilityId("media").Click();

        // 紧凑形态/iOS tab 模式:先进组落地页(landing-music 行);
        // Android rail 宽形态:弹二级抽屉(music 项);Windows:组条目直接选中首叶
        var childEntry = Driver.FindByAccessibilityIdOrDefault("landing-music", 8)
            ?? Driver.FindByAccessibilityIdOrDefault("music", 4);
        Shot("group-opened");

        if (childEntry is null)
        {
            // Windows:点组即选中首个子页,内容已切换
            Driver.WaitForAccessibilityId("counterBtn");
            Shot("group-child");
            return;
        }

        childEntry.Click();
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
        Assert.That(back, Is.Not.Null,
            "Expected a back affordance (toolbar back / nav bar back) on the group child page.");
        back!.Click();
        Driver.WaitForAccessibilityId("landing-music");
        Shot("group-back-to-landing");
    }
}
