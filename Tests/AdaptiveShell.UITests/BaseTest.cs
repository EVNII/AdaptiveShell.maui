using NUnit.Framework;
using OpenQA.Selenium;
using OpenQA.Selenium.Appium;

namespace AdaptiveShell.UITests;

public abstract class BaseTest
{
    protected AppiumDriver Driver = null!;

    [OneTimeSetUp]
    public void CreateSession()
    {
        Driver = AppiumSetup.CreateDriver();
        try
        {
            WaitForShell();
        }
        catch
        {
            // fixture 级失败不走 TearDown,这里补截图,CI 上才能看到首屏真实状态
            SaveScreenshot("OneTimeSetUp");
            throw;
        }
    }

    // 等壳的首个导航项出现,说明首屏已渲染。
    // 资源紧张的模拟器上系统弹窗(如启动器 ANR "isn't responding")会挡住
    // 无障碍树,期间周期性地把这类弹窗点掉再等
    void WaitForShell()
    {
        for (int attempt = 0; attempt < 6; attempt++)
        {
            DismissAnrDialogs();
            try
            {
                Driver.WaitForAccessibilityId("home", 30);
                return;
            }
            catch (WebDriverTimeoutException)
            {
            }
        }

        Driver.WaitForAccessibilityId("home");
    }

    void DismissAnrDialogs()
    {
        try
        {
            var buttons = Driver.FindElements(
                By.XPath("//*[@package='android' and @text='Wait']"));
            foreach (var button in buttons)
            {
                button.Click();
                Thread.Sleep(3000);
            }
        }
        catch (Exception)
        {
            // 没有弹窗,或会话尚不可交互
        }
    }

    [OneTimeTearDown]
    public void CloseSession()
    {
        Driver?.Quit();
        Driver = null!;
    }

    [TearDown]
    public void CaptureScreenshotOnFailure()
    {
        if (TestContext.CurrentContext.Result.Outcome.Status
            != NUnit.Framework.Interfaces.TestStatus.Failed)
        {
            return;
        }

        SaveScreenshot(TestContext.CurrentContext.Test.Name);
    }

    void SaveScreenshot(string testName)
    {
        try
        {
            var dir = Path.Combine(AppiumSetup.RepoRoot, "TestResults", "screenshots");
            Directory.CreateDirectory(dir);
            var name = $"{TestContext.CurrentContext.Test.ClassName}.{testName}"
                .Replace("\"", "").Replace("/", "_");
            var path = Path.Combine(dir, $"{name}.png");
            Driver.GetScreenshot().SaveAsFile(path);
            TestContext.Out.WriteLine($"Screenshot saved: {path}");
        }
        catch (Exception ex)
        {
            TestContext.Out.WriteLine($"Failed to capture screenshot: {ex.Message}");
        }
    }
}
