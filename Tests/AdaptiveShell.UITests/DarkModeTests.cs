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
            }
            catch (Exception ex)
            {
                TestContext.Out.WriteLine($"Failed to restore light mode: {ex.Message}");
            }
        }
    }
}
