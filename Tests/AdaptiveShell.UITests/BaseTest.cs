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
        }
        catch (Exception ex)
        {
            TestContext.Out.WriteLine($"Failed to capture screenshot: {ex.Message}");
        }
    }
}
