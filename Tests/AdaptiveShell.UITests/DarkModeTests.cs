using NUnit.Framework;

namespace AdaptiveShell.UITests;

[TestFixture]
public class DarkModeTests : BaseTest
{
    [Test]
    public void DarkMode_ShellAndGroupFlow()
    {
        try
        {
            Appearance.SetDark(true);
            // uimode 配置变更后,resource-id 类查找会有一段陈旧窗口期,
            // 探针元素(resource-id)可被定位才说明无障碍树已恢复
            WaitForA11ySettled();

            // 深色下导航壳依然可用:所有顶层项可定位
            Driver.WaitForAccessibilityId("home");
            Driver.WaitForAccessibilityId("home2");
            Driver.WaitForAccessibilityId("media");
            Shot("dark-launch");

            Driver.WaitForAccessibilityId("home2").Click();
            Driver.WaitForAccessibilityId("counterBtn");
            Shot("dark-home2-selected");

            Driver.WaitForAccessibilityId("media").Click();
            Shot("dark-group-opened");

            var childEntry = Driver.FindByAccessibilityIdOrDefault("landing-music", 8)
                ?? Driver.FindByAccessibilityIdOrDefault("music", 4);
            childEntry?.Click();
            Driver.WaitForAccessibilityId("counterBtn");
            Shot("dark-group-child");
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

    // 当前页面(无论 home 还是组子页)都有 counterBtn,适合当探针
    void WaitForA11ySettled() => Driver.WaitForAccessibilityId("counterBtn", 30);

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
