using NUnit.Framework;

namespace AdaptiveShell.UITests;

[TestFixture]
public class AccessibilityTests : BaseTest
{
    static readonly string[] TopLevelIds = { "home", "home2", "media" };

    [Test]
    public void EveryNavItem_HasNonEmptyAccessibilityLabel()
    {
        foreach (var id in TopLevelIds)
        {
            var element = Driver.WaitForAccessibilityId(id);
            Assert.That(element.AccessibilityLabel(), Is.Not.Null.And.Not.Empty,
                $"Nav item '{id}' exposes no accessibility label.");
        }
    }

    [Test]
    public void NavItemIdentifiers_AreStableAcrossNavigation()
    {
        // 标识必须不随选中态/内容切换漂移,否则无障碍与测试定位都不可靠
        foreach (var id in TopLevelIds)
        {
            Driver.WaitForAccessibilityId(id);
        }

        Driver.WaitForAccessibilityId("home2").Click();
        Driver.WaitForAccessibilityId("counterBtn");
        Driver.WaitForAccessibilityId("home").Click();

        foreach (var id in TopLevelIds)
        {
            Driver.WaitForAccessibilityId(id);
        }
    }
}
