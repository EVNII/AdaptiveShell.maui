using OpenQA.Selenium;
using OpenQA.Selenium.Appium;
using OpenQA.Selenium.Support.UI;
using System.Diagnostics;
using System.Text.Json;

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
        return WaitForAny(driver, ResolveTimeout(timeoutSeconds), id,
            MobileBy.AccessibilityId(id), MobileBy.Id(id));
    }

    public static AppiumElement WaitFor(
        this AppiumDriver driver, By by, int timeoutSeconds = 0)
    {
        return WaitForAny(driver, ResolveTimeout(timeoutSeconds), null, by);
    }

    static int ResolveTimeout(int timeoutSeconds) =>
        timeoutSeconds > 0 ? timeoutSeconds : DefaultTimeoutSeconds;

    static AppiumElement WaitForAny(AppiumDriver driver, int timeoutSeconds, string? id, params By[] locators)
    {
        var wait = new WebDriverWait(driver, TimeSpan.FromSeconds(timeoutSeconds));
        var trace = NativeVisibilityTrace.Create(driver, timeoutSeconds, id, locators);
        // The native tree can replace a node between lookup and Displayed.
        // Re-run the locators within the original deadline when that happens.
        wait.IgnoreExceptionTypes(typeof(NoSuchElementException), typeof(NotFoundException),
            typeof(StaleElementReferenceException));
        try
        {
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
                        var displayed = element.Displayed;
                        trace?.Observe(locator, (AppiumElement)element, displayed);
                        if (displayed)
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
        catch (WebDriverTimeoutException)
        {
            trace?.SaveLateTimeoutSource();
            throw;
        }
    }

    // An opt-in CI diagnostic. The original first-match lookup, Displayed check,
    // return value and WebDriverWait budget above remain the test's decision.
    sealed class NativeVisibilityTrace
    {
        readonly AppiumDriver driver;
        readonly By[] locators;
        readonly Stopwatch elapsed = Stopwatch.StartNew();
        readonly int timeoutSeconds;
        readonly string waitId = Guid.NewGuid().ToString("N");
        readonly string testName = NUnit.Framework.TestContext.CurrentContext.Test.FullName;
        double nextObservation;

        NativeVisibilityTrace(AppiumDriver driver, int timeoutSeconds, By[] locators)
        {
            this.driver = driver;
            this.timeoutSeconds = timeoutSeconds;
            this.locators = locators;
        }

        public static NativeVisibilityTrace? Create(AppiumDriver driver, int timeoutSeconds,
            string? id, By[] locators) =>
            id == "home" && AppiumSetup.Platform == "ios" && AppiumSetup.Form == "wide"
            && string.Equals(Environment.GetEnvironmentVariable("UITEST_TRACE_NATIVE_VISIBILITY"),
                "true", StringComparison.OrdinalIgnoreCase)
                ? new NativeVisibilityTrace(driver, timeoutSeconds, locators) : null;

        public void Observe(By selectedLocator, AppiumElement selected, bool displayed)
        {
            if (elapsed.Elapsed.TotalSeconds < nextObservation)
                return;
            // Leave the last five seconds for the original lookup, rather than
            // starting another diagnostic batch near its existing deadline.
            var started = elapsed.Elapsed.TotalSeconds;
            nextObservation = started + 3;
            var selectedAt = DateTimeOffset.UtcNow;
            var selectedId = selected.Id;
            // Persist the original observation before FindElements/attribute
            // probes, which may refresh WDA snapshots and element cache entries.
            Append(new
            {
                event_type = "original_first_match_before_diagnostic_reads",
                wait_id = waitId, test = testName,
                original_timeout_seconds = timeoutSeconds,
                elapsed_seconds = started, observed_at = selectedAt,
                locator = selectedLocator.ToString(), element_uuid = selectedId,
                displayed,
            });
            var nodes = new Dictionary<string, object?>();
            var matches = new List<object>();
            var errors = new List<string>();
            if (started < timeoutSeconds - 5)
            {
                foreach (var locator in locators)
                {
                    if (elapsed.Elapsed.TotalSeconds >= timeoutSeconds - 5)
                    {
                        errors.Add("Remaining original wait budget prevented further diagnostic remote reads.");
                        break;
                    }
                    try
                    {
                        var found = driver.FindElements(locator);
                        var ids = found.Select(element => element.Id).ToArray();
                        matches.Add(new { locator = locator.ToString(), element_uuids = ids });
                        foreach (var element in found)
                        {
                            if (nodes.ContainsKey(element.Id))
                                continue;
                            nodes[element.Id] = ReadNode(element,
                                element.Id == selectedId ? displayed : null, selectedAt, errors);
                        }
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"{locator}: {ex.GetType().Name}: {ex.Message}");
                    }
                }
            }
            else
            {
                errors.Add("Near original wait deadline: only the original first-match observation was recorded.");
            }

            Append(new
            {
                event_type = "during_home_wait",
                wait_id = waitId,
                test = testName,
                original_timeout_seconds = timeoutSeconds,
                started_elapsed_seconds = started,
                completed_elapsed_seconds = elapsed.Elapsed.TotalSeconds,
                selected_first = new
                {
                    locator = selectedLocator.ToString(), element_uuid = selectedId,
                    displayed, observed_at = selectedAt,
                },
                locator_matches = matches,
                nodes,
                errors,
                observation_contract = "Sequential actual remote reads; timestamps identify timing. No diagnostic result changes the original first-match/Displayed decision.",
            });
            nextObservation = elapsed.Elapsed.TotalSeconds + 3;
        }

        object ReadNode(AppiumElement element, bool? originalDisplayed,
            DateTimeOffset originalAt, List<string> errors)
        {
            object? Read(string property, Func<object?> get)
            {
                if (elapsed.Elapsed.TotalSeconds >= timeoutSeconds - 5)
                {
                    errors.Add($"{element.Id}/{property}: omitted near original wait deadline.");
                    return null;
                }
                try { return get(); }
                catch (Exception ex)
                {
                    errors.Add($"{element.Id}/{property}: {ex.GetType().Name}: {ex.Message}");
                    return null;
                }
            }

            var at = DateTimeOffset.UtcNow;
            var actualDisplayed = originalDisplayed is bool previous ? previous
                : Read("Displayed", () => element.Displayed);
            var enabled = Read("Enabled", () => element.Enabled);
            var name = Read("name", () => element.GetAttribute("name"));
            var type = Read("type", () => element.TagName);
            var label = Read("label", () => element.GetAttribute("label"));
            var location = Read("Location", () => element.Location);
            var size = Read("Size", () => element.Size);
            return new
            {
                element_uuid = element.Id, started_at = at,
                completed_at = DateTimeOffset.UtcNow,
                displayed = actualDisplayed, enabled, name, type, label,
                location, size,
                displayed_observation = originalDisplayed.HasValue
                    ? "original selected-first Displayed read" : "diagnostic Displayed read",
                original_displayed_at = originalDisplayed.HasValue ? originalAt : (DateTimeOffset?)null,
            };
        }

        void Append(object record)
        {
            try
            {
                var resultsDirectory = Path.Combine(AppiumSetup.RepoRoot, "TestResults");
                Directory.CreateDirectory(resultsDirectory);
                var entry = new Dictionary<string, object?>
                {
                    ["schema_version"] = 1,
                    ["trace_enabled"] = true,
                    ["platform"] = AppiumSetup.Platform,
                    ["form"] = AppiumSetup.Form,
                    ["github_run_id"] = Environment.GetEnvironmentVariable("GITHUB_RUN_ID"),
                    ["github_run_attempt"] = Environment.GetEnvironmentVariable("GITHUB_RUN_ATTEMPT"),
                    ["github_sha"] = Environment.GetEnvironmentVariable("GITHUB_SHA"),
                    ["github_job"] = Environment.GetEnvironmentVariable("GITHUB_JOB"),
                    ["device_udid"] = Environment.GetEnvironmentVariable("UITEST_DEVICE_UDID"),
                };
                foreach (var property in JsonSerializer.SerializeToElement(record).EnumerateObject())
                    entry.Add(property.Name, property.Value);
                File.AppendAllText(Path.Combine(resultsDirectory, "ios-sidebar-home-visibility.jsonl"),
                    JsonSerializer.Serialize(entry) + Environment.NewLine);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Native visibility diagnostic evidence could not be written: {ex}");
            }
        }

        public void SaveLateTimeoutSource()
        {
            var resultsDirectory = Path.Combine(AppiumSetup.RepoRoot, "TestResults");
            var path = Path.Combine(resultsDirectory, $"ios-sidebar-home-timeout-late-{waitId}.xml");
            try
            {
                Directory.CreateDirectory(resultsDirectory);
                var startedAt = DateTimeOffset.UtcNow;
                File.WriteAllText(path, driver.PageSource);
                Append(new
                {
                    event_type = "after_home_wait_timeout_late_source",
                    wait_id = waitId, test = testName,
                    started_at = startedAt, completed_at = DateTimeOffset.UtcNow,
                    source = path,
                    observation_contract = "Late source collected after the original timeout; it does not describe earlier wait observations.",
                });
            }
            catch (Exception ex)
            {
                Append(new { event_type = "late_source_error", wait_id = waitId,
                    error = $"{ex.GetType().Name}: {ex.Message}" });
            }
        }
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

    public static AppiumElement? FindOrDefault(
        this AppiumDriver driver, By by, int timeoutSeconds = 5)
    {
        try
        {
            return driver.WaitFor(by, timeoutSeconds);
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
