using NUnit.Framework;

namespace AdaptiveShell.UITests;

[TestFixture]
public class AdaptiveChromeTests : BaseTest
{
    [Test]
    public void Chrome_MatchesExpectedFormFactor()
    {
        var railToggle = Driver.FindByAccessibilityIdOrDefault("Open navigation menu", 5);

        switch (AppiumSetup.Platform, AppiumSetup.Form)
        {
            case ("android", "wide"):
                Assert.That(railToggle, Is.Not.Null,
                    "Wide form should present the navigation rail with its menu toggle.");
                break;
            case ("android", "compact"):
                Assert.That(railToggle, Is.Null,
                    "Compact form should use bottom navigation without a rail toggle.");
                break;
            default:
                // 未指定形态时只验证壳可用
                Driver.WaitForAccessibilityId("home");
                break;
        }
    }

    [Test]
    public void WideForm_RailGroupOpensDrawer()
    {
        if (AppiumSetup.Platform != "android" || AppiumSetup.Form != "wide")
        {
            Assert.Ignore("Rail drawer is an Android wide-form behavior.");
        }

        Driver.WaitForAccessibilityId("media").Click();
        Driver.WaitForAccessibilityId("music").Click();
        Driver.WaitForAccessibilityId("counterBtn");
    }

    [Test]
    public void RailToggle_ExpandsAndCollapsesRail()
    {
        if (AppiumSetup.Platform != "android" || AppiumSetup.Form != "wide")
        {
            Assert.Ignore("Rail expand/collapse is an Android wide-form behavior.");
        }

        var toggle = Driver.WaitForAccessibilityId("Open navigation menu");
        toggle.Click();
        Driver.WaitForAccessibilityId("Collapse navigation rail");
        Driver.WaitForAccessibilityId("Collapse navigation rail").Click();
        // 收起后标签是 "Expand navigation rail"(初始的 "Open navigation menu" 只在创建时出现一次)
        Driver.WaitForAccessibilityId("Expand navigation rail");
    }
}
