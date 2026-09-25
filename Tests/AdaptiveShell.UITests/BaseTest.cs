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
            // 等壳的首个导航项出现,说明首屏已渲染
            Driver.WaitForAccessibilityId("home");
        }
        catch
        {
            // fixture 级失败不走 TearDown,这里补截图,CI 上才能看到首屏真实状态
            SaveScreenshot("OneTimeSetUp");
            throw;
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
