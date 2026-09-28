using NUnit.Framework;
using OpenQA.Selenium;
using OpenQA.Selenium.Appium;

namespace AdaptiveShell.UITests;

public abstract class BaseTest
{
    // 会话由 SessionHost 在整个程序集范围内共享创建/销毁
    protected AppiumDriver Driver => SessionHost.Driver;

    int _shotSequence = 1;

    /// <summary>关键步骤留档截图,归入 TestResults/shots/&lt;platform&gt;-&lt;form&gt;/,供报告汇总。</summary>
    protected void Shot(string label)
    {
        try
        {
            Shots.Save(Driver, label, ++_shotSequence);
        }
        catch (Exception ex)
        {
            TestContext.Out.WriteLine($"Failed to capture shot '{label}': {ex.Message}");
        }
    }

    [TearDown]
    public void CaptureScreenshotOnFailure()
    {
        if (TestContext.CurrentContext.Result.Outcome.Status
            != NUnit.Framework.Interfaces.TestStatus.Failed)
        {
            return;
        }

        try
        {
            Shots.SaveFailure(Driver,
                TestContext.CurrentContext.Test.ClassName!,
                TestContext.CurrentContext.Test.Name);

            // 同时留一份无障碍树 XML,Appium 视角与 adb dump 可能不一致
            var dir = Path.Combine(AppiumSetup.RepoRoot, "TestResults", "screenshots");
            var name = $"{TestContext.CurrentContext.Test.ClassName}.{TestContext.CurrentContext.Test.Name}"
                .Replace("\"", "").Replace("/", "_");
            File.WriteAllText(Path.Combine(dir, $"{name}.xml"), Driver.PageSource);
        }
        catch (Exception ex)
        {
            TestContext.Out.WriteLine($"Failed to capture screenshot: {ex.Message}");
        }
    }
}
