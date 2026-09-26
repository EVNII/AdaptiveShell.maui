using NUnit.Framework;
using OpenQA.Selenium;
using OpenQA.Selenium.Appium;

namespace AdaptiveShell.UITests;

/// <summary>
/// 整个测试程序集共享一个 Appium 会话。
/// iOS 上每个会话都要重启 WebDriverAgent,反复建会话既慢又容易在
/// teardown/重启竞态里挂掉("Reset: failed to terminate" + ECONNREFUSED)。
/// </summary>
[SetUpFixture]
public class SessionHost
{
    public static AppiumDriver Driver = null!;

    [OneTimeSetUp]
    public void CreateSession()
    {
        Driver = AppiumSetup.CreateDriver();

        // 等壳的首个导航项出现,说明首屏已渲染。
        // 资源紧张的模拟器上系统弹窗(如启动器 ANR "isn't responding")会挡住
        // 无障碍树,等待过程里由 WaitFor 轮询顺带点掉(见 ElementExtensions)
        for (int attempt = 0; attempt < 6; attempt++)
        {
            ElementExtensions.DismissAnrDialogs(Driver);
            try
            {
                Driver.WaitForAccessibilityId("home", 30);
                Shots.Save(Driver, "launch", 1);
                return;
            }
            catch (WebDriverTimeoutException)
            {
            }
        }

        Driver.WaitForAccessibilityId("home");
        Shots.Save(Driver, "launch", 1);
    }

    [OneTimeTearDown]
    public void CloseSession()
    {
        Driver?.Quit();
        Driver = null!;
    }
}
