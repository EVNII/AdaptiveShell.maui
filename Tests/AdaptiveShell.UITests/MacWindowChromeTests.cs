using System.Globalization;
using System.Xml.Linq;
using NUnit.Framework;

namespace AdaptiveShell.UITests;

[TestFixture]
[NonParallelizable]
public class MacWindowChromeTests : BaseTest
{
    [Test]
    public void WindowButtons_AreContainedInSidebar()
    {
        if (AppiumSetup.Platform != "maccatalyst")
        {
            Assert.Ignore("Window traffic lights are a Mac Catalyst behavior.");
        }

        Driver.WaitForAccessibilityId("home");
        var source = Driver.PageSource;
        var results = Path.Combine(AppiumSetup.RepoRoot, "TestResults");
        Directory.CreateDirectory(results);
        File.WriteAllText(Path.Combine(results, "mac-window-chrome.xml"), source);
        // Evidence is required, so capture errors must fail this test.
        Shots.Save(Driver, "window-chrome", 26);

        var tree = XDocument.Parse(source);
        var homes = tree.Descendants()
            .Where(element => (string?)element.Attribute("identifier") == "home")
            .ToArray();
        Assert.That(homes, Has.Length.EqualTo(1),
            "Expected one home navigation item in the accessibility tree.");
        var sidebar = homes[0].Ancestors()
            .FirstOrDefault(element => element.Name.LocalName == "XCUIElementTypeCollectionView");
        Assert.That(sidebar, Is.Not.Null,
            "The home navigation item must belong to a sidebar collection.");
        var sidebarBounds = ReadBounds(sidebar!, "sidebar");

        var window = sidebar!.Ancestors()
            .FirstOrDefault(element => element.Name.LocalName == "XCUIElementTypeWindow");
        Assert.That(window, Is.Not.Null, "The sidebar must belong to a Mac window.");

        foreach (var identifier in new[]
        {
            "_XCUI:CloseWindow", "_XCUI:MinimizeWindow", "_XCUI:FullScreenWindow",
        })
        {
            var buttons = window!.Descendants()
                .Where(element => (string?)element.Attribute("identifier") == identifier)
                .ToArray();
            Assert.That(buttons, Has.Length.EqualTo(1),
                $"Expected one {identifier} button in the sidebar's window.");
            var buttonBounds = ReadBounds(buttons[0], identifier);
            var centerX = buttonBounds.X + buttonBounds.Width / 2;
            var centerY = buttonBounds.Y + buttonBounds.Height / 2;
            TestContext.Out.WriteLine(FormattableString.Invariant(
                $"{identifier} center=({centerX}, {centerY}); sidebar={sidebarBounds}"));

            Assert.Multiple(() =>
            {
                Assert.That(centerX, Is.InRange(sidebarBounds.X, sidebarBounds.X + sidebarBounds.Width),
                    $"{identifier} center must be horizontally inside the sidebar.");
                Assert.That(centerY, Is.InRange(sidebarBounds.Y, sidebarBounds.Y + sidebarBounds.Height),
                    $"{identifier} center must be vertically inside the sidebar.");
            });
        }
    }

    static Bounds ReadBounds(XElement element, string label)
    {
        double Read(string attribute)
        {
            var parsed = double.TryParse((string?)element.Attribute(attribute),
                NumberStyles.Float, CultureInfo.InvariantCulture, out var value);
            Assert.That(parsed && double.IsFinite(value), Is.True,
                $"{label} must expose a finite {attribute} coordinate.");
            return value;
        }

        var bounds = new Bounds(Read("x"), Read("y"), Read("width"), Read("height"));
        Assert.Multiple(() =>
        {
            Assert.That(bounds.Width, Is.GreaterThan(0), $"{label} must have a nonempty width.");
            Assert.That(bounds.Height, Is.GreaterThan(0), $"{label} must have a nonempty height.");
        });
        TestContext.Out.WriteLine($"{label} bounds={bounds}");
        return bounds;
    }

    readonly record struct Bounds(double X, double Y, double Width, double Height)
    {
        public override string ToString() =>
            FormattableString.Invariant($"({X}, {Y}, {Width}, {Height})");
    }
}
