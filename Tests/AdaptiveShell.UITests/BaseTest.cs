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
            Shot("launch");
        }
        catch
        {
            // fixture 级失败不走 TearDown,这里补截图,CI 上才能看到首屏真实状态
            SaveScreenshot("OneTimeSetUp");
            throw;
        }
    }

    int _shotSequence;

    /// <summary>关键步骤留档截图,归入 TestResults/shots/&lt;platform&gt;-&lt;form&gt;/,供报告汇总。</summary>
    protected void Shot(string label)
    {
        try
        {
            var form = AppiumSetup.Form ?? "default";
            var dir = Path.Combine(AppiumSetup.RepoRoot, "TestResults", "shots",
                $"{AppiumSetup.Platform}-{form}");
            Directory.CreateDirectory(dir);
            var name = $"{++_shotSequence:00}-{label}.png"
                .Replace("\"", "").Replace("/", "_");
            var path = Path.Combine(dir, name);
            Driver.GetScreenshot().SaveAsFile(path);
            TestContext.Out.WriteLine($"Shot saved: {path}");
        }
        catch (Exception ex)
        {
            TestContext.Out.WriteLine($"Failed to capture shot '{label}': {ex.Message}");
        }
    }

    // 等壳的首个导航项出现,说明首屏已渲染。
    // 资源紧张的模拟器上系统弹窗(如启动器 ANR "isn't responding")会挡住
    // 无障碍树,等待过程里由 WaitFor 轮询顺带点掉(见 ElementExtensions)
    void WaitForShell()
    {
        for (int attempt = 0; attempt < 6; attempt++)
        {
            ElementExtensions.DismissAnrDialogs(Driver);
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
