# AdaptiveShell.Maui

Adaptive shell navigation for .NET MAUI. The navigation chrome adapts to the platform and window size — sidebar on large screens, navigation rail on medium screens, bottom navigation on small screens — with support for navigation groups, per-page icons and accent colors.

## Platforms

- Android
- iOS
- Mac Catalyst

## Install

```bash
dotnet add package AdaptiveShell.Maui
```

## Usage

Register the handlers in `MauiProgram.cs`:

```csharp
using AdaptiveShell.Hosting;

var builder = MauiApp.CreateBuilder();
builder
    .UseMauiApp<App>()
    .UseAdaptiveShell();
```

Derive your app shell from `AShell` and declare navigation items in XAML:

```xaml
<a:AShell
    x:Class="MyApp.AppShell"
    xmlns="http://schemas.microsoft.com/dotnet/2021/maui"
    xmlns:x="http://schemas.microsoft.com/winfx/2009/xaml"
    xmlns:a="clr-namespace:AdaptiveShell.Controls;assembly=AdaptiveShell"
    Title="MyApp">

    <a:AShellContent
        Title="Home"
        Icon="home.svg"
        ContentTemplate="{DataTemplate local:MainPage}" />

    <a:AShellGroup Title="Media" Icon="folder.svg">
        <a:AShellContent
            Title="Music"
            Icon="star.svg"
            ContentTemplate="{DataTemplate local:MusicPage}" />
        <a:AShellContent
            Title="Photos"
            Icon="star.svg"
            ContentTemplate="{DataTemplate local:PhotosPage}" />
    </a:AShellGroup>
</a:AShell>
```

- `AShellContent` — a top-level navigation item hosting a page.
- `AShellGroup` — groups several items behind one entry (flyout/drawer).
- `FullBleed` — lets page content extend under the floating navigation bar; drive it with `AdaptiveTrigger` visual states to react to window size.

See `Example/ExampleAShellApp` for a full sample.

## Accessibility

`AShellItem.AutomationId` (falling back to `Title`) is projected onto the native navigation views on every platform — `accessibilityIdentifier` on iOS/Mac Catalyst, `content-desc` on Android, `AutomationProperties.AutomationId` on Windows — so navigation items have stable identifiers for assistive technologies and UI tests. Group landing-page rows are exposed as `landing-<id>`.

## Testing

E2E tests live in `Tests/AdaptiveShell.UITests`. They drive the example app through the accessibility tree via Appium — locators use accessibility identifiers, never screen coordinates.

Prerequisites:

- Node.js, then `npm i -g appium`
- Drivers: `appium driver install uiautomator2 xcuitest mac2` (macOS) / `appium driver install windows` (Windows)

Run locally:

```bash
# macOS: Android / iOS / Mac Catalyst
Tests/AdaptiveShell.UITests/run-uitest.sh <android|ios|maccatalyst> [compact|wide]
```

```powershell
# Windows
Tests/AdaptiveShell.UITests/run-uitest.ps1
```

Configuration via environment variables: `UITEST_PLATFORM`, `UITEST_FORM` (`compact`|`wide`), `UITEST_APP_PATH`, `UITEST_DEVICE_NAME`, `UITEST_DEVICE_UDID`, `UITEST_APPIUM_URL`.

CI (`.github/workflows/uitest.yml`) runs Android (phone + tablet emulator matrix), iOS and Windows on every push/PR; Mac Catalyst runs locally only.

## Release & quality gate

Releases are gated on a full E2E matrix that runs on the **public repo** ([EVNII/AdaptiveShell.maui](https://github.com/EVNII/AdaptiveShell.maui), where GitHub Actions is free) via `.github/workflows/release-uitest.yml`, triggered on every sync to `main`:

- **iOS** 18 (macos-15) / 26 (macos-26) / 27 (xcode-27 preview, experimental) × { iPhone (compact), iPhone Duo (when available), iPad (wide) } — devices are discovered dynamically from the installed simulator runtimes
- **Android** API 26–36 (Appium UiAutomator2's floor is Android 8.0/API 26; API 23–25 remain compile-level coverage) × { phone, tablet }
- **Windows** single cell; **Mac Catalyst** experimental (hosted runners cannot grant the accessibility permission the Mac2 driver needs — verify locally)

The gate: `publish.yml`'s `release-gate` job waits for the matrix run matching the tagged commit and blocks the NuGet push unless every non-experimental cell is green. Cells whose environment does not exist (e.g. an iOS major not present on the runner image) are skipped rather than failed.

Screenshots are captured at key steps in every test (`Shot(...)` in `Tests/AdaptiveShell.UITests/BaseTest.cs`), uploaded per cell as artifacts, and assembled into a report published to GitHub Pages: <https://evnii.github.io/AdaptiveShell.maui/>.

## License

[MIT](LICENSE)
