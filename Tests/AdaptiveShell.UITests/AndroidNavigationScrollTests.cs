using System.Diagnostics;
using NUnit.Framework;
using OpenQA.Selenium;
using OpenQA.Selenium.Appium;
using OpenQA.Selenium.Support.UI;

namespace AdaptiveShell.UITests;

// Build the app with -p:NavigationOverflowSample=true and opt in with
// UITEST_NAVIGATION_OVERFLOW=1. Run the same 12 top-level / 18 Media child fixture
// in short/tall wide windows and compact windows to cover measured overflow.
[TestFixture]
public class AndroidNavigationScrollTests : BaseTest
{
    const string MainScroll = "Main navigation";
    const string GroupScroll = "Navigation group items";
    const string LastTop = "overflow-top-12";
    const string LastChild = "overflow-child-18";
    readonly Dictionary<string, (int Width, int Height)> _rowSizes = new();

    [SetUp]
    public void RequireOverflowFixture()
    {
        _rowSizes.Clear();
        if (Environment.GetEnvironmentVariable("UITEST_NAVIGATION_OVERFLOW") != "1"
            || AppiumSetup.Platform != "android" || AppiumSetup.Form is not ("wide" or "compact"))
        {
            Assert.Ignore("Opt-in Android overflow fixture requires UITEST_NAVIGATION_OVERFLOW=1 and UITEST_FORM=wide|compact.");
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void RailAndGroupDrawer_ReachFirstAndLastItems(bool expanded)
    {
        if (AppiumSetup.Form != "wide")
            Assert.Ignore("This test covers Android wide navigation rail and drawer.");

        // The header belongs to the same scroll content. Return to the top
        // before interacting with the rail expansion control.
        ScrollToStart(MainScroll);
        var collapse = FindVisible("Collapse navigation rail");
        collapse?.Click();
        if (expanded)
        {
            var expand = FindVisible("Open navigation menu") ?? FindVisible("Expand navigation rail");
            Assert.That(expand, Is.Not.Null, "Expected the collapsed rail expansion control.");
            expand!.Click();
            Driver.WaitForAccessibilityId("Collapse navigation rail");
        }

        var main = GetScroll(MainScroll);
        RecordRowSize("home", main, MainScroll);
        ReportOverflow(LastTop, main, MainScroll);
        Shot(expanded ? "overflow-expanded-top" : "overflow-collapsed-top");

        ScrollTo(LastTop, MainScroll, "down").Click();
        Driver.WaitForAccessibilityId($"page-{LastTop}");
        Shot(expanded ? "overflow-expanded-bottom" : "overflow-collapsed-bottom");

        ScrollTo("home", MainScroll, "up").Click();
        Driver.WaitForAccessibilityId("page-home");
        ScrollTo("media", MainScroll, "down").Click();

        var group = GetScroll(GroupScroll);
        RecordRowSize("music", group, GroupScroll);
        ReportOverflow(LastChild, group, GroupScroll);
        Shot(expanded ? "overflow-expanded-drawer-top" : "overflow-collapsed-drawer-top");
        var lastChild = ScrollTo(LastChild, GroupScroll, "down");
        Shot(expanded ? "overflow-expanded-drawer-bottom" : "overflow-collapsed-drawer-bottom");
        lastChild.Click();
        Driver.WaitForAccessibilityId($"page-{LastChild}");
        AssertDrawerClosed();

        // Selecting the already active last child must still dismiss the drawer.
        ScrollTo("media", MainScroll, "up").Click();
        ScrollTo(LastChild, GroupScroll, "down").Click();
        Driver.WaitForAccessibilityId($"page-{LastChild}");
        AssertDrawerClosed();

        // Reopen and return through the first child as well.
        ScrollTo("media", MainScroll, "up").Click();
        ScrollTo("music", GroupScroll, "up").Click();
        Driver.WaitForAccessibilityId("page-music");
        AssertDrawerClosed();
        Shot(expanded ? "overflow-expanded-child-selected" : "overflow-collapsed-child-selected");

        ScrollTo("home", MainScroll, "up").Click();
        Driver.WaitForAccessibilityId("page-home");
        if (expanded)
        {
            ScrollToStart(MainScroll);
            Driver.WaitForAccessibilityId("Collapse navigation rail").Click();
            Driver.WaitForAccessibilityId("Expand navigation rail");
        }
    }

    [Test]
    public void CompactBottomNavigation_ReachesFirstAndLastItemsHorizontally()
    {
        if (AppiumSetup.Form != "compact")
            Assert.Ignore("This test covers Android compact bottom navigation.");

        ScrollToStart(MainScroll, "left");
        AssertCompactBottomNavigation();
        var bottom = GetScroll(MainScroll);
        RecordRowSize("home", bottom, MainScroll);
        Assert.That(FindVisible(LastTop, bottom), Is.Null,
            "Twelve full-size native items should overflow the compact bottom viewport.");
        ReportOverflow(LastTop, bottom, MainScroll);
        Shot("overflow-compact-bottom-start");

        ScrollTo(LastTop, MainScroll, "right").Click();
        Driver.WaitForAccessibilityId($"page-{LastTop}");
        AssertCompactBottomNavigation();
        Assert.That(FindVisible(LastTop, GetScroll(MainScroll)), Is.Not.Null,
            "The selected last item should remain fully visible in the bottom viewport.");
        Shot("overflow-compact-bottom-end");

        // Selecting the current item again retains the same page and bottom form.
        WaitForSettledItem(LastTop, MainScroll).Click();
        Driver.WaitForAccessibilityId($"page-{LastTop}");
        AssertCompactBottomNavigation();

        ScrollTo("home", MainScroll, "left").Click();
        Driver.WaitForAccessibilityId("page-home");
        AssertCompactBottomNavigation();
        Assert.That(FindVisible("home", GetScroll(MainScroll)), Is.Not.Null,
            "The first item should be reachable again by horizontal scrolling.");
        Shot("overflow-compact-bottom-return");
    }

    void AssertCompactBottomNavigation()
    {
        var bottom = GetScroll(MainScroll);
        Assert.That(bottom.GetAttribute("class"), Is.EqualTo("android.widget.HorizontalScrollView"),
            "Compact width must retain bottom navigation with horizontal overflow.");
        var window = Driver.Manage().Window.Size;
        Assert.That(bottom.Location.Y, Is.GreaterThan(window.Height / 2),
            "The main navigation viewport should remain at the bottom of the compact window.");
        Assert.That(bottom.Size.Width, Is.GreaterThan(bottom.Size.Height),
            "Bottom navigation should span horizontally across the compact window.");
        Assert.That(bottom.Location.X + bottom.Size.Width, Is.LessThanOrEqualTo(window.Width + 1));
        Assert.That(bottom.Location.Y + bottom.Size.Height, Is.LessThanOrEqualTo(window.Height + 1));
        foreach (var toggle in new[] { "Open navigation menu", "Expand navigation rail", "Collapse navigation rail" })
            Assert.That(FindVisible(toggle), Is.Null, "Item overflow must not switch compact navigation to a rail.");
    }

    void AssertDrawerClosed()
    {
        // The current page marker already exists when selecting the same child.
        // Synchronize on the drawer itself so that this remains a real dismissal
        // assertion without retrying the click.
        var wait = new WebDriverWait(Driver, TimeSpan.FromSeconds(5));
        try
        {
            wait.Until(driver => driver.FindElements(ScrollLocator(GroupScroll)).Count == 0);
        }
        catch (WebDriverTimeoutException)
        {
            Assert.Fail("Selecting a child, including the current child, should dismiss the drawer.");
        }
    }

    static By ScrollLocator(string description) =>
        By.XPath($"//android.widget.ScrollView[@content-desc='{description}']"
            + $" | //android.widget.HorizontalScrollView[@content-desc='{description}']");

    AppiumElement GetScroll(string description) => Driver.WaitFor(ScrollLocator(description), 15);

    void RecordRowSize(string firstIdentity, AppiumElement scroll, string description)
    {
        var first = FindVisible(firstIdentity, scroll);
        Assert.That(first, Is.Not.Null, "First navigation item should start fully in view.");
        var size = first!.Size;
        _rowSizes[description] = (size.Width, size.Height);
    }

    void ReportOverflow(string lastIdentity, AppiumElement scroll, string description)
    {
        // The same fixture runs in short and tall windows. Height determines
        // whether scrolling is needed; the item count stays identical.
        var requiresScrolling = FindVisible(lastIdentity, scroll) is null;
        var row = _rowSizes[description];
        TestContext.Out.WriteLine($"{description}: viewport={scroll.Size.Width}x{scroll.Size.Height}, "
            + $"full item={row.Width}x{row.Height}, requires scrolling={requiresScrolling}.");
    }

    void ScrollToStart(string description, string direction = "up")
    {
        for (int attempt = 0; attempt < 20; attempt++)
        {
            var scroll = GetScroll(description);
            if (Driver.ExecuteScript("mobile: scrollGesture", new Dictionary<string, object>
                {
                    ["elementId"] = scroll.Id,
                    ["direction"] = direction,
                    ["percent"] = 0.8,
                    ["speed"] = 800
                }) is false)
            {
                WaitForSettledItem("home", description);
                return;
            }
        }
        throw new AssertionException($"Could not scroll {description} to its start.");
    }

    AppiumElement ScrollTo(string identity, string description, string direction)
    {
        for (int attempt = 0; attempt < 20; attempt++)
        {
            var scroll = GetScroll(description);
            var target = FindVisible(identity, scroll);
            if (target is not null)
                return WaitForSettledItem(identity, description);

            var canScroll = Driver.ExecuteScript("mobile: scrollGesture", new Dictionary<string, object>
            {
                ["elementId"] = scroll.Id,
                ["direction"] = direction,
                ["percent"] = 0.8,
                ["speed"] = 800
            });
            if (canScroll is false)
            {
                // The last gesture can reveal the target while also reaching
                // the end, so inspect once more before reporting failure.
                target = FindVisible(identity, GetScroll(description));
                if (target is not null)
                    return WaitForSettledItem(identity, description);
                break;
            }
        }

        throw new AssertionException($"Could not scroll {description} {direction} to visible item '{identity}'. "
            + "Build ExampleAShellApp with -p:NavigationOverflowSample=true.");
    }

    AppiumElement WaitForSettledItem(string identity, string description)
    {
        // Fast native gestures can leave a ScrollView flinging after Appium
        // returns. Its next tap stops that fling instead of invoking the row.
        // Require stable geometry before issuing the single navigation click.
        (int X, int Y, int Width, int Height)? previousBounds = null;
        var stableFor = Stopwatch.StartNew();
        var wait = new WebDriverWait(Driver, TimeSpan.FromSeconds(5))
        {
            PollingInterval = TimeSpan.FromMilliseconds(150)
        };
        wait.IgnoreExceptionTypes(typeof(StaleElementReferenceException));
        return wait.Until(_ =>
        {
            var target = FindVisible(identity, GetScroll(description));
            if (target is null)
            {
                previousBounds = null;
                stableFor.Restart();
                return null;
            }

            var location = target.Location;
            var size = target.Size;
            var bounds = (location.X, location.Y, size.Width, size.Height);
            if (previousBounds != bounds)
            {
                previousBounds = bounds;
                stableFor.Restart();
                return null;
            }
            return stableFor.ElapsedMilliseconds >= 750 ? target : null;
        })!;
    }

    AppiumElement? FindVisible(string identity, AppiumElement? viewport = null)
    {
        var minimumSize = viewport is not null
            && _rowSizes.TryGetValue(viewport.GetAttribute("content-desc"), out var size)
                ? size : (Width: 0, Height: 0);
        foreach (var locator in new[] { MobileBy.AccessibilityId(identity), MobileBy.Id(identity) })
        {
            foreach (var element in Driver.FindElements(locator))
            {
                try
                {
                    if (element.Displayed && (viewport is null || IsInside(element, viewport, minimumSize)))
                        return (AppiumElement)element;
                }
                catch (StaleElementReferenceException)
                {
                    // A native rail rebuild can replace an accessibility node.
                }
            }
        }
        return null;
    }

    static bool IsInside(IWebElement item, IWebElement viewport, (int Width, int Height) minimumSize)
    {
        var location = item.Location;
        var size = item.Size;
        var origin = viewport.Location;
        var bounds = viewport.Size;
        // Android accessibility reports clipped bounds for a partially visible
        // row. Containment alone is insufficient: compare with the first fully
        // visible row measured in this same expanded/collapsed presentation.
        return size.Width > 0 && size.Height > 0
            && size.Width >= minimumSize.Width && size.Height >= minimumSize.Height
            && location.X >= origin.X - 1 && location.Y >= origin.Y - 1
            && location.X + size.Width <= origin.X + bounds.Width + 1
            && location.Y + size.Height <= origin.Y + bounds.Height + 1;
    }
}
