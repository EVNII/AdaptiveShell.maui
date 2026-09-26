using OpenQA.Selenium.Appium;

namespace AdaptiveShell.UITests;

/// <summary>截图留档:关键步骤 shots(按 platform-form 归档)与失败截图。</summary>
public static class Shots
{
    public static void Save(AppiumDriver driver, string label, int sequence)
    {
        var form = AppiumSetup.Form ?? "default";
        var dir = Path.Combine(AppiumSetup.RepoRoot, "TestResults", "shots",
            $"{AppiumSetup.Platform}-{form}");
        Directory.CreateDirectory(dir);
        var name = $"{sequence:00}-{label}.png".Replace("\"", "").Replace("/", "_");
        driver.GetScreenshot().SaveAsFile(Path.Combine(dir, name));
    }

    public static void SaveFailure(AppiumDriver driver, string className, string testName)
    {
        var dir = Path.Combine(AppiumSetup.RepoRoot, "TestResults", "screenshots");
        Directory.CreateDirectory(dir);
        var name = $"{className}.{testName}".Replace("\"", "").Replace("/", "_");
        driver.GetScreenshot().SaveAsFile(Path.Combine(dir, $"{name}.png"));
    }
}
