using OpenQA.Selenium;
using OpenQA.Selenium.Appium;
using OpenQA.Selenium.Support.UI;

namespace AdaptiveShell.UITests;

public static class ElementExtensions
{
    static int DefaultTimeoutSeconds =>
        int.TryParse(Environment.GetEnvironmentVariable("UITEST_TIMEOUT_SECONDS"), out var s) ? s : 60;

    /// <summary>
    /// 按自动化标识等待元素出现。跨平台差异:iOS/Mac/Windows 上 AutomationId 即
    /// 无障碍标识(accessibilityIdentifier/AutomationId),Android 上 MAUI 把它映射为
    /// resource-id 而非 content-desc,因此两种定位策略都尝试。
    /// </summary>
    public static AppiumElement WaitForAccessibilityId(
        this AppiumDriver driver, string id, int timeoutSeconds = 0)
    {
        return WaitForAny(driver, ResolveTimeout(timeoutSeconds), MobileBy.AccessibilityId(id), MobileBy.Id(id));
    }

    public static AppiumElement WaitFor(
        this AppiumDriver driver, By by, int timeoutSeconds = 0)
    {
        return WaitForAny(driver, ResolveTimeout(timeoutSeconds), by);
    }

    static int ResolveTimeout(int timeoutSeconds) =>
        timeoutSeconds > 0 ? timeoutSeconds : DefaultTimeoutSeconds;

    static AppiumElement WaitForAny(AppiumDriver driver, int timeoutSeconds, params By[] locators)
    {
        var wait = new WebDriverWait(driver, TimeSpan.FromSeconds(timeoutSeconds));
        wait.IgnoreExceptionTypes(typeof(NoSuchElementException), typeof(NotFoundException));
        return (AppiumElement)wait.Until(d =>
        {
            // 系统 ANR 弹窗(如启动器 "isn't responding")会挡住无障碍树,
            // 等待期间顺手点掉,否则弹窗期间任何元素都找不到
            DismissAnrDialogs(driver);

            foreach (var locator in locators)
            {
                try
                {
                    var element = d.FindElement(locator);
                    if (element.Displayed)
                    {
                        return element;
                    }
                }
                catch (NoSuchElementException)
                {
                }
                catch (NotFoundException)
                {
                }
            }

            return null;
        })!;
    }

    public static void DismissAnrDialogs(AppiumDriver driver)
    {
        try
        {
            var buttons = driver.FindElements(
                By.XPath("//*[@package='android' and @text='Wait']"));
            foreach (var button in buttons)
            {
                button.Click();
                Thread.Sleep(2000);
            }
        }
        catch (Exception)
        {
            // 没有弹窗,或会话尚不可交互
        }
    }

    public static AppiumElement? FindByAccessibilityIdOrDefault(
        this AppiumDriver driver, string id, int timeoutSeconds = 5)
    {
        try
        {
            return driver.WaitForAccessibilityId(id, timeoutSeconds);
        }
        catch (WebDriverTimeoutException)
        {
            return null;
        }
    }

    public static bool ExistsByAccessibilityId(this AppiumDriver driver, string id, int timeoutSeconds = 5)
    {
        return driver.FindByAccessibilityIdOrDefault(id, timeoutSeconds) is not null;
    }

    /// <summary>元素的无障碍朗读标签(Android content-desc / iOS label / Windows Name)。</summary>
    public static string? AccessibilityLabel(this AppiumElement element)
    {
        foreach (var attribute in new[] { "content-desc", "label", "Name" })
        {
            string? value;
            try
            {
                value = element.GetAttribute(attribute);
            }
            catch (WebDriverException)
            {
                // 平台不认识的属性名(iOS 问 content-desc 会直接抛错)
                continue;
            }

            if (!string.IsNullOrEmpty(value))
            {
                return value;
            }
        }

        return null;
    }
}
